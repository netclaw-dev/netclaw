// -----------------------------------------------------------------------
// <copyright file="SecretsJsonDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class SecretsJsonDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public SecretsJsonDoctorCheckTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_plaintext_provider_token_expiry_is_not_an_unencrypted_secret()
    {
        // The real writer produces the file, so the check sees what the product saves.
        SecretsFileWriter.Write(
            _paths.SecretsPath,
            """{"Providers":{"copilot":{"OAuthRefreshToken":"refresh-1","OAuthTokenExpiry":"2026-06-01T01:00:00.0000000+00:00"}}}""",
            SecretsProtection.CreateProtector(_paths));

        var result = await new SecretsJsonDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }
}
