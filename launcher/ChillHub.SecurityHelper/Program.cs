namespace ChillHub.SecurityHelper {
    using System.IO;
    using System.Windows;
    using ChillHub.Security;

    internal static class Program {
        [STAThread]
        private static void Main(string[] args) {
            try {
                var application = new Application();
                application.Resources.MergedDictionaries.Add(new ResourceDictionary {
                    Source = new Uri("/ChillHub.SecurityHelper;component/Themes/Theme.Dark.xaml", UriKind.Relative),
                });
                if (args.Length == 3 && args[0] == "--elevated") {
                    application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    PrivilegedOperation.Serve(args[1], args[2]);
                    application.Shutdown();
                    return;
                }
                if (!Update.UpdateLock.TryAcquire(AppContext.BaseDirectory, 0, out var installLock) || installLock == null) {
                    MessageBox.Show("Лаунчер применяет обновление. Откройте настройку защиты после его завершения.", "Chill Hub"); return;
                }
                try {
                    RunWindow(application, args);
                }
                finally { Update.UpdateLock.Release(installLock); }
            }
            catch (Exception ex) {
                MessageBox.Show("Не удалось открыть настройку исключений.\n\n" + ex.Message, "Chill Hub", MessageBoxButton.OK, MessageBoxImage.Warning);
                Environment.ExitCode = 1;
            }
        }

        private static void RunWindow(Application application, string[] args) {
            using var instance = SecurityUpdateGuard.TryAcquire();
            if (instance == null) { MessageBox.Show("Настройка защиты уже открыта или лаунчер применяет обновление. Дождитесь завершения.", "Chill Hub"); return; }
            var paths = new List<string> { AppContext.BaseDirectory };
            bool remove = false;
            for (int i = 0; i < args.Length; i++) {
                if (args[i] == "--remove") { remove = true; continue; }
                if ((args[i] == "--games" || args[i] == "--updates" || args[i] == "--extra") && i + 1 < args.Length) { paths.Add(args[++i]); }
                else { throw new ArgumentException("Неизвестный параметр помощника."); }
            }
            if (paths.Count == 1) {
                // Standalone invocation: no guessed game library, only the known self-update location.
                paths.Add(Path.Combine(Path.GetTempPath(), "ChillHub", "SelfUpdate"));
            }
            application.Run(new ExclusionsWindow(paths, remove));
        }
    }
}
