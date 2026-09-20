// <copyright file="FeedbackClose.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    /// <summary>
    /// Закрытие формы обратной связи: спрашивать ли и какими словами.
    /// <para>
    /// Пустую форму закрывать молча — правильно: вопрос там не о чем, а лишнее окно на
    /// каждый промах по крестику раздражает. А набранный текст пропадал без единого
    /// слова, и восстановить его было неоткуда.
    /// </para>
    /// </summary>
    internal static class FeedbackClose {
        /// <summary>Заголовок вопроса.</summary>
        internal const string Title = "Закрыть форму?";

        /// <summary>Что будет, если закрыть.</summary>
        internal const string Body = "Введённый текст будет потерян.";

        /// <summary>Спрашивать ли перед закрытием.</summary>
        /// <param name="name">Что набрано в поле имени.</param>
        /// <param name="contact">Что набрано в поле контакта.</param>
        /// <param name="comment">Что набрано в поле сообщения.</param>
        /// <returns>true — есть что терять, спрашиваем.</returns>
        internal static bool NeedsConfirm(string? name, string? contact, string? comment)
            => !string.IsNullOrWhiteSpace(name)
               || !string.IsNullOrWhiteSpace(contact)
               || !string.IsNullOrWhiteSpace(comment);
    }
}
