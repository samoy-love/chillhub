namespace ChillHub.Security {
    public static class SecurityOperationLifetime {
        // A broken pipe or journal failure must not release the update lock while
        // the elevated child can still be changing settings or holding its files.
        public static async Task<T> RunAsync<T>(Func<Task<T>> operation, Action disconnect, Func<Task> waitForExit) {
            try { return await operation(); }
            finally {
                try { disconnect(); }
                finally { await waitForExit(); }
            }
        }
    }
}
