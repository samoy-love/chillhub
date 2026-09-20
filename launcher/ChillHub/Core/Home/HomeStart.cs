// <copyright file="HomeStart.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.Home {
    using System;

    using ChillHub.Core;

    /// <summary>
    /// Что приехало с сервера при открытии главного экрана.
    /// </summary>
    /// <param name="Games">Список игр или null, если сервер не ответил.</param>
    /// <param name="News">Лента лаунчера или null: без неё лаунчер работает целиком.</param>
    /// <param name="GamesError">Причина отказа по играм — из неё складывается «сервер недоступен».</param>
    internal sealed record HomeStart(GamesResponse? Games, NewsIndex? News, Exception? GamesError);
}
