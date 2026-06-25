// -----------------------------------------------------------------------
// <copyright file="CopilotTokenExchangerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers.GitHubCopilot;

public sealed class CopilotTokenExchangerTests
{
    private static readonly OAuthAuth PublicOAuth = new()
    {
        SupportedAuthMethods = [AuthMethod.OAuthDevice],
        TokenEndpoint = new Uri("https://github.com/login/oauth/access_token"),
        DeviceEndpoint = new Uri("https://github.com/login/device/code"),
        ClientId = "copilot-client",
        Scope = "read:user",
        UseProprietaryDeviceFlow = false,
    };

    private static ProviderEntry EntryWithOAuth(string token) =>
        new()
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString(token),
        };

    private static ProviderEntry ExpiredEntryWithOAuth(
        DateTimeOffset now,
        string accessToken = "gho_old",
        string? refreshToken = "refresh-old") =>
        new()
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = new SensitiveString(accessToken),
            OAuthRefreshToken = refreshToken is null ? null : new SensitiveString(refreshToken),
            OAuthTokenExpiry = now.AddMinutes(-1),
        };

    private static HttpResponseMessage TokenResponse(string token, long expiresAt) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { token, expires_at = expiresAt }),
                Encoding.UTF8,
                "application/json"),
        };

    [Fact]
    public async Task GetToken_FirstCall_FetchesAndCaches()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return TokenResponse("copilot-token-1",
                DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds());
        });
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));

        var token = await exchanger.GetTokenAsync(EntryWithOAuth("gho_1"),
            TestContext.Current.CancellationToken);

        Assert.Equal("copilot-token-1", token);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetToken_NamedProvider_ExpiredOAuthToken_RefreshesBeforeTokenExchange()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        WriteProviderConfig(paths, "copilot");

        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var requestUris = new List<string>();
        string? exchangeAuthorization = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            requestUris.Add(request.RequestUri!.ToString());
            return request.RequestUri!.ToString() switch
            {
                "https://github.com/login/oauth/access_token" => FakeHttpMessageHandler.JsonResponse(new
                {
                    access_token = "gho_new",
                    refresh_token = "refresh-new",
                    expires_in = 3600,
                }),
                "https://api.github.com/copilot_internal/v2/token" => CaptureExchangeRequest(request),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        var service = CreateRefreshService(paths, time, handler);
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler), time, service);
        var entry = ExpiredEntryWithOAuth(now);

        var token = await exchanger.GetTokenAsync(
            "copilot",
            entry,
            PublicOAuth,
            TestContext.Current.CancellationToken);

        Assert.Equal("copilot-token", token);
        Assert.Equal("gho_new", entry.OAuthAccessToken!.Value);
        Assert.Equal("refresh-new", entry.OAuthRefreshToken!.Value);
        Assert.Equal(now.AddHours(1), entry.OAuthTokenExpiry);
        Assert.Equal("Bearer gho_new", exchangeAuthorization);
        Assert.Equal([
            "https://github.com/login/oauth/access_token",
            "https://api.github.com/copilot_internal/v2/token",
        ], requestUris);

        using var secretsDoc = JsonDocument.Parse(File.ReadAllText(paths.SecretsPath));
        var secretProvider = secretsDoc.RootElement.GetProperty("Providers").GetProperty("copilot");
        Assert.Equal("gho_new", secretProvider.GetProperty("OAuthAccessToken").GetString());
        Assert.Equal("refresh-new", secretProvider.GetProperty("OAuthRefreshToken").GetString());

        using var configDoc = JsonDocument.Parse(File.ReadAllText(paths.NetclawConfigPath));
        var configProvider = configDoc.RootElement.GetProperty("Providers").GetProperty("copilot");
        Assert.Equal(now.AddHours(1), DateTimeOffset.Parse(configProvider.GetProperty("OAuthTokenExpiry").GetString()!));

        HttpResponseMessage CaptureExchangeRequest(HttpRequestMessage request)
        {
            exchangeAuthorization = request.Headers.Authorization!.ToString();
            return TokenResponse("copilot-token", now.AddMinutes(30).ToUnixTimeSeconds());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetToken_NamedProvider_RefreshFailure_ThrowsBeforeTokenExchange(bool hasRefreshToken)
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.ToString() switch
            {
                "https://github.com/login/oauth/access_token" when hasRefreshToken => FakeHttpMessageHandler.JsonResponse(
                    new { error = "invalid_grant" }, HttpStatusCode.BadRequest),
                "https://api.github.com/copilot_internal/v2/token" => throw new InvalidOperationException(
                    "token exchange should not run after failed refresh"),
                _ => throw new InvalidOperationException("unexpected HTTP request"),
            });
        var service = CreateRefreshService(paths, time, handler);
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler), time, service);

        var ex = await Assert.ThrowsAsync<ProviderOAuthRefreshRequiredException>(() =>
            exchanger.GetTokenAsync(
                "copilot",
                ExpiredEntryWithOAuth(now, refreshToken: hasRefreshToken ? "refresh-old" : null),
                PublicOAuth,
                TestContext.Current.CancellationToken));

        Assert.Contains("provider fix copilot", ex.Message);
    }

    [Theory]
    [InlineData(25, "copilot-token-1", 1)]
    [InlineData(29, "copilot-token-2", 2)]
    public async Task GetToken_UsesRefreshBuffer(int advanceMinutes, string expectedToken, int expectedCalls)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return TokenResponse(
                $"copilot-token-{callCount}",
                time.GetUtcNow().AddMinutes(30).ToUnixTimeSeconds());
        });
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler), time);

        var entry = EntryWithOAuth("gho_1");
        await exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(advanceMinutes));
        var token = await exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken);

        Assert.Equal(expectedToken, token);
        Assert.Equal(expectedCalls, callCount);
    }

    [Fact]
    public async Task GetToken_DistinctOAuthTokens_CacheSeparately()
    {
        var requestsByToken = new Dictionary<string, int>();
        var handler = new FakeHttpMessageHandler(request =>
        {
            var auth = request.Headers.GetValues("Authorization").First();
            requestsByToken[auth] = requestsByToken.GetValueOrDefault(auth) + 1;
            return TokenResponse($"copilot-for-{auth}",
                DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds());
        });
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));

        await exchanger.GetTokenAsync(EntryWithOAuth("gho_A"),
            TestContext.Current.CancellationToken);
        await exchanger.GetTokenAsync(EntryWithOAuth("gho_B"),
            TestContext.Current.CancellationToken);
        await exchanger.GetTokenAsync(EntryWithOAuth("gho_A"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, requestsByToken["Bearer gho_A"]);
        Assert.Equal(1, requestsByToken["Bearer gho_B"]);
    }

    [Fact]
    public async Task GetToken_Unauthorized_ThrowsCopilotAuthExpired()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));

        await Assert.ThrowsAsync<CopilotAuthExpiredException>(() =>
            exchanger.GetTokenAsync(EntryWithOAuth("gho_revoked"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetToken_ServerError_ThrowsInvalidOperationWithDetail()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream offline", Encoding.UTF8, "text/plain"),
        });
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exchanger.GetTokenAsync(EntryWithOAuth("gho_1"),
                TestContext.Current.CancellationToken));

        Assert.Contains("HTTP 502", ex.Message);
        Assert.Contains("upstream offline", ex.Message);
    }

    [Fact]
    public async Task GetToken_MalformedTokenPayload_ThrowsAtExchangeBoundary()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exchanger.GetTokenAsync(EntryWithOAuth("gho_1"),
                TestContext.Current.CancellationToken));

        Assert.Contains("no 'token' field", ex.Message);
    }

    [Fact]
    public async Task GetToken_MissingOAuthToken_Throws()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            TokenResponse("never-called", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()));
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));

        var entry = new ProviderEntry
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.OAuthDevice,
            OAuthAccessToken = null,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetToken_TokenRequest_SendsEditorIntegrationContract()
    {
        // The exchange endpoint gates on the editor-integration header set —
        // Copilot-Integration-Id in particular is what makes GitHub's gateway
        // route through the Copilot permission model. Missing any of these
        // headers returns HTTP 403 "Resource not accessible by integration"
        // even with a valid OAuth-App user-to-server token. Lock this set in
        // so a future cleanup pass doesn't silently regress the integration.
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            captured = request;
            return TokenResponse("copilot",
                DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds());
        });
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));

        await exchanger.GetTokenAsync(EntryWithOAuth("ghu_oauth"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("https://api.github.com/copilot_internal/v2/token",
            captured!.RequestUri!.ToString());
        Assert.Equal("Bearer ghu_oauth",
            captured.Headers.GetValues("Authorization").Single());
        Assert.Contains(captured.Headers.Accept, h => h.MediaType == "application/json");

        Assert.Equal(NetclawUserAgent.Value,
            string.Join(" ", captured.Headers.GetValues("User-Agent")));
        Assert.Equal("copilot-token",
            captured.Headers.GetValues(NetclawUserAgent.ComponentHeader).Single());

        Assert.Equal($"Netclaw/{BuildInfo.Version}",
            captured.Headers.GetValues("Editor-Version").Single());
        Assert.Equal($"netclaw/{BuildInfo.Version}",
            captured.Headers.GetValues("Editor-Plugin-Version").Single());
        Assert.Equal("vscode-chat", captured.Headers.GetValues("Copilot-Integration-Id").Single());
        Assert.Equal("2022-11-28", captured.Headers.GetValues("X-GitHub-Api-Version").Single());
    }

    [Fact]
    public async Task GetAccessToken_TokenResponseWithEndpoints_ReturnsCopilotApiBase()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    token = "copilot-ghe",
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
                    endpoints = new
                    {
                        api = "https://prod-sdc-01.api.githubcopilot.com/chat/completions",
                        telemetry = "https://telemetry.example.invalid",
                    },
                }),
                Encoding.UTF8,
                "application/json"),
        });
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));

        var token = await exchanger.GetAccessTokenAsync(EntryWithOAuth("gho_ghe"),
            TestContext.Current.CancellationToken);

        Assert.Equal("copilot-ghe", token.Token);
        Assert.Equal("https://prod-sdc-01.api.githubcopilot.com/", token.CopilotApiBase?.ToString());
    }

    [Fact]
    public async Task GetToken_UsesConfiguredGitHubApiBaseAndExchangePath()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            captured = request;
            return TokenResponse("copilot-ghe",
                DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds());
        });
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));
        var entry = EntryWithOAuth("gho_ghe");
        entry.SetVendorOptions(new JsonObject
        {
            ["GitHubApiBase"] = "https://api.example.ghe.com",
            ["CopilotTokenExchangePath"] = "/enterprise/copilot/token",
        });

        var token = await exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken);

        Assert.Equal("copilot-ghe", token);
        Assert.Equal("https://api.example.ghe.com/enterprise/copilot/token",
            captured!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetToken_EnvironmentMode_UsesOfficialEnvPrecedence()
    {
        var previousCopilot = Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN");
        var previousGh = Environment.GetEnvironmentVariable("GH_TOKEN");
        var previousGitHub = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("COPILOT_GITHUB_TOKEN", "github_pat_copilot");
            Environment.SetEnvironmentVariable("GH_TOKEN", "github_pat_gh");
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", "github_pat_github");

            string? authorization = null;
            var handler = new FakeHttpMessageHandler(request =>
            {
                authorization = request.Headers.Authorization!.ToString();
                return TokenResponse("copilot-env",
                    DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds());
            });
            var exchanger = new CopilotTokenExchanger(new HttpClient(handler));
            var entry = new ProviderEntry
            {
                Type = "github-copilot",
                AuthMethod = AuthMethod.ApiKey,
            };
            entry.SetVendorOptions(new JsonObject
            {
                ["AuthMode"] = "Environment",
            });

            var token = await exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken);

            Assert.Equal("copilot-env", token);
            Assert.Equal("Bearer github_pat_copilot", authorization);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COPILOT_GITHUB_TOKEN", previousCopilot);
            Environment.SetEnvironmentVariable("GH_TOKEN", previousGh);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previousGitHub);
        }
    }

    [Fact]
    public async Task GetToken_NamedProvider_GheOptions_RefreshesAgainstGitHubHostAndExchangesAgainstApiBase()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        WriteProviderConfig(paths, "copilot-ghe");

        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var requestUris = new List<string>();
        var handler = new FakeHttpMessageHandler(request =>
        {
            requestUris.Add(request.RequestUri!.ToString());
            return request.RequestUri!.ToString() switch
            {
                "https://my-company-ghe.ghe.com/login/oauth/access_token" => FakeHttpMessageHandler.JsonResponse(new
                {
                    access_token = "gho_ghe_new",
                    refresh_token = "refresh-ghe-new",
                    expires_in = 3600,
                }),
                "https://api.my-company-ghe.ghe.com/copilot_internal/v2/token" => TokenResponse(
                    "copilot-ghe",
                    now.AddMinutes(30).ToUnixTimeSeconds()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        var service = CreateRefreshService(paths, time, handler);
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler), time, service);
        var entry = ExpiredEntryWithOAuth(now);
        entry.SetVendorOptions(new JsonObject
        {
            ["GitHubHost"] = "https://my-company-ghe.ghe.com",
            ["GitHubApiBase"] = "https://api.my-company-ghe.ghe.com",
        });
        var oauth = GitHubCopilotDescriptor.CreateOAuthAuth(
            GitHubCopilotDescriptor.ResolveOptions(entry));

        var token = await exchanger.GetTokenAsync(
            "copilot-ghe",
            entry,
            oauth,
            TestContext.Current.CancellationToken);

        Assert.Equal("copilot-ghe", token);
        Assert.Equal([
            "https://my-company-ghe.ghe.com/login/oauth/access_token",
            "https://api.my-company-ghe.ghe.com/copilot_internal/v2/token",
        ], requestUris);
    }

    [Fact]
    public async Task GetToken_HostEnvironmentDefaults_ConfiguresExchangeEndpoint()
    {
        var previousHost = Environment.GetEnvironmentVariable("GH_HOST");
        var previousApi = Environment.GetEnvironmentVariable("GITHUB_API_URL");
        try
        {
            Environment.SetEnvironmentVariable("GH_HOST", "my-company-ghe.ghe.com");
            Environment.SetEnvironmentVariable("GITHUB_API_URL", "https://api.my-company-ghe.ghe.com");

            HttpRequestMessage? captured = null;
            var handler = new FakeHttpMessageHandler(request =>
            {
                captured = request;
                return TokenResponse("copilot-env-host",
                    DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds());
            });
            var exchanger = new CopilotTokenExchanger(new HttpClient(handler));
            var entry = new ProviderEntry
            {
                Type = "github-copilot",
                AuthMethod = AuthMethod.ApiKey,
                ApiKey = new SensitiveString("github_pat_configured"),
            };

            var token = await exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken);

            Assert.Equal("copilot-env-host", token);
            Assert.Equal("https://api.my-company-ghe.ghe.com/copilot_internal/v2/token",
                captured!.RequestUri!.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_HOST", previousHost);
            Environment.SetEnvironmentVariable("GITHUB_API_URL", previousApi);
        }
    }

    [Fact]
    public async Task GetToken_GheEnvironmentDefaults_KeepCopilotApiSeparateFromGitHubApi()
    {
        var names = new[]
        {
            "GHE_HOST",
            "GH_HOST",
            "COPILOT_GH_HOST",
            "GITHUB_SERVER_URL",
            "GITHUB_API_URL",
        };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("GHE_HOST", "example.ghe.com");
            Environment.SetEnvironmentVariable("GH_HOST", "example.ghe.com");
            Environment.SetEnvironmentVariable("COPILOT_GH_HOST", "example.ghe.com");
            Environment.SetEnvironmentVariable("GITHUB_SERVER_URL", "https://example.ghe.com");
            Environment.SetEnvironmentVariable("GITHUB_API_URL", "https://api.example.ghe.com");

            HttpRequestMessage? captured = null;
            var handler = new FakeHttpMessageHandler(request =>
            {
                captured = request;
                return TokenResponse("copilot-ghe-env",
                    DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds());
            });
            var exchanger = new CopilotTokenExchanger(new HttpClient(handler));
            var entry = new ProviderEntry
            {
                Type = "github-copilot",
                AuthMethod = AuthMethod.ApiKey,
                ApiKey = new SensitiveString("github_pat_configured"),
            };

            var token = await exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken);
            var options = GitHubCopilotDescriptor.ResolveOptions(entry);

            Assert.Equal("copilot-ghe-env", token);
            Assert.Equal("https://api.example.ghe.com/copilot_internal/v2/token",
                captured!.RequestUri!.ToString());
            Assert.Equal(new Uri("https://api.githubcopilot.com"), options.CopilotApiBase);
            Assert.Equal(new Uri("https://example.ghe.com"), options.GitHubHost);
            Assert.Equal(new Uri("https://api.example.ghe.com"), options.GitHubApiBase);
        }
        finally
        {
            foreach (var (name, value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Theory]
    [InlineData("ghp_classic")]
    [InlineData("not-a-github-token")]
    public async Task GetToken_UnsupportedTokenType_ThrowsBeforeExchange(string token)
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("token exchange should not run"));
        var exchanger = new CopilotTokenExchanger(new HttpClient(handler));
        var entry = new ProviderEntry
        {
            Type = "github-copilot",
            AuthMethod = AuthMethod.ApiKey,
            ApiKey = new SensitiveString(token),
        };
        entry.SetVendorOptions(new JsonObject
        {
            ["AuthMode"] = "ApiKey",
        });

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetToken_EnvironmentMode_MissingEnvToken_ThrowsBeforeExchange()
    {
        var previousCopilot = Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN");
        var previousGh = Environment.GetEnvironmentVariable("GH_TOKEN");
        var previousGitHub = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("COPILOT_GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

            var handler = new FakeHttpMessageHandler(_ =>
                throw new InvalidOperationException("token exchange should not run"));
            var exchanger = new CopilotTokenExchanger(new HttpClient(handler));
            var entry = new ProviderEntry
            {
                Type = "github-copilot",
                AuthMethod = AuthMethod.ApiKey,
            };
            entry.SetVendorOptions(new JsonObject
            {
                ["AuthMode"] = "Environment",
            });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                exchanger.GetTokenAsync(entry, TestContext.Current.CancellationToken));

            Assert.Contains("No GitHub token found", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COPILOT_GITHUB_TOKEN", previousCopilot);
            Environment.SetEnvironmentVariable("GH_TOKEN", previousGh);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previousGitHub);
        }
    }

    private static ProviderOAuthTokenRefreshService CreateRefreshService(
        NetclawPaths paths,
        FakeTimeProvider time,
        FakeHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var factory = new DeviceFlowServiceFactory(
            new OAuthDeviceFlowService(httpClient, time),
            new OpenAiDeviceFlowService(httpClient, time));

        return new ProviderOAuthTokenRefreshService(paths, factory, timeProvider: time);
    }

    private static void WriteProviderConfig(NetclawPaths paths, string providerName)
    {
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, $$"""
            {
              "configVersion": 1,
              "Providers": {
                "{{providerName}}": {
                  "Type": "github-copilot",
                  "AuthMethod": "OAuthDevice"
                }
              }
            }
            """);
    }
}
