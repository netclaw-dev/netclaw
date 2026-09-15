// -----------------------------------------------------------------------
// <copyright file="ShellFileSystemTreeAccessPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree;

namespace Netclaw.Security;

/// <summary>
/// Classifies parser-owned filesystem tree effects for reusable approval.
/// This policy does not parse command options or grant path authority.
/// </summary>
internal static class ShellFileSystemTreeAccessPolicy
{
    private const string GetChildItem = "Get-ChildItem";

    internal static bool RequiresExactApproval(
        ShellExecutionEnvironment environment,
        IReadOnlyList<CommandOccurrence> commands)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(commands);

        return commands.Any(command => RequiresExactApproval(environment, command));
    }

    internal static bool RequiresExactApproval(
        ShellExecutionEnvironment environment,
        CommandOccurrence command)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return RequiresExactApproval(environment.Grammar, command);
    }

    internal static bool RequiresExactApproval(
        ShellGrammar grammar,
        CommandOccurrence command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var accesses = command.FileSystemTreeAccesses;
        var canonicalVerb = command.Clause.Verb.CanonicalVerb
            ?? command.Clause.Verb.Joined;
        var isAuditedGetChildItem = grammar == ShellGrammar.PowerShell
            && string.Equals(
                canonicalVerb,
                GetChildItem,
                StringComparison.OrdinalIgnoreCase);
        if (accesses.Count == 0)
            return isAuditedGetChildItem;

        if (!isAuditedGetChildItem)
            return true;

        if (accesses.Count != 1)
        {
            return true;
        }

        return !CanUseReusableApproval(command, accesses[0]);
    }

    private static bool CanUseReusableApproval(
        CommandOccurrence command,
        ShellFileSystemTreeAccess access)
    {
        if (!IsReusableTraversal(access.Traversal))
            return false;

        if (access.RootArgument is { } rootArgument)
        {
            return command.Arguments.Any(argument => ReferenceEquals(argument, rootArgument))
                   && command.Clause.Elements.Any(element =>
                       ReferenceEquals(element, rootArgument.Element))
                   && !rootArgument.Argument.IsFlag
                   && !rootArgument.Argument.IsCwdAttribution
                   && rootArgument.Argument.IsPath
                   && RootMatchesArgument(command, access.Root, rootArgument);
        }

        return access.Root is ShellValueDomain.Exact root
               && IsValidValue(root.Value)
               && command.WorkingDirectory is ShellValueDomain.Exact cwd
               && string.Equals(root.Value, cwd.Value, StringComparison.Ordinal);
    }

    private static bool RootMatchesArgument(
        CommandOccurrence command,
        ShellValueDomain root,
        AnalyzedArgument argument)
        => root switch
        {
            ShellValueDomain.Exact exact =>
                IsValidValue(exact.Value)
                && string.Equals(
                    exact.Value,
                    argument.Argument.Resolved,
                    StringComparison.OrdinalIgnoreCase),
            ShellValueDomain.PathPattern pattern =>
                argument.Argument.Kind == ArgKind.Glob
                && TryGetDecodedRootArgument(command, argument, out var decoded)
                && string.Equals(pattern.Pattern, decoded, StringComparison.Ordinal)
                && TryGetLeafPatternCoveringDirectory(
                    decoded,
                    command,
                    argument,
                    out var coveringDirectory)
                && AreEquivalentWindowsPaths(
                    pattern.CoveringDirectory,
                    coveringDirectory),
            _ => false
        };

    private static bool TryGetDecodedRootArgument(
        CommandOccurrence command,
        AnalyzedArgument argument,
        out string decoded)
    {
        var sharedArguments = command.Arguments
            .Where(candidate => ReferenceEquals(candidate.Element, argument.Element))
            .ToArray();
        decoded = argument.Element.Value;
        if (sharedArguments.Length == 1)
            return !string.IsNullOrWhiteSpace(decoded);

        var sharedFlag = sharedArguments.Length == 2
            ? sharedArguments.SingleOrDefault(candidate => candidate.Argument.IsFlag)
            : null;
        if (sharedFlag is null)
            return false;

        var prefix = sharedFlag.Argument.Raw;
        if (decoded.Length <= prefix.Length + 1
            || !decoded.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || decoded[prefix.Length] is not (':' or '='))
        {
            return false;
        }

        decoded = decoded[(prefix.Length + 1)..];
        return !string.IsNullOrWhiteSpace(decoded);
    }

    private static bool TryGetLeafPatternCoveringDirectory(
        string pattern,
        CommandOccurrence command,
        AnalyzedArgument argument,
        out string coveringDirectory)
    {
        coveringDirectory = string.Empty;
        if (!IsReusableLeafPattern(pattern))
            return false;

        var globIndex = pattern.IndexOfAny(['*', '?', '[']);
        var separator = pattern.LastIndexOfAny(['/', '\\'], globIndex);
        var coveringSource = separator < 0 ? "." : pattern[..separator];
        if (separator == 2 && pattern.Length > 1 && pattern[1] == ':')
            coveringSource = pattern[..3];

        return command.WorkingDirectory is ShellValueDomain.Exact cwd
               && argument.Argument.Resolved is null
               && argument.AuthoredFileSystemValue is ShellValueDomain.Unknown
               && ShellPathRules.TryResolve(
                   coveringSource,
                   cwd.Value,
                   ShellPathStyle.Windows,
               out coveringDirectory);
    }

    internal static bool IsReusableLeafPattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var globIndex = pattern.IndexOfAny(['*', '?', '[']);
        if (globIndex < 0
            || pattern.AsSpan(globIndex + 1).IndexOfAny('/', '\\') >= 0)
        {
            return false;
        }

        if (pattern.AsSpan(1) is [':', not ('/' or '\\'), ..])
        {
            return false;
        }

        var separator = pattern.LastIndexOfAny(['/', '\\'], globIndex);
        return separator != 0 && !IsIncompleteUncLeafPattern(pattern, separator);
    }

    private static bool IsIncompleteUncLeafPattern(string pattern, int separator)
    {
        if (pattern.Length < 2
            || pattern[0] is not ('/' or '\\')
            || pattern[1] is not ('/' or '\\'))
        {
            return false;
        }

        var shareSeparator = pattern.IndexOfAny(['/', '\\'], 2);
        return shareSeparator <= 2
               || separator <= shareSeparator
               || pattern.AsSpan(2, shareSeparator - 2) is "?" or ".";
    }

    private static bool AreEquivalentWindowsPaths(string left, string right)
        => ShellPathRules.TryNormalize(left, ShellPathStyle.Windows, out var normalizedLeft)
           && ShellPathRules.TryNormalize(right, ShellPathStyle.Windows, out var normalizedRight)
           && ShellPathRules.Equals(
               normalizedLeft,
               normalizedRight,
               ShellPathStyle.Windows);

    internal static bool IsReusableTraversal(ShellTreeTraversalMode traversal)
        => Enum.IsDefined(traversal)
           && traversal is ShellTreeTraversalMode.DirectChildren
               or ShellTreeTraversalMode.RecursiveWithoutFollowingLinks;

    private static bool IsValidValue(string value)
        => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
}
