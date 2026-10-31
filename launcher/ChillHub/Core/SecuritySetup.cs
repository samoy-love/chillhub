namespace ChillHub.Core {
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Windows;
    using ChillHub.Security;

    internal static class SecuritySetup {
        internal const string UpdateBusyMessage = "Завершите настройку защиты и закройте её окно, затем повторите обновление. Загруженные файлы сохранены.";

        internal static async Task<IDisposable?> PrepareForUpdateAsync() {
            var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ChillHub.SecurityHelper.exe"));
            foreach (var process in Process.GetProcessesByName("ChillHub.SecurityHelper")) {
                using (process) {
                    try {
                        if (!string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) { continue; }
                        // The helper rejects WM_CLOSE while a confirmed change is in progress.
                        // Never kill it or interrupt an elevated operation.
                        if (process.CloseMainWindow()) {
                            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
                        }
                    }
                    catch (OperationCanceledException) { /* The helper is busy; the guard below keeps the downloaded update pending. */ }
                    catch (Exception ex) { Logging.Logger.Warn("SecuritySetup.BeforeUpdate: " + ex.Message); }
                }
            }
            try { return SecurityUpdateGuard.TryAcquire(); }
            catch (Exception ex) { Logging.Logger.Warn("SecuritySetup.UpdateLock: " + ex.Message); return null; }
        }

        internal static async Task OpenAsync(string? extra = null, bool offerOnce = false) {
            try {
                if (offerOnce && SecurityJournal.Read(SecurityJournal.DefaultPath).IntroductionShown) { return; }
                var exe = Path.Combine(AppContext.BaseDirectory, "ChillHub.SecurityHelper.exe");
                if (!File.Exists(exe)) { throw new FileNotFoundException("Помощник отсутствует. Переустановите Chill Hub или настройте исключения вручную."); }
                var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                if (offerOnce) { start.ArgumentList.Add("--offer"); }
                start.ArgumentList.Add("--games"); start.ArgumentList.Add(ConfigService.Current.GamesPath);
                start.ArgumentList.Add("--updates"); start.ArgumentList.Add(SelfUpdate.SelfUpdatePaths.Default.TempRoot);
                if (!string.IsNullOrWhiteSpace(extra)) { start.ArgumentList.Add("--extra"); start.ArgumentList.Add(extra); }
                using var process = Process.Start(start);
                if (process != null) { await process.WaitForExitAsync().ConfigureAwait(true); }
            }
            catch (Exception ex) {
                Logging.Logger.Warn("SecuritySetup: " + ex.Message);
                MessageBox.Show(ex is FileNotFoundException ? "Помощник отсутствует. Переустановите Chill Hub." : SecuritySetupText.OpenFailed, "Исключения антивируса", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        internal static async Task OfferSteamAsync(string directory) {
            try {
                if (ExclusionPolicy.IsWithin(directory, ConfigService.Current.GamesPath)) { return; }
                var path = ExclusionPolicy.Normalize(directory);
                bool offer = false;
                SecurityJournal.Update(SecurityJournal.DefaultPath, journal => {
                    if (!journal.OfferedSteamPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) {
                        journal.OfferedSteamPaths.Add(path);
                        offer = true;
                    }
                });
                if (!offer) { return; }
                if (MessageBox.Show("Для модов Steam можно отдельно настроить антивирус. Открыть настройку сейчас?", "Установка модов", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) {
                    await OpenAsync(path).ConfigureAwait(true);
                }
            }
            catch (Exception ex) { Logging.Logger.Warn("SecuritySetup.Steam: " + ex.Message); }
        }
    }
}
