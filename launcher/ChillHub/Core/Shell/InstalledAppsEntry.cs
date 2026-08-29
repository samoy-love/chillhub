// <copyright file="InstalledAppsEntry.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Shell {
    using System;
    using System.Globalization;
    using System.IO;
    using System.Threading.Tasks;

    using ChillHub.Core.Game;
    using ChillHub.Core.Logging;
    using ChillHub.Core.SelfUpdate;

    using Microsoft.Win32;

    /// <summary>
    /// Запись лаунчера в «Приложения и возможности» / «Установка и удаление программ».
    /// <para>
    /// Запись создаёт установщик (scripts/installer.nsi), но одного этого мало по трём
    /// причинам, и каждая из них уже наблюдалась.
    /// </para>
    /// <para>
    /// 1. ЗАПИСЬ ПРОПАДАЕТ. Ключ один на пользователя и на все копии лаунчера
    /// (HKCU\...\Uninstall\ChillHub). Любое тихое удаление — прогон
    /// scripts/ci/smoke-installer.ps1, ручная проверка установщика, откат неудачной
    /// установки — стирает его целиком, включая запись НАСТОЯЩЕЙ установки. Лаунчер
    /// при этом продолжает работать, а в списке программ его больше нет: удалить его
    /// штатным способом стало нечем.
    /// </para>
    /// <para>
    /// 2. ВЕРСИЯ ПРОТУХАЕТ. DisplayVersion пишет установщик, а дальше лаунчер обновляет
    /// себя сам (Core/SelfUpdate) — установщик при этом не запускается. В списке программ
    /// навсегда остаётся версия, с которой человек ставил лаунчер год назад.
    /// </para>
    /// <para>
    /// 3. РАЗМЕР НЕ ТОТ. EstimatedSize Windows не пересчитывает никогда: что записали при
    /// установке, то и показывается. Установщик считает свежераспакованный каталог, где
    /// игр ещё нет, — то есть показывает пару сотен мегабайт там, где лаунчер вместе с
    /// играми занимает десятки гигабайт. А место человек ищет именно в этом списке.
    /// </para>
    /// <para>
    /// Поэтому запись обновляется при каждом запуске. Ключ единственный, значения те же —
    /// повторная запись ничего не размножает.
    /// </para>
    /// </summary>
    internal static class InstalledAppsEntry {
        /// <summary>Ключ записи в списке установленных программ (тот же, что пишет установщик).</summary>
        internal const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ChillHub";

        /// <summary>Имя деинсталлятора рядом с лаунчером; оно же — признак настоящей установки.</summary>
        internal const string UninstallerName = "Uninstall.exe";

        /// <summary>Имя в списке программ. Совпадает с APP_TITLE установщика.</summary>
        private const string ProgramName = "Chill Hub";

        /// <summary>Издатель в списке программ. Совпадает с COMPANY_NAME установщика.</summary>
        private const string PublisherName = "Chill Hub";

        /// <summary>Ссылка «сайт программы» и «поддержка». Совпадает с APP_URL установщика.</summary>
        private const string HomeUrl = "https://launcher.samoy.love";

        /// <summary>
        /// Потолок EstimatedSize. Значение реестра — DWORD, а .NET отдаёт его как int:
        /// каталог больше 2 ТиБ переполнил бы разряд и показал бы отрицательный размер.
        /// </summary>
        private const long MaxSizeKib = int.MaxValue;

        /// <summary>
        /// Обход каталога. Отдельным швом: настоящий обход зависит от того, что лежит
        /// на диске у разработчика, и тест про подсчёт размера иначе не написать.
        /// </summary>
        internal static Func<string, long> DirectorySize { get; set; } = GameDiskInfo.GetDirectorySize;

        /// <summary>
        /// Ключ, в который пишется запись. Подставляется ТОЛЬКО тестом — и по той же
        /// причине, по которой эта запись вообще понадобилась: ключ один на всю машину,
        /// и тест, пишущий в настоящий, стёр бы разработчику строку его собственной
        /// установленной копии.
        /// </summary>
        internal static string RegistryKeyPath { get; set; } = KeyPath;

        /// <summary>Возвращает подставленные тестом швы к настоящим.</summary>
        internal static void ResetForTests() {
            DirectorySize = GameDiskInfo.GetDirectorySize;
            RegistryKeyPath = KeyPath;
        }

        /// <summary>
        /// Обновляет запись в фоне и немедленно возвращает управление.
        /// <para>
        /// Обход папки с играми — это чтение десятков тысяч файлов на медленном диске.
        /// На пути запуска ему делать нечего: список программ подождёт, а окно лаунчера — нет.
        /// </para>
        /// </summary>
        internal static void RefreshInBackground()
            => Task.Run(() => {
                try {
                    Refresh();
                }
                catch (Exception ex) {
                    // Список программ — удобство, а не работа лаунчера. Падать здесь не за что.
                    Logger.Warn("Не удалось обновить запись в списке программ: " + ex.Message);
                }
            });

        /// <summary>
        /// Обновляет запись для работающей копии лаунчера.
        /// </summary>
        /// <returns>true, если запись обновлена.</returns>
        internal static bool Refresh() {
            var installDir = SelfUpdatePaths.Default.TargetDir;
            return Refresh(installDir, ConfigService.Current.GamesPath, SelfUpdateVersions.ReadLocalVersion(installDir));
        }

        /// <summary>
        /// Обновляет запись по заданным путям.
        /// <para>
        /// ПИШЕМ ТОЛЬКО ТАМ, ГДЕ РЯДОМ ЛЕЖИТ ДЕИНСТАЛЛЯТОР. Без этой проверки запись
        /// появлялась бы и от сборки, запущенной из bin\Debug, и от распакованного
        /// куда-нибудь архива: в списке программ возникала бы строка «Chill Hub» с
        /// кнопкой удаления, которая ничего не удаляет. Наличие Uninstall.exe — ровно
        /// признак того, что эту копию поставил установщик и удалять её есть чем.
        /// </para>
        /// </summary>
        /// <param name="installDir">Каталог установки лаунчера.</param>
        /// <param name="gamesDir">Каталог с играми из настроек.</param>
        /// <param name="version">Установленная версия лаунчера.</param>
        /// <returns>true, если запись обновлена; false, если это не установленная копия.</returns>
        internal static bool Refresh(string installDir, string gamesDir, string version) {
            var uninstaller = UninstallerPath(installDir);
            if (uninstaller.Length == 0 || !File.Exists(uninstaller)) {
                return false;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath, writable: true);
            if (key == null) {
                return false;
            }

            key.SetValue("DisplayName", ProgramName, RegistryValueKind.String);
            key.SetValue("UninstallString", "\"" + uninstaller + "\"", RegistryValueKind.String);
            key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
            key.SetValue("DisplayIcon", Path.Combine(installDir, "ChillHub.exe"), RegistryValueKind.String);
            key.SetValue("Publisher", PublisherName, RegistryValueKind.String);
            key.SetValue("URLInfoAbout", HomeUrl, RegistryValueKind.String);
            key.SetValue("HelpLink", HomeUrl, RegistryValueKind.String);

            // У установщика нет ни режима изменения, ни режима восстановления: без этих
            // флагов Windows показывает кнопки, которые просто запускают установку заново.
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            if (!string.IsNullOrWhiteSpace(version)) {
                key.SetValue("DisplayVersion", version, RegistryValueKind.String);
            }

            // Дату установки не переписываем: она про установку, а не про сегодняшний
            // запуск. Заполняем только там, где её не проставил установщик.
            if (key.GetValue("InstallDate") == null) {
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), RegistryValueKind.String);
            }

            key.SetValue("EstimatedSize", (int)TotalSizeKib(installDir, gamesDir), RegistryValueKind.DWord);
            return true;
        }

        /// <summary>Путь к деинсталлятору рядом с лаунчером; пустая строка, если каталог не задан.</summary>
        /// <param name="installDir">Каталог установки лаунчера.</param>
        /// <returns>Полный путь к Uninstall.exe либо пустая строка.</returns>
        internal static string UninstallerPath(string installDir)
            => string.IsNullOrWhiteSpace(installDir) ? string.Empty : Path.Combine(installDir, UninstallerName);

        /// <summary>
        /// Размер, который увидит человек в списке программ: сам лаунчер плюс папка с играми.
        /// <para>
        /// Игры и есть то, ради чего в этот список заходят: лаунчер весит пару сотен
        /// мегабайт и среди прочих программ не выделяется ничем, а игры — десятки
        /// гигабайт. Показывать только каталог установки — значит прятать от человека
        /// ровно тот объём, который он ищет.
        /// </para>
        /// <para>
        /// Папка с играми ЛЕЖИТ ОТДЕЛЬНО и обычно на другом диске, поэтому прибавить её
        /// вслепую нельзя: указанная внутри каталога установки, она посчиталась бы дважды.
        /// </para>
        /// </summary>
        /// <param name="installDir">Каталог установки лаунчера.</param>
        /// <param name="gamesDir">Каталог с играми из настроек.</param>
        /// <returns>Размер в КиБ, обрезанный по разрядности DWORD.</returns>
        internal static long TotalSizeKib(string installDir, string gamesDir) {
            var bytes = DirectorySize(installDir ?? string.Empty);
            if (!IsInside(installDir, gamesDir)) {
                bytes += DirectorySize(gamesDir ?? string.Empty);
            }

            return ToKib(bytes);
        }

        /// <summary>
        /// Переводит байты в КиБ с округлением ВВЕРХ и обрезает по потолку DWORD.
        /// <para>
        /// Вверх, а не вниз: непустой каталог размером в сотню байт должен показаться
        /// как 1 КиБ, а не как «размер не указан».
        /// </para>
        /// </summary>
        /// <param name="bytes">Размер в байтах.</param>
        /// <returns>Размер в КиБ.</returns>
        internal static long ToKib(long bytes) {
            if (bytes <= 0) {
                return 0;
            }

            // Делим ДО прибавления остатка: «(bytes + 1023) / 1024» на размерах,
            // близких к пределу long, переполняет разряд и даёт отрицательный размер.
            var kib = (bytes / 1024) + (bytes % 1024 == 0 ? 0 : 1);
            return kib > MaxSizeKib ? MaxSizeKib : kib;
        }

        /// <summary>
        /// Лежит ли <paramref name="inner"/> внутри <paramref name="outer"/> (или совпадает с ним).
        /// <para>
        /// Сравнение по разделителю, а не по префиксу строки: иначе D:\Games ложно
        /// оказался бы внутри D:\GamesData.
        /// </para>
        /// </summary>
        /// <param name="outer">Внешний каталог.</param>
        /// <param name="inner">Проверяемый каталог.</param>
        /// <returns>true, если пути совпадают либо inner вложен в outer.</returns>
        internal static bool IsInside(string? outer, string? inner) {
            var a = Normalize(outer);
            var b = Normalize(inner);
            if (a.Length == 0 || b.Length == 0) {
                return false;
            }

            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
                || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Приводит путь к виду, пригодному для сравнения: без завершающих разделителей.</summary>
        /// <param name="path">Исходный путь.</param>
        /// <returns>Нормализованный путь либо пустая строка.</returns>
        private static string Normalize(string? path) {
            if (string.IsNullOrWhiteSpace(path)) {
                return string.Empty;
            }

            try {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) {
                // Путь из конфига правится руками и может быть невалидным. Считаем такой
                // каталог отдельным: хуже посчитать дважды, чем уронить обновление записи.
                Logger.Warn($"InstalledAppsEntry: путь '{path}' разобрать не удалось: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
