// <copyright file="ShortcutRequestFile.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;
    using System.Globalization;
    using System.IO;
    using System.Threading;

    /// <summary>
    /// Передача запроса ярлыка уже запущенному лаунчеру.
    /// <para>
    /// Вторая копия лаунчера не запускается (см. <see cref="ChillHub.Core.SingleInstance"/>):
    /// она лишь поднимает окно первой именованным событием и завершается. Событие не несёт
    /// ничего, кроме самого факта сигнала, — а ярлык обязан открыть КОНКРЕТНУЮ игру.
    /// Поэтому запрос кладётся в файл рядом с конфигом: копия, которой не разрешили
    /// запуститься, пишет его ДО сигнала, а живой лаунчер забирает по сигналу.
    /// </para>
    /// <para>
    /// Запрос забирается ровно один раз и сразу удаляется. Невостребованная запись
    /// протухает: без срока годности ярлык, нажатый при выключенном лаунчере, который так
    /// и не поднялся (обязательное обновление, отказ пользователя), открывал бы игру при
    /// следующем запуске — через час или через неделю.
    /// </para>
    /// </summary>
    internal static class ShortcutRequestFile {
        /// <summary>
        /// Сколько запрос считается свежим. Двух минут хватает и на окно самообновления,
        /// и на медленный старт; всё, что дольше, — уже не «пользователь только что нажал
        /// на ярлык».
        /// </summary>
        private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Каталог тот же, что у конфига (%APPDATA%\ChillHub), и по той же причине:
        /// %LOCALAPPDATA%\ChillHub — это каталог УСТАНОВКИ лаунчера, и любой файл оттуда
        /// уезжает в пакет самообновления (см. комментарий в Core/Config.cs).
        /// </summary>
        private static readonly string DefaultAppDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChillHub");

        /// <summary>
        /// Подменённый на время теста каталог. AsyncLocal, а не обычное поле: классы xUnit
        /// идут параллельно, и подмена в одном не должна уводить файл у другого.
        /// </summary>
        private static readonly AsyncLocal<string?> ScopedAppDir = new AsyncLocal<string?>();

        private static string AppDir => ScopedAppDir.Value ?? DefaultAppDir;

        private static string RequestPath => Path.Combine(AppDir, "shortcut_request.txt");

        /// <summary>Уводит файл запроса в подставной каталог — для тестов.</summary>
        /// <param name="dir">Каталог, играющий роль %APPDATA%\ChillHub.</param>
        /// <returns>Объект, возвращающий настоящий каталог.</returns>
        internal static IDisposable OverrideDirForTests(string dir) => new AppDirOverride(dir);

        /// <summary>
        /// Кладёт запрос на диск. Ошибки гасятся: не сумели передать — лаунчер просто
        /// откроется на каталоге, а не на странице игры.
        /// </summary>
        /// <param name="request">Запрос ярлыка; null — обычный запуск, писать нечего.</param>
        /// <param name="nowUtc">Момент записи; по умолчанию — сейчас.</param>
        internal static void Write(ShortcutRequest? request, DateTime? nowUtc = null) {
            if (request == null || string.IsNullOrWhiteSpace(request.GameId)) {
                return;
            }

            try {
                Directory.CreateDirectory(AppDir);
                var stamp = (nowUtc ?? DateTime.UtcNow).Ticks.ToString(CultureInfo.InvariantCulture);
                File.WriteAllLines(RequestPath, new[] { stamp, request.GameId, request.Title, request.ExePath });
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"ShortcutRequestFile.Write('{request.GameId}'): {ex.Message}");
            }
        }

        /// <summary>
        /// Забирает и удаляет запрос.
        /// </summary>
        /// <param name="nowUtc">Момент чтения; по умолчанию — сейчас.</param>
        /// <returns>Свежий запрос либо null, если его нет или он протух.</returns>
        internal static ShortcutRequest? Consume(DateTime? nowUtc = null) {
            string[] lines;
            try {
                if (!File.Exists(RequestPath)) {
                    return null;
                }

                lines = File.ReadAllLines(RequestPath);
            }
            catch (Exception ex) {
                Logging.Logger.Warn($"ShortcutRequestFile.Consume: {ex.Message}");
                return null;
            }
            finally {
                // Удаляем в любом случае, даже если прочитать не удалось: файл, который не
                // читается, мешал бы каждому следующему запуску.
                try {
                    File.Delete(RequestPath);
                }
                catch (Exception ex) {
                    Logging.Logger.Warn($"ShortcutRequestFile.Consume: не удалось удалить запрос: {ex.Message}");
                }
            }

            if (lines.Length < 2
                || !long.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                || ticks < 0 || ticks > DateTime.MaxValue.Ticks
                || string.IsNullOrWhiteSpace(lines[1])) {
                return null;
            }

            // Отрицательный возраст — это переведённые назад часы, а не запрос из будущего:
            // такой записи верить нельзя ровно так же, как протухшей.
            var age = (nowUtc ?? DateTime.UtcNow) - new DateTime(ticks, DateTimeKind.Utc);
            if (age > Freshness || age < -Freshness) {
                return null;
            }

            return new ShortcutRequest(
                lines[1].Trim(),
                lines.Length > 2 ? lines[2].Trim() : string.Empty,
                lines.Length > 3 ? lines[3].Trim() : string.Empty);
        }

        /// <summary>Возвращает файл запроса на настоящее место.</summary>
        private sealed class AppDirOverride : IDisposable {
            private readonly string? previous;

            internal AppDirOverride(string dir) {
                this.previous = ScopedAppDir.Value;
                ScopedAppDir.Value = dir;
            }

            public void Dispose() => ScopedAppDir.Value = this.previous;
        }
    }
}
