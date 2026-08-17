// <copyright file="UiAnimations.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI {
    using System.ComponentModel;

    /// <summary>
    /// Общий выключатель бесконечных анимаций разметки: скелетонов, шиммеров, бегунков.
    /// <para>
    /// Каждая такая анимация — вечный клок в <c>TimeManager</c>, а он тикает на UI-потоке
    /// каждый кадр независимо от того, видно ли окно. Свёрнутый в трей лаунчер жёг на этом
    /// около 2% ядра, а свёрнутый в панель задач — ещё и заметную долю видеокарты: анимаций
    /// набиралось полтора десятка (скелетоны прячутся <c>Collapsed</c>, а не выгружаются,
    /// индикатор проверки игр вовсе стоял <c>IsIndeterminate</c> с разметки).
    /// </para>
    /// <para>
    /// Разметка привязывает начало и остановку анимации к паре условий: собственная
    /// <c>IsVisible</c> элемента (прячет свёрнутые секции и спрятанное окно) и это свойство
    /// (прячет свёрнутое в панель задач окно, у которого <c>IsVisible</c> остаётся истинной).
    /// Ставит его <c>MainWindow</c> — см. <c>SyncAnimationsWithWindowState</c>.
    /// </para>
    /// </summary>
    public sealed class UiAnimations : INotifyPropertyChanged {
        private static readonly UiAnimations Shared = new UiAnimations();

        private bool enabled = true;

        private UiAnimations() {
        }

        /// <inheritdoc/>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Gets единственный экземпляр — привязки в разметке ходят через него.</summary>
        public static UiAnimations Instance => Shared;

        /// <summary>
        /// Gets or sets a value indicating whether окно на экране, то есть бесконечным анимациям
        /// есть кому показываться. По умолчанию true: разметку читают и тесты, и окно
        /// самообновления, которым знать про состояние главного окна незачем.
        /// </summary>
        public bool Enabled {
            get => this.enabled;

            set {
                if (this.enabled == value) {
                    return;
                }

                this.enabled = value;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.Enabled)));
            }
        }
    }
}
