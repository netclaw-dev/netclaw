// -----------------------------------------------------------------------
// <copyright file="WebhooksCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Netclaw.Cli.Json;
using Netclaw.Cli.Webhooks;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Webhooks;

/// <summary>
/// The <c>netclaw webhooks</c> surface. Reads run against canonical disk with no
/// daemon. Writes are daemon-only, so every <c>set</c> or <c>delete</c> test that
/// reaches the write step supplies a <see cref="FakeWebhookDaemon"/> and asserts
/// the patch the CLI sent. A test that stops at argument grammar, at the merge
/// preview, or at <c>--dry-run</c> never contacts the daemon and passes none.
/// </summary>
public sealed class WebhooksCommandTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public static TheoryData<string, string[]> InvalidSetArgumentsCases { get; } = new()
    {
        {
            "MissingPrompt",
            [
                "webhooks", "set", "test-route",
                "--secret", "test-secret"
            ]
        },
        {
            "MissingSecret",
            [
                "webhooks", "set", "test-route",
                "--prompt", "Test prompt"
            ]
        },
        {
            "ConflictingCreateAndUpdateOnly",
            [
                "webhooks", "set", "test-route",
                "--prompt", "Test prompt",
                "--secret", "test-secret",
                "--create-only",
                "--update-only"
            ]
        },
        {
            "ConflictingEnabledFlags",
            [
                "webhooks", "set", "test-route",
                "--prompt", "Test prompt",
                "--secret", "test-secret",
                "--enabled",
                "--disabled"
            ]
        },
        {
            "ConflictingDeliveryFlags",
            [
                "webhooks", "set", "test-route",
                "--prompt", "Test prompt",
                "--secret", "test-secret",
                "--delivery-required",
                "--no-delivery-required"
            ]
        },
        {
            "InvalidVerificationKind",
            [
                "webhooks", "set", "test-route",
                "--prompt", "Test prompt",
                "--secret", "test-secret",
                "--verification-kind", "invalid"
            ]
        },
        {
            "InvalidAudience",
            [
                "webhooks", "set", "test-route",
                "--prompt", "Test prompt",
                "--secret", "test-secret",
                "--audience", "invalid"
            ]
        },
        {
            "MissingVerificationKindValue",
            [
                "webhooks", "set", "test-route",
                "--prompt", "Test prompt",
                "--secret", "test-secret",
                "--verification-kind"
            ]
        },
        {
            "MissingNotificationChannelValue",
            [
                "webhooks", "set", "test-route",
                "--prompt", "Test prompt",
                "--secret", "test-secret",
                "--notification-channel"
            ]
        }
    };

    public static TheoryData<string, string[]> MissingFlagValueCases { get; } = new()
    {
        {
            "MissingPromptValue",
            [
                "webhooks", "set", "test-route",
                "--prompt",
                "--secret", "test-secret"
            ]
        },
        {
            "MissingSecretFlagValue",
            [
                "webhooks", "set", "test-route",
                "--prompt", "Test prompt",
                "--secret"
            ]
        }
    };

    public WebhooksCommandTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task List_NoRoutes_ReturnsZero()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "list"], _paths);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task List_WithRoutes_ReturnsZero()
    {
        CreateValidRoute("test-route");

        var result = await WebhooksCommand.RunAsync(["webhooks", "list"], _paths);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task List_InvalidRouteWithNullVerification_DoesNotCrash()
    {
        WriteRouteText("bad", """
{
  "enabled": true,
  "verification": null,
  "events": [],
  "audience": "Public",
  "prompt": "x",
  "notifyInstructions": "",
  "deliveryRequired": true,
  "notificationTarget": null,
  "maxBodyBytes": 1024,
  "rateLimitPerMinute": 1
}
""");

        var result = await WebhooksCommand.RunAsync(["webhooks", "list"], _paths, TextWriter.Null);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task List_Json_MarksInvalidRouteAsInvalid()
    {
        WriteRouteText("bad", """
{
  "enabled": true,
  "verification": null,
  "events": [],
  "audience": "Public",
  "prompt": "x",
  "notifyInstructions": "",
  "deliveryRequired": true,
  "notificationTarget": null,
  "maxBodyBytes": 1024,
  "rateLimitPerMinute": 1
}
""");

        using var stdout = new StringWriter();
        var result = await WebhooksCommand.RunAsync(["webhooks", "list", "--json"], _paths, stdout);
        Assert.Equal(0, result);

        var list = JsonSerializer.Deserialize<List<RouteListItem>>(stdout.ToString(), JsonDefaults.ConfigRead);
        Assert.NotNull(list);
        var item = Assert.Single(list!);
        Assert.Equal("bad", item.Name);
        Assert.Equal("invalid", item.Status);
        Assert.Equal("unknown", item.Verification);
    }

    [Fact]
    public async Task Show_ExistingRoute_ReturnsZero()
    {
        CreateValidRoute("test-route");

        var result = await WebhooksCommand.RunAsync(["webhooks", "show", "test-route"], _paths);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Show_NonexistentRoute_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "show", "nonexistent"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Show_MissingRouteName_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "show"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Show_InvalidRouteWithNullVerification_ReturnsOne()
    {
        WriteRouteText("bad", """
{
  "enabled": true,
  "verification": null,
  "events": [],
  "audience": "Public",
  "prompt": "x",
  "notifyInstructions": "",
  "deliveryRequired": true,
  "notificationTarget": null,
  "maxBodyBytes": 1024,
  "rateLimitPerMinute": 1
}
""");

        var result = await WebhooksCommand.RunAsync(["webhooks", "show", "bad"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_NewRoute_SendsTheRouteToTheDaemon()
    {
        var daemon = FakeWebhookDaemon.Healthy(_paths);

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "new-route",
            "--prompt", "Test prompt",
            "--secret", "test-secret"
        ], _paths, output: null, daemon.Api);

        Assert.Equal(0, result);
        using var body = daemon.SingleUpsertBody("new-route");
        Assert.Equal("Test prompt", body.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("test-secret", body.RootElement.GetProperty("secret").GetString());

        // The daemon writes the file, so the CLI must leave the directory alone.
        Assert.False(File.Exists(Path.Combine(_paths.WebhooksDirectory, "new-route.json")));
    }

    [Fact]
    public async Task Set_WriteFailure_DoesNotReportSuccess()
    {
        var daemon = new FakeWebhookDaemon(_paths, request => request.Method == HttpMethod.Put
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : FakeWebhookDaemon.RouteList());
        using var output = new StringWriter();

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "blocked-route",
            "--prompt", "Test prompt",
            "--secret", "test-secret"
        ], _paths, output, daemon.Api);

        Assert.Equal(1, result);
        Assert.DoesNotContain("[OK]", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_WithUppercaseRoute_NormalizesToLowercase()
    {
        var daemon = FakeWebhookDaemon.Healthy(_paths);

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "GitHub-Issues",
            "--prompt", "Test prompt",
            "--secret", "test-secret"
        ], _paths, output: null, daemon.Api);

        Assert.Equal(0, result);
        Assert.Contains(daemon.Calls, call => call.Method == "PUT" && call.Path == "/api/webhooks/github-issues");
    }

    [Theory]
    [MemberData(nameof(InvalidSetArgumentsCases))]
    public async Task Set_InvalidArguments_ReturnsOne(string caseName, string[] args)
    {
        var result = await WebhooksCommand.RunAsync(args, _paths);

        Assert.True(result == 1, $"expected exit code 1 for case: {caseName}");
    }

    [Fact]
    public async Task Set_DryRun_DoesNotCreateFile()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "dry-run-route",
            "--prompt", "Test prompt",
            "--secret", "test-secret",
            "--dry-run"
        ], _paths);

        Assert.Equal(0, result);
        Assert.False(File.Exists(Path.Combine(_paths.WebhooksDirectory, "dry-run-route.json")));
    }

    [Fact]
    public async Task Set_CreateOnly_FailsIfExists()
    {
        CreateValidRoute("existing-route");

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "existing-route",
            "--prompt", "Updated prompt",
            "--secret", "updated-secret",
            "--create-only"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_UpdateOnly_FailsIfNotExists()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "nonexistent-route",
            "--prompt", "Test prompt",
            "--secret", "test-secret",
            "--update-only"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_InvalidRouteName_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "../secrets",
            "--prompt", "Test prompt",
            "--secret", "test-secret"
        ], _paths);

        Assert.Equal(1, result);
        Assert.False(File.Exists(Path.Combine(_paths.ConfigDirectory, "secrets.json")));
    }

    [Theory]
    [MemberData(nameof(MissingFlagValueCases))]
    public async Task Set_MissingFlagValue_ReturnsOneWithoutPersisting(string caseName, string[] args)
    {
        var result = await WebhooksCommand.RunAsync(args, _paths);

        Assert.True(result == 1, $"expected exit code 1 for case: {caseName}");
        Assert.False(File.Exists(Path.Combine(_paths.WebhooksDirectory, "test-route.json")));
    }

    [Fact]
    public async Task Set_MissingPromptFile_DoesNotPartiallyUpdateExistingRoute()
    {
        CreateValidRoute("test-route", secret: "before-secret", prompt: "before-prompt");

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt-file", Path.Combine(_dir.Path, "missing.txt"),
            "--secret", "after-secret"
        ], _paths);

        Assert.Equal(1, result);

        var route = ReadRoute("test-route");
        Assert.Equal("before-prompt", route.Prompt);
        Assert.Equal("before-secret", route.Verification.Secret!.Value);
    }

    [Fact]
    public async Task Set_MultipleSecretSources_ReturnsOne()
    {
        var secretFile = Path.Combine(_dir.Path, "secret.txt");
        File.WriteAllText(secretFile, "file-secret");

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt", "Test prompt",
            "--secret", "inline-secret",
            "--secret-file", secretFile
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_MultiplePromptSources_ReturnsOne()
    {
        var promptFile = Path.Combine(_dir.Path, "prompt.txt");
        File.WriteAllText(promptFile, "file prompt");

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt", "inline prompt",
            "--prompt-file", promptFile,
            "--secret", "test-secret"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_MissingSecretEnvVariable_ReturnsOne()
    {
        Environment.SetEnvironmentVariable("NETCLAW_WEBHOOK_TEST_MISSING_SECRET", null);

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "test-route",
            "--prompt", "Test prompt",
            "--secret-env", "NETCLAW_WEBHOOK_TEST_MISSING_SECRET"
        ], _paths);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Set_TimestampedHmac_Sends_advanced_settings()
    {
        var daemon = FakeWebhookDaemon.Healthy(_paths);

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "stripe-events",
            "--prompt", "Process Stripe event",
            "--secret", "whsec_test",
            "--verification-kind", "hmac-timestamped",
            "--signature-header", "Stripe-Signature",
            "--timestamp-field", "timestamp",
            "--signature-field", "signature",
            "--signed-payload-separator", "::",
            "--signature-tolerance-seconds", "120"
        ], _paths, output: null, daemon.Api);

        Assert.Equal(0, result);
        using var body = daemon.SingleUpsertBody("stripe-events");
        var patch = body.RootElement;
        Assert.Equal("HmacTimestamped", patch.GetProperty("verificationKind").GetString());
        Assert.Equal("Stripe-Signature", patch.GetProperty("signatureHeaderName").GetString());
        Assert.Equal("timestamp", patch.GetProperty("timestampField").GetString());
        Assert.Equal("signature", patch.GetProperty("signatureField").GetString());
        Assert.Equal("::", patch.GetProperty("signedPayloadSeparator").GetString());
        Assert.Equal(120, patch.GetProperty("toleranceSeconds").GetInt32());
    }

    [Fact]
    public async Task Set_HeaderSecret_Accepts_documented_hyphenated_spelling()
    {
        var daemon = FakeWebhookDaemon.Healthy(_paths);

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "internal-events",
            "--prompt", "Process internal event",
            "--secret", "shared-secret",
            "--verification-kind", "header-secret",
            "--secret-header", "X-Internal-Secret"
        ], _paths, output: null, daemon.Api);

        Assert.Equal(0, result);
        using var body = daemon.SingleUpsertBody("internal-events");
        Assert.Equal("HeaderSecret", body.RootElement.GetProperty("verificationKind").GetString());
    }

    [Fact]
    public async Task Set_Timestamp_options_with_body_hmac_fails_without_persisting()
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "invalid-route",
            "--prompt", "Process event",
            "--secret", "secret",
            "--timestamp-field", "t"
        ], _paths);

        Assert.Equal(1, result);
        Assert.False(File.Exists(Path.Combine(_paths.WebhooksDirectory, "invalid-route.json")));
    }

    [Theory]
    [InlineData("v1", "v1")]
    [InlineData(" timestamp", "v1")]
    [InlineData("time=stamp", "v1")]
    [InlineData("time stamp", "v1")]
    [InlineData("téstamp", "v1")]
    public async Task Set_Unusable_timestamp_fields_fail_without_persisting(
        string timestampField,
        string signatureField)
    {
        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "invalid-route",
            "--prompt", "Process event",
            "--secret", "secret",
            "--verification-kind", "hmac-timestamped",
            "--timestamp-field", timestampField,
            "--signature-field", signatureField
        ], _paths);

        Assert.Equal(1, result);
        Assert.False(File.Exists(Path.Combine(_paths.WebhooksDirectory, "invalid-route.json")));
    }

    [Fact]
    public async Task Set_Unrelated_update_leaves_the_legacy_verifier_untouched()
    {
        CreateValidRoute("legacy-route");
        var daemon = FakeWebhookDaemon.Healthy(_paths);

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "legacy-route",
            "--rate-limit", "12"
        ], _paths, output: null, daemon.Api);

        Assert.Equal(0, result);

        // The patch carries only the flag the operator passed. Every verifier
        // field stays null, so the daemon keeps the stored HMAC settings and adds
        // no timestamp fields to a route that has none.
        using var body = daemon.SingleUpsertBody("legacy-route");
        var patch = body.RootElement;
        Assert.Equal(12, patch.GetProperty("rateLimitPerMinute").GetInt32());
        Assert.Equal(JsonValueKind.Null, patch.GetProperty("verificationKind").ValueKind);
        Assert.Equal(JsonValueKind.Null, patch.GetProperty("toleranceSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, patch.GetProperty("timestampField").ValueKind);
        Assert.Equal(JsonValueKind.Null, patch.GetProperty("signatureField").ValueKind);
    }

    [Fact]
    public async Task Set_MalformedExistingRoute_ReturnsOneWithoutOverwriting()
    {
        WriteRouteText("malformed-route", "{");

        var result = await WebhooksCommand.RunAsync([
            "webhooks", "set", "malformed-route",
            "--prompt", "updated prompt",
            "--secret", "updated-secret"
        ], _paths);

        Assert.Equal(1, result);
        Assert.Equal("{", File.ReadAllText(Path.Combine(_paths.WebhooksDirectory, "malformed-route.json")));
    }

    [Fact]
    public async Task Show_Json_adds_timestamp_fields_only_for_timestamped_kind()
    {
        CreateValidRoute("legacy-route");
        var timestamped = new WebhookRouteConfig
        {
            Prompt = "Process Stripe event",
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.HmacTimestamped,
                Secret = new SensitiveString("whsec_test"),
                SignatureHeaderName = "Stripe-Signature"
            }
        };
        new WebhookRouteStore(_paths).Save("stripe-events", timestamped);

        using var legacyOutput = new StringWriter();
        using var timestampedOutput = new StringWriter();
        Assert.Equal(0, await WebhooksCommand.RunAsync(
            ["webhooks", "show", "legacy-route", "--json"], _paths, legacyOutput));
        Assert.Equal(0, await WebhooksCommand.RunAsync(
            ["webhooks", "show", "stripe-events", "--json"], _paths, timestampedOutput));

        using var legacy = JsonDocument.Parse(legacyOutput.ToString());
        using var stripe = JsonDocument.Parse(timestampedOutput.ToString());
        var legacyVerification = legacy.RootElement.GetProperty("verification");
        var stripeVerification = stripe.RootElement.GetProperty("verification");
        Assert.False(legacyVerification.TryGetProperty("toleranceSeconds", out _));
        Assert.Equal(300, stripeVerification.GetProperty("toleranceSeconds").GetInt32());
        Assert.Equal("t", stripeVerification.GetProperty("timestampField").GetString());
        Assert.Equal("v1", stripeVerification.GetProperty("signatureField").GetString());
        Assert.Equal(".", stripeVerification.GetProperty("signedPayloadSeparator").GetString());
    }

    [Fact]
    public async Task Delete_ExistingRoute_ReturnsZero()
    {
        CreateValidRoute("delete-me");
        var daemon = FakeWebhookDaemon.Healthy(_paths);

        var result = await WebhooksCommand.RunAsync(
            ["webhooks", "delete", "delete-me", "--force"], _paths, output: null, daemon.Api);

        Assert.Equal(0, result);
        Assert.Contains(daemon.Calls, call => call.Method == "DELETE" && call.Path == "/api/webhooks/delete-me");
    }

    [Fact]
    public async Task Delete_NonexistentRoute_ReturnsOne()
    {
        // The daemon owns the route set, so a missing route is its 404, not a
        // missing file on the CLI's disk.
        var daemon = new FakeWebhookDaemon(_paths, request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : FakeWebhookDaemon.RouteList());

        var result = await WebhooksCommand.RunAsync(
            ["webhooks", "delete", "nonexistent", "--force"], _paths, output: null, daemon.Api);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Delete_MissingRouteName_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "delete"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Delete_InvalidRouteName_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "delete", "../secrets", "--force"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Validate_ValidRoute_ReturnsZero()
    {
        CreateValidRoute("valid-route");

        var result = await WebhooksCommand.RunAsync(["webhooks", "validate", "valid-route"], _paths);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Validate_TimestampedRoute_UsesDocumentedKindSpelling()
    {
        CreateValidRoute("timestamped-route");
        var route = ReadRoute("timestamped-route");
        route.Verification.Kind = WebhookVerifierKind.HmacTimestamped;
        new WebhookRouteStore(_paths).Save("timestamped-route", route);
        using var output = new StringWriter();

        var result = await WebhooksCommand.RunAsync(
            ["webhooks", "validate", "timestamped-route"],
            _paths,
            output);

        Assert.Equal(0, result);
        Assert.Contains("Verification: hmac-timestamped", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Hmac", 0, "not valid", true)]
    [InlineData("HmacTimestamped", 0, "t", false)]
    [InlineData("HmacTimestamped", 300, "not valid", false)]
    [InlineData("HmacTimestamped", 300, "t", true)]
    public void RouteSchema_applies_timestamp_constraints_only_to_timestamped_kind(
        string kind,
        int toleranceSeconds,
        string timestampField,
        bool expectedValid)
    {
        var schema = JsonSchema.FromText(LoadRouteSchema());
        var route = new JsonObject
        {
            ["prompt"] = "process delivery",
            ["verification"] = new JsonObject
            {
                ["kind"] = kind,
                ["toleranceSeconds"] = toleranceSeconds,
                ["timestampField"] = timestampField
            }
        };

        var evaluation = schema.Evaluate(route);

        Assert.Equal(expectedValid, evaluation.IsValid);
    }

    [Fact]
    public void RouteSchema_accepts_exact_store_serialization()
    {
        var route = new WebhookRouteConfig
        {
            Prompt = "process delivery",
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.HmacTimestamped,
                Secret = new SensitiveString("test-secret"),
                ToleranceSeconds = 300,
                TimestampField = "t",
                SignatureField = "v1"
            }
        };
        new WebhookRouteStore(_paths).Save("schema-route", route);
        var serializedRoute = JsonNode.Parse(
            File.ReadAllText(Path.Combine(_paths.WebhooksDirectory, "schema-route.json")));
        var schema = JsonSchema.FromText(LoadRouteSchema());

        var evaluation = schema.Evaluate(serializedRoute);

        Assert.True(evaluation.IsValid);
    }

    [Fact]
    public async Task Validate_NonexistentRoute_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "validate", "nonexistent"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Validate_MissingRouteName_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "validate"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Validate_InvalidRouteName_ReturnsOne()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "validate", "../secrets"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Validate_InvalidRouteWithNullVerification_ReturnsOne()
    {
        WriteRouteText("bad", """
{
  "enabled": true,
  "verification": null,
  "events": [],
  "audience": "Public",
  "prompt": "x",
  "notifyInstructions": "",
  "deliveryRequired": true,
  "notificationTarget": null,
  "maxBodyBytes": 1024,
  "rateLimitPerMinute": 1
}
""");

        var result = await WebhooksCommand.RunAsync(["webhooks", "validate", "bad"], _paths);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Help_ReturnsZero()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "help"], _paths);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task HelpFlag_ReturnsZero()
    {
        var result = await WebhooksCommand.RunAsync(["webhooks", "--help"], _paths);
        Assert.Equal(0, result);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task List_TrailingHelpFlag_PrintsHelp_AndDoesNotList(string helpToken)
    {
        // A configured route WOULD show up in `webhooks list`'s output if the command ran for
        // real, so its absence from stdout proves the help check pre-empted execution rather
        // than just happening to print a route table that also mentions "Usage".
        CreateValidRoute("test-route");

        using var stdout = new StringWriter();
        var result = await WebhooksCommand.RunAsync(["webhooks", "list", helpToken], _paths, stdout);

        Assert.Equal(0, result);
        Assert.Contains("Usage: netclaw webhooks <subcommand>", stdout.ToString());
        Assert.DoesNotContain("test-route", stdout.ToString());
    }

    [Fact]
    public async Task Set_TrailingHelpFlag_PrintsMoreSpecificSetHelp_NotGenericHelp()
    {
        // `set` has its own more specific WriteSetHelp() and must not be shadowed by the
        // generic trailing-help check added for list/show/delete/validate.
        using var stdout = new StringWriter();
        var result = await WebhooksCommand.RunAsync(["webhooks", "set", "test-route", "--help"], _paths, stdout);

        Assert.Equal(0, result);
        Assert.Contains("Usage: netclaw webhooks set <route> [options]", stdout.ToString());
    }

    private void CreateValidRoute(string routeName, string secret = "test-secret", string prompt = "Test prompt")
    {
        var route = new WebhookRouteConfig
        {
            Enabled = true,
            Prompt = prompt,
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.Hmac,
                Secret = new SensitiveString(secret)
            }
        };

        var store = new WebhookRouteStore(_paths);
        store.Save(routeName, route);
    }

    private WebhookRouteConfig ReadRoute(string routeName)
    {
        var store = new WebhookRouteStore(_paths);
        Assert.True(store.TryGet(routeName, out var match));
        Assert.NotNull(match.Definition);
        return match.Definition!;
    }

    private void WriteRouteText(string routeName, string text)
    {
        File.WriteAllText(Path.Combine(_paths.WebhooksDirectory, $"{routeName}.json"), text);
    }

    private static string LoadRouteSchema()
    {
        using var stream = typeof(EmbeddedSchemaLoader).Assembly.GetManifestResourceStream(
            "Netclaw.Configuration.Schemas.webhook-route.v1.schema.json");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    private sealed class RouteListItem
    {
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Verification { get; set; } = string.Empty;
    }
}
