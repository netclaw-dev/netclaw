// -----------------------------------------------------------------------
// <copyright file="ShellExecutionEnvironment.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using ShellSyntaxTree;

namespace Netclaw.Security;

public enum ShellGrammar
{
    Bash,
    PowerShell
}

public enum ShellPathStyle
{
    Posix,
    Windows
}

/// <summary>
/// Immutable canonical description shared by shell execution, syntax parsing,
/// security policy, and model-visible runtime context.
/// </summary>
public sealed class ShellExecutionEnvironment
{
    private const int MaxWrapperDepth = 8;
    private static readonly Lazy<ShellExecutionEnvironment> CurrentEnvironment = new(CreateCurrent);

    private ShellExecutionEnvironment(
        string platform,
        string executable,
        ShellGrammar grammar,
        ShellPathStyle pathStyle,
        IShellParser parser,
        ImmutableArray<string> commandArguments)
    {
        Platform = platform;
        Executable = executable;
        Grammar = grammar;
        PathStyle = pathStyle;
        Parser = parser;
        CommandArguments = commandArguments;
    }

    public static ShellExecutionEnvironment Current => CurrentEnvironment.Value;

    public string Platform { get; }

    public string Executable { get; }

    public ShellGrammar Grammar { get; }

    public ShellPathStyle PathStyle { get; }

    public IShellParser Parser { get; }

    public ImmutableArray<string> CommandArguments { get; }

    public IShellParser CreateParser(string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return Parser;

        return Grammar switch
        {
            ShellGrammar.Bash => new BashParser(new BashParserOptions
            {
                WorkingDirectory = workingDirectory
            }),
            ShellGrammar.PowerShell => new PwshParser(new PwshParserOptions
            {
                WorkingDirectory = workingDirectory
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(Grammar), Grammar, "Unsupported shell grammar.")
        };
    }

    public bool LooksLikePath(string token)
        => ApprovalSemantics().LooksLikePath(token);

    public string? NormalizePathToken(string path, string? workingDirectory = null)
        => ApprovalSemantics().NormalizePathToken(path, workingDirectory);

    public bool TryExtractStaticPathTokens(
        string command,
        string? workingDirectory,
        out IReadOnlyList<string> pathTokens)
    {
        var analysis = Analyze(command, workingDirectory);
        if (analysis.Failure != ShellAnalysisFailure.None || analysis.HasDynamicSyntax)
        {
            pathTokens = [];
            return false;
        }

        var paths = new List<string>();
        foreach (var clause in analysis.Clauses)
        {
            foreach (var argument in clause.Args)
            {
                if (argument.IsCwdAttribution || !argument.IsPath)
                    continue;

                // arg.Raw keeps the source quotes, and a leading quote breaks
                // the drive-letter check in LooksLikePath (`"C:\x"` shifts the
                // colon off index 1). Strip quotes for the predicate, then
                // record the parser's resolved absolute path when it has one so
                // downstream trust-zone checks see the real target, not a
                // quoted or relative token.
                var unquoted = StripSurroundingQuotes(argument.Raw);
                if (!LooksLikePath(unquoted))
                    continue;

                paths.Add(!string.IsNullOrEmpty(argument.Resolved) ? argument.Resolved : unquoted);
            }

            foreach (var redirect in clause.Redirects)
            {
                var target = StripSurroundingQuotes(redirect.Target);
                if (LooksLikePath(target))
                    paths.Add(target);
            }
        }

        pathTokens = paths;
        return true;
    }

    private static string StripSurroundingQuotes(string value)
    {
        if (value.Length >= 2
            && (value[0] == '"' || value[0] == '\'')
            && value[^1] == value[0])
        {
            return value[1..^1];
        }

        return value;
    }

    internal ShellCommandAnalysis Analyze(string command, string? workingDirectory = null)
    {
        if (Grammar == ShellGrammar.Bash)
            return ShellCommandAnalyzer.Bash.Analyze(command, workingDirectory);

        var clauses = new List<Clause>();
        var failure = Analyze(command, workingDirectory, depth: 0, clauses);
        return new ShellCommandAnalysis(clauses, failure);
    }

    private ShellAnalysisFailure Analyze(
        string command,
        string? workingDirectory,
        int depth,
        List<Clause> clauses)
    {
        if (depth > MaxWrapperDepth)
            return ShellAnalysisFailure.Unresolved;

        if (Grammar == ShellGrammar.PowerShell && ContainsUnsupportedWindowsShell(command))
            return ShellAnalysisFailure.UnsupportedShellWrapper;

        ParsedCommand parsed;
        try
        {
            parsed = CreateParser(workingDirectory).Parse(command);
        }
        catch
        {
            return ShellAnalysisFailure.Unresolved;
        }

        if (parsed.IsUnparseable || parsed.Clauses.Count == 0)
            return ShellAnalysisFailure.Unresolved;

        if (Grammar == ShellGrammar.PowerShell
            && parsed.Clauses.Any(static clause => IsUnsupportedWindowsShellVerb(clause.Verb.Tokens.FirstOrDefault())))
        {
            return ShellAnalysisFailure.UnsupportedShellWrapper;
        }

        if (Grammar == ShellGrammar.PowerShell
            && parsed.Clauses.Any(IsIndirectShellProcessWrapper))
        {
            clauses.AddRange(parsed.Clauses);
            return ShellAnalysisFailure.UnsupportedShellWrapper;
        }

        var innerCommands = ApprovalSemantics().ExtractInnerCommands(command);
        var hasUnexpandedWrapper = parsed.Clauses.Any(IsUnexpandedWrapperClause);
        if (innerCommands.Count == 0 || !hasUnexpandedWrapper)
        {
            clauses.AddRange(parsed.Clauses);
            return ShellAnalysisFailure.None;
        }

        clauses.AddRange(parsed.Clauses.Where(clause =>
            !IsUnexpandedWrapperClause(clause) || HasPosixShellInvokerInArguments(clause)));
        foreach (var innerCommand in innerCommands)
        {
            var failure = Analyze(innerCommand, workingDirectory, depth + 1, clauses);
            if (failure != ShellAnalysisFailure.None)
                return failure;
        }

        return ShellAnalysisFailure.None;
    }

    private bool ContainsUnsupportedWindowsShell(string command)
    {
        foreach (var segment in ApprovalSemantics().SplitCompoundCommand(command))
        {
            var firstToken = ShellTokenizer.Tokenize(segment).FirstOrDefault();
            if (firstToken is null)
                continue;

            if (IsUnsupportedWindowsShellVerb(firstToken))
                return true;
        }

        return false;
    }

    private static bool IsUnsupportedWindowsShellVerb(string? token)
    {
        if (token is null)
            return false;

        var verb = ShellTokenizer.TrimShellPunctuation(token);
        return verb.Equals("cmd", StringComparison.OrdinalIgnoreCase)
               || verb.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
               || verb.Equals("powershell", StringComparison.OrdinalIgnoreCase)
               || verb.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIndirectShellProcessWrapper(Clause clause)
    {
        var verb = NormalizePowerShellVerb(
            clause.Verb.CanonicalVerb ?? clause.Verb.Tokens.FirstOrDefault() ?? string.Empty);
        if (!verb.Equals("Start-Process", StringComparison.OrdinalIgnoreCase))
            return false;

        return clause.Args.Any(static arg =>
        {
            var target = arg.Raw.Trim('"', '\'');
            return target.Equals("cmd", StringComparison.OrdinalIgnoreCase)
                   || target.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
                   || target.Equals("powershell", StringComparison.OrdinalIgnoreCase)
                   || target.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
                   || target.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
                   || target.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);
        });
    }

    internal static string NormalizePowerShellVerb(string verb)
    {
        var separator = verb.LastIndexOf('\\');
        return separator >= 0 ? verb[(separator + 1)..] : verb;
    }

    private bool IsUnexpandedWrapperClause(Clause clause)
    {
        if (clause.Verb.Tokens.Count == 0 || clause.Args.Count == 0)
            return false;

        if (Grammar == ShellGrammar.Bash
            && (clause.Verb.Tokens.Any(static token => IsPosixShellInvokerToken(token))
                || HasPosixShellInvokerInArguments(clause)))
        {
            return clause.Args.Any(static arg =>
                arg.Raw.Length > 1
                && arg.Raw[0] == '-'
                && !arg.Raw.StartsWith("--", StringComparison.Ordinal)
                && arg.Raw.AsSpan(1).IndexOf('c') >= 0);
        }

        return false;
    }

    private static bool HasPosixShellInvokerInArguments(Clause clause)
        => clause.Args.Any(static arg =>
            arg.Kind != ArgKind.DynamicSkip && IsPosixShellInvokerToken(arg.Raw));

    private static bool IsPosixShellInvokerToken(string token)
        => PosixShellApprovalSemantics.IsPosixShellInvoker(
            ShellTokenizer.TrimShellPunctuation(token));

    private IShellApprovalSemantics ApprovalSemantics()
        => PathStyle == ShellPathStyle.Windows
            ? WindowsShellApprovalSemantics.Instance
            : PosixShellApprovalSemantics.Instance;

    public static ShellExecutionEnvironment Bash(string executable = "/bin/bash")
        => new(
            platform: "unix",
            executable,
            ShellGrammar.Bash,
            ShellPathStyle.Posix,
            new BashParser(),
            ["-c"]);

    public static ShellExecutionEnvironment PowerShell(string executable = "pwsh")
        => new(
            platform: "windows",
            executable,
            ShellGrammar.PowerShell,
            ShellPathStyle.Windows,
            new PwshParser(),
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command"]);

    private static ShellExecutionEnvironment CreateCurrent()
    {
        if (OperatingSystem.IsWindows())
            return PowerShell();

        var platform = OperatingSystem.IsMacOS() ? "macos" : "linux";
        var environment = Bash();
        return new ShellExecutionEnvironment(
            platform,
            environment.Executable,
            environment.Grammar,
            environment.PathStyle,
            environment.Parser,
            environment.CommandArguments);
    }
}
