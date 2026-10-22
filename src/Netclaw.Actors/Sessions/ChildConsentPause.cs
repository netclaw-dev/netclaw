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
    // <child-consent-predicate>
    // Security boundary (child consent pause, design D5). While the pause applies, it holds automated input and child
    // result notices. It never holds a person input or an UntrustedExternal input. It never holds an approval notice,
    // because the notice tells the model the state of the pause. A webhook that names the notice kind is not a notice
    // (TurnContext.IsChildConsentNotice), so the pause holds it as automation. The caller parses the recorded
    // authority first, and an input with a damaged authority never comes here.
    public static bool Holds(TurnContext authority, bool isChildNotice)
        => isChildNotice
           || (SessionToolWorkflow.IsAutomation(authority.RequesterPrincipal) && !authority.IsChildConsentNotice);
    // </child-consent-predicate>
}
