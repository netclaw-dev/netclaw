// -----------------------------------------------------------------------
// <copyright file="RedactingLoggerFactoryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class RedactingLoggerFactoryTests
{
    private readonly CapturingLoggerProvider _capture = new();

    private ILogger Logger(string category = "Some.ThirdParty.Library")
        => new RedactingLoggerFactory(new LoggerFactory([_capture])).CreateLogger(category);

    [Fact]
    public void A_framework_record_is_redacted()
    {
        Logger().LogInformation("calling upstream with Authorization: Bearer abc123");

        var record = Assert.Single(_capture.Records);
        Assert.Equal("Some.ThirdParty.Library", record.Category);
        Assert.Contains("Authorization: Bearer ***REDACTED***", record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Structured_values_are_redacted_and_keep_their_names_and_types()
    {
        Logger().LogInformation(
            "Refresh failed: {Body} after {InputTokens} tokens",
            "Authorization: Bearer abc123",
            1234);

        var record = Assert.Single(_capture.Records);
        var values = record.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("Authorization: Bearer ***REDACTED***", values["Body"]);
        Assert.Equal(1234, values["InputTokens"]);
        Assert.Contains("{OriginalFormat}", values.Keys);
        Assert.DoesNotContain("abc123", record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_settings_that_name_a_secret_word_stay_unchanged()
    {
        Logger().LogInformation("max_tokens=4096 total_tokens=1234");

        Assert.Equal("max_tokens=4096 total_tokens=1234", Assert.Single(_capture.Records).Message);
    }

    [Fact]
    public void An_exception_with_a_secret_is_replaced_by_a_redacted_copy()
    {
        var original = new InvalidOperationException("provider said {\"access_token\":\"abc123\"}");

        Logger().LogWarning(original, "Refresh failed");

        var logged = Assert.Single(_capture.Records).Exception;
        Assert.NotNull(logged);
        Assert.NotSame(original, logged);
        Assert.DoesNotContain("abc123", logged.ToString(), StringComparison.Ordinal);
        Assert.Contains(typeof(InvalidOperationException).FullName!, logged.Message, StringComparison.Ordinal);
        // The caller still has its original exception, for example to rethrow it.
        Assert.Contains("abc123", original.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_without_a_secret_keeps_its_instance()
    {
        var original = new HttpRequestException("connection refused");

        Logger().LogWarning(original, "Connect failed");

        Assert.Same(original, Assert.Single(_capture.Records).Exception);
    }

    [Fact]
    public void A_record_that_cannot_be_redacted_is_withheld()
    {
        var original = new InvalidOperationException("DB_PASSWORD=hunter2");

        Logger("Netclaw.Example").LogError(original, "DB_PASSWORD=hunter2 {Value}", new ThrowingValue());

        var record = Assert.Single(_capture.Records);
        Assert.Equal("Netclaw.Example", record.Category);
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Equal("[log record withheld: redaction failed: FormatException]", record.Message);
        Assert.Empty(record.Values);
        Assert.Null(record.Exception);
    }

    [Fact]
    public void A_scope_that_cannot_be_redacted_is_withheld_without_throwing()
    {
        var logger = Logger();
        using (logger.BeginScope(new ThrowingValue()))
        {
            logger.LogInformation("inside scope");
        }

        var scopes = Assert.Single(_capture.Records).Scopes;
        Assert.Equal("[log scope withheld: redaction failed: FormatException]", Assert.Single(scopes));
    }

    [Fact]
    public void Scope_values_are_redacted_and_keep_their_names()
    {
        var logger = Logger();
        using (logger.BeginScope(new Dictionary<string, object?> { ["Upstream"] = "DB_PASSWORD=hunter2", ["Attempt"] = 2 }))
        using (logger.BeginScope("Authorization: Bearer abc123"))
        {
            logger.LogInformation("inside scope");
        }

        var scopes = Assert.Single(_capture.Records).Scopes;
        var pairs = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(scopes[0])
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("DB_PASSWORD=***REDACTED***", pairs["Upstream"]);
        Assert.Equal(2, pairs["Attempt"]);
        Assert.Equal("Authorization: Bearer ***REDACTED***", scopes[1]);
    }

    private sealed class ThrowingValue
    {
        public override string ToString() => throw new FormatException("cannot render");
    }
}
