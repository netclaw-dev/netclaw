// -----------------------------------------------------------------------
// <copyright file="SystemdUserService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;

namespace Netclaw.Cli.Daemon;

internal enum SystemdUserServiceOwnershipKind
{
    Unmanaged,
    Managed,
    Unknown
}

internal sealed record SystemdUserServiceOwnership(
    SystemdUserServiceOwnershipKind Kind,
    string Message)
{
    public static SystemdUserServiceOwnership Managed(string message) =>
        new(SystemdUserServiceOwnershipKind.Managed, message);

    public static SystemdUserServiceOwnership Unmanaged(string message) =>
        new(SystemdUserServiceOwnershipKind.Unmanaged, message);

    public static SystemdUserServiceOwnership Unknown(string message) =>
        new(SystemdUserServiceOwnershipKind.Unknown, message);
}

internal sealed class SystemdUserService(
    string? unitFilePath = null,
    ISystemCommandRunner? commandRunner = null,
    bool? enabledOnThisPlatform = null)
{
    private const string ServiceName = "netclaw.service";

    private readonly string _unitFilePath = unitFilePath ?? DaemonManager.SystemdUserUnitFilePath;
    private readonly ISystemCommandRunner _commandRunner = commandRunner ?? ProcessSystemCommandRunner.Instance;
    private readonly bool _enabledOnThisPlatform = enabledOnThisPlatform ?? OperatingSystem.IsLinux();

    public async Task<SystemdUserServiceOwnership> GetOwnershipAsync()
    {
        if (!_enabledOnThisPlatform)
            return SystemdUserServiceOwnership.Unmanaged("systemd user services are Linux-only.");

        if (!File.Exists(_unitFilePath))
            return SystemdUserServiceOwnership.Unmanaged("No netclaw systemd user service is installed.");

        var active = await _commandRunner.RunAsync("systemctl", $"--user is-active --quiet {ServiceName}");
        if (active.Success)
            return SystemdUserServiceOwnership.Managed("netclaw.service is active.");

        var enabled = await _commandRunner.RunAsync("systemctl", $"--user is-enabled --quiet {ServiceName}");
        if (enabled.Success)
            return SystemdUserServiceOwnership.Managed("netclaw.service is enabled.");

        if (active.ExecutionError is not null || enabled.ExecutionError is not null)
        {
            return SystemdUserServiceOwnership.Unknown(
                $"Could not execute systemctl: {active.ExecutionError ?? enabled.ExecutionError}");
        }

        if (!string.IsNullOrWhiteSpace(active.StandardError) || !string.IsNullOrWhiteSpace(enabled.StandardError))
        {
            return SystemdUserServiceOwnership.Unknown(
                $"Could not determine netclaw.service state: {active.Message}; {enabled.Message}");
        }

        return SystemdUserServiceOwnership.Unmanaged(
            "netclaw.service is installed but neither active nor enabled.");
    }

    public async Task<DaemonResult> StopAsync()
    {
        var result = await _commandRunner.RunAsync("systemctl", $"--user stop {ServiceName}");
        return ToDaemonResult(result, "Stopped systemd user service.");
    }

    public async Task<DaemonResult> StartAsync()
    {
        var result = await _commandRunner.RunAsync("systemctl", $"--user start {ServiceName}");
        return ToDaemonResult(result, "Started systemd user service.");
    }

    private static DaemonResult ToDaemonResult(SystemCommandResult result, string successMessage) =>
        result.Success
            ? new DaemonResult(true, successMessage)
            : new DaemonResult(false, result.Message);
}

internal interface ISystemCommandRunner
{
    Task<SystemCommandResult> RunAsync(string command, string arguments);
}

internal sealed record SystemCommandResult(
    int ExitCode,
    string StandardError,
    string? ExecutionError = null)
{
    public bool Success => ExecutionError is null && ExitCode == 0;

    public string Message => ExecutionError
        ?? (string.IsNullOrWhiteSpace(StandardError)
            ? $"Command exited with code {ExitCode}."
            : StandardError.Trim());
}

internal sealed class ProcessSystemCommandRunner : ISystemCommandRunner
{
    public static ProcessSystemCommandRunner Instance { get; } = new();

    public async Task<SystemCommandResult> RunAsync(string command, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return new SystemCommandResult(-1, string.Empty, $"Failed to start command '{command}'.");

            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            return new SystemCommandResult(proc.ExitCode, stderr);
        }
        catch (Exception ex)
        {
            return new SystemCommandResult(-1, string.Empty, ex.Message);
        }
    }
}
