namespace ChillHub.Core.Home {
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Runtime.InteropServices;

    using Microsoft.Win32.SafeHandles;

    /// <summary>Удерживает путь на диске, чтобы его не подменили ссылкой во время обхода.</summary>
    internal sealed class DeletionPathGuard : IDisposable {
        private readonly List<SafeFileHandle> handles = new();

        internal static DeletionPathGuard HoldRoot(string root) {
            var guard = new DeletionPathGuard();
            try {
                var parents = new Stack<string>();
                for (var dir = new DirectoryInfo(root); dir != null; dir = dir.Parent) {
                    parents.Push(dir.FullName);
                }

                // Сначала закрепляем родителей: OPEN_REPARSE_POINT относится только
                // к последней части пути и сам по себе не защищает от подмены предка.
                foreach (var parent in parents) {
                    var handle = HoldEntry(parent);
                    if (handle == null) {
                        throw new DirectoryNotFoundException(parent);
                    }

                    guard.handles.Add(handle);
                    var attributes = File.GetAttributes(parent);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory) {
                        throw new IOException("Games directory has a linked ancestor.");
                    }
                }

                return guard;
            }
            catch {
                guard.Dispose();
                throw;
            }
        }

        /// <summary>Открывает саму запись, запрещая запись, переименование и удаление до Dispose.</summary>
        internal static SafeFileHandle? HoldEntry(string path) {
            var full = Path.GetFullPath(path);
            var extended = full;
            if (!full.StartsWith(@"\\?\", StringComparison.Ordinal)) {
                extended = full.StartsWith(@"\\", StringComparison.Ordinal)
                    ? @"\\?\UNC\" + full.Substring(2)
                    : @"\\?\" + full;
            }
            // GENERIC_READ, FILE_SHARE_READ, OPEN_EXISTING,
            // FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT.
            // Нулевой desiredAccess не подходит: метаданные не удерживают share-lock.
            var handle = NativeMethods.CreateFile(extended, 0x80000000, 1, 0, 3, 0x02200000, 0);
            if (!handle.IsInvalid) {
                return handle;
            }

            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is 2 or 3) {
                return null;
            }

            throw new IOException($"Cannot hold deletion path: {path}", new Win32Exception(error));
        }

        public void Dispose() {
            for (var i = this.handles.Count - 1; i >= 0; i--) {
                this.handles[i].Dispose();
            }

            this.handles.Clear();
        }

        private static class NativeMethods {
            [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern SafeFileHandle CreateFile(
                string path, uint desiredAccess, uint shareMode, nint securityAttributes,
                uint creationDisposition, uint flagsAndAttributes, nint templateFile);
        }
    }
}
