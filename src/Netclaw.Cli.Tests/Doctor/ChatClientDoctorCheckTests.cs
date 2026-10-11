// -----------------------------------------------------------------------
// <copyright file="ChatClientDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using Netclaw.Cli;
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Provider;
using Netclaw.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

[Collection(Netclaw.Cli.Tests.LegacyModelEnvironmentCollection.Name)]
public sealed class ChatClientDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ReturnsWarning_WhenNoProvidersConfigured()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("No-Op chat client", result.Message);
        Assert.Contains("netclaw init", result.Remediation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("netclaw provider add", result.Remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsPass_WhenProvidersAndModelsAreValid()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "openrouter": { "Type": "openrouter", "AuthMethod": "ApiKey" }
              },
              "Models": {
                "Main": { "Provider": "openrouter", "ModelId": "anthropic/claude-haiku-4" }
              }
            }
            """);
        WriteSecrets(paths, """
            {
              "Providers": {
                "openrouter": { "ApiKey": "sk-test" }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("openrouter", result.Message);
    }

    [Fact]
    public async Task ReturnsWarning_WhenProviderExistsButMainModelMissing()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "local-ollama": { "Type": "ollama" }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Models:Main missing", result.Message);
        Assert.DoesNotContain("Real chat client configured", result.Message);
        Assert.Contains("netclaw model", result.Remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsWarning_WhenMainModelSectionHasNoProviderOrModel()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "local-ollama": { "Type": "ollama" }
              },
              "Models": {
                "Main": { }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("no model selected", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsError_WhenReferencedProviderMissingType()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "openrouter": { }
              },
              "Models": {
                "Main": { "Provider": "openrouter", "ModelId": "anthropic/claude-haiku-4" }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("missing required Type", result.Message);
    }

    [Fact]
    public async Task ReturnsError_WhenFallbackReferencesUnknownProvider()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "openrouter": { "Type": "openrouter" }
              },
              "Models": {
                "Main": { "Provider": "openrouter", "ModelId": "anthropic/claude-haiku-4" },
                "Fallback": { "Provider": "missing", "ModelId": "qwen3:30b" }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("Fallback", result.Message);
        Assert.Contains("missing", result.Message);
    }

    [Fact]
    public async Task ReturnsWarning_WhenModelReferencesUnknownProvider()
    {
        // Regression: a typo in Models:Main.Provider (e.g. operator typed
        // "ollama-local1" instead of "ollama-local") used to crash the daemon
        // with an unhandled ProviderPluginFactory exception. We now treat it
        // as degraded mode — same remediation as genuinely-no-provider —
        // and surface the typo via the No-Op banner's available-providers
        // line and this doctor warning.
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "ollama-local": { "Type": "ollama" }
              },
              "Models": {
                "Main": { "Provider": "ollama-local1", "ModelId": "qwen3:30b" }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("No-Op chat client", result.Message);
        Assert.Contains("ollama-local1", result.Message);
        Assert.Contains("ollama-local", result.Message);
    }

    [Fact]
    public async Task ReturnsWarning_WhenConfigFileMissing()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        // Missing config short-circuits via DoctorJsonConfigReader and yields its own
        // warning — the chat-client check is not reached.
        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Equal(CliConfigPreflight.MissingConfigMessage, result.Message);
    }

    [Fact]
    public async Task ReturnsError_WhenKnownApiKeyProviderHasNoApiKey()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "openrouter": { "Type": "openrouter", "AuthMethod": "ApiKey" }
              },
              "Models": {
                "Main": { "Provider": "openrouter", "ModelId": "anthropic/claude-haiku-4" }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("requires ApiKey", result.Message);
        Assert.DoesNotContain("Real chat client configured", result.Message);
    }

    [Fact]
    public async Task ReturnsError_WhenOptionalApiKeyProviderSelectsApiKeyWithoutKey()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "local-vllm": { "Type": "openai-compatible", "AuthMethod": "ApiKey" }
              },
              "Models": {
                "Main": { "Provider": "local-vllm", "ModelId": "model-a" }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("requires ApiKey", result.Message);
        Assert.DoesNotContain("Real chat client configured", result.Message);
    }

    [Fact]
    public async Task ReturnsPass_WhenOptionalApiKeyProviderSelectsNoneWithoutKey()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "local-vllm": { "Type": "openai-compatible", "AuthMethod": "None" }
              },
              "Models": {
                "Main": { "Provider": "local-vllm", "ModelId": "model-a" }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("Real chat client configured", result.Message);
    }

    [Fact]
    public async Task ReturnsError_WhenProviderTypeUnknown()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "custom": { "Type": "not-a-provider" }
              },
              "Models": {
                "Main": { "Provider": "custom", "ModelId": "model-a" }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("unknown Type", result.Message);
        Assert.Contains("not-a-provider", result.Message);
    }

    [Fact]
    public async Task ReturnsError_WhenProviderTypeValueIsNotString()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "openrouter": { "Type": 123 }
              },
              "Models": {
                "Main": { "Provider": "openrouter", "ModelId": "anthropic/claude-haiku-4" }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("Providers.openrouter.Type", result.Message);
        Assert.Contains("must be a string", result.Message);
    }

    [Fact]
    public async Task ReturnsError_WhenDefinitionProviderValueIsNotString()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "local-ollama": { "Type": "ollama" }
              },
              "Models": {
                "Definitions": { "fast": { "Provider": 123, "ModelId": "qwen3:30b" } },
                "Roles": { "Main": "fast" }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("Models.Definitions.fast.Provider", result.Message);
        Assert.Contains("must be a string", result.Message);
    }

    [Fact]
    public async Task ReturnsError_NamingTheConflictAndSayingTheDaemonWillNotStart_WhenModelsMixShapes()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "local-ollama": { "Type": "ollama" }
              },
              "Models": {
                "Main": { "Provider": "local-ollama", "ModelId": "qwen3:30b" },
                "Definitions": { "fast": { "Provider": "local-ollama", "ModelId": "qwen3:30b" } },
                "Roles": { "Main": "fast" }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("mixes legacy inline roles (Models:Main)", result.Message);
        Assert.Contains("The daemon will not start with this configuration. A running daemon keeps using its previous configuration", result.Message);
        Assert.DoesNotContain("configuration banner", result.Message);
        Assert.DoesNotContain("doctor --fix", result.Message + result.Remediation);
    }

    [Fact]
    public async Task ReturnsError_WhenARoleBoundContextWindowIsBelowTheStartupMinimum()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "local-ollama": { "Type": "ollama" }
              },
              "Models": {
                "Definitions": { "fast": { "Provider": "local-ollama", "ModelId": "qwen3:30b", "ContextWindow": 100 } },
                "Roles": { "Main": "fast" }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("Models:Definitions:fast:ContextWindow (100) is below minimum", result.Message);
        Assert.StartsWith("Invalid model configuration: Models:", result.Message);
    }

    [Fact]
    public async Task ReturnsError_WhenModelProviderValueIsNotString()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "local-ollama": { "Type": "ollama" }
              },
              "Models": {
                "Main": { "Provider": 123, "ModelId": "qwen3:30b" }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("Models.Main.Provider", result.Message);
        Assert.Contains("must be a string", result.Message);
    }

    [Fact]
    public async Task ReturnsError_WhenModelIdValueIsNotString()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "local-ollama": { "Type": "ollama" }
              },
              "Models": {
                "Main": { "Provider": "local-ollama", "ModelId": 123 }
              }
            }
            """);

        var check = CreateCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("Models.Main.ModelId", result.Message);
        Assert.Contains("must be a string", result.Message);
    }

    [Fact]
    public async Task EvaluatesBoundConfiguration_OnEnvOnlyInstance()
    {
        // No netclaw.json at all — configuration arrives entirely through the
        // bound IConfiguration (in production: NETCLAW_ env vars). The check
        // must evaluate what the daemon will actually run with, not report
        // "not configured" because the file is absent.
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:openrouter:Type"] = "openrouter",
                ["Providers:openrouter:AuthMethod"] = "ApiKey",
                ["Providers:openrouter:ApiKey"] = "sk-test",
                ["Models:Main:Provider"] = "openrouter",
                ["Models:Main:ModelId"] = "anthropic/claude-haiku-4",
            })
            .Build();

        // Trip the reader's env-config detection (process-level signal that
        // this is an env-configured instance rather than an unconfigured one).
        Environment.SetEnvironmentVariable("NETCLAW_Models__Main__ModelId", "anthropic/claude-haiku-4");
        try
        {
            var check = new ChatClientDoctorCheck(paths, configuration, ProviderCommand.CreateDefaultRegistry());
            var result = await check.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(DoctorSeverity.Pass, result.Severity);
            Assert.Contains("Real chat client configured", result.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETCLAW_Models__Main__ModelId", null);
        }
    }

    private NetclawPaths CreatePathsWithConfig(string configJson)
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, configJson);
        return paths;
    }

    private static ChatClientDoctorCheck CreateCheck(NetclawPaths paths) => new(
        paths,
        BuildConfiguration(paths),
        ProviderCommand.CreateDefaultRegistry());

    private static IConfigurationRoot BuildConfiguration(NetclawPaths paths) => new ConfigurationBuilder()
        .AddJsonFile(paths.NetclawConfigPath, optional: true, reloadOnChange: false)
        .AddJsonFile(paths.SecretsPath, optional: true, reloadOnChange: false)
        .Build();

    private static void WriteSecrets(NetclawPaths paths, string secretsJson) =>
        File.WriteAllText(paths.SecretsPath, secretsJson);

    private string CreateTempBasePath()
    {
        var path = Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
