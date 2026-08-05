// <copyright file="AcrylicHelperTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;
    using System.Windows;
    using System.Windows.Interop;

    using ChillHub.Core.UI;

    using Microsoft.Win32;

    using Xunit;

    /// <summary>
    /// Тёмный заголовок окна и чтение системной темы.
    /// <para>
    /// Это чистая косметика через Win32, и цена ошибки здесь не в некрасивом окне,
    /// а в падении: <c>DwmSetWindowAttribute</c> вызывается из <c>SourceInitialized</c>,
    /// то есть в момент создания каждого окна лаунчера. Исключение оттуда — это лаунчер,
    /// который не открывается вовсе, причём только на той сборке Windows, где атрибута нет.
    /// Проверяем ровно это: ни один вход не должен бросать.
    /// </para>
    /// <para>
    /// Окна создаются, но не показываются: дескриптор заводится через
    /// <see cref="WindowInteropHelper.EnsureHandle"/>, а <c>Show</c> не вызывается никогда —
    /// мигающее окно посреди прогона недопустимо.
    /// </para>
    /// </summary>
    public class AcrylicHelperTests {
        /// <summary>
        /// Окно ещё без дескриптора — обычное дело, если тему пробуют применить до создания
        /// источника. Вызов обязан просто ничего не сделать: передавать нулевой HWND в Win32 нельзя.
        /// </summary>
        [Fact]
        public void ОкноБезДескриптораНеДоходитДоWin32() {
            UiThread.Run(() => {
                var window = new Window();
                try {
                    Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);

                    AcrylicHelper.ApplyTitleBarTheme(window, true);
                    AcrylicHelper.ApplyTitleBarTheme(window, false);
                }
                finally {
                    window.Close();
                }
            });
        }

        /// <summary>
        /// Настоящий вызов Win32 на живом дескрипторе — оба значения. Именно этот путь
        /// проходит каждое окно лаунчера при создании, и падение здесь означает,
        /// что приложение не запускается.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ТемаЗаголовкаПрименяетсяКНастоящемуОкнуБезПадения(bool dark) {
            UiThread.Run(() => {
                var window = new Window();
                try {
                    var hwnd = new WindowInteropHelper(window).EnsureHandle();
                    Assert.NotEqual(IntPtr.Zero, hwnd);

                    AcrylicHelper.ApplyTitleBarTheme(window, dark);
                }
                finally {
                    window.Close();
                }
            });
        }

        /// <summary>
        /// Окно уже закрыто, а тему всё ещё применяют — так бывает, когда лаунчер закрывают
        /// во время создания диалога. Дескриптора больше нет, и вызов обязан промолчать,
        /// а не утащить закрытие в исключение.
        /// </summary>
        [Fact]
        public void ЗакрытоеОкноНеРоняетПрименениеТемы() {
            UiThread.Run(() => {
                var window = new Window();
                _ = new WindowInteropHelper(window).EnsureHandle();
                window.Close();

                AcrylicHelper.ApplyTitleBarTheme(window, true);
            });
        }

        /// <summary>
        /// Системная тема читается из реестра. Проверяем против того же значения, прочитанного
        /// напрямую: перепутанные 0 и 1 дали бы светлый заголовок на тёмной системе — ровно
        /// ту рассинхронизацию, ради которой чтение и заведено.
        /// </summary>
        [Fact]
        public void СистемнаяТемаЧитаетсяИзРеестраБезИскажений() {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var raw = key?.GetValue("AppsUseLightTheme");

            // Ноль в реестре означает тёмную тему; отсутствие значения — светлую.
            var expected = raw is int i && i == 0;

            Assert.Equal(expected, AcrylicHelper.IsSystemAppsDark());
        }

        /// <summary>
        /// Чтение темы дёргается на каждое открытие окна и обязано быть стабильным:
        /// разный ответ на два подряд идущих вызова означал бы окна разного цвета
        /// в одном приложении.
        /// </summary>
        [Fact]
        public void ЧтениеСистемнойТемыПовторяемо() {
            var first = AcrylicHelper.IsSystemAppsDark();

            Assert.Equal(first, AcrylicHelper.IsSystemAppsDark());
            Assert.Equal(first, AcrylicHelper.IsSystemAppsDark());
        }
    }
}
