// -----------------------------------------------------------------------
// <copyright file="ProbeDiagnosticsLogTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class ProbeDiagnosticsLogTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();

    [Fact]
    public void An_entry_masks_secrets_and_stays_on_one_line()
    {
        var paths = new NetclawPaths(_directory.Path);

        ProbeDiagnosticsLog.Write(
            paths,
            source: "test",
            providerType: "openai",
            endpoint: "https://api.example.com/v1",
            probeId: "probe-1",
            outcome: "failed",
            detail: "upstream said api_key=abc123" + (char)0x2028 + "17:00:00.000 [INF] fake",
            exception: new HttpRequestException("rejected Authorization: Bearer abc123"));

        var text = File.ReadAllText(Path.Combine(paths.LogsDirectory, "provider-probe.log"));
        Assert.DoesNotContain("abc123", text, StringComparison.Ordinal);
        Assert.Contains("api_key=***REDACTED***", text, StringComparison.Ordinal);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain((char)0x2028, text);
    }

    public void Dispose() => _directory.Dispose();
}
