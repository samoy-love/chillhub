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
                // The parent reports channel failures. A modal dialog in the elevated
                // child would keep its process (and the parent's busy state) alive.
                if (args.Length == 0 || args[0] != "--elevated") {
                    MessageBox.Show("Не удалось открыть настройку исключений.\n\n" + ex.Message, "Chill Hub", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                Environment.ExitCode = 1;
            }
        }

        private static void RunWindow(Application application, string[] args) {
            using var instance = SecurityUpdateGuard.TryAcquire();
            if (instance == null) { MessageBox.Show("Настройка защиты уже открыта или лаунчер применяет обновление. Дождитесь завершения.", "Chill Hub"); return; }
            var request = SecuritySetupRequest.Parse(args, AppContext.BaseDirectory, Path.Combine(Path.GetTempPath(), "ChillHub", "SelfUpdate"));
            if (request.OfferOnce && SecurityJournal.Read(SecurityJournal.DefaultPath).IntroductionShown) { return; }
            application.Run(new ExclusionsWindow(request.Paths, request.Remove));
        }
    }
}
