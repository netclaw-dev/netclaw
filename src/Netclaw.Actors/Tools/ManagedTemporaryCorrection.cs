// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryCorrection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>Advice that asks an agent to submit a different tool call. A correction grants no authority.</summary>
internal abstract record ToolCorrection
{
    private ToolCorrection() { }

    /// <summary>Suggests the current run's managed temporary directory instead of a platform temporary root.</summary>
    internal sealed record ManagedTemporaryDirectorySuggested(
        ManagedTemporaryCorrectionTarget Target) : ToolCorrection;

    /// <summary>Suggests a native tool instead of invoking that tool name through the shell.</summary>
    internal sealed record NativeToolSuggested(ToolName ToolName) : ToolCorrection;

    /// <summary>Suggests a one-call shell directory without changing the project declaration.</summary>
    internal sealed record ShellWorkingDirectorySuggested(string Directory) : ToolCorrection;

    /// <summary>
    /// Suggests a rewrite that gives the shell command known command words. A
    /// grant can cover a call only by its command words, so the call does not run.
    /// </summary>
    internal sealed record ShellCommandWordsRewriteSuggested(
        ShellCommandWordsRewrite Rewrite,
        ApprovalShell Shell) : ToolCorrection;

    /// <summary>
    /// Suggests double quotes around each word that the shell can expand to
    /// file names with a value that Netclaw cannot prove. The command words are
    /// known, so a quoted word is one unknown operand that a grant for anywhere
    /// can cover (decision D1). The call does not run.
    /// </summary>
    /// <param name="Words">The source text of each such word, in command order.</param>
    internal sealed record ShellWordQuoteSuggested(IReadOnlyList<string> Words) : ToolCorrection;

    /// <summary>
    /// Asks for a shorter shell command. The approval prompt cannot show the
    /// full command, so the operator could not see what they approve.
    /// </summary>
    /// <param name="Length">The length of the longest text that the prompt would show.</param>
    internal sealed record ShellCommandTooLongToShow(int Length) : ToolCorrection;
}

/// <summary>Groups compatible correction facts for one tool attempt.</summary>
/// <remarks>
/// The collection has no authority or side effects. The coordinator selects the
/// applicable facts, and the delivery factory defines response and state behavior.
/// </remarks>
internal sealed class ToolCorrectionCollection
{
    private readonly IReadOnlyList<ToolCorrection> _items;

    internal ToolCorrectionCollection(IEnumerable<ToolCorrection> corrections)
    {
        ArgumentNullException.ThrowIfNull(corrections);

        var items = new List<ToolCorrection>();
        foreach (var correction in corrections)
        {
            ArgumentNullException.ThrowIfNull(correction);
            if (items.Contains(correction))
                throw new ArgumentException("Correction collections cannot contain duplicates.", nameof(corrections));

            items.Add(correction);
        }

        if (items.Count == 0)
            throw new ArgumentException("Correction collections cannot be empty.", nameof(corrections));

        _items = Array.AsReadOnly(items.ToArray());
    }

    internal IReadOnlyList<ToolCorrection> Items => _items;
}

/// <summary>Defines the shared correction content, receipt, and actor state change.</summary>
internal sealed record ToolCorrectionDelivery(
    string Content,
    ToolInvocationReceipt.Correction Receipt,
    ToolName? NativeTool,
    ManagedTemporaryCorrectionChange? ManagedTemporaryStateChange)
{
    /// <summary>Creates one delivery result from the corrections that policy selected.</summary>
    /// <remarks>
    /// Native-tool advice requires a new call and a fresh authorization attempt.
    /// Temporary-only advice records one exact retry key after the result reaches the model.
    /// </remarks>
    internal static ToolCorrectionDelivery Create(
        ToolCorrectionCollection corrections,
        ManagedTemporaryCallSemantics? managedTemporaryCall)
    {
        ArgumentNullException.ThrowIfNull(corrections);

        return corrections.Items switch
        {
            [ToolCorrection.ShellWorkingDirectorySuggested shell] => CreateShellDirectory(shell.Directory),
            [ToolCorrection.ShellCommandWordsRewriteSuggested words] => CreateCommandWords(words),
            [ToolCorrection.ShellWordQuoteSuggested quote] => CreateWordQuote(quote),
            [ToolCorrection.ShellCommandTooLongToShow tooLong] => CreateShorterCommand(tooLong.Length),
            [ToolCorrection.NativeToolSuggested native] => CreateNative(native.ToolName, temporaryTarget: null),
            [ToolCorrection.ManagedTemporaryDirectorySuggested temporary] when managedTemporaryCall is not null
                => CreateTemporary(temporary.Target, managedTemporaryCall),
            [ToolCorrection.ManagedTemporaryDirectorySuggested]
                => throw new InvalidOperationException("A temporary correction requires exact call semantics."),
            [ToolCorrection.NativeToolSuggested native, ToolCorrection.ManagedTemporaryDirectorySuggested temporary]
                => CreateNative(native.ToolName, temporary.Target),
            [ToolCorrection.ManagedTemporaryDirectorySuggested temporary, ToolCorrection.NativeToolSuggested native]
                => CreateNative(native.ToolName, temporary.Target),
            _ => throw new InvalidOperationException("The correction collection has an unsupported combination or duplicate fact.")
        };
    }

    private static ToolCorrectionDelivery CreateShellDirectory(string directory)
        => new(
            "Tool execution deferred: use_shell_working_directory\n" +
            $"One-call working directory: '{directory}'.",
            new ToolInvocationReceipt.Correction(ToolRemediationCode.UseShellWorkingDirectory),
            NativeTool: null,
            ManagedTemporaryStateChange: null);

    private static ToolCorrectionDelivery CreateCommandWords(ToolCorrection.ShellCommandWordsRewriteSuggested words)
        => new(
            "Tool execution deferred: rewrite_shell_command_words\n" + DescribeRewrite(words),
            new ToolInvocationReceipt.Correction(ToolRemediationCode.RewriteShellCommandWords),
            NativeTool: null,
            ManagedTemporaryStateChange: null);

    private static ToolCorrectionDelivery CreateWordQuote(ToolCorrection.ShellWordQuoteSuggested quote)
        => new(
            "Tool execution deferred: rewrite_shell_command_words\n" + DescribeWordQuote(quote),
            new ToolInvocationReceipt.Correction(ToolRemediationCode.RewriteShellCommandWords),
            NativeTool: null,
            ManagedTemporaryStateChange: null);

    /// <summary>
    /// Names each word and shows the word in double quotes. The example is
    /// shown only when double quotes keep the meaning of every other part of
    /// the word: a word with a quote, a backslash, a glob character, a brace,
    /// or a tilde gets the advice without the example.
    /// </summary>
    internal static string DescribeWordQuote(ToolCorrection.ShellWordQuoteSuggested quote)
    {
        var lines = quote.Words.Select(static word =>
            word.IndexOfAny(['"', '\'', '\\', '*', '?', '[', '{', '~']) < 0
                ? $"The shell can expand the word {word} to file names, and Netclaw cannot prove its value. Put the word in double quotes: \"{word}\"."
                : $"The shell can expand the word {word} to file names, and Netclaw cannot prove its value. Put each expansion in that word in double quotes.");
        return string.Join('\n', lines)
               + "\nA word in double quotes stays one word, and the shell does not expand it to file names. "
               + "If the word must expand to file names, write each path literally.";
    }

    private static ToolCorrectionDelivery CreateShorterCommand(int length)
        => new(
            "Tool execution deferred: shorten_shell_command\n"
            + $"This command is too long to show for approval ({length} characters, limit {ApprovalOptionKeys.MaxCommandTextChars}). "
            + "Write long text (a body, a script, file contents) to a file, then pass the file to the command "
            + "(for example `--body-file <file>` or `git commit -F <file>`). Then run the command again.",
            new ToolInvocationReceipt.Correction(ToolRemediationCode.ShortenShellCommand),
            NativeTool: null,
            ManagedTemporaryStateChange: null);

    internal static string DescribeRewrite(ToolCorrection.ShellCommandWordsRewriteSuggested words)
        => (words.Rewrite, words.Shell) switch
        {
            (ShellCommandWordsRewrite.UsePathGlob, ApprovalShell.PowerShell) =>
                "A bare wildcard pattern can expand to a command word. Quote the pattern, for example '*.cs', or use a path with a separator, for example ./*.cs.",
            (ShellCommandWordsRewrite.UsePathGlob, _) =>
                "A bare glob can expand to a command word. Use ./* (a path with /) instead of a bare glob, for example ./*.cs instead of *.cs.",
            (ShellCommandWordsRewrite.WriteWordsLiterally, _) =>
                "An expansion can change a command word. Write the command words literally.",
            (ShellCommandWordsRewrite.RunCommandsSeparately, _) =>
                "A brace list, word splitting, or an expansion can change the command words. Run each command separately, and write the command words literally.",
            (ShellCommandWordsRewrite.WriteProgramPathInFull, _) =>
                "A ~ in the program path is an expansion. Write the full path of the program, for example /home/user/bin/tool instead of ~/bin/tool.",
            _ => throw new ArgumentOutOfRangeException(nameof(words), words.Rewrite, "Unknown command-word rewrite."),
        };

    private static ToolCorrectionDelivery CreateNative(ToolName tool, ManagedTemporaryCorrectionTarget? temporaryTarget)
    {
        var content = $"Shell execution stopped because '{tool}' is a native Netclaw tool.";
        if (temporaryTarget is { } target)
            content += $"\nManaged temporary directory: '{target.ManagedTemporaryDirectory}'.";

        return new ToolCorrectionDelivery(
            content,
            new ToolInvocationReceipt.Correction(ToolRemediationCode.UseNativeTool),
            tool,
            ManagedTemporaryStateChange: null);
    }

    private static ToolCorrectionDelivery CreateTemporary(
        ManagedTemporaryCorrectionTarget retryTarget,
        ManagedTemporaryCallSemantics managedTemporaryCall)
    {
        var correctionKey = new ManagedTemporaryCorrectionKey(managedTemporaryCall, retryTarget);
        return new ToolCorrectionDelivery(
            ManagedTemporaryCorrection.BuildSuggestion(retryTarget.ManagedTemporaryDirectory),
            new ToolInvocationReceipt.Correction(ToolRemediationCode.UseManagedTemporaryDirectory),
            NativeTool: null,
            new ManagedTemporaryCorrectionChange.Arm(correctionKey));
    }
}

/// <summary>Captures the execution-relevant arguments of one corrected tool call.</summary>
internal abstract record ManagedTemporaryCallSemantics(string ToolName, TimeSpan Timeout)
{
    /// <summary>Captures one shell call without model-facing rationale.</summary>
    internal sealed record ShellCall(
        ApprovalShell Shell,
        string Command,
        string? WorkingDirectory,
        bool Background,
        TimeSpan Timeout)
        : ManagedTemporaryCallSemantics(ShellTool.ToolName, Timeout);

    /// <summary>Captures one structured file-write call.</summary>
    internal sealed record FileWriteCall(string Path, string? Content, TimeSpan Timeout)
        : ManagedTemporaryCallSemantics(FileWriteTool.ToolName, Timeout);

    /// <summary>Captures one structured file-edit call.</summary>
    internal sealed record FileEditCall(
        string Path,
        string? OldString,
        string? NewString,
        bool? ReplaceAll,
        TimeSpan Timeout)
        : ManagedTemporaryCallSemantics(FileEditTool.ToolName, Timeout);
}

/// <summary>Binds one exact corrected call to the platform root and suggested managed directory.</summary>
internal readonly record struct ManagedTemporaryCorrectionKey(
    ManagedTemporaryCallSemantics Call,
    ManagedTemporaryCorrectionTarget Target);

/// <summary>Describes an actor-owned change to the one-turn correction state.</summary>
internal abstract record ManagedTemporaryCorrectionChange
{
    private ManagedTemporaryCorrectionChange() { }

    /// <summary>Commits a correction key after its guidance reaches the model.</summary>
    internal sealed record Arm(ManagedTemporaryCorrectionKey Key) : ManagedTemporaryCorrectionChange;

    /// <summary>Removes a correction key after one matching retry claims it.</summary>
    internal sealed record Consume(ManagedTemporaryCorrectionKey Key) : ManagedTemporaryCorrectionChange;
}

/// <summary>Provides a thread-safe, consume-once view of the keys that one actor committed.</summary>
internal sealed class ManagedTemporaryCorrectionDispatch
{
    internal static ManagedTemporaryCorrectionDispatch Empty { get; } = new([]);

    private readonly IReadOnlyList<ManagedTemporaryCorrectionKey> _armed;
    private readonly ConcurrentDictionary<ManagedTemporaryCorrectionKey, byte> _consumed = new();

    internal ManagedTemporaryCorrectionDispatch(IEnumerable<ManagedTemporaryCorrectionKey> armed)
        => _armed = Array.AsReadOnly(armed.ToArray());

    internal bool TryConsume(ManagedTemporaryCallSemantics call, out ManagedTemporaryCorrectionKey key)
    {
        foreach (var candidate in _armed)
        {
            if (candidate.Call != call || !_consumed.TryAdd(candidate, 0))
                continue;

            key = candidate;
            return true;
        }

        key = default;
        return false;
    }
}

/// <summary>Owns correction keys for one actor turn. A key is armed after commit and consumed once.</summary>
internal sealed class ManagedTemporaryCorrectionState
{
    private readonly HashSet<ManagedTemporaryCorrectionKey> _keys = [];

    internal ManagedTemporaryCorrectionDispatch Snapshot() => new(_keys);

    internal void Apply(ManagedTemporaryCorrectionChange? change)
    {
        switch (change)
        {
            case ManagedTemporaryCorrectionChange.Arm arm:
                _keys.Add(arm.Key);
                break;
            case ManagedTemporaryCorrectionChange.Consume consume:
                _keys.Remove(consume.Key);
                break;
        }
    }

    internal void Clear() => _keys.Clear();
}

/// <summary>Builds shared correction state for parent and child tool execution paths.</summary>
internal static class ManagedTemporaryCorrection
{
    /// <summary>Projects a tool call to the fields that must remain equal on an immediate retry.</summary>
    internal static ManagedTemporaryCallSemantics? BuildCallSemantics(
        FunctionCallContent toolCall,
        ToolCallMeta? meta,
        TimeSpan timeout,
        ApprovalShell shell = ApprovalShell.Bash)
    {
        if (string.Equals(toolCall.Name, ShellTool.ToolName, StringComparison.Ordinal))
        {
            var command = ToolArgumentHelper.GetString(toolCall.Arguments, "Command")
                ?? ToolArgumentHelper.GetString(toolCall.Arguments, "command");
            if (string.IsNullOrWhiteSpace(command))
                return null;

            var explicitCwd = ToolArgumentHelper.GetString(toolCall.Arguments, "WorkingDirectory");
            return new ManagedTemporaryCallSemantics.ShellCall(
                shell,
                command,
                string.IsNullOrWhiteSpace(explicitCwd) ? null : explicitCwd,
                meta?.Background == true,
                timeout);
        }

        if (toolCall.Name is not (FileWriteTool.ToolName or FileEditTool.ToolName))
            return null;

        var path = ToolArgumentHelper.GetString(toolCall.Arguments, "Path")
            ?? ToolArgumentHelper.GetString(toolCall.Arguments, "path");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (toolCall.Name == FileWriteTool.ToolName)
        {
            return new ManagedTemporaryCallSemantics.FileWriteCall(
                path,
                ToolArgumentHelper.GetString(toolCall.Arguments, "Content"),
                timeout);
        }

        return new ManagedTemporaryCallSemantics.FileEditCall(
            path,
            ToolArgumentHelper.GetString(toolCall.Arguments, "OldString"),
            ToolArgumentHelper.GetString(toolCall.Arguments, "NewString"),
            ToolArgumentHelper.GetBoolStrict(toolCall.Arguments, "ReplaceAll"),
            timeout);
    }

    /// <summary>Builds the correction returned before an approval request.</summary>
    internal static string BuildSuggestion(string managedTemporaryDirectory)
        => "Tool execution deferred: use_managed_temporary_directory\n" +
           $"Managed temporary directory: '{managedTemporaryDirectory}'.";

    /// <summary>Builds the hint returned when the user denies the corrected retry.</summary>
    internal static string BuildDenialHint(string managedTemporaryDirectory)
        => $"Hint: Use the managed temporary directory '{managedTemporaryDirectory}' for disposable artifacts. " +
           "The shared platform temporary root is not a trusted root for this session.";
}
