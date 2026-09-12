// -----------------------------------------------------------------------
// <copyright file="OneTimeApprovalKeys.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Actors.Tools;

internal static class OneTimeApprovalKeys
{
    private const string CandidateKeyPrefix = "\0candidate-v2:";

    public static IReadOnlyList<string> Create(ToolApprovalContext context)
        => Create(context.Patterns, context.Candidates ?? [], context.Cwd);

    public static IReadOnlyList<string> Create(
        IReadOnlyList<string> patterns,
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd)
    {
        var keys = new List<string>(patterns.Count + candidates.Count);
        keys.AddRange(patterns);
        keys.AddRange(candidates.Select(candidate => CreateCandidateKey(candidate, cwd)));
        return keys;
    }

    public static bool Matches(
        string? approvedToolName,
        IReadOnlySet<string> approvedKeys,
        string toolName,
        ToolApprovalContext approvalContext)
        => !string.IsNullOrEmpty(approvedToolName)
           && string.Equals(approvedToolName, toolName, StringComparison.Ordinal)
           && approvedKeys.SetEquals(Create(approvalContext));

    private static string CreateCandidateKey(ApprovalCandidate candidate, string? cwd)
    {
        var ignoreCase = candidate.Shell == ApprovalShell.PowerShell ||
                         (candidate.Shell is null && OperatingSystem.IsWindows());
        var tokens = candidate.VerbTokens ?? [candidate.Verb];
        var effectiveDirectory = candidate.Directory ?? cwd ?? string.Empty;
        if (ignoreCase)
            effectiveDirectory = effectiveDirectory.ToUpperInvariant();

        var payloadBuilder = new StringBuilder();
        payloadBuilder.Append((int?)candidate.Shell ?? -1).Append(':');
        payloadBuilder.Append(tokens.Count).Append(':');
        foreach (var token in tokens)
        {
            var normalizedToken = ignoreCase ? token.ToUpperInvariant() : token;
            payloadBuilder.Append(normalizedToken.Length).Append(':').Append(normalizedToken);
        }

        payloadBuilder.Append(effectiveDirectory.Length).Append(':').Append(effectiveDirectory);
        var payload = payloadBuilder.ToString();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return CandidateKeyPrefix + Convert.ToHexString(hash);
    }
}
