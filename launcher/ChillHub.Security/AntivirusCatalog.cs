namespace ChillHub.Security {
    using System.Management;
    using Microsoft.Win32;

    public sealed record AntivirusGuide(string Id, string Name, string Instructions, string Url, string[] Aliases) {
        public override string ToString() => this.Name;
    }

    public static class AntivirusCatalog {
        public static IReadOnlyList<AntivirusGuide> All { get; } = new[] {
            new AntivirusGuide("defender", "Microsoft Defender", "Безопасность Windows → Защита от вирусов и угроз → Управление настройками → Исключения → Добавить или удалить исключения. Добавьте перечисленные папки. Автоматическая настройка ниже относится только к Defender.", "https://support.microsoft.com/en-us/windows/security/threat-malware-protection/virus-and-threat-protection-in-the-windows-security-app", new[] { "defender", "security essentials" }),
            new AntivirusGuide("kaspersky", "Kaspersky", "Настройки безопасности → Угрозы и исключения → Настроить исключения → Добавить. Выберите папку и компоненты проверки файлов. Повторите для каждой папки. Названия пунктов зависят от версии.", "https://support.kaspersky.ru/help/Kaspersky/Win21.7/ru-RU/227390.htm", new[] { "kaspersky", "касперск" }),
            new AntivirusGuide("eset", "ESET", "Расширенные настройки (F5) → Сканирование → Исключения → Исключения из проверки производительности. Добавьте папки. Исключения обнаружений по имени или хешу — отдельный механизм; не импортируйте чужую конфигурацию. См. справку вашей версии.", "https://support.eset.com/en/kb2769-exclude-files-or-folders-from-scanning-in-eset-windows-home-products", new[] { "eset", "nod32" }),
            new AntivirusGuide("avast", "Avast", "Меню → Настройки → Общие → Исключения → Добавить исключение. Выберите папку; повторите для остальных путей. Инструкция для Avast Antivirus.", "https://support.avast.com/en-nz/article/antivirus-scan-exclusions", new[] { "avast" }),
            new AntivirusGuide("avg", "AVG", "Меню → Настройки → Общие → Исключения → Добавить исключение → Файл / папка. Добавьте перечисленные папки. Инструкция для AVG Antivirus.", "https://support.avg.com/SupportArticleView?l=en&urlname=avg-antivirus-scan-exclusions", new[] { "avg" }),
            new AntivirusGuide("bitdefender", "Bitdefender", "Защита → Антивирус → Открыть → Настройки → Управление исключениями → Добавить исключение. Укажите папку и включите исключение для антивируса. Повторите для каждого пути.", "https://www.bitdefender.com/consumer/support/answer/13427/", new[] { "bitdefender" }),
            new AntivirusGuide("norton", "Norton", "Безопасность → Расширенная безопасность → Компьютер → Антивирус → Исключения → Добавить. Выберите папку. В старых версиях исключения сканирования и защиты в реальном времени настраиваются отдельно.", "https://support.norton.com/sp/en/us/home/current/solutions/v20250131135259243", new[] { "norton", "symantec" }),
            new AntivirusGuide("malwarebytes", "Malwarebytes", "Откройте список разрешений (Allow list), добавьте файл или папку и подтвердите предупреждение. Повторите для остальных папок. Названия разделов зависят от версии приложения.", "https://help.malwarebytes.com/hc/en-us/articles/31589453538075-Add-or-remove-items-from-Malwarebytes-Allow-list", new[] { "malwarebytes" }),
            new AntivirusGuide("drweb", "Dr.Web", "Центр безопасности → Исключения → Файлы и папки. Добавьте перечисленные папки и выберите проверку файлов сканером и SpIDer Guard. Для других компонентов действуют отдельные настройки.", "https://support.drweb.ru/show_faq/?lng=ru&question=14310", new[] { "dr.web", "drweb", "doctor web" }),
            new AntivirusGuide("trendmicro", "Trend Micro", "Настройки → Списки исключений → Программы / папки → Добавить. Выберите папку и сохраните изменения. Повторите для каждого пути.", "https://helpcenter.trendmicro.com/en-us/article/tmka-14498", new[] { "trend micro", "trendmicro" }),
            new AntivirusGuide("mcafee", "McAfee", "Полное исключение папок этим способом не поддерживается. Моя защита → Сканирование в реальном времени → Исключённые файлы → Добавить файл. McAfee проверяет файл перед добавлением и может отказать; после изменения файла исключение снимается. Это не исключение всей библиотеки игр.", "https://www.mcafee.com/support/s/article/000001661?language=en_US", new[] { "mcafee" }),
            new AntivirusGuide("other", "Другой антивирус", "Откройте настройки вашего антивируса и найдите раздел исключений или разрешённых файлов. Скопируйте нужные пути из списка. Если настройка недоступна, обратитесь к администратору или поддержке антивируса. Chill Hub не может проверить ручную настройку.", "", Array.Empty<string>()),
        };

        public static AntivirusGuide Match(string name) => All.FirstOrDefault(g => g.Aliases.Any(a => name.Contains(a, StringComparison.OrdinalIgnoreCase))) ?? All[^1];

        public static IReadOnlyList<string> Discover() {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try {
                using var search = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName FROM AntivirusProduct");
                using var items = search.Get();
                foreach (ManagementObject item in items) {
                    using (item) { if (item["displayName"] is string name) { names.Add(name); } }
                }
            }
            catch (Exception) { /* Registry fallback and manual selection remain available. */ }
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser }) {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 }) {
                    try {
                        using var root = RegistryKey.OpenBaseKey(hive, view);
                        using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                        foreach (var keyName in uninstall?.GetSubKeyNames() ?? Array.Empty<string>()) {
                            using var entry = uninstall?.OpenSubKey(keyName);
                            if (entry?.GetValue("DisplayName") is string name && Match(name).Id != "other") { names.Add(name); }
                        }
                    }
                    catch (Exception) { /* Product may hide its registration. Never claim discovery is complete. */ }
                }
            }
            return names.OrderBy(n => n).ToArray();
        }
    }
}
