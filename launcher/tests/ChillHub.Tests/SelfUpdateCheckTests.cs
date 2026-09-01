// <copyright file="SelfUpdateCheckTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Net.Http;
    using System.Threading.Tasks;

    using ChillHub.Core.SelfUpdate;
    using ChillHub.Core.Sync;

    using Xunit;

    /// <summary>
    /// Решение «надо ли обновлять сам лаунчер».
    /// <para>
    /// Самообновление кладёт файлы поверх работающего приложения, и при ошибке
    /// пользователь остаётся с лаунчером, который уже не может обновиться сам —
    /// чинится только переустановкой. Поэтому проверяется не только «когда
    /// обновляемся», но и — прежде всего — «когда НЕ обновляемся»: совпавшие
    /// версии, понижение версии, битый ответ сервера, отсутствие сети.
    /// </para>
    /// </summary>
    public class SelfUpdateCheckTests {
        /// <summary>
        /// A1. Главный предохранитель: версии совпали — обновляться не надо ВООБЩЕ,
        /// и счётчик попыток обнуляется. Без этого лаунчер обновляется по кругу,
        /// потому что preserve-файлы с манифестом не сходятся никогда.
        /// </summary>
        [Fact]
        public async Task СовпавшиеВерсииОстанавливаютОбновление() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.2.3");
            var attempts = stand.Attempts;
            attempts.Register("1.2.3");

            var decision = await Check(stand, "{\"version\":\"1.2.3\"}", new FakeSync(), attempts);

            Assert.Equal(SelfUpdateState.UpToDate, decision.State);
            Assert.False(decision.UpdateRequired);
            Assert.Equal("Установлена актуальная версия лаунчера.", decision.Ui.StatusText);
            Assert.Equal("Продолжить", decision.Ui.ButtonContent);
            Assert.True(decision.Ui.ButtonEnabled);
            Assert.Equal(0, attempts.Get("1.2.3"));
        }

        /// <summary>
        /// Понижение версии обновлением не считается: latest.json, указывающий на СТАРУЮ
        /// сборку (откат оператора, протухший кеш, чужой адрес сервера), молча заменял бы
        /// лаунчер на более раннюю версию вместе с уже закрытыми в ней дырами.
        /// </summary>
        [Fact]
        public async Task ПонижениеВерсииНеСчитаетсяОбновлением() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.3.0");

            var decision = await Check(stand, "{\"version\":\"1.2.9\"}", new FakeSync());

            Assert.Equal(SelfUpdateState.UpToDate, decision.State);
            Assert.False(decision.UpdateRequired);
        }

        /// <summary>
        /// Счётчик попыток при отказе от понижения НЕ сбрасывается: сервер, который
        /// откатили назад, не может обезвредить защиту от петли.
        /// </summary>
        [Fact]
        public async Task ОтказОтПониженияНеСбрасываетСчётчик() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.3.0");
            var attempts = stand.Attempts;
            attempts.Register("1.2.9");

            await Check(stand, "{\"version\":\"1.2.9\"}", new FakeSync(), attempts);

            Assert.Equal(1, attempts.Get("1.2.9"));
        }

        /// <summary>
        /// A6. Недопустимый номер версии блокирует обновление целиком: эта строка
        /// станет частью пути и аргументов внешнего процесса.
        /// </summary>
        [Theory]
        [InlineData("../../Startup")]
        [InlineData("1.2.3 --dst C:\\\\Windows")]
        [InlineData("latest")]
        public async Task НедопустимаяВерсияСервераБлокируетОбновление(string version) {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.2.3");

            var decision = await Check(stand, $"{{\"version\":\"{version}\"}}", new FakeSync());

            Assert.Equal(SelfUpdateState.InvalidRemoteVersion, decision.State);
            Assert.False(decision.UpdateRequired);
            Assert.Contains("недопустимый номер версии", decision.Ui.StatusText, StringComparison.Ordinal);

            // Кнопку оставляем рабочей: запереть пользователя в диалоге нельзя.
            Assert.True(decision.Ui.ButtonEnabled);
            Assert.Equal("Продолжить", decision.Ui.ButtonContent);
        }

        /// <summary>Сервер не сообщил версию — решает пользователь, а не мы за него.</summary>
        [Theory]
        [InlineData("{}")]
        [InlineData("{\"version\":\"\"}")]
        [InlineData("{\"version\":\"   \"}")]
        [InlineData("null")]
        public async Task ОтсутствующаяВерсияСервераОставляетВыборПользователю(string body) {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.2.3");

            var decision = await Check(stand, body, new FakeSync());

            Assert.Equal(SelfUpdateState.VersionUnknown, decision.State);
            Assert.Equal("Информация о версии отсутствует.", decision.Ui.StatusText);
            Assert.True(decision.Ui.ButtonEnabled);
        }

        /// <summary>Нет сети — лаунчер обязан запускаться, а не запираться в окне обновления.</summary>
        [Fact]
        public async Task БезСетиЛаунчерВсёРавноЗапускается() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.2.3");
            using var http = new HttpClient(SelfUpdateHandler.Offline("нет маршрута"));

            var decision = await NewChecker(stand, http, new FakeSync(), stand.Attempts).CheckAsync();

            Assert.Equal(SelfUpdateState.CheckFailed, decision.State);
            Assert.False(decision.UpdateRequired);
            Assert.True(decision.Ui.ButtonEnabled);
            // Текст исключения («нет маршрута») уезжает в лог: на экране игрок читает
            // причину и то, что будет дальше, — см. Core.Net.OfflineMessage.
            Assert.Contains("Не удалось проверить обновления", decision.Ui.StatusText, StringComparison.Ordinal);
            Assert.Contains("запустится", decision.Ui.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain("нет маршрута", decision.Ui.StatusText, StringComparison.Ordinal);
        }

        /// <summary>Сервер отдал мусор вместо JSON — тот же исход, что и «нет сети».</summary>
        [Fact]
        public async Task МусорВместоJsonНеЛомаетЗапуск() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.2.3");

            var decision = await Check(stand, "<html>502</html>", new FakeSync());

            Assert.Equal(SelfUpdateState.CheckFailed, decision.State);
            Assert.True(decision.Ui.ButtonEnabled);
        }

        /// <summary>
        /// Манифест новой версии не прошёл проверку структуры — предлагать обновление
        /// нельзя: качать по такому манифесту мы всё равно откажемся. Кнопка гасится,
        /// потому что нажимать её бессмысленно.
        /// </summary>
        [Fact]
        public async Task ОтклонённыйМанифестБлокируетКнопку() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.2.3");
            var sync = new FakeSync {
                OnManifest = _ => throw new ManifestValidationException("путь выходит за корень"),
            };

            var decision = await Check(stand, "{\"version\":\"1.2.4\"}", sync);

            Assert.Equal(SelfUpdateState.ManifestRejected, decision.State);
            Assert.False(decision.UpdateRequired);
            Assert.False(decision.Ui.ButtonEnabled);
            Assert.Contains("путь выходит за корень", decision.Ui.StatusText, StringComparison.Ordinal);
        }

        /// <summary>
        /// Манифест недоступен (сеть моргнула) — откатываемся на сравнение по версии,
        /// как было до диффа. Иначе новая версия просто не доехала бы до пользователя.
        /// </summary>
        [Fact]
        public async Task НедоступныйМанифестОткатываетНаСравнениеВерсий() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.2.3");
            var sync = new FakeSync { OnManifest = _ => throw new HttpRequestException("timeout") };

            var decision = await Check(stand, "{\"version\":\"1.2.4\"}", sync);

            Assert.Equal(SelfUpdateState.UpdateAvailable, decision.State);
            Assert.True(decision.UpdateRequired);
            Assert.Equal("1.2.4", decision.RemoteVersion);
            Assert.Equal("1.2.3", decision.LocalVersion);
            Assert.Equal("Обновить и перезапустить", decision.Ui.ButtonContent);
        }

        /// <summary>Файл отличается от манифеста — обновление действительно нужно.</summary>
        [Fact]
        public async Task РасхождениеСМанифестомТребуетОбновления() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.2.3");
            stand.Install.WriteFile("ChillHub.dll", "старое");
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Different("ChillHub.dll")),
            };

            var decision = await Check(stand, "{\"version\":\"1.2.4\"}", sync);

            Assert.Equal(SelfUpdateState.UpdateAvailable, decision.State);
        }

        /// <summary>
        /// Версии разные, но все файлы уже на месте — гонять апдейтер незачем.
        /// Счётчик попыток при этом сбрасывается.
        /// </summary>
        [Fact]
        public async Task СовпавшиеФайлыОтменяютОбновлениеПриРазныхВерсиях() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.2.3");
            stand.Install.WriteFile("ChillHub.dll", "новое");
            var attempts = stand.Attempts;
            attempts.Register("1.2.4");
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Matching(stand.Install.Root, "ChillHub.dll")),
            };

            var decision = await Check(stand, "{\"version\":\"1.2.4\"}", sync, attempts);

            Assert.Equal(SelfUpdateState.UpToDate, decision.State);
            Assert.Equal(0, attempts.Get("1.2.4"));
        }

        /// <summary>
        /// A2. Preserve-файл расходится с манифестом ВСЕГДА: апдейтер его не перезаписывает.
        /// Пока он считался причиной обновления, лаунчер и апдейтер спорили вечно —
        /// это и была петля самообновления.
        /// </summary>
        [Theory]
        [InlineData("config.json")]
        [InlineData("launcher.version")]
        [InlineData("launcher.update-status")]
        [InlineData("Uninstall.exe")]
        [InlineData("filelist.txt")]
        [InlineData("apply-update.log")]
        public async Task PreserveФайлНеПричинаОбновления(string rel) {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.2.3");
            stand.Install.WriteFile(rel, "местное состояние");
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Different(rel)),
            };

            var decision = await Check(stand, "{\"version\":\"1.2.4\"}", sync);

            Assert.Equal(SelfUpdateState.UpToDate, decision.State);
        }

        /// <summary>
        /// A4. Обновление на одну версию применялось трижды и не доехало — дальше не пускаем,
        /// но и в тупик не загоняем: кнопка становится «Проверить целостность».
        /// </summary>
        [Fact]
        public async Task ТриНеудачныеПопыткиОстанавливаютАвтообновление() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.2.3");
            var attempts = stand.Attempts;
            for (var i = 0; i < SelfUpdateChecker.MaxSameVersionAttempts; i++) {
                attempts.Register("1.2.4");
            }

            var sync = new FakeSync { OnManifest = _ => throw new HttpRequestException("timeout") };
            var decision = await Check(stand, "{\"version\":\"1.2.4\"}", sync, attempts);

            Assert.Equal(SelfUpdateState.LoopBlocked, decision.State);
            Assert.True(decision.LoopBlocked);
            Assert.False(decision.UpdateRequired);
            Assert.Equal("Проверить целостность", decision.Ui.ButtonContent);
            Assert.True(decision.Ui.ButtonEnabled);

            // Пользователю показывают, куда смотреть: журнал апдейтера и файл счётчика.
            Assert.Contains("apply-update.log", decision.Ui.StatusText, StringComparison.Ordinal);
            Assert.Contains(attempts.FilePath, decision.Ui.StatusText, StringComparison.Ordinal);
            Assert.Equal("1.2.4", decision.RemoteVersion);
        }

        /// <summary>На одну попытку меньше порога обновление ещё разрешено.</summary>
        [Fact]
        public async Task ПередПоследнейПопыткойОбновлениеЕщёРазрешено() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.2.3");
            var attempts = stand.Attempts;
            for (var i = 0; i < SelfUpdateChecker.MaxSameVersionAttempts - 1; i++) {
                attempts.Register("1.2.4");
            }

            var sync = new FakeSync { OnManifest = _ => throw new HttpRequestException("timeout") };
            var decision = await Check(stand, "{\"version\":\"1.2.4\"}", sync, attempts);

            Assert.Equal(SelfUpdateState.UpdateAvailable, decision.State);
        }

        /// <summary>A10. Strip-prefix пакета доезжает до окна: его же получит апдейтер.</summary>
        [Fact]
        public async Task StripPrefixПопадаетВРешение() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.2.3");
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(
                    SelfUpdateManifest.Different("ChillHub/ChillHub.exe"),
                    SelfUpdateManifest.Different("ChillHub/ChillHub.dll")),
            };

            var decision = await Check(stand, "{\"version\":\"1.2.4\"}", sync);

            Assert.Equal(SelfUpdateState.UpdateAvailable, decision.State);
            Assert.Equal("ChillHub", decision.StripPrefix);
        }

        /// <summary>Манифест запрашивается по адресу конкретной версии, а не «latest».</summary>
        [Fact]
        public async Task МанифестЗапрашиваетсяПоНомеруВерсии() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("launcher.version", "1.2.3");
            var sync = new FakeSync { OnManifest = _ => new Manifest() };

            await Check(stand, "{\"version\":\"1.2.4\"}", sync);

            Assert.Contains("https://example.test/manifests/launcher/1.2.4.json", sync.ManifestUrls);
        }

        // -------------------------------------------------------------------
        // A4. Выход из состояния «остановлено защитой от петли».
        // -------------------------------------------------------------------

        /// <summary>
        /// Целостность в порядке — петля была ложной: пишем маркер, сбрасываем счётчик
        /// и выпускаем пользователя. Это единственный выход, не требующий переустановки.
        /// </summary>
        [Fact]
        public async Task ЦелаяУстановкаСнимаетБлокировкуПетли() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("ChillHub.dll", "новое");
            var attempts = stand.Attempts;
            attempts.Register("1.2.4");
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Matching(stand.Install.Root, "ChillHub.dll")),
            };
            var ui = new UiRecorder();

            var result = await NewChecker(stand, Offline(), sync, attempts).VerifyIntegrityAsync("1.2.4", ui.Apply);

            Assert.Equal("Установлена актуальная версия лаунчера.", result.Ui.StatusText);
            Assert.True(result.Ui.ButtonEnabled);
            Assert.Equal(0, attempts.Get("1.2.4"));
            Assert.Equal("1.2.4", SelfUpdateVersions.ReadLocalVersion(stand.Install.Root));
        }

        /// <summary>
        /// Расхождения есть — счётчик НЕ сбрасываем (защита обязана остаться), но кнопку
        /// «Продолжить» даём: запирать пользователя в диалоге нельзя.
        /// </summary>
        [Fact]
        public async Task РасхожденияОставляютЗащитуНоВыпускаютПользователя() {
            using var stand = new SelfUpdateStand();
            var attempts = stand.Attempts;
            attempts.Register("1.2.4");
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(
                    SelfUpdateManifest.Different("ChillHub.dll"),
                    SelfUpdateManifest.Different("ChillHub.exe")),
            };

            var result = await NewChecker(stand, Offline(), sync, attempts).VerifyIntegrityAsync("1.2.4", _ => { });

            Assert.Contains("Проверка целостности не пройдена: расхождений 2", result.Ui.StatusText, StringComparison.Ordinal);
            Assert.Contains("ChillHub.dll — missing", result.Ui.StatusText, StringComparison.Ordinal);
            Assert.Equal("Продолжить", result.Ui.ButtonContent);
            Assert.True(result.Ui.ButtonEnabled);
            Assert.Equal(1, attempts.Get("1.2.4"));
        }

        /// <summary>Длинный список расхождений обрезается — окно не должно превращаться в лог.</summary>
        [Fact]
        public async Task ДлинныйСписокРасхожденийОбрезается() {
            using var stand = new SelfUpdateStand();
            var files = new ChillHub.Core.Sync.ManifestFile[8];
            for (var i = 0; i < files.Length; i++) {
                files[i] = SelfUpdateManifest.Different($"file{i}.dll");
            }

            var sync = new FakeSync { OnManifest = _ => SelfUpdateManifest.Of(files) };

            var result = await NewChecker(stand, Offline(), sync, stand.Attempts).VerifyIntegrityAsync("1.2.4", _ => { });

            Assert.Contains("расхождений 8", result.Ui.StatusText, StringComparison.Ordinal);
            Assert.Contains("... и ещё 3", result.Ui.StatusText, StringComparison.Ordinal);
        }

        /// <summary>
        /// Проверка целостности не состоялась (нет сети) — счётчик не сбрасываем,
        /// но кнопку возвращаем: иначе окно превращается в тупик.
        /// </summary>
        [Fact]
        public async Task НеудачнаяПроверкаЦелостностиНеСбрасываетСчётчик() {
            using var stand = new SelfUpdateStand();
            var attempts = stand.Attempts;
            attempts.Register("1.2.4");
            var sync = new FakeSync { OnManifest = _ => throw new HttpRequestException("нет сети") };

            var result = await NewChecker(stand, Offline(), sync, attempts).VerifyIntegrityAsync("1.2.4", _ => { });

            Assert.Contains("Не удалось проверить целостность", result.Ui.StatusText, StringComparison.Ordinal);
            Assert.True(result.Ui.ButtonEnabled);
            Assert.Equal(1, attempts.Get("1.2.4"));
        }

        /// <summary>
        /// Файлы совпали, а маркер не записался — счётчик НЕ сбрасываем. Иначе защита,
        /// которая обязана остановить петлю, обезврежена тем же кодом, что её проверяет.
        /// </summary>
        [Fact]
        public async Task НеудачаЗаписиМаркераНеСбрасываетСчётчик() {
            using var stand = new SelfUpdateStand();
            stand.Install.WriteFile("ChillHub.dll", "новое");
            System.IO.Directory.CreateDirectory(stand.Install.PathTo("launcher.version"));
            var attempts = stand.Attempts;
            attempts.Register("1.2.4");
            var sync = new FakeSync {
                OnManifest = _ => SelfUpdateManifest.Of(SelfUpdateManifest.Matching(stand.Install.Root, "ChillHub.dll")),
            };

            var result = await NewChecker(stand, Offline(), sync, attempts).VerifyIntegrityAsync("1.2.4", _ => { });

            Assert.Contains("записать отметку о версии не удалось", result.Ui.StatusText, StringComparison.Ordinal);
            Assert.Equal("Продолжить", result.Ui.ButtonContent);
            Assert.True(result.Ui.ButtonEnabled);
            Assert.Equal(1, attempts.Get("1.2.4"));
        }

        /// <summary>
        /// Проверять целостность по недопустимой версии нельзя — этот номер уже
        /// отвергнут как данные. Просто выпускаем пользователя, не трогая ни сеть, ни статус.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("../../Startup")]
        public async Task БезГодногоНомераВерсииПроверкаНеЗапускается(string? version) {
            using var stand = new SelfUpdateStand();
            var sync = new FakeSync { OnManifest = _ => throw new InvalidOperationException("сюда ходить нельзя") };
            var ui = new UiRecorder();

            var result = await NewChecker(stand, Offline(), sync, stand.Attempts).VerifyIntegrityAsync(version, ui.Apply);

            Assert.Empty(sync.ManifestUrls);
            Assert.Empty(ui.States);
            Assert.Null(result.Ui.StatusText);
            Assert.Equal("Продолжить", result.Ui.ButtonContent);
            Assert.True(result.Ui.ButtonEnabled);
        }

        private static HttpClient Offline() => new HttpClient(SelfUpdateHandler.Offline());

        private static SelfUpdateChecker NewChecker(SelfUpdateStand stand, HttpClient http, ISyncService sync, UpdateAttemptsStore attempts)
            => new SelfUpdateChecker(http, sync, () => "https://example.test", stand.Paths, attempts);

        private static async Task<SelfUpdateDecision> Check(
            SelfUpdateStand stand, string latestJson, ISyncService sync, UpdateAttemptsStore? attempts = null) {
            using var http = new HttpClient(SelfUpdateHandler.Json(latestJson));
            return await NewChecker(stand, http, sync, attempts ?? stand.Attempts).CheckAsync();
        }
    }
}
