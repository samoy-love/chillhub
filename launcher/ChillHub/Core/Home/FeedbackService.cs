// <copyright file="FeedbackService.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using System.Windows.Threading;

    /// <summary>
    /// Обратная связь: отправка сообщения на сервер, оффлайн-очередь на диске
    /// (%APPDATA%\ChillHub\feedback_queue.json) и фоновый ретрай.
    /// Класс не знает про конкретные контролы — с UI связан только через два колбэка.
    /// </summary>
    internal sealed class FeedbackService {
        /// <summary>Одно сообщение обратной связи. Сериализуется в очередь — имена полей менять нельзя.</summary>
        internal sealed record FeedbackDraft(string Name, string Contact, string Type, string Comment, bool AttachLogs, Dictionary<string, string>? System);

        /// <summary>Порт админки в локальной разработке: туда уходит fallback при недоступном API.</summary>
        private const int LocalAdminPort = 55777;

        /// <summary>Сколько сообщений разбираем за один проход очереди.</summary>
        private const int MaxSentPerFlush = 5;

        private readonly HttpClient http;
        private readonly Func<string> baseApiProvider;
        private readonly Action<string> showToast;
        private readonly Action<string> setStatus;

        private List<FeedbackDraft> queue = new();
        private DispatcherTimer? retryTimer;

        internal FeedbackService(HttpClient http, Func<string> baseApiProvider, Action<string> showToast, Action<string> setStatus) {
            this.http = http;
            this.baseApiProvider = baseApiProvider;
            this.showToast = showToast;
            this.setStatus = setStatus;
        }

        /// <summary>Путь к файлу оффлайн-очереди.</summary>
        internal static string QueuePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub", "feedback_queue.json");

        /// <summary>Сколько сообщений сейчас ждёт отправки.</summary>
        internal int PendingCount => this.queue.Count;

        /// <summary>Собирает краткую информацию о системе для прикрепления к сообщению.</summary>
        internal static Dictionary<string, string> CollectSystemInfo() {
            var dict = new Dictionary<string, string>();
            try {
                dict["os"] = Environment.OSVersion.VersionString;
                dict["arch"] = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                dict["dotnet"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
                dict["machineName"] = Environment.MachineName;
                dict["appVersion"] = typeof(FeedbackService).Assembly.GetName().Version?.ToString() ?? string.Empty;
            }
            catch (Exception ex) {
                // Диагностика не обязательна: отправим сообщение с тем, что успели собрать.
                Logging.Logger.Warn($"Feedback.CollectSystemInfo: {ex.Message}");
            }

            return dict;
        }

        /// <summary>Поднимает очередь с диска и запускает фоновый ретрай.</summary>
        internal void Start() {
            this.LoadQueue();
            this.StartRetryLoop();
        }

        /// <summary>Кладёт сообщение в очередь и сразу пробует её разобрать.</summary>
        internal void Enqueue(FeedbackDraft d) {
            this.queue.Add(d);
            this.SaveQueue();
            _ = this.FlushNowAsync();
        }

        /// <summary>
        /// Пробует отправить сообщение. В silent-режиме (фоновые ретраи) пользователю ничего не показываем.
        /// </summary>
        internal async Task<bool> TrySendAsync(FeedbackDraft d, bool silent = true) {
            try {
                var baseApi = this.baseApiProvider().TrimEnd('/');
                var url = baseApi + "/feedback/submit";

                // Общий персистентный лимит (делится с ErrorReporter): не заваливаем сервер отчётами
                if (!ErrorReporter.TryConsumeManual(out var retryAfter)) {
                    if (!silent) {
                        var mins = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
                        var text = $"Лимит ручных отправок исчерпан. Повторите через ~{mins} мин.";
                        this.setStatus(text);
                        this.showToast(text);
                    }

                    return false;
                }

                var (logsPayload, extraSystem) = await this.BuildDiagnosticsAsync(d).ConfigureAwait(true);

                using var req = new HttpRequestMessage(HttpMethod.Post, url) {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new {
                            name = d.Name,
                            contact = d.Contact,
                            type = d.Type,
                            comment = d.Comment,
                            attachLogs = d.AttachLogs,
                            logs = logsPayload,
                            system = extraSystem,
                        }),
                        Encoding.UTF8,
                        "application/json"),
                };

                HttpResponseMessage res;
                try {
                    res = await this.http.SendAsync(req).ConfigureAwait(false);
                }
                catch (Exception exSend) {
                    Logging.Logger.Error(exSend, "Feedback.Send.HttpError");
                    if (!silent) {
                        this.showToast("Не удалось отправить (сеть/сервер недоступны)");
                    }

                    // Локальная разработка: сеть до API не поднялась — пробуем порт админки
                    return await this.TryAdminFallbackAsync(baseApi, req.Content, "Feedback.Send.HttpError.Fallback").ConfigureAwait(false);
                }

                if (res.IsSuccessStatusCode) {
                    return true;
                }

                // Тело ответа — в лог: без него разбор жалоб «не отправляется» невозможен
                string body = await ReadBodySafeAsync(res).ConfigureAwait(false);
                Logging.Logger.Warn($"Feedback.Send failed: {(int)res.StatusCode} {res.ReasonPhrase}; body='{body}'");

                // Локальная разработка: например, 404 на порту API — повторяем на порт админки
                if (await this.TryAdminFallbackAsync(baseApi, req.Content, "Feedback.Send.FallbackUnexpected").ConfigureAwait(false)) {
                    return true;
                }

                if (!silent) {
                    this.showToast($"Сервер отклонил отправку: {(int)res.StatusCode}");
                }

                return false;
            }
            catch (Exception ex) {
                Logging.Logger.Error(ex, "Feedback.Send.Unexpected");
                if (!silent) {
                    this.showToast("Ошибка отправки");
                }

                return false;
            }
        }

        /// <summary>Разбирает оффлайн-очередь: до пяти сообщений за проход, с одной короткой повторной попыткой.</summary>
        internal async Task FlushNowAsync() {
            try {
                if (this.queue.Count == 0) {
                    return;
                }

                int i = 0;
                int sent = 0;
                while (i < this.queue.Count && sent < MaxSentPerFlush) {
                    var d = this.queue[i];
                    var ok = await this.TrySendAsync(d, silent: true).ConfigureAwait(true);
                    if (!ok) {
                        // короткий бэкофф и одна повторная попытка
                        await Task.Delay(800).ConfigureAwait(true);
                        ok = await this.TrySendAsync(d, silent: true).ConfigureAwait(true);
                    }

                    if (ok) {
                        this.queue.RemoveAt(i);
                        sent++;
                    }
                    else {
                        i++;
                    }
                }

                if (sent > 0) {
                    this.SaveQueue();
                    this.showToast(sent == 1
                        ? "Одно отложенное сообщение отправлено"
                        : $"Отправлены отложенные сообщения: {sent}");
                }
            }
            catch (Exception ex) {
                // Фоновая операция: молчим для пользователя, но фиксируем — очередь останется на диске.
                Logging.Logger.Error(ex, "Feedback.FlushQueue");
            }
        }

        /// <summary>Строит локальный URL админки для дев-окружения. Для не-localhost возвращает false.</summary>
        internal static bool TryBuildLocalAdminUrl(string baseApi, out string adminUrl) {
            adminUrl = string.Empty;
            if (!Uri.TryCreate(baseApi, UriKind.Absolute, out var u)) {
                return false;
            }

            var host = (u.Host ?? string.Empty).ToLowerInvariant();
            if (host != "localhost" && host != "127.0.0.1") {
                return false;
            }

            var ub = new UriBuilder(u) { Port = LocalAdminPort };
            adminUrl = new Uri(ub.Uri, "/feedback/submit").ToString();
            return true;
        }

        private async Task<(string Logs, Dictionary<string, string>? System)> BuildDiagnosticsAsync(FeedbackDraft d) {
            Dictionary<string, string>? extraSystem = d.System;
            if (!d.AttachLogs) {
                return (string.Empty, extraSystem);
            }

            try {
                var bundle = await Task.Run(() => Diagnostics.Build()).ConfigureAwait(true);
                extraSystem ??= new Dictionary<string, string>();
                foreach (var kv in bundle.SystemHints) {
                    extraSystem[kv.Key] = kv.Value;
                }

                return (bundle.LogsMarkdown, extraSystem);
            }
            catch (Exception ex) {
                // Не фатально: отправим сообщение без логов, чем не отправим вовсе.
                Logging.Logger.Warn($"Feedback.BuildDiagnostics: логи не собраны: {ex.Message}");
                return (string.Empty, extraSystem);
            }
        }

        private async Task<bool> TryAdminFallbackAsync(string baseApi, HttpContent content, string logContext) {
            if (!TryBuildLocalAdminUrl(baseApi, out var adminUrl)) {
                return false;
            }

            try {
                using var req = new HttpRequestMessage(HttpMethod.Post, adminUrl) { Content = content };
                var res = await this.http.SendAsync(req).ConfigureAwait(false);
                if (res.IsSuccessStatusCode) {
                    return true;
                }

                var body = await ReadBodySafeAsync(res).ConfigureAwait(false);
                Logging.Logger.Warn($"Feedback.Send fallback failed: {(int)res.StatusCode} {res.ReasonPhrase}; body='{body}'");
                return false;
            }
            catch (Exception ex) {
                Logging.Logger.Error(ex, logContext);
                return false;
            }
        }

        private static async Task<string> ReadBodySafeAsync(HttpResponseMessage res) {
            try {
                return await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"Feedback: тело ответа прочитать не удалось: {ex.Message}");
                return string.Empty;
            }
        }

        private void LoadQueue() {
            try {
                var p = QueuePath;
                var dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrWhiteSpace(dir)) {
                    Directory.CreateDirectory(dir);
                }

                if (!File.Exists(p)) {
                    this.queue = new List<FeedbackDraft>();
                    return;
                }

                var json = File.ReadAllText(p, Encoding.UTF8);
                this.queue = JsonSerializer.Deserialize<List<FeedbackDraft>>(json) ?? new List<FeedbackDraft>();
            }
            catch (Exception ex) {
                // Битый или недоступный файл очереди: начинаем с пустой, иначе форма обратной связи не работает.
                Logging.Logger.Warn($"Feedback.LoadQueue: очередь не прочитана, начинаем с пустой: {ex.Message}");
                this.queue = new List<FeedbackDraft>();
            }
        }

        private void SaveQueue() {
            try {
                var p = QueuePath;
                var dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrWhiteSpace(dir)) {
                    Directory.CreateDirectory(dir);
                }

                var json = JsonSerializer.Serialize(this.queue, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(p, json, Encoding.UTF8);
            }
            catch (Exception ex) {
                // Сообщение останется только в памяти до перезапуска — это заметная потеря, пишем Error.
                Logging.Logger.Error(ex, "Feedback.SaveQueue");
            }
        }

        private void StartRetryLoop() {
            try {
                this.retryTimer?.Stop();
                this.retryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
                this.retryTimer.Tick += async (s, e) => await this.FlushNowAsync().ConfigureAwait(true);
                this.retryTimer.Start();
            }
            catch (Exception ex) {
                // Без таймера очередь разберётся при следующем запуске лаунчера.
                Logging.Logger.Error(ex, "Feedback.StartRetryLoop");
            }
        }
    }
}
