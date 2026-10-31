namespace ChillHub.Security {
    /// <summary>The installer and launcher share one entry point and one set of folders.</summary>
    public sealed record SecuritySetupRequest(string[] Paths, bool Remove, bool OfferOnce) {
        public static SecuritySetupRequest Parse(string[] args, string installDirectory, string updatesDirectory) {
            var paths = new List<string> { installDirectory };
            bool remove = false, offer = false;
            for (int i = 0; i < args.Length; i++) {
                switch (args[i]) {
                    case "--remove": remove = true; break;
                    case "--offer": offer = true; break;
                    case "--updates" when i + 1 < args.Length: updatesDirectory = args[++i]; break;
                    case "--games" or "--extra" when i + 1 < args.Length: paths.Add(args[++i]); break;
                    default: throw new ArgumentException("Неизвестный параметр помощника.");
                }
            }
            if (remove && offer) { throw new ArgumentException("Удаление исключений нельзя предлагать при первом запуске."); }
            paths.Add(updatesDirectory);
            return new(paths.ToArray(), remove, offer);
        }
    }
}
