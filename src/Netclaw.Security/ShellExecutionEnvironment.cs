// -----------------------------------------------------------------------
// <copyright file="ShellExecutionEnvironment.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Diagnostics;
using ShellSyntaxTree;

namespace Netclaw.Security;

/// <summary>
/// Identifies the operating-system family that owns the native shell host.
/// </summary>
public enum ShellPlatform
{
    Linux,
    MacOS,
    Windows
}

/// <summary>
/// Identifies the grammar used to parse submitted shell source.
/// </summary>
public enum ShellGrammar
{
    Bash,
    PowerShell
}

/// <summary>
/// Identifies the path rules used by the selected shell host.
/// </summary>
public enum ShellPathStyle
{
    Posix,
    Windows
}

/// <summary>
/// Describes one immutable native shell identity for a daemon process.
/// </summary>
public sealed class ShellExecutionEnvironment
{
    private static readonly ImmutableArray<string> BashCommandArguments = ["-c"];
    private static readonly ImmutableArray<string> PowerShellCommandArguments =
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command"];

    private ShellExecutionEnvironment(
        ShellPlatform platform,
        string executablePath,
        ShellGrammar grammar,
        ShellPathStyle pathStyle,
        ImmutableArray<string> commandArguments,
        PwshDialect? powerShellDialect)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("The shell executable path is required.", nameof(executablePath));
        if (!IsFullyQualified(executablePath, pathStyle))
            throw new ArgumentException("The shell executable path must be absolute for its path style.", nameof(executablePath));
        if (commandArguments.IsDefaultOrEmpty)
            throw new ArgumentException("At least one fixed shell argument is required.", nameof(commandArguments));

        Platform = platform;
        ExecutablePath = executablePath;
        Grammar = grammar;
        PathStyle = pathStyle;
        CommandArguments = commandArguments;
        PowerShellDialect = powerShellDialect;
    }

    /// <summary>
    /// Gets the native platform selected at daemon startup.
    /// </summary>
    public ShellPlatform Platform { get; }

    /// <summary>
    /// Gets the absolute executable path that passed host resolution.
    /// </summary>
    public string ExecutablePath { get; }

    /// <summary>
    /// Gets the executable file name for logs and model context.
    /// </summary>
    public string ExecutableName
    {
        get
        {
            var separator = Math.Max(
                ExecutablePath.LastIndexOf('/'),
                ExecutablePath.LastIndexOf('\\'));
            return separator < 0 ? ExecutablePath : ExecutablePath[(separator + 1)..];
        }
    }

    /// <summary>
    /// Gets the grammar that parses submitted source.
    /// </summary>
    public ShellGrammar Grammar { get; }

    /// <summary>
    /// Gets the path rules used by policy and parser resolution.
    /// </summary>
    public ShellPathStyle PathStyle { get; }

    /// <summary>
    /// Gets the fixed arguments placed before one submitted command argument.
    /// </summary>
    public IReadOnlyList<string> CommandArguments { get; }

    /// <summary>
    /// Gets the selected PowerShell dialect, or <see langword="null"/> for Bash.
    /// </summary>
    public PwshDialect? PowerShellDialect { get; }

    /// <summary>
    /// Creates the fixed Bash identity used on Linux and macOS.
    /// </summary>
    public static ShellExecutionEnvironment CreateBash(ShellPlatform platform)
    {
        if (platform is not (ShellPlatform.Linux or ShellPlatform.MacOS))
            throw new ArgumentOutOfRangeException(nameof(platform), platform, "Bash is supported only on Linux and macOS.");

        return new ShellExecutionEnvironment(
            platform,
            "/bin/bash",
            ShellGrammar.Bash,
            ShellPathStyle.Posix,
            BashCommandArguments,
            powerShellDialect: null);
    }

    /// <summary>
    /// Creates a Windows PowerShell identity from the executable path that passed probing.
    /// </summary>
    public static ShellExecutionEnvironment CreatePowerShell(
        string executablePath,
        PwshDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("The shell executable path is required.", nameof(executablePath));
        if (dialect is not (PwshDialect.PowerShell7 or PwshDialect.WindowsPowerShell51))
            throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "A supported PowerShell dialect is required.");

        var expectedExecutableName = dialect == PwshDialect.PowerShell7
            ? "pwsh.exe"
            : "powershell.exe";
        var separator = Math.Max(
            executablePath.LastIndexOf('/'),
            executablePath.LastIndexOf('\\'));
        var executableName = separator < 0
            ? executablePath
            : executablePath[(separator + 1)..];
        if (!string.Equals(executableName, expectedExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Dialect {dialect} requires {expectedExecutableName}.",
                nameof(executablePath));
        }

        return new ShellExecutionEnvironment(
            ShellPlatform.Windows,
            executablePath,
            ShellGrammar.PowerShell,
            ShellPathStyle.Windows,
            PowerShellCommandArguments,
            dialect);
    }

    /// <summary>
    /// Parses source with the environment's grammar, dialect, working directory, and unknown initial state.
    /// </summary>
    public ParsedCommand Parse(string source, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Grammar switch
        {
            ShellGrammar.Bash => new BashParser(new BashParserOptions
            {
                WorkingDirectory = workingDirectory,
                InitialStateMode = BashInitialStateMode.Unknown
            }).Parse(source),
            ShellGrammar.PowerShell when PowerShellDialect is { } dialect =>
                new PwshParser(new PwshParserOptions
                {
                    WorkingDirectory = workingDirectory,
                    InitialStateMode = PwshInitialStateMode.Unknown,
                    Dialect = dialect
                }).Parse(source),
            _ => throw new InvalidOperationException("The shell environment has no supported parser identity.")
        };
    }

    /// <summary>
    /// Creates fresh process-start data for one submitted command.
    /// </summary>
    public ProcessStartInfo CreateProcessStartInfo(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in CommandArguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static bool IsFullyQualified(string path, ShellPathStyle pathStyle) => pathStyle switch
    {
        ShellPathStyle.Posix => path.Length > 1 && path[0] == '/',
        ShellPathStyle.Windows => IsFullyQualifiedWindowsPath(path),
        _ => false
    };

    private static bool IsFullyQualifiedWindowsPath(string path)
    {
        if (path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && IsWindowsSeparator(path[2]))
        {
            return true;
        }

        if (path.Length < 5 || !IsWindowsSeparator(path[0]) || !IsWindowsSeparator(path[1]))
            return false;

        var serverEnd = path.IndexOfAny(['\\', '/'], 2);
        if (serverEnd <= 2 || serverEnd == path.Length - 1)
            return false;

        var shareEnd = path.IndexOfAny(['\\', '/'], serverEnd + 1);
        return shareEnd > serverEnd + 1 && shareEnd < path.Length - 1;
    }

    private static bool IsWindowsSeparator(char value) => value is '\\' or '/';
}
