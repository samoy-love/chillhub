// <copyright file="HomeDialogsTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.AccessControl;
    using System.Security.Principal;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;

    using ChillHub.Core;
    using ChillHub.Core.Home;

    using Xunit;

    /// <summary>
    /// Проверка папки для игр и вопрос об удалении локальных файлов.
    /// <para>
    /// Всё в этом файле висит на одном сценарии: пользователь нажал «Установить», а писать
    /// в выбранную папку нельзя. Правильный ответ — предложить другую папку. Неправильный —
    /// молча уронить установку с непонятной ошибкой; именно так это и выглядело бы, если бы
    /// отказ в доступе не был распознан.
    /// </para>
    /// <para>
    /// Ни один тест не открывает окно: показ диалогов уведён за швы
    /// <see cref="HomeDialogs.AskYesNo"/>, <see cref="HomeDialogs.ShowError"/> и
    /// <see cref="HomeDialogs.PickFolder"/>. Модальное окно в прогоне — это повисший CI.
    /// </para>
    /// </summary>
    public class HomeDialogsTests : IDisposable {
        public void Dispose() => HomeDialogs.ResetDialogsForTests();

        // ---- ProbeWritable ----

        /// <summary>Обычная папка пользователя: писать можно, и пробный файл за собой убирается.</summary>
        [Fact]
        public void ЗаписьВДоступнуюПапкуРазрешенаИСледовНеОставляет() {
            using var dir = new TempDir();

            Assert.Equal(HomeDialogs.WriteProbe.Ok, HomeDialogs.ProbeWritable(dir.Root));
            Assert.Empty(Directory.GetFiles(dir.Root));
        }

        /// <summary>
        /// Папки для игр может ещё не быть — при первой установке её нет всегда.
        /// Проверка обязана создать её сама, а не объявить недоступной.
        /// </summary>
        [Fact]
        public void НесуществующаяПапкаСоздаётся() {
            using var dir = new TempDir();
            var target = dir.PathTo("games/chillhub");

            Assert.Equal(HomeDialogs.WriteProbe.Ok, HomeDialogs.ProbeWritable(target));
            Assert.True(Directory.Exists(target), "папка не создана");
        }

        /// <summary>
        /// Нет прав на запись — это именно тот случай, ради которого проверка и заведена:
        /// пользователю обязаны предложить другую папку.
        /// </summary>
        [Fact]
        public void ПапкаБезПравНаЗаписьВидитсяКакОтказ() {
            using var dir = new TempDir();
            var target = Path.Combine(dir.Root, "readonly");
            Directory.CreateDirectory(target);

            using (new DenyWriteScope(target)) {
                Assert.Equal(HomeDialogs.WriteProbe.Denied, HomeDialogs.ProbeWritable(target));
            }
        }

        /// <summary>
        /// Пробный файл остался от прошлого раза и открыт только для чтения. Windows отвечает
        /// на такое отказом в доступе — и это по-прежнему повод предложить другую папку.
        /// </summary>
        [Fact]
        public void ПробныйФайлТолькоДляЧтенияСчитаетсяОтказом() {
            using var dir = new TempDir();
            var probe = Path.Combine(dir.Root, ".write_test.tmp");
            File.WriteAllText(probe, string.Empty);
            File.SetAttributes(probe, FileAttributes.ReadOnly);
            try {
                Assert.Equal(HomeDialogs.WriteProbe.Denied, HomeDialogs.ProbeWritable(dir.Root));
            }
            finally {
                File.SetAttributes(probe, FileAttributes.Normal);
            }
        }

        /// <summary>
        /// Пробный файл держит другой процесс — например, вторая копия лаунчера.
        /// Windows отдаёт это как IOException, и вердикт всё равно «сюда писать нельзя».
        /// </summary>
        [Fact]
        public void ЗанятыйПробныйФайлСчитаетсяОтказом() {
            using var dir = new TempDir();
            var probe = Path.Combine(dir.Root, ".write_test.tmp");
            using var hold = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None);

            Assert.Equal(HomeDialogs.WriteProbe.Denied, HomeDialogs.ProbeWritable(dir.Root));
        }

        /// <summary>
        /// На месте папки лежит файл. Это сбой, но не отказ в доступе: предлагать пользователю
        /// выбрать другую папку тут не за что, и беспокоить его вопросом не надо.
        /// </summary>
        [Fact]
        public void ФайлВместоПапкиНеПутаетсяСОтказомВДоступе() {
            using var dir = new TempDir();
            var asFile = dir.WriteFile("занято.dat", "не папка");

            Assert.Equal(HomeDialogs.WriteProbe.UnknownIoError, HomeDialogs.ProbeWritable(asFile));
        }

        // ---- ClassifyIoFailure ----

        /// <summary>
        /// Главное, ради чего вердикт перестал зависеть от текста: сообщение IOException
        /// локализовано. На немецкой Windows там «Zugriff verweigert», на французской
        /// «accès refusé», на японской — иероглифы. Разбор по словам «доступ»/«access»
        /// не узнавал бы отказ, пользователю не предложили бы другую папку, и установка
        /// упиралась бы в непонятный сбой на любой системе, кроме русской и английской.
        /// </summary>
        [Theory]
        [InlineData("Zugriff verweigert")]
        [InlineData("Accès refusé")]
        [InlineData("アクセスが拒否されました")]
        [InlineData("Acceso denegado")]
        [InlineData("Отказано в доступе")]
        [InlineData("Access to the path is denied")]
        public void ОтказВДоступеУзнаётсяНаЛюбомЯзыке(string message) {
            var ex = new IOException(message) { HResult = unchecked((int)0x80070005) };

            Assert.Equal(HomeDialogs.WriteProbe.Denied, HomeDialogs.ClassifyIoFailure(ex));
        }

        /// <summary>
        /// Отказ приходит не только кодом 5: защита от записи, занятый и заблокированный файл,
        /// нехватка привилегий — для пользователя это одно и то же «сюда писать нельзя».
        /// </summary>
        [Theory]
        [InlineData(5)]
        [InlineData(19)]
        [InlineData(32)]
        [InlineData(33)]
        [InlineData(1314)]
        public void КодыОтказаВДоступеРаспознаютсяПоНомеру(int win32) {
            var ex = new IOException("текста нет") { HResult = unchecked((int)0x80070000) | win32 };

            Assert.Equal(HomeDialogs.WriteProbe.Denied, HomeDialogs.ClassifyIoFailure(ex));
        }

        /// <summary>
        /// Кончилось место, пропал сетевой диск, не нашлась часть пути — это сбои, но не отказы.
        /// Предлагать другую папку тут бессмысленно: вопрос ничего не починит, а установка
        /// прервётся вопросом на ровном месте.
        /// </summary>
        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(112)]
        [InlineData(1231)]
        public void ПостороннийКодWin32ЗаОтказНеСчитается(int win32) {
            var ex = new IOException("сбой") { HResult = unchecked((int)0x80070000) | win32 };

            Assert.Equal(HomeDialogs.WriteProbe.UnknownIoError, HomeDialogs.ClassifyIoFailure(ex));
        }

        /// <summary>
        /// HRESULT бывает не из Win32 — исключение могли собрать вручную или обернуть.
        /// Тогда работает прежний разбор по тексту: русская и английская локали не должны
        /// потерять уже работавшее распознавание из-за перехода на коды.
        /// </summary>
        [Theory]
        [InlineData("Отказано в доступе к папке", true)]
        [InlineData("Access is denied", true)]
        [InlineData("Устройство не готово", false)]
        public void БезКодаWin32ВердиктВыноситсяПоТексту(string message, bool denied) {
            var ex = new IOException(message) { HResult = unchecked((int)0x80131620) };

            var expected = denied ? HomeDialogs.WriteProbe.Denied : HomeDialogs.WriteProbe.UnknownIoError;
            Assert.Equal(expected, HomeDialogs.ClassifyIoFailure(ex));
        }

        // ---- EnsureGamesPathAccessibleOrPrompt ----

        /// <summary>
        /// Папка в порядке — пользователя не трогаем вовсе. Лишний вопрос перед каждой
        /// установкой раздражает сильнее, чем помогает.
        /// </summary>
        [Fact]
        public void ДоступнаяПапкаВопросовНеВызывает() {
            using var scope = new ConfigDirsScope();
            using var games = new TempDir();
            UseGamesPath(games.Root);
            var log = new DialogLog();

            Assert.True(HomeDialogs.EnsureGamesPathAccessibleOrPrompt());
            Assert.Empty(log.Asked);
            Assert.Equal(0, log.PickCalls);
        }

        /// <summary>
        /// Сбой, не похожий на отказ в доступе, вопросом не сопровождается: выбор другой папки
        /// его не исправит, а установку прерывать не за что.
        /// </summary>
        [Fact]
        public void НеясныйСбойПроверкиНеБеспокоитПользователя() {
            using var scope = new ConfigDirsScope();
            using var games = new TempDir();
            UseGamesPath(games.WriteFile("занято.dat", "не папка"));
            var log = new DialogLog();

            Assert.True(HomeDialogs.EnsureGamesPathAccessibleOrPrompt());
            Assert.Empty(log.Asked);
        }

        /// <summary>
        /// Пользователь отказался выбирать другую папку — установку продолжать нельзя.
        /// Диалог выбора папки при этом не открывается: «нет» значит «нет».
        /// </summary>
        [Fact]
        public void ОтказОтВыбораПапкиОстанавливаетУстановку() {
            using var scope = new ConfigDirsScope();
            using var games = new TempDir();
            using var denied = new DeniedGamesPath(games.Root);
            UseGamesPath(games.Root);
            var log = new DialogLog { Answer = false };

            Assert.False(HomeDialogs.EnsureGamesPathAccessibleOrPrompt());
            Assert.Single(log.Asked);
            Assert.Equal(0, log.PickCalls);
        }

        /// <summary>
        /// Согласие и годная папка: путь уезжает в конфиг и на диск. Без записи на диск
        /// выбор жил бы до перезапуска, и следующий запуск снова упёрся бы в ту же папку.
        /// </summary>
        [Fact]
        public void ВыбраннаяПапкаСохраняетсяВКонфигИНаДиск() {
            using var scope = new ConfigDirsScope();
            using var games = new TempDir();
            using var fresh = new TempDir();
            using var denied = new DeniedGamesPath(games.Root);
            UseGamesPath(games.Root);
            var log = new DialogLog { Answer = true, Folder = fresh.Root };

            Assert.True(HomeDialogs.EnsureGamesPathAccessibleOrPrompt());
            Assert.Equal(fresh.Root, ConfigService.Current.GamesPath);
            Assert.Equal(fresh.Root, scope.ReadConfigFromDisk().GamesPath);
            Assert.Empty(log.Errors);
        }

        /// <summary>Пользователь закрыл выбор папки — прежний путь остаётся, установка не идёт.</summary>
        [Fact]
        public void ОтменаВыбораПапкиОставляетПрежнийПуть() {
            using var scope = new ConfigDirsScope();
            using var games = new TempDir();
            using var denied = new DeniedGamesPath(games.Root);
            UseGamesPath(games.Root);
            var log = new DialogLog { Answer = true, Folder = null };

            Assert.False(HomeDialogs.EnsureGamesPathAccessibleOrPrompt());
            Assert.Equal(games.Root, ConfigService.Current.GamesPath);
        }

        /// <summary>
        /// Новая папка тоже недоступна — молча принять её нельзя: пользователь ушёл бы
        /// в установку с тем же отказом, только уже без объяснения.
        /// </summary>
        [Fact]
        public void НедоступнаяНоваяПапкаОтвергаетсяСОшибкой() {
            using var scope = new ConfigDirsScope();
            using var games = new TempDir();
            using var denied = new DeniedGamesPath(games.Root);
            UseGamesPath(games.Root);
            var log = new DialogLog { Answer = true, Folder = games.WriteFile("файл.dat", "не папка") };

            Assert.False(HomeDialogs.EnsureGamesPathAccessibleOrPrompt());
            Assert.Equal(games.Root, ConfigService.Current.GamesPath);
            Assert.Single(log.Errors);
        }

        /// <summary>
        /// Папку выбрали, а настройки записать не вышло. Установку это не отменяет — папка
        /// годная, — но пользователю обязаны сказать, что выбор не переживёт перезапуск.
        /// </summary>
        [Fact]
        public void СбойСохраненияНастроекНеОтменяетВыборНоЗамеченПользователем() {
            using var scope = new ConfigDirsScope();
            using var games = new TempDir();
            using var fresh = new TempDir();
            using var denied = new DeniedGamesPath(games.Root);
            UseGamesPath(games.Root);
            var log = new DialogLog { Answer = true, Folder = fresh.Root };
            scope.BlockAppDir();

            Assert.True(HomeDialogs.EnsureGamesPathAccessibleOrPrompt());
            Assert.Equal(fresh.Root, ConfigService.Current.GamesPath);
            Assert.Single(log.Errors);
        }

        /// <summary>
        /// Выбор папки может упасть сам — например, диалог не поднялся. Это отказ,
        /// а не повод уронить установку исключением из проверки прав.
        /// </summary>
        [Fact]
        public void СбойДиалогаВыбораПапкиПревращаетсяВОтказ() {
            using var scope = new ConfigDirsScope();
            using var games = new TempDir();
            using var denied = new DeniedGamesPath(games.Root);
            UseGamesPath(games.Root);
            HomeDialogs.AskYesNo = (_, _) => true;
            HomeDialogs.PickFolder = () => throw new InvalidOperationException("диалог не поднялся");

            Assert.False(HomeDialogs.EnsureGamesPathAccessibleOrPrompt());
        }

        // ---- Вопрос об удалении локальных файлов ----

        /// <summary>
        /// Вопрос обязан назвать и игру, и папку: пользователь соглашается на безвозвратное
        /// удаление, и «удалить файлы?» без имени папки — это согласие вслепую.
        /// </summary>
        [Fact]
        public void ВопросОбУдаленииНазываетИгруИПапку() {
            UiThread.Run(() => {
                var content = HomeDialogs.BuildConfirmDeleteContent(new Grid(), "Lethal Company", @"D:\Games\ChillHub\lethal");

                Assert.Contains("Lethal Company", content.Question.Text, StringComparison.Ordinal);
                Assert.Contains("D:/Games/ChillHub/lethal", content.FolderLine.Text, StringComparison.Ordinal);
                Assert.Equal("Отмена", content.CancelButton.Content);
                Assert.Equal("Удалить", content.DeleteButton.Content);
            });
        }

        /// <summary>
        /// Задвоенные слеши в пути приходят из склейки каталогов. Показывать их пользователю
        /// нельзя: «D:\\Games\\\\game» читается как другая папка, а согласие тут необратимо.
        /// </summary>
        [Fact]
        public void ПутьВВопросеПоказываетсяВНормальномВиде() {
            UiThread.Run(() => {
                var content = HomeDialogs.BuildConfirmDeleteContent(new Grid(), "Игра", @"D:\Games\\ChillHub\\game");

                Assert.Contains("D:/Games/ChillHub/game", content.FolderLine.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("//", content.FolderLine.Text, StringComparison.Ordinal);
            });
        }

        /// <summary>
        /// Ресурсов темы может не быть — окно строится из кода и владельца ему передают
        /// какой есть. Тогда берутся запасные цвета: белый текст на чёрном читается,
        /// а прозрачный на прозрачном — нет.
        /// </summary>
        [Fact]
        public void БезРесурсовТемыБерутсяЗапасныеЦвета() {
            UiThread.Run(() => {
                var content = HomeDialogs.BuildConfirmDeleteContent(new Grid(), "Игра", @"D:\game");

                Assert.Equal(Color.FromRgb(18, 18, 18), ((SolidColorBrush)content.Root.Background).Color);
                Assert.Equal(Colors.White, ((SolidColorBrush)content.Question.Foreground).Color);
                Assert.Equal(Color.FromRgb(156, 163, 175), ((SolidColorBrush)content.FolderLine.Foreground).Color);
                Assert.Null(content.CancelButton.Style);
                Assert.Null(content.DeleteButton.Style);
            });
        }

        /// <summary>
        /// Когда тема на месте, окно берёт её кисти и стили кнопок: системный вид посреди
        /// тёмного лаунчера выглядит как чужое окно, а на такое не жмут.
        /// </summary>
        [Fact]
        public void РесурсыТемыПодхватываютсяОкном() {
            UiThread.Run(() => {
                var owner = new Grid();
                var surface = new SolidColorBrush(Colors.DarkSlateGray);
                var primary = new Style(typeof(Button));
                var ghost = new Style(typeof(Button));
                owner.Resources["Brush.Surface"] = surface;
                owner.Resources["Brush.Title"] = Brushes.Red;
                owner.Resources["Style.Button.Primary"] = primary;
                owner.Resources["Style.Button.GhostNeutral"] = ghost;

                var content = HomeDialogs.BuildConfirmDeleteContent(owner, "Игра", @"D:\game");

                Assert.Same(surface, content.Root.Background);
                Assert.Same(Brushes.Red, content.Question.Foreground);
                Assert.Same(primary, content.DeleteButton.Style);
                Assert.Same(ghost, content.CancelButton.Style);
            });
        }

        /// <summary>
        /// Подставляет папку игр и глушит автоотчёты об ошибках.
        /// <para>
        /// Второе обязательно: под <see cref="ConfigDirsScope"/> конфиг разворачивается заново,
        /// со значениями по умолчанию, а отправка отчётов там включена. Тест, дошедший до
        /// <c>Logger.Error</c>, полез бы в сеть и в файл квоты отчётов в настоящем %APPDATA%.
        /// </para>
        /// </summary>
        private static void UseGamesPath(string path) {
            var cfg = ConfigService.Current;
            cfg.GamesPath = path;
            cfg.AutoErrorReports = false;
        }

        /// <summary>
        /// Подставной показ диалогов: запоминает, о чём спросили, и отвечает заранее заданным.
        /// Настоящие окна в прогоне не поднимаются — модальное окно повесило бы его насмерть.
        /// </summary>
        private sealed class DialogLog {
            internal DialogLog() {
                HomeDialogs.AskYesNo = (message, caption) => {
                    this.Asked.Add(message);
                    return this.Answer;
                };
                HomeDialogs.ShowError = (message, caption) => this.Errors.Add(message);
                HomeDialogs.PickFolder = () => {
                    this.PickCalls++;
                    return this.Folder;
                };
            }

            /// <summary>Ответ пользователя на вопрос «выбрать другую папку?».</summary>
            internal bool Answer { get; init; }

            /// <summary>Что вернёт диалог выбора папки; null — пользователь отказался.</summary>
            internal string? Folder { get; init; }

            /// <summary>Заданные вопросы.</summary>
            internal List<string> Asked { get; } = new();

            /// <summary>Показанные сообщения об ошибке.</summary>
            internal List<string> Errors { get; } = new();

            /// <summary>Сколько раз открывали выбор папки.</summary>
            internal int PickCalls { get; private set; }
        }

        /// <summary>
        /// Делает папку недоступной для записи, не трогая права: кладёт туда пробный файл
        /// только для чтения. Проверка споткнётся ровно об него — как и при настоящем
        /// отсутствии прав, но без правки списков доступа.
        /// </summary>
        private sealed class DeniedGamesPath : IDisposable {
            private readonly string probe;

            internal DeniedGamesPath(string folder) {
                this.probe = Path.Combine(folder, ".write_test.tmp");
                File.WriteAllText(this.probe, string.Empty);
                File.SetAttributes(this.probe, FileAttributes.ReadOnly);
            }

            public void Dispose() {
                try {
                    File.SetAttributes(this.probe, FileAttributes.Normal);
                }
                catch (IOException) {
                    // Файла уже нет — убирать нечего.
                }
            }
        }

        /// <summary>
        /// Запрещает текущему пользователю запись в папку через список доступа
        /// и снимает запрет обратно. Настоящий отказ прав, а не его имитация.
        /// </summary>
        private sealed class DenyWriteScope : IDisposable {
            private readonly DirectoryInfo info;
            private readonly FileSystemAccessRule rule;

            internal DenyWriteScope(string folder) {
                this.info = new DirectoryInfo(folder);
                var me = WindowsIdentity.GetCurrent().User!;
                this.rule = new FileSystemAccessRule(me, FileSystemRights.CreateFiles | FileSystemRights.WriteData, AccessControlType.Deny);

                var acl = this.info.GetAccessControl();
                acl.AddAccessRule(this.rule);
                this.info.SetAccessControl(acl);
            }

            public void Dispose() {
                var acl = this.info.GetAccessControl();
                acl.RemoveAccessRule(this.rule);
                this.info.SetAccessControl(acl);
            }
        }
    }
}
