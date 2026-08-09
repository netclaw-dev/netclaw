// -----------------------------------------------------------------------
// <copyright file="OpenAiCodexTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.OpenAi;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OpenAiCodexTests
{
    // ────────────────────────────────────────────────────────────────────
    //  JwtAccountIdExtractor
    // ────────────────────────────────────────────────────────────────────

    public sealed class JwtAccountIdExtractorTests
    {
        [Fact]
        public void Extract_ReturnsOidClaim()
        {
            var jwt = JwtTestToken.Make(new { oid = "org-abc123" });

            var result = JwtAccountIdExtractor.Extract(jwt);

            Assert.Equal("org-abc123", result);
        }

        [Fact]
        public void Extract_MalformedToken_NoDots_ReturnsNull()
        {
            var result = JwtAccountIdExtractor.Extract("not-a-jwt");

            Assert.Null(result);
        }

        [Fact]
        public void Extract_EmptyString_ReturnsNull()
        {
            var result = JwtAccountIdExtractor.Extract("");

            Assert.Null(result);
        }

        [Fact]
        public void Extract_FallsBackToOrgsArrayId()
        {
            var jwt = JwtTestToken.Make(new
            {
                orgs = new[]
                {
                    new { id = "org-fallback-42" }
                }
            });

            var result = JwtAccountIdExtractor.Extract(jwt);

            Assert.Equal("org-fallback-42", result);
        }

        [Fact]
        public void Extract_PrefersOidOverOrgs()
        {
            var jwt = JwtTestToken.Make(new
            {
                oid = "org-primary",
                orgs = new[]
                {
                    new { id = "org-secondary" }
                }
            });

            var result = JwtAccountIdExtractor.Extract(jwt);

            Assert.Equal("org-primary", result);
        }

        [Fact]
        public void Extract_NoOidNoOrgs_ReturnsNull()
        {
            var jwt = JwtTestToken.Make(new { sub = "user-1", email = "a@b.com" });

            var result = JwtAccountIdExtractor.Extract(jwt);

            Assert.Null(result);
        }

        [Fact]
        public void Extract_ReturnsNestedChatGptAccountClaim()
        {
            var jwt = JwtTestToken.Make(new Dictionary<string, object>
            {
                ["https://api.openai.com/auth"] = new Dictionary<string, object>
                {
                    ["chatgpt_account_id"] = "account-from-id-token"
                }
            });

            var result = JwtAccountIdExtractor.Extract(jwt);

            Assert.Equal("account-from-id-token", result);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  OpenAiCodexRequestPolicy
    // ────────────────────────────────────────────────────────────────────

    public sealed class OpenAiCodexRequestPolicyTests
    {
        private static readonly OAuthAuth OpenAiOAuth = new()
        {
            SupportedAuthMethods = [AuthMethod.OAuthDevice, AuthMethod.OAuthPkce],
            TokenEndpoint = new Uri("https://auth.openai.com/oauth/token"),
            DeviceEndpoint = new Uri("https://auth.openai.com/api/accounts/deviceauth/usercode"),
            ClientId = "client-id",
            UseProprietaryDeviceFlow = true,
        };

        [Fact]
        public void Process_AddsChatGptAccountIdHeader()
        {
            var policy = new OpenAiCodexRequestPolicy("account-123");
            var pipeline = ClientPipeline.Create(new ClientPipelineOptions());
            using var message = pipeline.CreateMessage();
            message.Request.Method = "POST";
            message.Request.Uri = new Uri("https://chatgpt.com/backend-api/codex/responses");

            var terminal = new TerminalPolicy();
            policy.Process(message, [policy, terminal], 0);

            Assert.True(terminal.WasCalled);
            message.Request.Headers.TryGetValue("ChatGPT-Account-Id", out var accountId);
            Assert.Equal("account-123", accountId);
        }

        [Fact]
        public async Task ProcessAsync_RefreshesExpiredOAuthBeforeApplyingAccountHeader()
        {
            using var dir = new DisposableTempDir();
            var paths = new NetclawPaths(dir.Path);
            var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
            var time = new FakeTimeProvider(now);
            var idToken = JwtTestToken.Make(new Dictionary<string, object>
            {
                ["https://api.openai.com/auth"] = new Dictionary<string, object>
                {
                    ["chatgpt_account_id"] = "account-new"
                }
            });
            var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(new
            {
                access_token = "access-new",
                refresh_token = "refresh-new",
                id_token = idToken,
                expires_in = 3600,
            }));
            var httpClient = new HttpClient(handler);
            var refreshService = new ProviderOAuthTokenRefreshService(
                paths,
                new DeviceFlowServiceFactory(
                    new OAuthDeviceFlowService(httpClient, time),
                    new OpenAiDeviceFlowService(httpClient, time)),
                NullNotificationSink.Instance,
                time);
            var entry = new ProviderEntry
            {
                Type = "openai",
                AuthMethod = AuthMethod.OAuthDevice,
                OAuthAccessToken = new SensitiveString("access-old"),
                OAuthRefreshToken = new SensitiveString("refresh-old"),
                OAuthTokenExpiry = now.AddMinutes(-1),
                OAuthAccountId = new SensitiveString("account-old"),
            };
            var credential = new ApiKeyCredential("access-old");
            var policy = new OpenAiCodexRequestPolicy(
                "openai-codex", entry, OpenAiOAuth, credential, refreshService);
            var pipeline = ClientPipeline.Create(new ClientPipelineOptions());
            using var message = pipeline.CreateMessage();
            message.Request.Method = "POST";
            message.Request.Uri = new Uri("https://chatgpt.com/backend-api/codex/responses");
            var terminal = new TerminalPolicy();

            await policy.ProcessAsync(message, [policy, terminal], 0);

            Assert.True(terminal.WasCalled);
            Assert.Equal("access-new", entry.OAuthAccessToken!.Value);
            Assert.Equal("refresh-new", entry.OAuthRefreshToken!.Value);
            message.Request.Headers.TryGetValue("ChatGPT-Account-Id", out var accountId);
            Assert.Equal("account-new", accountId);
        }

        private sealed class TerminalPolicy : PipelinePolicy
        {
            public bool WasCalled { get; private set; }

            public override void Process(
                PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
            {
                WasCalled = true;
            }

            public override ValueTask ProcessAsync(
                PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
            {
                WasCalled = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  OpenAiDescriptor (merged — supports both API key and OAuth)
    // ────────────────────────────────────────────────────────────────────

    public sealed class OpenAiDescriptorTests
    {
        private readonly OpenAiDescriptor _descriptor = new(new HttpClient(
            new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{}")
            })));

        [Fact]
        public void TypeKey_IsOpenAi()
        {
            Assert.Equal("openai", _descriptor.TypeKey);
        }

        [Fact]
        public void Auth_IsMultiAuth()
        {
            Assert.IsType<MultiAuth>(_descriptor.Auth);
        }

        [Fact]
        public void SupportedAuthMethods_ContainsBothOAuthAndApiKey()
        {
            Assert.Contains(AuthMethod.OAuthPkce, _descriptor.Auth.SupportedAuthMethods);
            Assert.Contains(AuthMethod.OAuthDevice, _descriptor.Auth.SupportedAuthMethods);
            Assert.Contains(AuthMethod.ApiKey, _descriptor.Auth.SupportedAuthMethods);
        }

        [Fact]
        public void MultiAuth_HasCustomLabels()
        {
            var multi = Assert.IsType<MultiAuth>(_descriptor.Auth);
            Assert.NotNull(multi.AuthMethodLabels);
            Assert.True(multi.AuthMethodLabels.ContainsKey(AuthMethod.OAuthPkce));
            Assert.True(multi.AuthMethodLabels.ContainsKey(AuthMethod.ApiKey));
        }

        [Fact]
        public void GetOAuthConfig_ReturnsOAuthAuth()
        {
            var oauth = _descriptor.Auth.GetOAuthConfig();
            Assert.NotNull(oauth);
            Assert.NotNull(oauth.AuthorizationEndpoint);
            Assert.NotNull(oauth.TokenEndpoint);
        }

        [Fact]
        public void GetApiKeyGuidanceUrl_ReturnsUrl()
        {
            var url = _descriptor.Auth.GetApiKeyGuidanceUrl();
            Assert.NotNull(url);
            Assert.Contains("platform.openai.com", url.AbsoluteUri);
        }

        [Fact]
        public async Task ProbeAsync_WithOAuthToken_LiveDiscoveryUnavailable_ReturnsFailure()
        {
            var entry = new ProviderEntry
            {
                Type = "openai",
                AuthMethod = AuthMethod.OAuthPkce,
                OAuthAccessToken = new SensitiveString("fake-oauth-token"),
                OAuthAccountId = new SensitiveString("account-1"),
            };

            var result = await _descriptor.ProbeAsync(entry, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
            Assert.Empty(result.Models);
        }

        [Fact]
        public async Task ProbeAsync_WithOAuthToken_QueriesCurrentCodexModelsEndpoint()
        {
            HttpRequestMessage? capturedRequest = null;
            var handler = new FakeHttpMessageHandler(request =>
            {
                capturedRequest = request;
                return FakeHttpMessageHandler.JsonResponse(new
                {
                    models = new object[]
                    {
                        new
                        {
                            slug = "gpt-5.6-sol",
                            visibility = "list",
                            context_window = 272000,
                            input_modalities = new[] { "text", "image" },
                        },
                        new
                        {
                            slug = "gpt-5.6-sol-wm",
                            visibility = "hide",
                            context_window = 123,
                        }
                    }
                });
            });
            var descriptor = new OpenAiDescriptor(new HttpClient(handler));
            var entry = new ProviderEntry
            {
                Type = "openai",
                AuthMethod = AuthMethod.OAuthDevice,
                OAuthAccessToken = new SensitiveString("oauth-token"),
                OAuthAccountId = new SensitiveString("account-1"),
            };

            var result = await descriptor.ProbeAsync(entry, TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Null(result.ErrorMessage);
            var model = Assert.Single(result.Models);
            Assert.Equal("gpt-5.6-sol", model.ModelId.Value);
            Assert.Equal(272000, model.ContextWindowTokens);
            Assert.Equal(ModelModality.Text | ModelModality.Image, model.InputModalities);
            // The catalog row omits output_modalities; discovery reports it as unknown
            // (null) rather than guessing Text, so the daemon resolves it at runtime
            // instead of a guess being persisted as a permanent override (#1290).
            Assert.Null(model.OutputModalities);

            Assert.NotNull(capturedRequest);
            Assert.Equal(
                $"{OpenAiDescriptor.CodexBackendEndpoint}/models?client_version=0.147.0",
                capturedRequest!.RequestUri!.ToString());
            Assert.Equal("Bearer", capturedRequest.Headers.Authorization!.Scheme);
            Assert.Equal("oauth-token", capturedRequest.Headers.Authorization.Parameter);
            Assert.True(capturedRequest.Headers.TryGetValues("ChatGPT-Account-Id", out var accountIds));
            Assert.Equal("account-1", Assert.Single(accountIds));
        }

        [Fact]
        public void ParseCodexModels_IncompleteContextWindow_ReturnsFailure()
        {
            var json = """
                       {
                         "models": [
                           { "slug": "future-model", "visibility": "list", "input_modalities": ["text"] }
                         ]
                       }
                       """;

            var result = OpenAiDescriptor.ParseCodexModels(json);

            Assert.False(result.Success);
            Assert.Contains("incomplete context-window metadata", result.ErrorMessage);
            Assert.Empty(result.Models);
        }

        [Fact]
        public void ParseCodexModels_IncompleteInputModalities_ReturnsFailure()
        {
            var json = """
                       {
                         "models": [
                           { "slug": "future-model", "visibility": "list", "context_window": 272000 }
                         ]
                       }
                       """;

            var result = OpenAiDescriptor.ParseCodexModels(json);

            Assert.False(result.Success);
            Assert.Contains("incomplete input-modality metadata", result.ErrorMessage);
            Assert.Empty(result.Models);
        }

        [Fact]
        public async Task ProbeAsync_WithLegacyJwtOAuthToken_UsesJwtAccountIdAndReturnsProbeFailure()
        {
            var entry = new ProviderEntry
            {
                Type = "openai",
                AuthMethod = AuthMethod.OAuthPkce,
                OAuthAccessToken = new SensitiveString(JwtTestToken.Make(new { oid = "legacy-account" })),
            };

            var result = await _descriptor.ProbeAsync(entry, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
            Assert.Empty(result.Models);
        }

        [Fact]
        public async Task ProbeAsync_WithOpaqueOAuthTokenMissingAccountId_ReturnsFailure()
        {
            var entry = new ProviderEntry
            {
                Type = "openai",
                AuthMethod = AuthMethod.OAuthPkce,
                OAuthAccessToken = new SensitiveString("opaque-oauth-token"),
            };

            var result = await _descriptor.ProbeAsync(entry, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Contains("account ID", result.ErrorMessage);
        }

        [Fact]
        public async Task ProbeAsync_WithoutOAuthToken_ReturnsFailure()
        {
            var entry = new ProviderEntry
            {
                Type = "openai",
                AuthMethod = AuthMethod.OAuthPkce,
            };

            var result = await _descriptor.ProbeAsync(entry, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
            Assert.Empty(result.Models);
        }

        [Fact]
        public async Task ProbeAsync_WithExpiredToken_ReturnsFailure()
        {
            var now = new DateTimeOffset(2026, 3, 18, 12, 0, 0, TimeSpan.Zero);
            var fakeTime = new FakeTimeProvider(now);
            var descriptor = new OpenAiDescriptor(new HttpClient(), fakeTime);

            var entry = new ProviderEntry
            {
                Type = "openai",
                AuthMethod = AuthMethod.OAuthPkce,
                OAuthAccessToken = new SensitiveString("fake-oauth-token"),
                OAuthTokenExpiry = now.AddHours(-1),
                OAuthAccountId = new SensitiveString("account-1"),
            };

            var result = await descriptor.ProbeAsync(entry, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Contains("expired", result.ErrorMessage);
            Assert.Contains("netclaw provider fix", result.ErrorMessage);
            Assert.Empty(result.Models);
        }

        [Fact]
        public async Task ProbeAsync_WithFutureExpiry_ReturnsSuccess()
        {
            var now = new DateTimeOffset(2026, 3, 18, 12, 0, 0, TimeSpan.Zero);
            var fakeTime = new FakeTimeProvider(now);
            var descriptor = new OpenAiDescriptor(new HttpClient(new FakeHttpMessageHandler(_ =>
                FakeHttpMessageHandler.JsonResponse(new
                {
                    models = new object[]
                    {
                        new
                        {
                            slug = "gpt-live-codex",
                            visibility = "list",
                            context_window = 272000,
                            input_modalities = new[] { "text", "image" },
                        }
                    }
                }))), fakeTime);

            var entry = new ProviderEntry
            {
                Type = "openai",
                AuthMethod = AuthMethod.OAuthPkce,
                OAuthAccessToken = new SensitiveString("fake-oauth-token"),
                OAuthTokenExpiry = now.AddHours(1),
                OAuthAccountId = new SensitiveString("account-1"),
            };

            var result = await descriptor.ProbeAsync(entry, TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.NotEmpty(result.Models);
        }

        [Fact]
        public async Task ProbeAsync_WithoutApiKey_ReturnsFailure()
        {
            var entry = new ProviderEntry
            {
                Type = "openai",
                AuthMethod = AuthMethod.ApiKey,
            };

            var result = await _descriptor.ProbeAsync(entry, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
            Assert.Empty(result.Models);
        }
    }

}
