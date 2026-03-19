using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.OpenAi;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OpenAiCodexTests
{
    /// <summary>
    /// Build a fake JWT from a JSON payload. No real signing — just base64url encoding.
    /// </summary>
    private static string MakeJwt(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var header = Base64UrlEncode("{}");
        var body = Base64UrlEncode(json);
        return $"{header}.{body}.fakesig";
    }

    private static string Base64UrlEncode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    // ────────────────────────────────────────────────────────────────────
    //  JwtAccountIdExtractor
    // ────────────────────────────────────────────────────────────────────

    public sealed class JwtAccountIdExtractorTests
    {
        [Fact]
        public void Extract_ReturnsOidClaim()
        {
            var jwt = MakeJwt(new { oid = "org-abc123" });

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
            var jwt = MakeJwt(new
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
            var jwt = MakeJwt(new
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
            var jwt = MakeJwt(new { sub = "user-1", email = "a@b.com" });

            var result = JwtAccountIdExtractor.Extract(jwt);

            Assert.Null(result);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  OpenAiDescriptor (merged — supports both API key and OAuth)
    // ────────────────────────────────────────────────────────────────────

    public sealed class OpenAiDescriptorTests
    {
        private readonly OpenAiDescriptor _descriptor = new(new HttpClient());

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
        public async Task ProbeAsync_WithOAuthToken_ReturnsCuratedModels()
        {
            var entry = new ProviderEntry
            {
                Type = "openai",
                AuthMethod = AuthMethod.OAuthPkce,
                OAuthAccessToken = new SensitiveString("fake-oauth-token"),
            };

            var result = await _descriptor.ProbeAsync(entry);

            Assert.True(result.Success);
            Assert.Null(result.ErrorMessage);
            Assert.NotEmpty(result.Models);
            Assert.Contains(result.Models, m => m.ModelId == "o3");

            // All curated models should have context windows and modalities populated
            Assert.All(result.Models, m =>
            {
                Assert.NotNull(m.ContextWindowTokens);
                Assert.True(m.ContextWindowTokens > 32_768,
                    $"{m.ModelId} should have context window > 32K, got {m.ContextWindowTokens}");
                Assert.True(m.InputModalities.HasFlag(ModelModality.Text | ModelModality.Image),
                    $"{m.ModelId} should accept text+image input");
            });
        }

        [Fact]
        public async Task ProbeAsync_WithoutOAuthToken_ReturnsFailure()
        {
            var entry = new ProviderEntry
            {
                Type = "openai",
                AuthMethod = AuthMethod.OAuthPkce,
            };

            var result = await _descriptor.ProbeAsync(entry);

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
            };

            var result = await descriptor.ProbeAsync(entry);

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
            var descriptor = new OpenAiDescriptor(new HttpClient(), fakeTime);

            var entry = new ProviderEntry
            {
                Type = "openai",
                AuthMethod = AuthMethod.OAuthPkce,
                OAuthAccessToken = new SensitiveString("fake-oauth-token"),
                OAuthTokenExpiry = now.AddHours(1),
            };

            var result = await descriptor.ProbeAsync(entry);

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

            var result = await _descriptor.ProbeAsync(entry);

            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
            Assert.Empty(result.Models);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  OpenAiCodexCapabilityResolver
    // ────────────────────────────────────────────────────────────────────

    public sealed class CodexCapabilityResolverTests
    {
        private readonly OpenAiCodexCapabilityResolver _resolver = new();

        [Fact]
        public async Task ResolveAsync_KnownCodexModel_ReturnsCapabilities()
        {
            var result = await _resolver.ResolveAsync("gpt-5.3-codex");

            Assert.NotNull(result);
            Assert.Equal(256_000, result.ContextWindowTokens);
            Assert.True(result.InputModalities.HasFlag(ModelModality.Text | ModelModality.Image));
            Assert.Equal(ModelModality.Text, result.OutputModalities);
        }

        [Fact]
        public async Task ResolveAsync_UnknownModel_ReturnsNull()
        {
            var result = await _resolver.ResolveAsync("claude-3-opus");

            Assert.Null(result);
        }

        [Fact]
        public async Task ResolveAsync_AllCuratedModels_HaveContextWindow()
        {
            foreach (var model in OpenAiDescriptor.CuratedModels)
            {
                var result = await _resolver.ResolveAsync(model.ModelId);
                Assert.NotNull(result);
                Assert.NotNull(result.ContextWindowTokens);
                Assert.True(result.ContextWindowTokens > 32_768);
            }
        }
    }

}
