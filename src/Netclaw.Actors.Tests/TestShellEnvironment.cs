// -----------------------------------------------------------------------
// <copyright file="TestShellEnvironment.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using Netclaw.Daemon;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tests;

internal static class TestShellEnvironment
{
    public static ShellExecutionEnvironment Current { get; } = CreateCurrent();

    public static string PrintWorkingDirectoryCommand =>
        Current.Grammar == ShellGrammar.PowerShell
            ? "(Get-Location).Path"
            : "pwd";

    public static string LongRunningCommand =>
        Current.Grammar == ShellGrammar.PowerShell
            ? "Start-Sleep -Seconds 300"
            : "sleep 300";

    public static string DelayCommand(int seconds) =>
        Current.Grammar == ShellGrammar.PowerShell
            ? $"Start-Sleep -Seconds {seconds}"
            : $"sleep {seconds}";

    public static string CreateDirectoryCommandName =>
        Current.Grammar == ShellGrammar.PowerShell
            ? "New-Item"
            : "mkdir";

    public static string ReadFileCommand(string path) =>
        Current.Grammar == ShellGrammar.PowerShell
            ? $"Get-Content 'FileSystem::{path.Replace("'", "''", StringComparison.Ordinal)}'"
            : $"cat '{path.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    public static string StandardErrorCommand =>
        Current.Grammar == ShellGrammar.PowerShell
            ? "[Console]::Error.WriteLine('error')"
            : "echo error >&2";

    public static string TwoOutputLinesCommand =>
        Current.Grammar == ShellGrammar.PowerShell
            ? "Write-Output hello; Write-Output world"
            : "echo hello && echo world";

    public static ShellExecutionEnvironment CreateWindowsPowerShell51()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows PowerShell 5.1 tests require Windows.");
        }

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var executablePath = Path.Combine(
            systemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return ShellExecutionEnvironment.CreatePowerShell(
            executablePath,
            PwshDialect.WindowsPowerShell51);
    }

    private static ShellExecutionEnvironment CreateCurrent()
        => ShellExecutionEnvironmentResolver
            .CreateDefault(TimeProvider.System)
            .ResolveAsync(ShellExecutionEnvironmentResolver.DetectCurrentPlatform())
            .GetAwaiter()
            .GetResult()
            .Environment;
}
