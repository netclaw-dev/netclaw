using System.Text;
using System.Text.Json;
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
    //  OpenAiCodexDescriptor
    // ────────────────────────────────────────────────────────────────────

    public sealed class CodexDescriptorTests
    {
        private readonly OpenAiCodexDescriptor _descriptor = new();

        [Fact]
        public void TypeKey_IsOpenAiCodex()
        {
            Assert.Equal("openai-codex", _descriptor.TypeKey);
        }

        [Fact]
        public void SupportedAuthMethods_ContainsOnlyOAuthPkce()
        {
            Assert.Single(_descriptor.SupportedAuthMethods);
            Assert.Equal(AuthMethod.OAuthPkce, _descriptor.SupportedAuthMethods[0]);
        }

        [Fact]
        public void DefaultEndpoint_IsCodexBackend()
        {
            Assert.Equal("https://chatgpt.com/backend-api/codex", _descriptor.DefaultEndpoint);
        }

        [Fact]
        public async Task ProbeAsync_WithOAuthToken_ReturnsCuratedModels()
        {
            var entry = new ProviderEntry
            {
                Type = "openai-codex",
                AuthMethod = AuthMethod.OAuthPkce,
                OAuthAccessToken = new SensitiveString("fake-oauth-token"),
            };

            var result = await _descriptor.ProbeAsync(entry);

            Assert.True(result.Success);
            Assert.Null(result.ErrorMessage);
            Assert.NotEmpty(result.Models);
            Assert.Contains(result.Models, m => m.ModelId == "o3");
        }

        [Fact]
        public async Task ProbeAsync_WithoutOAuthToken_ReturnsFailure()
        {
            var entry = new ProviderEntry
            {
                Type = "openai-codex",
                AuthMethod = AuthMethod.OAuthPkce,
            };

            var result = await _descriptor.ProbeAsync(entry);

            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
            Assert.Empty(result.Models);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  OpenAiDescriptor (post-bifurcation)
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
        public void SupportedAuthMethods_ContainsOnlyApiKey()
        {
            Assert.Single(_descriptor.SupportedAuthMethods);
            Assert.Equal(AuthMethod.ApiKey, _descriptor.SupportedAuthMethods[0]);
        }

        [Fact]
        public void OAuthDeviceEndpoint_IsNull()
        {
            Assert.Null(_descriptor.OAuthDeviceEndpoint);
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
    //  OpenAiCodexConfigMigration
    // ────────────────────────────────────────────────────────────────────

    public sealed class ConfigMigrationTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly NetclawPaths _paths;

        public ConfigMigrationTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
            _paths = new NetclawPaths(_tempDir);
            Directory.CreateDirectory(_paths.ConfigDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        [Fact]
        public void MigrateIfNeeded_OpenAiWithOAuthPkce_MigratesToOpenAiCodex()
        {
            WriteConfig("""
            {
              "Providers": {
                "my-openai": {
                  "Type": "openai",
                  "Endpoint": "https://api.openai.com",
                  "AuthMethod": "OAuthPkce"
                }
              }
            }
            """);

            var migrated = OpenAiCodexConfigMigration.MigrateIfNeeded(_paths);

            Assert.True(migrated);

            var json = File.ReadAllText(_paths.NetclawConfigPath);
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement
                .GetProperty("Providers")
                .GetProperty("my-openai")
                .GetProperty("Type")
                .GetString();

            Assert.Equal("openai-codex", type);
        }

        [Fact]
        public void MigrateIfNeeded_OpenAiWithApiKey_DoesNotMigrate()
        {
            WriteConfig("""
            {
              "Providers": {
                "my-openai": {
                  "Type": "openai",
                  "Endpoint": "https://api.openai.com",
                  "AuthMethod": "ApiKey"
                }
              }
            }
            """);

            var migrated = OpenAiCodexConfigMigration.MigrateIfNeeded(_paths);

            Assert.False(migrated);

            var json = File.ReadAllText(_paths.NetclawConfigPath);
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement
                .GetProperty("Providers")
                .GetProperty("my-openai")
                .GetProperty("Type")
                .GetString();

            Assert.Equal("openai", type);
        }

        [Fact]
        public void MigrateIfNeeded_AnthropicWithOAuthDevice_DoesNotMigrate()
        {
            WriteConfig("""
            {
              "Providers": {
                "my-anthropic": {
                  "Type": "anthropic",
                  "Endpoint": "https://api.anthropic.com",
                  "AuthMethod": "OAuthDevice"
                }
              }
            }
            """);

            var migrated = OpenAiCodexConfigMigration.MigrateIfNeeded(_paths);

            Assert.False(migrated);

            var json = File.ReadAllText(_paths.NetclawConfigPath);
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement
                .GetProperty("Providers")
                .GetProperty("my-anthropic")
                .GetProperty("Type")
                .GetString();

            Assert.Equal("anthropic", type);
        }

        [Fact]
        public void MigrateIfNeeded_MissingConfigFile_ReturnsFalse()
        {
            // Don't write any config file — directory exists but file does not
            var migrated = OpenAiCodexConfigMigration.MigrateIfNeeded(_paths);

            Assert.False(migrated);
        }

        private void WriteConfig(string json)
        {
            File.WriteAllText(_paths.NetclawConfigPath, json);
        }
    }
}
