// <copyright file="SelfUpdateAotIsolationTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Text;

    using Xunit;

    /// <summary>
    /// Апдейтер обязан запускаться в ПОЛНОЙ изоляции — ровно так, как его копирует
    /// <c>PrepareUpdaterPayload</c> при самообновлении: только файлы, совпавшие с
    /// глобом <c>"ChillHub.Updater*"</c>, без self-contained-рантайма ChillHub
    /// (hostfxr.dll и т.п.), с которым апдейтер делит общую папку установки.
    /// <para>
    /// Баг ровно в этом месте оставлял пользователей с непрерывно предлагающим
    /// обновиться лаунчером. Причина — в разнице между <c>build</c> и
    /// <c>publish</c>: апдейтер подключён к ChillHub как ProjectReference и
    /// наследует его self-contained/RID-свойства ТОЛЬКО при обычной сборке
    /// (transitive build), которая и производит копию апдейтера в общей папке
    /// установки. Из-за унаследованных свойств runtimeconfig.json апдейтера
    /// получается в self-contained-формате ("includedFrameworks"), но собственного
    /// self-contained-рантайма у него при этом нет — он есть только в общей папке,
    /// потому что настоящий publish (который и раскладывает рантайм рядом с exe)
    /// проходит только для ChillHub, а апдейтер как референс — просто строится.
    /// В общей папке апдейтер выглядит рабочим (рантайм лежит рядом, общий с
    /// ChillHub), а в изоляции падает мгновенно — до единой строки в собственном
    /// журнале (само падение уходит не в apply-update.log, а в Windows Event Log:
    /// 'hostpolicy.dll' ... not found).
    /// </para>
    /// <para>
    /// Тест воспроизводит СТАРОЕ поведение по-настоящему, а не по описанию:
    /// собирает апдейтер тем же способом, каким его раньше строил ChillHub
    /// (<c>dotnet build</c>, а не <c>publish</c>, с self-contained/RID на
    /// командной строке — ровно то, что раньше давал унаследованный
    /// ProjectReference), копирует в изоляцию только то, что реально копирует
    /// PrepareUpdaterPayload, и убеждается, что копия падает ровно так же, как на
    /// проде. Требует одного управляемого <c>dotnet build</c> — без публикации и
    /// без тулчейна C++, поэтому идёт в общем тестовом прогоне, а не только в
    /// сборке инсталлятора.
    /// </para>
    /// <para>
    /// Проверку НОВОГО поведения (что после фикса — Native AOT — изолированный
    /// запуск уже работает) намеренно не дублирует здесь: она уже идёт настоящим
    /// прогоном на каждом PR в <c>Publish-UpdaterAot</c>
    /// (scripts/build-installer.ps1, шаг "Installer (NSIS)" в CI) — тем же
    /// ключом, той же изоляцией, тем же ожиданием кода 3. Повторять её ещё и
    /// здесь значило бы дважды на каждый PR платить временем на publish и
    /// нативное связывание за проверку одного и того же.
    /// </para>
    /// </summary>
    public class SelfUpdateAotIsolationTests {
        [Fact]
        public void СтараяСборкаПадаетВИзоляции() {
            var repoRoot = FindRepoRoot();
            var csproj = Path.Combine(repoRoot, "updater", "ChillHub.Updater.csproj");
            Assert.True(File.Exists(csproj), $"не нашли {csproj} — проверь FindRepoRoot");

            // -p:SelfContained=true -p:RuntimeIdentifier=win-x64 на КОМАНДНОЙ СТРОКЕ —
            // это ровно то, что раньше апдейтер получал от ChillHub как глобальные
            // свойства MSBuild при сборке ProjectReference. Мы используем `build`,
            // а не `publish`, потому что именно так апдейтер строился раньше: ChillHub
            // публиковался, а он как референс — просто собирался и копировался.
            using var oldBuildOut = new TempDir();
            var (oldBuildExit, oldBuildOutput) = Run(
                "dotnet",
                $"build \"{csproj}\" -c Release -p:SelfContained=true -p:RuntimeIdentifier=win-x64 -o \"{oldBuildOut.Root}\"",
                repoRoot,
                TimeSpan.FromMinutes(3));

            Assert.True(oldBuildExit == 0, $"сборка старой версии апдейтера упала неожиданно:\n{oldBuildOutput}");

            using var oldIsolated = new TempDir();
            CopyUpdaterArtifactGlob(oldBuildOut.Root, oldIsolated.Root);

            var oldExe = Path.Combine(oldIsolated.Root, "ChillHub.Updater.exe");
            Assert.True(File.Exists(oldExe), $"{oldExe} не появился после копирования по глобу PrepareUpdaterPayload");

            var (oldExitCode, oldOutput) = Run(oldExe, string.Empty, oldIsolated.Root, TimeSpan.FromSeconds(15));

            // Если это когда-нибудь станет 3 — воспроизведение старого бага сломалось
            // (например, сменился способ, которым MSBuild прокидывает self-contained/RID
            // в ProjectReference), и тест придётся пересматривать: сама по себе
            // сходимость с "3" здесь ничего не доказывает, раз стадия ничего не
            // воспроизводит.
            Assert.True(
                oldExitCode != 3,
                "воспроизведение старого бага не удалось: изолированная копия старой сборки апдейтера " +
                $"запустилась и корректно отказала (код 3), как будто рантайм ей не требовался. Вывод: {oldOutput}");
        }

        /// <summary>
        /// Копирует только то, что реально копирует PrepareUpdaterPayload —
        /// файлы, совпавшие с глобом "ChillHub.Updater*". Раздельная папка
        /// сборки (а не общая с ChillHub) без этого фильтра сама по себе ничего
        /// не воспроизводит: в ней и так лежит только апдейтер.
        /// </summary>
        private static void CopyUpdaterArtifactGlob(string sourceDir, string destDir) {
            foreach (var f in Directory.EnumerateFiles(sourceDir, "ChillHub.Updater*", SearchOption.TopDirectoryOnly)) {
                File.Copy(f, Path.Combine(destDir, Path.GetFileName(f)));
            }
        }

        /// <summary>
        /// Поднимается от каталога сборки тестов до корня репозитория по метке
        /// (CLAUDE.md + каталог updater/), а не по фиксированной глубине — тесты
        /// однажды уже переживали переименование родительской папки репозитория.
        /// </summary>
        private static string FindRepoRoot() {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null) {
                if (File.Exists(Path.Combine(current.FullName, "CLAUDE.md")) &&
                    Directory.Exists(Path.Combine(current.FullName, "updater"))) {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException($"не нашли корень репозитория, поднимаясь от {AppContext.BaseDirectory}");
        }

        private static (int ExitCode, string Output) Run(string fileName, string arguments, string workingDirectory, TimeSpan timeout) {
            var psi = new ProcessStartInfo {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"{fileName}: процесс не создан");
            var output = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) { output.AppendLine(e.Data); } };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) { output.AppendLine(e.Data); } };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (!proc.WaitForExit((int)timeout.TotalMilliseconds)) {
                try {
                    proc.Kill(entireProcessTree: true);
                }
                catch {
                }

                throw new TimeoutException($"{fileName} не завершился за {timeout}");
            }

            return (proc.ExitCode, output.ToString());
        }
    }
}
