// <copyright file="PlanOptions.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Sync {
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Чем манифест владеет в корне, куда его синхронизируют.
    /// <para>
    /// Пока манифест был один на папку, вопрос не стоял: всё, чего нет в манифесте, —
    /// лишний файл. Модпак ставится в ТУ ЖЕ папку, что и игра (BepInEx работает только
    /// так: <c>winhttp.dll</c> и <c>doorstop_config.ini</c> обязаны лежать рядом с exe),
    /// и синхронизация модпака по старому правилу снесла бы игру целиком — все 10 ГБ,
    /// которых нет в манифесте модов.
    /// </para>
    /// </summary>
    public enum ManifestScope {
        /// <summary>
        /// Манифест владеет всем корнем: любой посторонний файл считается лишним.
        /// Так работает сборка игры — и так же работал единственный режим до модпаков.
        /// </summary>
        WholeRoot,

        /// <summary>
        /// Манифест владеет только своими файлами и делит корень с чужим манифестом.
        /// Удаляется исключительно то, что принадлежало ПРЕДЫДУЩЕЙ установке этого же
        /// манифеста и пропало из новой версии (см. <see cref="PlanOptions.PreviousOwnedPaths"/>).
        /// </summary>
        OwnFilesOnly,
    }

    /// <summary>
    /// Настройки построения плана различий.
    /// </summary>
    public sealed class PlanOptions {
        /// <summary>
        /// Настройки по умолчанию: с кешем хешей, без отчёта о прогрессе,
        /// манифест владеет всем корнем.
        /// </summary>
        public static readonly PlanOptions Default = new PlanOptions();

        /// <summary>
        /// Gets or sets a value indicating whether игнорировать кеш хешей и перечитать каждый файл с диска.
        /// Обычной синхронизации это не нужно, а вот проверке целостности — обязательно:
        /// кеш считает файл валидным по совпадению размера и времени модификации,
        /// поэтому повреждённый «на месте» файл он подтвердил бы как исправный.
        /// </summary>
        public bool ForceRehash { get; set; }

        /// <summary>
        /// Gets or sets отчёт о прогрессе сравнения (этап "Checking").
        /// Пересчёт хешей всей игры занимает минуты, без прогресса UI выглядит зависшим.
        /// </summary>
        public IProgress<SyncProgress>? Progress { get; set; }

        /// <summary>
        /// Gets or sets то, чем этот манифест владеет в корне: всем корнем или только
        /// собственными файлами. От этого зависит ЕДИНСТВЕННОЕ необратимое действие
        /// синхронизации — список на удаление.
        /// </summary>
        public ManifestScope Scope { get; set; } = ManifestScope.WholeRoot;

        /// <summary>
        /// Gets or sets пути ПРЕДЫДУЩЕЙ установки этого же манифеста — относительные,
        /// в форме манифеста.
        /// <para>
        /// Смысл имеет только при <see cref="ManifestScope.OwnFilesOnly"/>: список на
        /// удаление считается как «было в прошлой версии модпака и пропало в новой».
        /// Пусто или null (первая установка) — модпак не удаляет ничего, и это
        /// правильный ответ: всё остальное в корне принадлежит игре.
        /// </para>
        /// </summary>
        public IReadOnlyCollection<string>? PreviousOwnedPaths { get; set; }

        /// <summary>
        /// Gets or sets пути, которыми в этом же корне владеет ЧУЖОЙ манифест.
        /// <para>
        /// Для синхронизации игры это файлы установленного модпака. Их не качают, не
        /// удаляют и вообще не замечают: иначе первое же обновление игры вынесло бы
        /// весь BepInEx как «лишние файлы», а «Проверить файлы» предложило бы удалить
        /// пару тысяч файлов модов.
        /// </para>
        /// <para>
        /// Список приходит СНАРУЖИ, из установленного манифеста модпака, а не задаётся
        /// константой в <c>IsIgnoredRelFile</c>: у разных игр моды кладут в корень
        /// разные папки данных (<c>Mirage/</c>, <c>settings/</c>), и глобальное правило
        /// подействовало бы ещё и на играх, где модов нет вовсе.
        /// </para>
        /// </summary>
        public IReadOnlyCollection<string>? ForeignPaths { get; set; }

        /// <summary>
        /// Gets or sets пути, которые ставятся один раз и дальше не сверяются:
        /// отсутствующий файл скачивается, существующий не трогают ни при каком
        /// расхождении хеша.
        /// <para>
        /// Нужно ровно для тех файлов манифеста, которые ПРАВИТ САМ ЛАУНЧЕР.
        /// У модпака такой один — <c>doorstop_config.ini</c>: переключение
        /// «с модами / без модов» меняет в нём значение ключа, а файл при этом
        /// перечислен в манифесте. Без исключения «Проверить файлы» после каждой
        /// ванильной сессии сообщала бы о повреждённом файле, а очередное
        /// «Обновить» возвращало бы моды во включённое состояние молча, за
        /// спиной у игрока.
        /// </para>
        /// </summary>
        public IReadOnlyCollection<string>? PreservePaths { get; set; }

        /// <summary>
        /// Gets or sets папки, из которых можно брать готовые файлы вместо загрузки.
        /// <para>
        /// Модпак принадлежит папке: играть и в копию из Steam, и в сборку с сервера
        /// значит поставить его дважды. Побайтово это одни и те же файлы, и качать их
        /// повторно — плата ни за что.
        /// </para>
        /// </summary>
        public IReadOnlyList<DonorRoot>? Donors { get; set; }

        /// <summary>
        /// Настройки для синхронизации ИГРЫ в корне, где может стоять модпак.
        /// Читает установленный манифест модпака с диска, поэтому вызывать её стоит
        /// оттуда же, откуда строится план, — не с UI-потока.
        /// </summary>
        /// <param name="localRoot">Корень локальной папки игры.</param>
        /// <returns>Настройки плана.</returns>
        public static PlanOptions ForGame(string localRoot) => new PlanOptions {
            Scope = ManifestScope.WholeRoot,
            ForeignPaths = Home.GameLocalState.ReadInstalledModPackPaths(localRoot),
        };

        /// <summary>
        /// Настройки для синхронизации МОДПАКА в корень игры: удаляется только то,
        /// что было в предыдущей версии модпака и пропало в новой.
        /// </summary>
        /// <param name="localRoot">Корень локальной папки игры.</param>
        /// <returns>Настройки плана.</returns>
        /// <param name="donorRoots">
        /// Другие папки этой же игры, где модпак уже может стоять: оттуда файлы
        /// копируются вместо загрузки.
        /// </param>
        public static PlanOptions ForModPack(string localRoot, IEnumerable<string?>? donorRoots = null) => new PlanOptions {
            Scope = ManifestScope.OwnFilesOnly,
            PreviousOwnedPaths = Home.GameLocalState.ReadInstalledModPackPaths(localRoot),
            PreservePaths = ModPackPreservePaths,
            Donors = LocalDonors.FromModPacks(donorRoots, localRoot),
        };

        /// <summary>
        /// Файлы модпака, которые правит сам лаунчер и потому не сверяются после
        /// установки. Имя берётся из <see cref="Mods.DoorstopConfig.FileName"/>, а не
        /// пишется строкой: разъехавшись, эти два места дали бы вечно «повреждённый»
        /// файл, и заметить это можно было бы только по жалобе.
        /// </summary>
        private static readonly string[] ModPackPreservePaths = {
            Mods.DoorstopConfig.FileName,
        };
    }
}
