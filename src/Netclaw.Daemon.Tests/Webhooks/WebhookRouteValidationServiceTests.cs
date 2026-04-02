using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Webhooks;
using Xunit;

namespace Netclaw.Daemon.Tests.Webhooks;

public sealed class WebhookRouteValidationServiceTests
{
    [Fact]
    public async Task StartAsync_throws_when_prompt_is_missing()
    {
        var config = new WebhooksConfig
        {
            Enabled = true,
            Routes = new Dictionary<string, WebhookRouteConfig>
            {
                ["github-issues"] = new()
                {
                    Verification = new WebhookVerificationConfig
                    {
                        Secret = new SensitiveString("secret")
                    }
                }
            }
        };

        var sut = new WebhookRouteValidationService(config, NullLogger<WebhookRouteValidationService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_throws_when_required_policy_has_no_target()
    {
        var config = new WebhooksConfig
        {
            Enabled = true,
            Routes = new Dictionary<string, WebhookRouteConfig>
            {
                ["github-issues"] = new()
                {
                    Prompt = "triage",
                    NotifyPolicy = NotificationPolicy.Required,
                    Verification = new WebhookVerificationConfig
                    {
                        Secret = new SensitiveString("secret")
                    }
                }
            }
        };

        var sut = new WebhookRouteValidationService(config, NullLogger<WebhookRouteValidationService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.StartAsync(TestContext.Current.CancellationToken));
    }
}
