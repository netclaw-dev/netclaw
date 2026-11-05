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
    public void Ordinary_settings_on_the_line_after_their_key_stay_unchanged()
    {
        Logger().LogInformation("max_tokens=\n4096 total_tokens=\n1234");

        Assert.Equal(
            "max_tokens=" + Environment.NewLine + "4096 total_tokens=" + Environment.NewLine + "1234",
            Assert.Single(_capture.Records).Message);
    }

    [Fact]
    public void An_attempt_id_and_its_template_stay_unchanged()
    {
        const string template = "Tool authorization attempt started authorizationAttemptId={AuthorizationAttemptId} sessionId={SessionId}";

        Logger().LogInformation(template, "auth-3f9a1c", "D1/1700000000.0001");

        var record = Assert.Single(_capture.Records);
        var values = record.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(
            "Tool authorization attempt started authorizationAttemptId=auth-3f9a1c sessionId=D1/1700000000.0001",
            record.Message);
        Assert.Equal("auth-3f9a1c", values["AuthorizationAttemptId"]);
        Assert.Equal(template, values["{OriginalFormat}"]);
    }

    // ── Log-only rules (#2770) ──

    [Fact]
    public void A_value_on_the_line_after_its_key_is_masked_in_the_message_and_in_a_value()
    {
        Logger().LogInformation("loaded environment:\nAPI_KEY=\nabc123secret\n{Dump}", "DB_PASSWORD=\r\nhunter2");

        var record = Assert.Single(_capture.Records);
        Assert.DoesNotContain("abc123secret", record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", record.Message, StringComparison.Ordinal);
        Assert.Contains("API_KEY=" + Environment.NewLine + "***REDACTED***", record.Message, StringComparison.Ordinal);
        var values = record.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("DB_PASSWORD=" + Environment.NewLine + "***REDACTED***", values["Dump"]);
    }

    [Theory]
    [InlineData("https://b.r2.cloudflarestorage.com/x.png?X-Amz-Signature=deadbeefcafe01&X-Amz-Credential=abc123def456%2F20261105")]
    [InlineData("https://b.r2.cloudflarestorage.com/x.png?X-Amz-Credential=abc123def456%2F20261105&X-Amz-Signature=deadbeefcafe01")]
    [InlineData("https://acct.blob.core.windows.net/c/x.png?sv=2024-11-04&se=2026-11-06&sig=deadbeefcafe01")]
    public void A_presigned_url_signature_is_masked_in_any_position(string url)
    {
        Logger().LogInformation("upload target {Url}", url);

        var record = Assert.Single(_capture.Records);
        var values = record.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var text in new[] { record.Message, (string)values["Url"]! })
        {
            Assert.DoesNotContain("deadbeefcafe01", text, StringComparison.Ordinal);
            Assert.DoesNotContain("abc123def456", text, StringComparison.Ordinal);
        }
    }

    // ── A value that the template marks as a secret (#2770) ──

    [Fact]
    public void A_value_that_only_the_template_marks_as_a_secret_is_masked()
    {
        Logger().LogInformation("Authorization: Bearer {Token}", "abc123xyz");

        var record = Assert.Single(_capture.Records);
        var values = record.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("Authorization: Bearer ***REDACTED***", record.Message);
        Assert.Equal("***REDACTED***", values["Token"]);
    }

    [Fact]
    public void A_template_marked_number_is_masked_and_an_ordinary_number_keeps_its_type()
    {
        Logger().LogInformation("password={Pin} after {Attempts} attempts", 4321, 2);

        var values = Assert.Single(_capture.Records).Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("***REDACTED***", values["Pin"]);
        Assert.Equal(2, values["Attempts"]);
    }

    [Fact]
    public void An_attempt_id_next_to_a_template_marked_secret_stays()
    {
        Logger().LogInformation(
            "authorizationAttemptId={AuthorizationAttemptId} api_key={Key}",
            "auth-3f9a1c",
            "abc123");

        var values = Assert.Single(_capture.Records).Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("auth-3f9a1c", values["AuthorizationAttemptId"]);
        Assert.Equal("***REDACTED***", values["Key"]);
    }

    [Theory]
    [InlineData("SessionId")]
    [InlineData("SubSessionId")]
    [InlineData("LogSource")]
    [InlineData("ActorPath")]
    [InlineData("AuthorizationAttemptId")]
    public void A_routing_key_inside_a_masked_secret_is_never_masked(string key)
    {
        // The token value runs to the next space, so the shared rules mask the routing value too.
        Logger().LogInformation($"callback token={{Token}}&route={{{key}}}", "abc123", "D1/1700000000.0001");

        var record = Assert.Single(_capture.Records);
        var values = record.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.DoesNotContain("D1/1700000000.0001", record.Message, StringComparison.Ordinal);
        Assert.Equal("D1/1700000000.0001", values[key]);
        Assert.Equal("***REDACTED***", values["Token"]);
    }

    [Fact]
    public void A_value_is_masked_by_its_own_position_not_by_its_text()
    {
        Logger().LogInformation("user {User} sent Authorization: Bearer {Token}", "abc123", "abc123");

        var record = Assert.Single(_capture.Records);
        var values = record.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("user abc123 sent Authorization: Bearer ***REDACTED***", record.Message);
        Assert.Equal("abc123", values["User"]);
        Assert.Equal("***REDACTED***", values["Token"]);
    }

    [Fact]
    public void A_header_value_split_by_the_template_is_masked()
    {
        Logger().LogInformation("Authorization: {Header}", "Bearer abc123xyz");

        var values = Assert.Single(_capture.Records).Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("***REDACTED***", values["Header"]);
    }

    [Fact]
    public void A_value_outside_the_template_keeps_its_own_redaction()
    {
        var logger = Logger();
        using (logger.BeginScope(new Dictionary<string, object?> { ["Adapter"] = "abc123" }))
        {
            logger.LogInformation("Authorization: Bearer {Token}", "abc123");
        }

        var record = Assert.Single(_capture.Records);
        var scope = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(Assert.Single(record.Scopes))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("abc123", scope["Adapter"]);
    }

    [Fact]
    public void A_bridge_state_with_a_prefix_and_values_by_name_is_masked()
    {
        // The Akka bridge passes its own formatted text, with a prefix, and gives values by name
        // in its own order.
        var state = new KeyValuePair<string, object?>[]
        {
            new("Token", "abc123xyz"),
            new("CallId", "call-1"),
            new("ActorPath", "akka://netclaw/user/session"),
            new("{OriginalFormat}", "call={CallId} Authorization: Bearer {Token}"),
        };

        Logger().Log(
            LogLevel.Information,
            default,
            state,
            null,
            static (_, _) => "[INFO][11/05/2026 17:00:00.000Z][Thread 0012][akka://netclaw/user/session] call=call-1 Authorization: Bearer abc123xyz");

        var record = Assert.Single(_capture.Records);
        var values = record.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.DoesNotContain("abc123xyz", record.Message, StringComparison.Ordinal);
        Assert.Equal("***REDACTED***", values["Token"]);
        Assert.Equal("call-1", values["CallId"]);
        Assert.Equal("akka://netclaw/user/session", values["ActorPath"]);
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

    /// <summary>
    /// The single-line console formatter writes the exception text and folds only
    /// <see cref="Environment.NewLine"/>. A lone CR or an escape sequence in a message that holds
    /// no secret must still be escaped before any text sink sees it.
    /// </summary>
    [Fact]
    public void An_exception_whose_text_needs_escaping_is_replaced_by_a_normalized_copy()
    {
        var forged = $"bad input{(char)0x1B}[2K{(char)13}17:00:00.000 [INF] forged{(char)0x2028}line";
        var original = new InvalidOperationException(forged);

        Logger().LogWarning(original, "Parse failed");

        var logged = Assert.Single(_capture.Records).Exception;
        Assert.NotNull(logged);
        Assert.NotSame(original, logged);
        var text = logged.ToString();
        Assert.StartsWith(typeof(InvalidOperationException).FullName!, text, StringComparison.Ordinal);
        Assert.StartsWith("bad input\\u001B[2K", logged.Message, StringComparison.Ordinal);
        Assert.Contains("bad input\\u001B[2K", text, StringComparison.Ordinal);
        Assert.DoesNotContain((char)0x1B, text);
        Assert.DoesNotContain((char)0x2028, text);
        Assert.DoesNotContain("\r17:00", text.Replace(Environment.NewLine, "\n"), StringComparison.Ordinal);
    }

    private sealed class ThrowingValue
    {
        public override string ToString() => throw new FormatException("cannot render");
    }
}
