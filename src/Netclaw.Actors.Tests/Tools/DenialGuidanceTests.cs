// -----------------------------------------------------------------------
// <copyright file="DenialGuidanceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// A denial by a person tells the model to stop and ask. The model must not
/// reach the same goal by another route. Other denials keep their own text.
/// </summary>
public sealed class DenialGuidanceTests
{
    private const string Guidance =
        "The user declined this action. Do not reach the same goal by another tool or route. "
        + "Tell the user what you wanted to do, and ask how to continue.";

    public static TheoryData<string, bool> Results => new()
    {
        { ConsentRefusalText.For(RefusalKind.Denied, "shell_execute"), true },
        { ConsentRefusalText.For(RefusalKind.TimedOut, "shell_execute"), false },
        { ConsentRefusalText.For(RefusalKind.PromptUnavailable, "shell_execute"), false },
        { new ToolAccessDeniedException("shell_execute is denied by policy").ToAgentResult(), false },
    };

    [Theory]
    [MemberData(nameof(Results))]
    public void Only_a_person_denial_holds_the_stop_and_ask_guidance(string result, bool expectGuidance)
    {
        Assert.StartsWith("Tool access denied: ", result, StringComparison.Ordinal);
        Assert.Equal(expectGuidance, result.Contains(Guidance, StringComparison.Ordinal));
    }
}
