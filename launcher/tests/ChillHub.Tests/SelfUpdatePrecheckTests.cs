// <copyright file="SelfUpdatePrecheckTests.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Tests {
    using System;

    using ChillHub.Core.SelfUpdate;

    using Xunit;

    /// <summary>
    /// Решение «показывать ли окно самообновления вообще» (см. <see cref="SelfUpdatePrecheck.NeedsWindow"/>).
    /// <para>
    /// Окно проверки обновлений раньше показывалось на КАЖДОМ запуске, даже когда
    /// проверять было нечего: пользователь видел лишний экран и должен был кликнуть
    /// «Продолжить», чтобы просто попасть в лаунчер актуальной версии. NeedsWindow —
    /// единственное место, решающее, пропустить ли этот экран.
    /// </para>
    /// </summary>
    public class SelfUpdatePrecheckTests {
        /// <summary>
        /// Актуальная версия и никакого исхода прошлого обновления — окно пропускаем
        /// и сразу показываем лаунчер.
        /// </summary>
        [Fact]
        public void АктуальнаяВерсияБезИсходаПрошлогоОбновленияПропускаетОкно() {
            var precheck = new SelfUpdatePrecheck {
                Decision = new SelfUpdateDecision { State = SelfUpdateState.UpToDate },
                PreviousOutcomeText = null,
            };

            Assert.False(precheck.NeedsWindow);
        }

        /// <summary>
        /// Проверка не состоялась (нет интернета, сервер молчит) — окно тоже пропускаем.
        /// Обновиться в этот момент всё равно нельзя, а модальное окно с ошибкой на
        /// пороге лаунчера — единственное, что игрок видел вместо запуска: нажать в нём
        /// можно было только «Продолжить». Про отсутствие связи скажет главный экран,
        /// там же есть и «Повторить».
        /// </summary>
        [Fact]
        public void НесостоявшаясяПроверкаНеЗапираетИгрокаВОкне() {
            var precheck = new SelfUpdatePrecheck {
                Decision = new SelfUpdateDecision { State = SelfUpdateState.CheckFailed },
                PreviousOutcomeText = null,
            };

            Assert.False(precheck.NeedsWindow);
        }

        /// <summary>
        /// Но если прошлое обновление провалилось, окно нужно даже без связи: рассказать
        /// об этом больше будет негде.
        /// </summary>
        [Fact]
        public void НесостоявшаясяПроверкаСИсходомПрошлогоОбновленияПоказываетОкно() {
            var precheck = new SelfUpdatePrecheck {
                Decision = new SelfUpdateDecision { State = SelfUpdateState.CheckFailed },
                PreviousOutcomeText = "Предыдущее обновление не было применено: диск переполнен",
            };

            Assert.True(precheck.NeedsWindow);
        }

        /// <summary>
        /// Доступное обновление — окно обязано появиться, иначе обновление применить
        /// нечем. Состояние передаётся именем: у Theory нет доступа к internal-типу
        /// <see cref="SelfUpdateState"/> напрямую — параметр метода обязан быть не менее
        /// доступным, чем сам метод (публичный по требованию xUnit).
        /// </summary>
        [Theory]
        [InlineData(nameof(SelfUpdateState.UpdateAvailable))]
        [InlineData(nameof(SelfUpdateState.LoopBlocked))]
        [InlineData(nameof(SelfUpdateState.VersionUnknown))]
        [InlineData(nameof(SelfUpdateState.ManifestRejected))]
        [InlineData(nameof(SelfUpdateState.InvalidRemoteVersion))]
        public void СостоянияСРазговоромКПользователюПоказываютОкно(string stateName) {
            var state = (SelfUpdateState)Enum.Parse(typeof(SelfUpdateState), stateName);
            var precheck = new SelfUpdatePrecheck {
                Decision = new SelfUpdateDecision { State = state },
                PreviousOutcomeText = null,
            };

            Assert.True(precheck.NeedsWindow);
        }

        /// <summary>
        /// Версия актуальна, но прошлое обновление провалилось и об этом ещё не
        /// рассказали — молча пропустить окно означало бы навсегда потерять эту
        /// диагностику: у пользователя больше не будет повода её увидеть.
        /// </summary>
        [Fact]
        public void АктуальнаяВерсияСНерассказаннымИсходомПрошлогоОбновленияПоказываетОкно() {
            var precheck = new SelfUpdatePrecheck {
                Decision = new SelfUpdateDecision { State = SelfUpdateState.UpToDate },
                PreviousOutcomeText = "Предыдущее обновление не было применено: диск переполнен",
            };

            Assert.True(precheck.NeedsWindow);
        }
    }
}
