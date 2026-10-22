// -----------------------------------------------------------------------
// <copyright file="ChildConsentPause.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// The one rule that decides which input the child consent pause holds (durable-child-consent-wait, design D5).
/// The drain path and the recovery path use it.
/// </summary>
internal static class ChildConsentPause
{
    // STUB (PR 0 test author): this body makes the PR 0 tests compile and fail on an assertion. The implementer
    // replaces it and makes SessionInputFlow.IsHeldByPause and LlmSessionActor.WasHeldByPause call it.
    public static bool Holds(TurnContext authority, bool isChildNotice) => false;
}
