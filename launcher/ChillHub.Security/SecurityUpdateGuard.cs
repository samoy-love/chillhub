namespace ChillHub.Security {
    public static class SecurityUpdateGuard {
        public const string MutexName = "Local\\ChillHub.SecurityUi";

        public static IDisposable? TryAcquire(string name = MutexName) {
            var mutex = new Mutex(true, name, out var created);
            if (!created) { mutex.Dispose(); return null; }
            return new Lease(mutex);
        }

        private sealed class Lease(Mutex mutex) : IDisposable {
            public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
        }
    }
}
