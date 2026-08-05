// <copyright file="SelfUpdatePaths.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.SelfUpdate {
    using System;
    using System.IO;

    /// <summary>
    /// Где живёт самообновление: каталог установки и корень временных сессий.
    /// <para>
    /// Раньше оба пути брались прямо из <see cref="AppDomain.CurrentDomain"/> и
    /// <see cref="Path.GetTempPath"/> в десятке мест. Это и делало процесс
    /// непроверяемым: любой тест писал бы в настоящую папку установки лаунчера.
    /// Пути собраны в одном месте и подставляются целиком.
    /// </para>
    /// </summary>
    internal sealed class SelfUpdatePaths {
        internal SelfUpdatePaths(string installDir, string tempRoot) {
            this.InstallDir = installDir ?? string.Empty;
            this.TempRoot = tempRoot ?? string.Empty;
        }

        /// <summary>Папка установки лаунчера (как её отдаёт AppDomain — с завершающим разделителем).</summary>
        internal string InstallDir { get; }

        /// <summary>Корень временных сессий обновления (%TEMP%\ChillHub\SelfUpdate).</summary>
        internal string TempRoot { get; }

        /// <summary>Настоящие пути работающего лаунчера.</summary>
        internal static SelfUpdatePaths Default => new SelfUpdatePaths(
            AppDomain.CurrentDomain.BaseDirectory,
            Path.Combine(Path.GetTempPath(), "ChillHub", "SelfUpdate"));

        /// <summary>Каталог установки без завершающего разделителя — в таком виде он уходит апдейтеру.</summary>
        internal string TargetDir => this.InstallDir.TrimEnd(Path.DirectorySeparatorChar);

        /// <summary>Маркер версии рядом с исполняемым файлом.</summary>
        internal string VersionMarker => Path.Combine(this.InstallDir, "launcher.version");

        /// <summary>Каталог сессии обновления на конкретную версию.</summary>
        internal string SessionRoot(string version) => Path.Combine(this.TempRoot, version);

        /// <summary>Подкаталог сессии с полезной нагрузкой (то, что копирует апдейтер).</summary>
        internal string PayloadDir(string version) => Path.Combine(this.SessionRoot(version), "payload");

        /// <summary>
        /// A6. Служебный подкаталог сессии: списки файлов, журнал, копия апдейтера.
        /// Отделён от полезной нагрузки, иначе «остаточное зеркалирование» в апдейтере
        /// копировало filelist.txt / apply-update.log / updater\ прямо в папку установки.
        /// </summary>
        internal string WorkDir(string version) => Path.Combine(this.SessionRoot(version), "work");
    }
}
