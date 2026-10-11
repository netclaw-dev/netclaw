// -----------------------------------------------------------------------
// <copyright file="ShellExecutionEnvironmentTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

public class ShellExecutionEnvironmentTests
{
    [Theory]
    [InlineData(ShellPlatform.Linux)]
    [InlineData(ShellPlatform.MacOS)]
    public void Bash_environment_has_fixed_native_identity(ShellPlatform platform)
    {
        var environment = ShellExecutionEnvironment.CreateBash(platform);

        Assert.Equal(platform, environment.Platform);
        Assert.Equal("/bin/bash", environment.ExecutablePath);
        Assert.Equal("bash", environment.ExecutableName);
        Assert.Equal(ShellGrammar.Bash, environment.Grammar);
        Assert.Equal(ShellPathStyle.Posix, environment.PathStyle);
        Assert.Equal(["-c"], environment.CommandArguments);
        Assert.Null(environment.BashVersion);
        Assert.Null(environment.PowerShellDialect);
    }

    [Fact]
    public void Bash_environment_rejects_Windows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ShellExecutionEnvironment.CreateBash(ShellPlatform.Windows));
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7, "C:\\Program Files\\PowerShell\\pwsh.exe")]
    [InlineData(PwshDialect.WindowsPowerShell51, "C:\\Windows\\PowerShell\\powershell.exe")]
    public void PowerShell_environment_has_fixed_native_identity(
        PwshDialect dialect,
        string executable)
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(executable, dialect);

        Assert.Equal(ShellPlatform.Windows, environment.Platform);
        Assert.Equal(executable, environment.ExecutablePath);
        Assert.Equal(
            dialect == PwshDialect.PowerShell7 ? "pwsh.exe" : "powershell.exe",
            environment.ExecutableName);
        Assert.Equal(ShellGrammar.PowerShell, environment.Grammar);
        Assert.Equal(ShellPathStyle.Windows, environment.PathStyle);
        Assert.Equal(
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command"],
            environment.CommandArguments);
        Assert.Equal(dialect, environment.PowerShellDialect);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pwsh.exe")]
    [InlineData("C:pwsh.exe")]
    [InlineData("\\pwsh.exe")]
    [InlineData("\\\\server\\pwsh.exe")]
    public void PowerShell_environment_rejects_non_absolute_path(string executable)
    {
        Assert.Throws<ArgumentException>(() =>
            ShellExecutionEnvironment.CreatePowerShell(executable, PwshDialect.PowerShell7));
    }

    [Theory]
    [InlineData(PwshDialect.Unknown)]
    [InlineData((PwshDialect)999)]
    public void PowerShell_environment_rejects_unknown_dialect(PwshDialect dialect)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ShellExecutionEnvironment.CreatePowerShell("C:\\PowerShell\\pwsh.exe", dialect));
    }

    [Theory]
    [InlineData("C:\\PowerShell\\powershell.exe", PwshDialect.PowerShell7)]
    [InlineData("C:\\PowerShell\\pwsh.exe", PwshDialect.WindowsPowerShell51)]
    public void PowerShell_environment_rejects_dialect_executable_mismatch(
        string executable,
        PwshDialect dialect)
    {
        Assert.Throws<ArgumentException>(() =>
            ShellExecutionEnvironment.CreatePowerShell(executable, dialect));
    }

    [Fact]
    public void Bash_parser_keeps_unknown_initial_state()
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);

        var parsed = environment.Parse("printf '%s' \"$value\"", "/repo");

        Assert.True(parsed.IsUnparseable);
        Assert.Contains("variable-attribute state", parsed.UnparseableReason);
    }

    [Theory]
    [InlineData(5, 2)]
    [InlineData(5, 3)]
    public void Probed_supported_Bash_parser_publishes_bounded_assignment_facts(
        int major,
        int minor)
    {
        var environment = ShellExecutionEnvironment.CreateBash(
            ShellPlatform.Linux,
            new Version(major, minor));

        var parsed = environment.Parse("root='/work/tree'; inspect \"$root/file\"", "/work");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var command = Assert.Single(parsed.Commands);
        var assignment = Assert.Single(command.Assignments);
        Assert.Equal("root", assignment.Name);
        Assert.Equal("/work/tree", Assert.IsType<ShellValueDomain.Exact>(
            assignment.EffectiveValue).Value);
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(5, 4)]
    [InlineData(6, 0)]
    public void Probed_unsupported_Bash_version_keeps_unknown_initial_state(
        int major,
        int minor)
    {
        var environment = ShellExecutionEnvironment.CreateBash(
            ShellPlatform.Linux,
            new Version(major, minor));

        var parsed = environment.Parse("root='/work/tree'; inspect \"$root/file\"", "/work");

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
    }

    [Fact]
    public void Parse_preserves_the_two_parameter_public_contract()
    {
        var parse = Assert.Single(
            typeof(ShellExecutionEnvironment).GetMethods(),
            static method => method.Name == nameof(ShellExecutionEnvironment.Parse));

        Assert.Equal(
            [typeof(string), typeof(string)],
            parse.GetParameters().Select(static parameter => parameter.ParameterType));
    }

    [Fact]
    public void PowerShell_parser_uses_selected_dialect()
    {
        var powerShell7 = ShellExecutionEnvironment.CreatePowerShell(
            "C:\\PowerShell\\7\\pwsh.exe",
            PwshDialect.PowerShell7);
        var windowsPowerShell = ShellExecutionEnvironment.CreatePowerShell(
            "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe",
            PwshDialect.WindowsPowerShell51);

        var accepted = powerShell7.Parse("Get-Item a && Get-Item b", "C:\\repo");
        var rejected = windowsPowerShell.Parse("Get-Item a && Get-Item b", "C:\\repo");

        Assert.False(accepted.IsUnparseable, accepted.UnparseableReason);
        Assert.Equal(2, accepted.Commands.Count);
        Assert.True(rejected.IsUnparseable);
        Assert.Contains("PowerShell 7 dialect", rejected.UnparseableReason);
    }

    [Fact]
    public void PowerShell_parser_publishes_isolated_assignment_facts()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            "C:\\PowerShell\\7\\pwsh.exe",
            PwshDialect.PowerShell7);

        var parsed = environment.Parse(
            "$root='C:/work/tree'; Get-Item \"$root/file\"",
            "C:/work");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var command = Assert.Single(parsed.Commands);
        var assignment = Assert.Single(command.Assignments);
        Assert.Equal("root", assignment.Name);
        Assert.Equal("C:/work/tree", Assert.IsType<ShellValueDomain.Exact>(
            assignment.EffectiveValue).Value);
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7, "C:\\PowerShell\\7\\pwsh.exe")]
    [InlineData(
        PwshDialect.WindowsPowerShell51,
        "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe")]
    public void PowerShell_parser_uses_the_isolated_initial_state(
        PwshDialect dialect,
        string executable)
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(executable, dialect);

        var parsed = environment.Parse(
            "foreach ($f in @('a.txt', 'b.txt')) { Remove-Item -LiteralPath $f }");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var occurrence = Assert.Single(parsed.Commands);
        Assert.True(occurrence.IsComplete);
        Assert.Equal(
            ["a.txt", "b.txt"],
            Assert.IsType<ShellValueDomain.FiniteSet>(
                occurrence.Arguments.Single(argument => argument.Argument.Raw == "$f").Value).Values);
    }

    [Fact]
    public void Process_start_info_appends_source_as_one_argument()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            "C:\\PowerShell\\7\\pwsh.exe",
            PwshDialect.PowerShell7);
        const string command = "Write-Output \"hello world\"\nGet-ChildItem";

        var startInfo = environment.CreateProcessStartInfo(command);

        Assert.Equal(environment.ExecutablePath, startInfo.FileName);
        Assert.Equal(
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
            startInfo.ArgumentList);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Fact]
    public void Process_start_info_is_fresh_for_each_call()
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.MacOS);

        var first = environment.CreateProcessStartInfo("git status");
        first.ArgumentList.Add("unexpected");
        var second = environment.CreateProcessStartInfo("git diff");

        Assert.NotSame(first, second);
        Assert.Equal(["-c", "git diff"], second.ArgumentList);
    }

    [Fact]
    public async Task Bash_startup_overrides_cannot_replace_an_authored_directory_change()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var environment = ShellExecutionEnvironment.CreateBash(
            OperatingSystem.IsMacOS() ? ShellPlatform.MacOS : ShellPlatform.Linux);
        var startInfo = environment.CreateProcessStartInfo("cd / && pwd");
        startInfo.Environment["BASH_ENV"] = "/dev/stdin";
        startInfo.Environment["BASH_FUNC_cd%%"] = "() { builtin cd /tmp; }";
        startInfo.Environment["SHELLOPTS"] = "physical";
        startInfo.Environment["BASHOPTS"] = "lastpipe";
        startInfo.Environment["POSIXLY_CORRECT"] = "y";
        startInfo.Environment["BASH_COMPAT"] = "4.2";
        startInfo.Environment["LD_PRELOAD"] = "/tmp/hidden.so";
        startInfo.Environment["DYLD_INSERT_LIBRARIES"] = "/tmp/hidden.dylib";
        startInfo.Environment["LIBPATH"] = "/tmp/lib";
        startInfo.Environment["SHLIB_PATH"] = "/tmp/shlib";
        startInfo.Environment["PATH"] = "/usr/bin:/bin";
        ShellExecutionEnvironment.RemoveBashStartupOverrides(startInfo.Environment);

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("/", output.Trim());
        Assert.Equal("/usr/bin:/bin", startInfo.Environment["PATH"]);
        Assert.DoesNotContain(startInfo.Environment.Keys, static key =>
            key is "BASH_ENV" or "ENV" or "SHELLOPTS" or "BASHOPTS" or "CDPATH"
                or "GLOBIGNORE" or "IFS" or "POSIXLY_CORRECT" or "BASH_COMPAT"
                or "LIBPATH" or "SHLIB_PATH"
            || key.StartsWith("BASH_FUNC_", StringComparison.Ordinal)
            || key.StartsWith("LD_", StringComparison.Ordinal)
            || key.StartsWith("DYLD_", StringComparison.Ordinal));
    }
}
