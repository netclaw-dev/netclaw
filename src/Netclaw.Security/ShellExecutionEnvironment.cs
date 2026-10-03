// -----------------------------------------------------------------------
// <copyright file="ShellExecutionEnvironment.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Diagnostics;
using Netclaw.Tools;
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
    private static readonly string[] TemporaryVariableNames = ["TMPDIR", "TMP", "TEMP"];
    private static readonly string[] BashUnsetLaunchVariables = ["CDPATH"];

    private ShellExecutionEnvironment(
        ShellPlatform platform,
        string executablePath,
        ShellGrammar grammar,
        ShellPathStyle pathStyle,
        ImmutableArray<string> commandArguments,
        Version? bashVersion,
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
        HomeDirectory = grammar == ShellGrammar.Bash
            ? ResolveLaunchHomeDirectory(pathStyle)
            : null;
        Grammar = grammar;
        PathStyle = pathStyle;
        CommandArguments = commandArguments;
        BashVersion = bashVersion;
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
    /// Gets the GNU Bash version that passed host probing, or <see langword="null"/>
    /// when no version proof exists.
    /// </summary>
    public Version? BashVersion { get; }

    /// <summary>
    /// Gets the selected PowerShell dialect, or <see langword="null"/> for Bash.
    /// </summary>
    public PwshDialect? PowerShellDialect { get; }

    /// <summary>
    /// Gets the <c>HOME</c> value that the launcher sets on each Bash process, or
    /// <see langword="null"/> when the daemon has no absolute home directory.
    /// </summary>
    /// <remarks>
    /// The value is the parser's default home directory, so <c>~</c> keeps its
    /// earlier result. Only the launch facts of a no-startup host let the parser
    /// also trust <c>$HOME</c> and a <c>cd</c> with no operand.
    /// </remarks>
    public string? HomeDirectory { get; }

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
            bashVersion: null,
            powerShellDialect: null);
    }

    /// <summary>
    /// Creates a Bash identity with the version that passed host probing.
    /// </summary>
    public static ShellExecutionEnvironment CreateBash(
        ShellPlatform platform,
        Version bashVersion)
    {
        ArgumentNullException.ThrowIfNull(bashVersion);
        if (platform is not (ShellPlatform.Linux or ShellPlatform.MacOS))
            throw new ArgumentOutOfRangeException(nameof(platform), platform, "Bash is supported only on Linux and macOS.");

        return new ShellExecutionEnvironment(
            platform,
            "/bin/bash",
            ShellGrammar.Bash,
            ShellPathStyle.Posix,
            BashCommandArguments,
            bashVersion,
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
            bashVersion: null,
            dialect);
    }

    /// <summary>
    /// Parses source with the environment's grammar, dialect, directory, and selected bounded initial state.
    /// </summary>
    public ParsedCommand Parse(string source, string? workingDirectory = null)
        => ParseForApproval(source, workingDirectory, publishAuthoredSourceFacts: false, launchEnvironment: null);

    /// <summary>
    /// Parses source for internal approval analysis with optional authored-source facts.
    /// </summary>
    /// <param name="launchEnvironment">
    /// The facts from <see cref="CreateLaunchEnvironment"/> for source that the launcher
    /// runs directly, or <see langword="null"/> for source that another process reads,
    /// such as the child of a <c>bash -lc</c> wrapper.
    /// </param>
    internal ParsedCommand ParseForApproval(
        string source,
        string? workingDirectory,
        bool publishAuthoredSourceFacts,
        ShellLaunchEnvironment? launchEnvironment)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Grammar switch
        {
            ShellGrammar.Bash => CreateBashParser(
                workingDirectory,
                publishAuthoredSourceFacts,
                launchEnvironment).Parse(source),
            ShellGrammar.PowerShell when PowerShellDialect is { } dialect =>
                new PwshParser(new PwshParserOptions
                {
                    WorkingDirectory = workingDirectory,
                    InitialStateMode = PowerShellInitialStateMode,
                    Dialect = dialect,
                    LaunchEnvironment = launchEnvironment
                }).Parse(source),
            _ => throw new InvalidOperationException("The shell environment has no supported parser identity.")
        };
    }

    /// <summary>
    /// Creates the parser facts for the variables that the launcher sets on a shell
    /// process. The facts and the launcher read <see cref="GetLaunchVariables"/>, so
    /// they cannot drift.
    /// </summary>
    /// <remarks>
    /// The launcher sets <c>HOME</c> in <see cref="CreateProcessStartInfo"/> and the
    /// temporary variables in <see cref="ApplyTemporaryVariables"/>. The parser uses
    /// the facts only under a no-startup initial state. A statement that can change a
    /// variable makes its value unknown again. The Bash facts also prove that
    /// <c>CDPATH</c> is unset, because <see cref="RemoveBashStartupOverrides"/> removes it.
    /// </remarks>
    internal ShellLaunchEnvironment CreateLaunchEnvironment(ManagedTemporaryLocation? temporary)
        => new(
            GetLaunchVariables(temporary),
            Grammar == ShellGrammar.Bash ? BashUnsetLaunchVariables : []);

    /// <summary>
    /// Gets the exact variables that a launched shell receives. The temporary variables
    /// are present only when the call has a managed temporary location.
    /// </summary>
    internal IReadOnlyList<KeyValuePair<string, string>> GetLaunchVariables(ManagedTemporaryLocation? temporary)
    {
        var variables = new List<KeyValuePair<string, string>>(4);
        if (temporary is { } location)
            variables.AddRange(GetTemporaryVariables(location));
        if (HomeDirectory is { } home)
            variables.Add(new("HOME", home));
        return variables;
    }

    /// <summary>Sets <c>TMPDIR</c>, <c>TMP</c>, and <c>TEMP</c> on one child environment.</summary>
    internal static void ApplyTemporaryVariables(
        IDictionary<string, string?> environment,
        ManagedTemporaryLocation temporary)
    {
        ArgumentNullException.ThrowIfNull(environment);
        foreach (var variable in GetTemporaryVariables(temporary))
            environment[variable.Key] = variable.Value;
    }

    /// <summary>Gets the exact temporary-variable value of a temporary location.</summary>
    internal static string GetTemporaryDirectoryValue(ManagedTemporaryLocation temporary)
        => PathUtility.Normalize(temporary.Directory.Value);

    /// <summary>Sets the start directory of one shell process.</summary>
    /// <remarks>
    /// SECURITY: the parser resolves a relative <c>cd</c> lexically from the working
    /// directory. Bash keeps an inherited <c>PWD</c> when it names the same directory,
    /// so the launcher sets <c>PWD</c> to the exact working directory. With a symbolic
    /// link in that path, <c>cd ..</c> then goes where the parser expects.
    /// </remarks>
    internal void ApplyWorkingDirectory(ProcessStartInfo startInfo, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        startInfo.WorkingDirectory = workingDirectory;
        if (Grammar == ShellGrammar.Bash)
            startInfo.Environment["PWD"] = workingDirectory;
    }

    private static IEnumerable<KeyValuePair<string, string>> GetTemporaryVariables(ManagedTemporaryLocation temporary)
    {
        var directory = GetTemporaryDirectoryValue(temporary);
        return TemporaryVariableNames.Select(name => new KeyValuePair<string, string>(name, directory));
    }

    // The daemon home is fixed for its lifetime. A home that is not absolute gives no HOME
    // fact, and the launcher then leaves HOME as the daemon process has it.
    private static string? ResolveLaunchHomeDirectory(ShellPathStyle pathStyle)
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return IsFullyQualified(home, pathStyle) ? home : null;
    }

    /// <summary>
    /// Parses unresolved Bash source for the hard-deny screen only.
    /// </summary>
    /// <remarks>
    /// The screen assumes a bounded initial state that this environment did not
    /// prove, so its facts never authorize a command. They can only add a denial
    /// to input that stays unresolved for approval.
    /// </remarks>
    internal ParsedCommand ParseForProhibitionScreen(
        string source,
        string? workingDirectory,
        BashInitialStateMode assumedState)
        => new BashParser(new BashParserOptions
        {
            WorkingDirectory = workingDirectory,
            InitialStateMode = assumedState
        }).Parse(source);

    internal bool TryProjectFiniteBashScopes(
        string source,
        string? workingDirectory,
        ManagedTemporaryLocation? temporary,
        out BashFiniteScopeProjection? projection)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Grammar != ShellGrammar.Bash)
        {
            projection = null;
            return false;
        }

        return CreateBashParser(
                workingDirectory,
                publishAuthoredSourceFacts: true,
                CreateLaunchEnvironment(temporary))
            .TryProjectFiniteScopes(source, out projection);
    }

    private BashParser CreateBashParser(
        string? workingDirectory,
        bool publishAuthoredSourceFacts,
        ShellLaunchEnvironment? launchEnvironment)
        => new(new BashParserOptions
        {
            WorkingDirectory = workingDirectory,
            InitialStateMode = BashInitialStateMode,
            PublishAuthoredSourceFacts = publishAuthoredSourceFacts,
            LaunchEnvironment = launchEnvironment
        });

    private BashInitialStateMode BashInitialStateMode =>
        BashVersion is { Major: 5, Minor: 2 or 3 }
        && ExecutablePath == "/bin/bash"
        && CommandArguments.SequenceEqual(BashCommandArguments)
            ? BashInitialStateMode.FreshNonInteractiveNoStartup
            : BashInitialStateMode.Unknown;

    private PwshInitialStateMode PowerShellInitialStateMode =>
        PowerShellDialect is PwshDialect.PowerShell7 or PwshDialect.WindowsPowerShell51
        && CommandArguments.SequenceEqual(PowerShellCommandArguments)
            ? PwshInitialStateMode.IsolatedNonInteractiveNoProfile
            : PwshInitialStateMode.Unknown;

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
        if (Grammar == ShellGrammar.Bash)
            RemoveBashStartupOverrides(startInfo.Environment);
        foreach (var variable in GetLaunchVariables(temporary: null))
            startInfo.Environment[variable.Key] = variable.Value;
        return startInfo;
    }

    internal static void RemoveBashStartupOverrides(IDictionary<string, string?> environment)
    {
        // The parser starts from the authored command. A shell startup hook or imported function
        // can change its verbs and directory effects before that command runs.
        foreach (var key in environment.Keys.Where(static key =>
                     key is "BASH_ENV" or "ENV" or "SHELLOPTS" or "BASHOPTS" or "CDPATH"
                         or "GLOBIGNORE" or "IFS" or "POSIXLY_CORRECT" or "BASH_COMPAT"
                         or "LIBPATH" or "SHLIB_PATH"
                     || key.StartsWith("BASH_FUNC_", StringComparison.Ordinal)
                     || key.StartsWith("LD_", StringComparison.Ordinal)
                     || key.StartsWith("DYLD_", StringComparison.Ordinal)).ToArray())
        {
            environment.Remove(key);
        }
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

internal static class ShellExecutionEnvironmentDefaults
{
    // Compatibility-only constructors in Netclaw.Security retain the historical
    // Bash contract. Daemon composition always supplies its resolved environment.
    internal static ShellExecutionEnvironment Bash { get; } =
        ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
}
