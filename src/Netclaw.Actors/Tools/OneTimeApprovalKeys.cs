// -----------------------------------------------------------------------
// <copyright file="OneTimeApprovalKeys.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using Netclaw.Security;

namespace Netclaw.Actors.Tools;

internal static class OneTimeApprovalKeys
{
    private const string CandidateKeyPrefix = "\0candidate-v1:";

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

    private static string CreateCandidateKey(ApprovalCandidate candidate, string? cwd)
    {
        var verb = candidate.Verb.ToUpperInvariant();
        var effectiveDirectory = candidate.Directory ?? cwd ?? string.Empty;
        if (OperatingSystem.IsWindows())
            effectiveDirectory = effectiveDirectory.ToUpperInvariant();

        var payload = $"{verb.Length}:{verb}{effectiveDirectory.Length}:{effectiveDirectory}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return CandidateKeyPrefix + Convert.ToHexString(hash);
    }
}
