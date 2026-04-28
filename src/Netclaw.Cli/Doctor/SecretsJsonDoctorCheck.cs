// -----------------------------------------------------------------------
// <copyright file="SecretsJsonDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.InteropServices;
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class SecretsJsonDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.SecretsPath))
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Secrets JSON",
                $"Secrets file not found at {paths.SecretsPath}.",
                "Create secrets.json if you need provider/slack credentials."));
        }

        string json;
        try
        {
            json = File.ReadAllText(paths.SecretsPath);
            using var _ = JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "Secrets JSON",
                $"Failed parsing {paths.SecretsPath}: {ex.Message}",
                "Fix malformed JSON in secrets.json."));
        }

        // Check file permissions on Unix — secrets.json should be owner-only (chmod 600)
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var mode = File.GetUnixFileMode(paths.SecretsPath);
            var groupOrOtherBits = mode & (
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

            if (groupOrOtherBits != 0)
            {
                return Task.FromResult(DoctorCheckResult.Warning(
                    "Secrets JSON",
                    $"secrets.json has overly permissive file mode ({mode}). Group/other users can read secrets.",
                    $"chmod 600 {paths.SecretsPath}"));
            }
        }

        // Check encryption status — warn if plaintext values exist
        var (encrypted, plaintext) = SecretsFileWriter.CountEncryptionStatus(json);
        if (plaintext > 0)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Secrets JSON",
                $"secrets.json has {plaintext} unencrypted value(s) ({encrypted} encrypted).",
                "Re-set secrets via `netclaw secrets set <key> <value>` to encrypt them."));
        }

        var encryptionNote = encrypted > 0
            ? $" All {encrypted} value(s) are encrypted."
            : string.Empty;

        return Task.FromResult(DoctorCheckResult.Pass(
            "Secrets JSON",
            $"secrets.json parses successfully and has secure file permissions.{encryptionNote}"));
    }
}
