namespace ChillHub.Security {
    public static class ExclusionVerification {
        // Defender saves preferences before its scanning engine receives them.
        // Retry a definite "not excluded" briefly; unavailable verification stays unknown.
        public static bool? WaitForApplied(Func<bool?> check, Action<TimeSpan> delay) {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            for (int attempt = 0; ; attempt++) {
                var result = check();
                if (result != false || attempt == 10 || elapsed.Elapsed >= TimeSpan.FromSeconds(5)) { return result; }
                delay(TimeSpan.FromMilliseconds(500));
            }
        }
    }
}
