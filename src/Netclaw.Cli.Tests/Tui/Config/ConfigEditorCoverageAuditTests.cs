// -----------------------------------------------------------------------
// <copyright file="ConfigEditorCoverageAuditTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class ConfigEditorCoverageAuditTests : IDisposable
{
    private static readonly IReadOnlySet<string> RoutedHandoffsOrGroups = new HashSet<string>(StringComparer.Ordinal)
    {
        "/provider",
        "/model",
        "/security"
    };

    private static readonly IReadOnlyDictionary<string, ConfigEditorCoverage> CoverageByEditorId =
        new Dictionary<string, ConfigEditorCoverage>(StringComparer.Ordinal)
        {
            ["audience-profiles"] = new(
                nameof(SecurityAccessViewModelTests),
                DynamicValidationCoverage.NotApplicable("Audience Profiles edits local ACL/profile config without a runtime probe."),
                null,
                new RuntimeConsumerCoverage(
                    "ToolAccessPolicy and runtime tool dispatch consume Tools.AudienceProfiles.",
                    [
                        "src/Netclaw.Actors.Tests/Tools/DispatchingToolExecutorTests.cs",
                        "src/Netclaw.Actors.Tests/Tools/McpToolAudienceGrantsTests.cs"
                    ])),
            ["channels"] = new(
                nameof(ChannelsConfigViewModelTests),
                DynamicValidationCoverage.Required(
                    nameof(ChannelsConfigViewModelTests),
                    nameof(ChannelsConfigViewModelTests.Save_from_input_surfaces_dynamic_validation_exception_as_status_without_persistence)),
                SecretCoverage.Required(
                    nameof(ChannelsConfigViewModelTests),
                    nameof(ChannelsConfigViewModelTests.Save_preserves_blank_existing_secrets_and_updates_config),
                    nameof(ChannelsConfigViewModelTests),
                    nameof(ChannelsConfigViewModelTests.Rotate_credentials_preserves_blank_secret_and_updates_nonblank_secret),
                    nameof(ChannelsConfigViewModelTests),
                    nameof(ChannelsConfigViewModelTests.Reset_connection_deletes_config_section_and_secrets_immediately)),
                new RuntimeConsumerCoverage(
                    "Slack, Discord, and Mattermost gateway options plus ACL/routing consume channel config.",
                    [
                        "src/Netclaw.Actors.Tests/Channels/Contracts/SlackAclContractTests.cs",
                        "src/Netclaw.Actors.Tests/Channels/Contracts/DiscordAclContractTests.cs",
                        "src/Netclaw.Actors.Tests/Channels/Contracts/MattermostAclContractTests.cs"
                    ])),
            ["enabled-features"] = new(
                nameof(SecurityAccessViewModelTests),
                DynamicValidationCoverage.NotApplicable("Enabled Features toggles local boolean runtime flags without a config-time probe."),
                null,
                new RuntimeConsumerCoverage(
                    "Daemon service registration and tool availability consume per-feature Enabled flags.",
                    [
                        "src/Netclaw.Actors.Tests/Tools/ToolRegistryTests.cs"
                    ])),
            ["exposure-mode"] = new(
                nameof(ExposureModeConfigViewModelTests),
                DynamicValidationCoverage.NotApplicable("Current Exposure Mode tests cover local merge and daemon consumer validation separately."),
                null,
                new RuntimeConsumerCoverage(
                    "DaemonConfig, exposure validation, and gateway authentication consume Daemon.ExposureMode.",
                    [
                        "src/Netclaw.Configuration.Tests/DaemonConfigTests.cs",
                        "src/Netclaw.Daemon.Tests/Services/ExposureModeValidationServiceTests.cs",
                        "src/Netclaw.Daemon.Tests/Security/SessionHubAuthorizationTests.cs"
                    ])),
            ["search"] = new(
                nameof(SearchConfigEditorViewModelTests),
                DynamicValidationCoverage.Required(
                    nameof(SearchConfigEditorViewModelTests),
                    nameof(SearchConfigEditorViewModelTests.Brave_probe_failure_opens_override_dialog_before_save)),
                SecretCoverage.NoExplicitDeleteFlow(
                    nameof(SearchConfigEditorViewModelTests),
                    nameof(SearchConfigEditorViewModelTests.Blank_secret_preserves_existing_secret),
                    nameof(SearchConfigEditorViewModelTests),
                    nameof(SearchConfigEditorViewModelTests.Save_anyway_persists_config_and_secret_semantically),
                    nameof(SearchConfigEditorViewModelTests),
                    nameof(SearchConfigEditorViewModelTests.Switching_to_zero_config_backend_preserves_existing_brave_secret),
                    "Search backend changes preserve dormant Brave credentials; there is no explicit delete affordance yet."),
                new RuntimeConsumerCoverage(
                    "Daemon search backend registration and WebSearchTool consume Search.Backend and backend-specific settings.",
                    [
                        "src/Netclaw.Actors.Tests/Tools/WebSearchToolTests.cs"
                    ])),
            ["security-posture"] = new(
                nameof(SecurityAccessViewModelTests),
                DynamicValidationCoverage.NotApplicable("Security Posture writes enum/default policy config without a runtime probe."),
                null,
                new RuntimeConsumerCoverage(
                    "Security policy defaults and tool execution policy consume Security.DeploymentPosture.",
                    [
                        "src/Netclaw.Configuration.Tests/SecurityPolicyDefaultsTests.cs",
                        "src/Netclaw.Actors.Tests/Tools/DispatchingToolExecutorTests.cs"
                    ])),
        };

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ConfigEditorCoverageAuditTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Visible_config_leaf_editors_match_coverage_inventory()
    {
        var visibleEditorIds = DiscoverVisibleConfigLeafEditorIds();

        Assert.Equal(
        [
            "audience-profiles",
            "channels",
            "enabled-features",
            "exposure-mode",
            "search",
            "security-posture"
        ], visibleEditorIds);
        Assert.Equal(visibleEditorIds, CoverageByEditorId.Keys.OrderBy(static key => key).ToArray());
    }

    [Fact]
    public void Visible_config_leaf_editors_declare_round_trip_coverage()
    {
        foreach (var editorId in DiscoverVisibleConfigLeafEditorIds())
        {
            var coverage = CoverageByEditorId[editorId];

            Assert.False(string.IsNullOrWhiteSpace(coverage.RoundTripTestClass),
                $"Config editor '{editorId}' must declare a round-trip test class.");
            AssertTestClassExists(coverage.RoundTripTestClass);
        }
    }

    [Fact]
    public void Visible_config_leaf_editors_declare_dynamic_validation_coverage()
    {
        foreach (var editorId in DiscoverVisibleConfigLeafEditorIds())
        {
            var coverage = CoverageByEditorId[editorId].DynamicValidation;

            if (coverage.HasDynamicValidation)
            {
                Assert.False(string.IsNullOrWhiteSpace(coverage.FakeFailureTestClass),
                    $"Config editor '{editorId}' has dynamic validation and must name its fake-failure test class.");
                Assert.False(string.IsNullOrWhiteSpace(coverage.FakeFailureTestMethod),
                    $"Config editor '{editorId}' has dynamic validation and must name its fake-failure test method.");
                AssertTestMethodExists(coverage.FakeFailureTestClass!, coverage.FakeFailureTestMethod!);
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(coverage.NotApplicableReason),
                $"Config editor '{editorId}' must justify why it has no dynamic validation path.");
        }
    }

    [Fact]
    public void Secret_writing_config_leaf_editors_declare_secret_lifecycle_coverage()
    {
        foreach (var (editorId, coverage) in CoverageByEditorId)
        {
            if (coverage.Secrets is not { } secretCoverage)
                continue;

            AssertTestMethodExists(secretCoverage.BlankPreserveTestClass, secretCoverage.BlankPreserveTestMethod);
            AssertTestMethodExists(secretCoverage.NonBlankReplaceTestClass, secretCoverage.NonBlankReplaceTestMethod);

            if (secretCoverage.SupportsExplicitDelete)
            {
                AssertTestMethodExists(secretCoverage.ExplicitDeleteTestClass!, secretCoverage.ExplicitDeleteTestMethod!);
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(secretCoverage.NoExplicitDeleteReason),
                $"Secret-writing config editor '{editorId}' must declare explicit-delete coverage or justify why no delete flow exists.");
            AssertTestMethodExists(secretCoverage.NoExplicitDeleteTestClass!, secretCoverage.NoExplicitDeleteTestMethod!);
        }
    }

    [Fact]
    public void Runtime_consumed_config_leaf_editors_name_consumers_and_contract_tests()
    {
        var repoRoot = FindRepoRoot();
        foreach (var editorId in DiscoverVisibleConfigLeafEditorIds())
        {
            var runtime = CoverageByEditorId[editorId].RuntimeConsumer;

            Assert.False(string.IsNullOrWhiteSpace(runtime.Consumer),
                $"Config editor '{editorId}' writes runtime-consumed config and must name its consumer.");
            Assert.NotEmpty(runtime.ContractTestFiles);
            foreach (var file in runtime.ContractTestFiles)
            {
                Assert.EndsWith("Tests.cs", file, StringComparison.Ordinal);
                var fullPath = Path.Combine(repoRoot, file.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(fullPath),
                    $"Config editor '{editorId}' declares missing runtime contract test file '{file}'.");
            }
        }
    }

    private string[] DiscoverVisibleConfigLeafEditorIds()
    {
        using var dashboard = new ConfigDashboardViewModel(new ConfigDashboardNavigationState());
        var rootEditors = dashboard.Items
            .Where(static item => item.Route is not null && !RoutedHandoffsOrGroups.Contains(item.Route))
            .Select(static item => RouteToEditorId(item.Route!));

        using var security = new SecurityAccessViewModel(_paths);
        var securityEditors = security.Items.Select(SecurityAccessItemToEditorId);

        return rootEditors.Concat(securityEditors).OrderBy(static id => id).ToArray();
    }

    private static string SecurityAccessItemToEditorId(SecurityAccessItem item)
    {
        return item.Label switch
        {
            "Security Posture" => "security-posture",
            "Enabled Features" => "enabled-features",
            "Audience Profiles" => "audience-profiles",
            _ when item.Route is not null => RouteToEditorId(item.Route),
            _ => throw new InvalidOperationException($"Security & Access item '{item.Label}' must be audited as a leaf editor.")
        };
    }

    private static string RouteToEditorId(string route) => route.TrimStart('/');

    private static void AssertTestClassExists(string testClassName)
    {
        var type = FindTestType(testClassName);
        Assert.True(type is not null, $"Declared test class '{testClassName}' was not found.");
    }

    private static void AssertTestMethodExists(string testClassName, string testMethodName)
    {
        var type = FindTestType(testClassName);
        Assert.True(type is not null, $"Declared test class '{testClassName}' was not found.");
        Assert.Contains(type!.GetMethods(), method => string.Equals(method.Name, testMethodName, StringComparison.Ordinal));
    }

    private static Type? FindTestType(string testClassName)
        => typeof(ConfigEditorCoverageAuditTests).Assembly
            .GetTypes()
            .FirstOrDefault(type => string.Equals(type.Name, testClassName, StringComparison.Ordinal));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IMPLEMENTATION_PLAN.md")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }

    private sealed record ConfigEditorCoverage(
        string RoundTripTestClass,
        DynamicValidationCoverage DynamicValidation,
        SecretCoverage? Secrets,
        RuntimeConsumerCoverage RuntimeConsumer);

    private sealed record DynamicValidationCoverage(
        bool HasDynamicValidation,
        string? FakeFailureTestClass,
        string? FakeFailureTestMethod,
        string? NotApplicableReason)
    {
        public static DynamicValidationCoverage Required(string fakeFailureTestClass, string fakeFailureTestMethod)
            => new(true, fakeFailureTestClass, fakeFailureTestMethod, null);

        public static DynamicValidationCoverage NotApplicable(string reason)
            => new(false, null, null, reason);
    }

    private sealed record SecretCoverage(
        string BlankPreserveTestClass,
        string BlankPreserveTestMethod,
        string NonBlankReplaceTestClass,
        string NonBlankReplaceTestMethod,
        bool SupportsExplicitDelete,
        string? ExplicitDeleteTestClass,
        string? ExplicitDeleteTestMethod,
        string? NoExplicitDeleteTestClass,
        string? NoExplicitDeleteTestMethod,
        string? NoExplicitDeleteReason)
    {
        public static SecretCoverage Required(
            string blankPreserveTestClass,
            string blankPreserveTestMethod,
            string nonBlankReplaceTestClass,
            string nonBlankReplaceTestMethod,
            string explicitDeleteTestClass,
            string explicitDeleteTestMethod)
            => new(
                blankPreserveTestClass,
                blankPreserveTestMethod,
                nonBlankReplaceTestClass,
                nonBlankReplaceTestMethod,
                true,
                explicitDeleteTestClass,
                explicitDeleteTestMethod,
                null,
                null,
                null);

        public static SecretCoverage NoExplicitDeleteFlow(
            string blankPreserveTestClass,
            string blankPreserveTestMethod,
            string nonBlankReplaceTestClass,
            string nonBlankReplaceTestMethod,
            string noExplicitDeleteTestClass,
            string noExplicitDeleteTestMethod,
            string noExplicitDeleteReason)
            => new(
                blankPreserveTestClass,
                blankPreserveTestMethod,
                nonBlankReplaceTestClass,
                nonBlankReplaceTestMethod,
                false,
                null,
                null,
                noExplicitDeleteTestClass,
                noExplicitDeleteTestMethod,
                noExplicitDeleteReason);
    }

    private sealed record RuntimeConsumerCoverage(string Consumer, IReadOnlyList<string> ContractTestFiles);
}
