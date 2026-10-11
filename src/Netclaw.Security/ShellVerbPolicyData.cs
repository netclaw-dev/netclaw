// -----------------------------------------------------------------------
// <copyright file="ShellVerbPolicyData.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Security;

/// <summary>
/// Verb lists that approval and protection policy apply to parser verbs.
/// </summary>
/// <remarks>
/// These lists are policy data. They name executables, but they do not parse
/// an executable's options or operands. ShellSyntaxTree supplies every verb,
/// argument, and path fact. The Shell Approval Abstraction Rule allows explicit
/// verb lists; it does not allow an executable-specific parser.
/// </remarks>
internal static class ShellVerbPolicyData
{
    /// <summary>
    /// Verbs that can copy, read, or send a file. With a default-layout hint in
    /// the command text, one of these verbs makes the shell text protected.
    /// </summary>
    internal static readonly HashSet<string> HighRiskVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "cat", "less", "more", "head", "tail", "grep", "rg", "find", "jq", "awk", "sed", "strings", "xxd", "hexdump",
        "cp", "mv", "tar", "zip", "unzip", "scp", "rsync", "curl", "wget", "nc", "ncat",
        "type", "findstr", "copy", "move", "xcopy", "robocopy", "del", "erase", "ren", "powershell", "powershell.exe", "pwsh", "pwsh.exe",
        "python", "python3", "node", "ruby", "perl", "php",
        "bash", "sh", "zsh"
    };

    /// <summary>
    /// Verbs whose operands are call-specific paths. A candidate keeps only
    /// the first verb token, so <c>grep secret /etc/passwd</c> becomes <c>grep</c>.
    /// </summary>
    private static readonly HashSet<string> PathAwareVerbs = new(HighRiskVerbs, StringComparer.OrdinalIgnoreCase)
    {
        "ls", "dir"
    };

    /// <summary>
    /// Verbs that only write to stdout without a redirect. Candidate
    /// extraction and the pure side-effect rule share this one list.
    /// </summary>
    internal static readonly HashSet<string> SingleTokenSideEffectVerbs = new(StringComparer.Ordinal)
    {
        "echo", "printf", ":", "true", "false"
    };

    /// <summary>
    /// Bash builtins that only test their operands and set the exit status.
    /// </summary>
    /// <remarks>
    /// SECURITY: these builtins can read an operand as a variable name. Bash
    /// evaluates an array subscript in that name as arithmetic, and the
    /// arithmetic runs a command substitution: <c>[ -v 'a[$(cmd)]' ]</c> runs
    /// <c>cmd</c>. Thus an operand is data only when the parser proves a bounded
    /// value with no <c>[</c>. See <c>ShellCommandAnalysis.HasOnlyDataOperands</c>.
    /// In PowerShell, <c>test</c> is not a builtin, so the list is Bash only.
    /// </remarks>
    internal static readonly HashSet<string> BashTestBuiltins = new(StringComparer.Ordinal)
    {
        "test", "["
    };

    /// <summary>
    /// Bash control-transfer builtins. They change only which statement runs
    /// next, as <c>:</c> and <c>true</c> change nothing. They are shell control
    /// facts, not program grammar.
    /// </summary>
    /// <remarks>
    /// ShellSyntaxTree 0.4.0-beta.18 parses <c>break</c> and <c>continue</c> with no
    /// operand or one decimal level, and <c>exit</c> and <c>return</c> with no
    /// operand or one bounded status. It joins the flow state at each one. Any
    /// other form is unparseable. A redirect or a substitution keeps its own
    /// checks. In PowerShell they are keywords, so the list is Bash only.
    /// </remarks>
    internal static readonly HashSet<string> BashControlTransferBuiltins = new(StringComparer.Ordinal)
    {
        "break", "continue", "exit", "return"
    };

    /// <summary>
    /// Returns true when the verb is a data command: an output command, or in
    /// Bash a test builtin or a control-transfer builtin. A data command has no path scope, and with no
    /// redirect it needs no approval.
    /// </summary>
    internal static bool IsDataCommand(string verb, ApprovalShell? shell)
        => SingleTokenSideEffectVerbs.Contains(verb)
           || shell == ApprovalShell.Bash
              && (BashTestBuiltins.Contains(verb) || BashControlTransferBuiltins.Contains(verb));

    /// <summary>
    /// Single-token commands with no subcommand grammar. Each operand is call-specific.
    /// </summary>
    /// <remarks>
    /// SECURITY: a verb here must not run its arguments. A prefix verb such as
    /// <c>env</c>, <c>xargs</c>, <c>sudo</c>, <c>timeout</c>, or <c>nohup</c> must
    /// stay out. Otherwise <c>env rm -rf ~</c> becomes the verb <c>env</c>.
    /// </remarks>
    private static readonly HashSet<string> SingleTokenCommandVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "date", "whoami", "id", "groups", "hostname", "uname", "uptime",
        "free", "nproc", "which",
        "Get-Date", "Get-ComputerInfo",
    };

    /// <summary>
    /// Verbs whose path operand names a directory, also when its basename has a dot.
    /// </summary>
    /// <remarks>
    /// ShellSyntaxTree 0.4.0-beta.5 does not tell whether an operand names a
    /// file or a directory. Until the parser supplies that fact, this list
    /// keeps the exact scope for navigation and traversal verbs.
    /// </remarks>
    internal static readonly HashSet<string> DirectoryOperandVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "find", "cd", "chdir", "pushd", "popd", "Set-Location", "Push-Location", "Pop-Location"
    };

    /// <summary>
    /// Bash programs that only read their operands and have no option that
    /// writes a file or runs a command. Policy data for decision D6: such a
    /// program may read a path that is write-protected but readable, for
    /// example <c>cat ~/.netclaw/config/netclaw.json</c>.
    /// </summary>
    /// <remarks>
    /// SECURITY: a listed program must never write a file that it names. A
    /// program with an output option (<c>sort -o</c>, <c>uniq in out</c>), a
    /// preprocessor (<c>rg --pre</c>), or a shell escape does not belong here.
    /// A redirect is a shell fact, not an operand, so it keeps write protection.
    /// </remarks>
    internal static readonly HashSet<string> ReadOnlyOperandVerbs = new(StringComparer.Ordinal)
    {
        "cat", "head", "tail", "wc", "grep", "jq", "diff"
    };

    /// <summary>
    /// POSIX shells that Netclaw expands when a wrapper form falls outside
    /// the ShellSyntaxTree wrapper contract, for example <c>bash -lc</c>.
    /// </summary>
    internal static readonly HashSet<string> PosixShellInvokers = new(StringComparer.Ordinal)
    {
        "bash", "sh", "dash", "ash", "ksh", "mksh", "zsh",
        "/bin/bash", "/bin/sh", "/bin/dash", "/bin/ash", "/bin/ksh", "/bin/mksh", "/bin/zsh",
        "/usr/bin/bash", "/usr/bin/sh", "/usr/bin/dash", "/usr/bin/ash", "/usr/bin/ksh", "/usr/bin/mksh", "/usr/bin/zsh"
    };

    /// <summary>
    /// File names of programs that read stdin as a script: POSIX shells,
    /// <c>fish</c>, C shells, <c>cmd</c>, and PowerShell. Policy data for the
    /// fixed stdin text rule (<c>ShellCommandAnalysis.HasShellReceiver</c>).
    /// A name has no <c>.exe</c> end: <see cref="IsScriptShellProgram"/>
    /// removes it.
    /// </summary>
    /// <remarks>
    /// This list is wider than <see cref="PosixShellInvokers"/>. That list
    /// selects the <c>-c</c> wrappers whose child source Netclaw parses as
    /// POSIX shell text, so a shell with another grammar must not join it.
    /// A list cannot be complete: a shell that is not here gets the result
    /// of its <c>-c</c> form.
    /// </remarks>
    internal static readonly HashSet<string> ScriptShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "sh", "dash", "ash", "ksh", "mksh", "zsh", "fish", "csh", "tcsh",
        "rbash", "rksh", "ksh93", "posh", "yash",
        "cmd", "powershell", "pwsh"
    };

    /// <summary>
    /// Returns true when the file name of a program word is a script shell.
    /// The path and a <c>.exe</c> end do not matter: <c>bash</c>,
    /// <c>./bash</c>, <c>/usr/local/bin/bash</c>, and <c>bash.exe</c> name
    /// the same kind of program.
    /// </summary>
    internal static bool IsScriptShellProgram(string word)
    {
        var program = LegacyShellTextScan.TrimShellPunctuation(word);
        var name = program[(program.LastIndexOfAny(['/', '\\']) + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        return ScriptShellNames.Contains(name);
    }

    /// <summary>
    /// Keeps only the first token of a parser verb chain when that token is a
    /// path-aware verb, a data command, or a single-token command.
    /// </summary>
    internal static string ApplyVerbShortCircuit(string? parsedVerb, ApprovalShell shell)
    {
        if (string.IsNullOrEmpty(parsedVerb))
            return string.Empty;

        var firstSpace = parsedVerb.IndexOf(' ', StringComparison.Ordinal);
        var firstToken = firstSpace < 0 ? parsedVerb : parsedVerb[..firstSpace];
        return HasSingleTokenVerbChain(firstToken) || IsDataCommand(firstToken, shell)
            ? firstToken
            : parsedVerb;
    }

    /// <summary>
    /// Returns true when policy data says that <paramref name="executable"/>
    /// has a one-token verb chain: a path-aware verb, a side-effect verb, or a
    /// single-token command. Words after such a verb are operands, not subcommands.
    /// </summary>
    internal static bool HasSingleTokenVerbChain(string executable)
        => PathAwareVerbs.Contains(executable)
           || SingleTokenSideEffectVerbs.Contains(executable)
           || SingleTokenCommandVerbs.Contains(executable);
}
