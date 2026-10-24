// -----------------------------------------------------------------------
// <copyright file="OneTimeApprovalKeys.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools.Authorization.Consent;

namespace Netclaw.Actors.Tools;

internal static class OneTimeApprovalKeys
{
    private const int NoAssignmentDigestKeyKind = 1;
    private const int ExactAssignmentDigestKeyKind = 2;

    private const string CandidateKeyPrefix = "\0candidate-v3:";
    private const string ShellSourceKeyPrefix = "\0shell-source-v1:";

    public static IReadOnlyList<string> Create(ToolApprovalContext context)
        => Create(
            context.Patterns,
            context.Candidates ?? [],
            context.Cwd,
            IsShell(context.ToolName) ? context.ShellSourceKey : null);

    public static IReadOnlyList<string> Create(
        IReadOnlyList<string> patterns,
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd)
        => Create(patterns, candidates, cwd, shellSourceKey: null);

    public static IReadOnlyList<string> Create(
        IReadOnlyList<string> patterns,
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        string? shellSourceKey)
    {
        var keys = new List<string>(patterns.Count + candidates.Count + (shellSourceKey is null ? 0 : 1));
        keys.AddRange(patterns);
        keys.AddRange(candidates.Select(candidate => CreateCandidateKey(candidate, cwd)));
        if (shellSourceKey is not null)
            keys.Add(shellSourceKey);
        return keys;
    }

    public static string? CreateShellSourceKey(string? command)
    {
        if (command is null)
            return null;

        // Preserve each UTF-16 code unit without normalization or encoder fallback.
        var bytes = new byte[checked(command.Length * sizeof(char))];
        for (var index = 0; index < command.Length; index++)
        {
            var value = command[index];
            bytes[index * 2] = (byte)(value >> 8);
            bytes[index * 2 + 1] = (byte)value;
        }

        var hash = SHA256.HashData(bytes);
        return ShellSourceKeyPrefix + Convert.ToHexString(hash);
    }

    /// <summary>Creates the "Once" answer for the prompt of <paramref name="toolName"/>.</summary>
    public static OneTimeConsent CreateConsent(string toolName, ToolApprovalContext context)
        => new(toolName, Create(context));

    public static bool Matches(
        OneTimeConsent? consent,
        string toolName,
        ToolApprovalContext approvalContext)
    {
        var isShellCall = IsShell(toolName) || IsShell(approvalContext.ToolName);
        return consent is not null
               && (!isShellCall || approvalContext.ShellSourceKey is not null)
               && consent.Covers(toolName, Create(approvalContext));
    }

    private static bool IsShell(string toolName)
        => string.Equals(toolName, "shell_execute", StringComparison.Ordinal);

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

        var assignmentDigest = candidate.AssignmentDigest?.Value ?? string.Empty;
        payloadBuilder.Append(candidate.AssignmentDigest is null
            ? NoAssignmentDigestKeyKind
            : ExactAssignmentDigestKeyKind).Append(':');
        payloadBuilder.Append(assignmentDigest.Length).Append(':').Append(assignmentDigest);
        payloadBuilder.Append(effectiveDirectory.Length).Append(':').Append(effectiveDirectory);
        var payload = payloadBuilder.ToString();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return CandidateKeyPrefix + Convert.ToHexString(hash);
    }
}
