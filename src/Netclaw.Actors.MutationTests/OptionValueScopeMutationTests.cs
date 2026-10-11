// -----------------------------------------------------------------------
// <copyright file="OptionValueScopeMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// The value of an inline option (<c>--name=value</c>) can name a path for the
/// program, so a value outside the working directory is a scope of the
/// candidate (#2364). These rows reject each unsafe change of
/// <c>ShellApprovalMatcher.ResolveOptionValuePathWords</c> and its use.
/// </summary>
public sealed class OptionValueScopeMutationTests : IDisposable
{
    private static readonly ShellExecutionEnvironment Bash52 =
        ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2));

    private static readonly ShellExecutionEnvironment PowerShell =
        ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);

    private readonly string _root;
    private readonly string _work;

    public OptionValueScopeMutationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "netclaw-option-value-" + Guid.NewGuid().ToString("N"));
        _work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        Directory.CreateDirectory(Path.Combine(_work, "bin"));
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        if (!OperatingSystem.IsWindows())
            Directory.CreateSymbolicLink(Path.Combine(_work, "lnk"), outside);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // {W} is the working directory, {R} is its parent, and {H} is the home
    // directory. "none" means that the occurrence has no candidate: the scope
    // is not fixed.
    [Theory]
    [InlineData("dotnet build --output=../outside/x", "{R}/outside/x")]
    [InlineData("dotnet build --output\\=../outside/x", "{R}/outside/x")]
    [InlineData("dotnet build --output'='../outside/x", "{R}/outside/x")]
    [InlineData("dotnet build \"--output=../outside/x\"", "{R}/outside/x")]
    [InlineData("dotnet build -p:OutDir=../outside/x", "{R}/outside/x")]
    [InlineData("dotnet build --output=..", "{R}")]
    [InlineData("dotnet build --output=/etc/x.dll", "/etc")]
    [InlineData("dotnet build --output=$HOME/x", "{H}/x")]
    [InlineData("dotnet build --a=../x --b=/etc/y.dll", "{R}/x,/etc")]
    [InlineData("dotnet build --output=lnk/x", "{W}/lnk/x")]
    [InlineData("dotnet build --output=$'../a\\nb'", "{R}")]
    [InlineData("dotnet build --output=bin/x", "{W}")]
    [InlineData("dotnet build --configuration=Release", "{W}")]
    [InlineData("dotnet build --output=~/x", "{W}")]
    [InlineData("dotnet build --output=\"\"", "{W}")]
    [InlineData("dotnet build --output=/netclaw-option-value-absent/x", "{W}")]
    [InlineData("dotnet build --output=$d/x", "{W}")]
    // A glob value gets the rule of a path word: its text before the first
    // glob character decides. A backslash is a Bash escape, not a separator.
    [InlineData("dotnet build --output=*.x", "{W}")]
    [InlineData("dotnet format --include=bin/*.cs", "{W}")]
    [InlineData("rg --glob=**/bin/** foo", "{W}")]
    [InlineData("rg --regexp=foo\\.bar.*", "{W}")]
    [InlineData("dotnet build --output=../outside/*.x", "{R}/outside")]
    [InlineData("dotnet build --output=\"../outside\"/*.x", "{R}/outside")]
    [InlineData("dotnet build -c ../outside/*.x", "{R}/outside")]
    [InlineData("dotnet build --output=bin/*/../../../x", "none")]
    [InlineData("dotnet build --output=lnk*/x", "none")]
    [InlineData("dotnet build --output=$HOME/*.x", "none")]
    [InlineData("dotnet build --output=bin/*\\\\..\\\\x", "{W}")]
    [InlineData("dotnet build --output='../a*b/x'", "{R}/a*b/x")]
    public void Bash_option_value_outside_the_working_directory_is_a_scope(string command, string expected)
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Equal(Expand(expected), Scopes(Bash52, command, _work));
    }

    // A Windows path on a POSIX host uses the lexical rule of its own style.
    // A text that is not a path of that style (a URL, a date) adds no scope.
    // A rooted text that the style cannot place is its own scope. A value
    // that the parser types as a path gets one scope, from the path word rule.
    [Theory]
    [InlineData("dotnet build --output=..\\x", @"C:\work", @"C:\x")]
    [InlineData("dotnet build -p:OutDir=..\\x", @"C:\work", @"C:\x")]
    [InlineData("dotnet build --output=bin\\x", @"C:\work", @"C:\work")]
    [InlineData("dotnet build --output=%USERPROFILE%\\x", @"C:\work", @"C:\work")]
    [InlineData("curl --url=https://example.com/a/b", @"C:\work", @"C:\work")]
    [InlineData("tool run --date=2026-10-01T10:00:00Z", @"C:\work", @"C:\work")]
    [InlineData("tool run --base=/api/v1", @"C:\work", "/api/v1")]
    [InlineData("dotnet build --output=$HOME/x", @"C:\work", "{H}/x")]
    [InlineData("dotnet build --output=\"$HOME/x\"", @"C:\work", "{H}/x")]
    [InlineData("dotnet build --output=a\\..\\..\\..\\x", @"C:\work", "none")]
    [InlineData("dotnet build --output=$HOME/*.x", @"C:\work", "none")]
    [InlineData("dotnet build --output=$env:USERPROFILE\\*.x", @"C:\work", "none")]
    [InlineData("Get-Content -Path:..\\x", @"C:\work", "C:/x")]
    [InlineData("dotnet build --output=..\\x", null, "none")]
    [InlineData("dotnet build --configuration=Release", null, "-")]
    public void Windows_style_option_value_uses_its_lexical_rule(
        string command,
        string? workingDirectory,
        string expected)
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Equal(Expand(expected), Scopes(PowerShell, command, workingDirectory));
    }

    private string Expand(string expected)
        => expected
            .Replace("{W}", _work, StringComparison.Ordinal)
            .Replace("{R}", _root, StringComparison.Ordinal)
            .Replace("{H}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal);

    private static string Scopes(
        ShellExecutionEnvironment environment,
        string command,
        string? workingDirectory)
    {
        var analysis = new ShellCommandPolicy(environment).Analyze(command, workingDirectory);
        var occurrence = Assert.Single(analysis.Commands);
        var candidates = new ShellApprovalMatcher(environment).ExtractCandidatesForOccurrence(
            occurrence,
            workingDirectory,
            resolveUnknownPathsFromEffectiveValues: false,
            LinkRule.FromVolumeRoot);
        return candidates is null
            ? "none"
            : string.Join(",", candidates.Select(static candidate => candidate.Directory ?? "-"));
    }
}
