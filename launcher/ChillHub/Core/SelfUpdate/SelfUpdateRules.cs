// <copyright file="SelfUpdateRules.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.SelfUpdate {
    using System.Text;

    using ChillHub.Update;

    /// <summary>
    /// Общие для всего самообновления мелочи, которые обязаны быть ОДНИМИ И ТЕМИ ЖЕ
    /// на всех шагах: кодировка служебных списков и набор preserve-правил.
    /// </summary>
    internal static class SelfUpdateRules {
        /// <summary>UTF-8 без BOM: BOM ломает сверку размеров/хешей служебных списков.</summary>
        internal static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>Единый список preserve-правил, общий с апдейтером.</summary>
        internal static readonly PreserveMatcher Preserve = new PreserveMatcher();
    }
}
