// -----------------------------------------------------------------------
// <copyright file="RetiredOAuthExpiryDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Reports each <c>Providers.&lt;name&gt;.OAuthTokenExpiry</c> key in netclaw.json. An older release
/// wrote the key there. The daemon still works with it, so this is information and not a warning:
/// it does not change the doctor exit code. <c>netclaw doctor --fix</c> moves the date to secrets.json
/// and removes the key.
/// </summary>
public sealed class RetiredOAuthExpiryDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    private const string CheckName = "OAuth Token Expiry";

    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, error) = DoctorJsonConfigReader.TryReadConfig(paths);
        if (error is not null)
            return Task.FromResult(error);

        var retired = root is null ? [] : OAuthTokenExpiryStorage.FindRetired(root);
        if (retired.Count == 0)
            return Task.FromResult(DoctorCheckResult.Pass(CheckName, "netclaw.json has no retired OAuthTokenExpiry key."));

        var keys = string.Join(", ", retired.Select(static key => $"Providers.{key.Provider}.{key.Key}"));
        return Task.FromResult(DoctorCheckResult.Pass(
            CheckName,
            $"netclaw.json has the retired key {keys}. Netclaw keeps the token expiry in secrets.json. "
            + "Run `netclaw doctor --fix` to move it after a backup."));
    }
}
