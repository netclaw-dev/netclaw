// -----------------------------------------------------------------------
// <copyright file="ShellRedirectPolicyFacts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

internal static class ShellRedirectPolicyFacts
{
    internal static bool HasFileWritingRedirect(CommandOccurrence occurrence) =>
        occurrence.Redirects.Any(static redirect => redirect is FileRedirectAnalysis
        {
            Mode: FileRedirectMode.Output
                or FileRedirectMode.Append
                or FileRedirectMode.CombinedOutput
                or FileRedirectMode.CombinedOutputAppend
        });
}
