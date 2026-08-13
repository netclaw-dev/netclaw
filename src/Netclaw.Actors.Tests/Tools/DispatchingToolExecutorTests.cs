// -----------------------------------------------------------------------
// <copyright file="DispatchingToolExecutorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class DispatchingToolExecutorTests
{
    private static readonly ShellExecutionEnvironment ShellEnvironment = TestShellEnvironment.Current;
    private readonly DispatchingToolExecutor _executor;
    private readonly DispatchingToolExecutor _restrictedExecutor;

    public DispatchingToolExecutorTests()
    {
        var baseConfig = new ToolConfig();
        baseConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Auto
            }
        };

        var commandPolicy = new ShellCommandPolicy(ShellEnvironment);
        var pathPolicy = new ToolPathPolicy(ShellEnvironment, []);
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(baseConfig, new NetclawPaths(), pathPolicy, commandPolicy);
        _executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                baseConfig,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                commandPolicy,
                pathPolicy));

        var restrictedConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        restrictedConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Auto
            }
        };
        restrictedConfig.AudienceProfiles.Team.AllowedTools = ["file_read", "file_list", "file_write", "file_edit", "attach_file", "shell_execute"];
        restrictedConfig.AudienceProfiles.Public.AllowedTools = ["file_read", "file_list", "attach_file"];
        var restrictedCommandPolicy = new ShellCommandPolicy(ShellEnvironment);
        var restrictedPathPolicy = new ToolPathPolicy(ShellEnvironment, []);
        var restrictedRegistry = new ToolRegistry();
        restrictedRegistry.WithFirstPartyTools(
            restrictedConfig,
            new NetclawPaths(),
            restrictedPathPolicy,
            restrictedCommandPolicy);
        _restrictedExecutor = new DispatchingToolExecutor(
            restrictedRegistry,
            new ToolAccessPolicy(
                restrictedConfig,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                restrictedCommandPolicy,
                restrictedPathPolicy));
    }

    [Fact]
    public async Task Verbose_tool_output_over_budget_is_windowed_and_spilled()
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "nc-disp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDir);
        try
        {
            // shell_execute declares the small verbose budget (2000); echo > 2000 chars.
            var toolCall = new FunctionCallContent("call-spill", "shell_execute",
                ToolInput.Create("Command", $"echo {new string('x', 3000)}"));
            var context = TestToolExecutionContext.CreateBound("slack/thread-1", sessionDir, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
            });

            var result = await _executor.ExecuteAsync(toolCall, context, CancellationToken.None);

            Assert.True(result.Length < 3000);                 // windowed inline, not the full 3000
            Assert.Contains("output saved to", result);
            Assert.Contains("file_read", result);
            var spill = Path.Combine(sessionDir, "tool-calls", "call-spill.log");
            Assert.True(File.Exists(spill));
            Assert.Contains(new string('x', 100), await File.ReadAllTextAsync(spill, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public async Task Spilled_output_is_redacted_before_write()
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "nc-disp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDir);
        try
        {
            // Secret + padding so it both redacts and exceeds the shell budget → spills.
            var toolCall = new FunctionCallContent("call-redact", "shell_execute",
                ToolInput.Create("Command", $"echo API_KEY=supersecret123 {new string('x', 3000)}"));
            var context = TestToolExecutionContext.CreateBound("slack/thread-1", sessionDir, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
            });

            var result = await _executor.ExecuteAsync(toolCall, context, CancellationToken.None);
            var onDisk = await File.ReadAllTextAsync(
                Path.Combine(sessionDir, "tool-calls", "call-redact.log"), CancellationToken.None);

            Assert.DoesNotContain("supersecret123", result);
            Assert.DoesNotContain("supersecret123", onDisk); // redacted before the spill write
        }
        finally
        {
            Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public async Task Small_output_is_redacted_without_spilling()
    {
        // Redaction happens centrally for every result, spill or not.
        var toolCall = new FunctionCallContent("call-r", "shell_execute",
            ToolInput.Create("Command", "echo API_KEY=secret123"));
        var context = TestToolExecutionContext.CreateBound("signalr/thread-1", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
        });

        var result = await _executor.ExecuteAsync(toolCall, context, CancellationToken.None);

        Assert.Contains("API_KEY=***REDACTED***", result);
        Assert.DoesNotContain("secret123", result);
        Assert.DoesNotContain("saved to", result); // small → no spill
    }

    [Fact]
    public async Task File_read_preserves_secret_values_for_model()
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "nc-disp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDir);
        try
        {
            // file_read suppresses output redaction so the model sees real
            // content and can write it back without corrupting secrets (#1333).
            var file = Path.Combine(sessionDir, "appsettings.json");
            await File.WriteAllTextAsync(file,
                """{"secretKey": "real-secret-value", "name": "myapp"}""",
                CancellationToken.None);
            var toolCall = new FunctionCallContent("call-secret", "file_read",
                ToolInput.Create("Path", file));
            var context = TestToolExecutionContext.CreateBound("slack/thread-1", sessionDir, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
            });

            var result = await _executor.ExecuteAsync(toolCall, context, CancellationToken.None);

            Assert.Contains("real-secret-value", result);
            Assert.DoesNotContain("***REDACTED***", result);
        }
        finally
        {
            Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public async Task Shell_output_still_redacts_secrets()
    {
        // Shell output continues to be redacted — only file tools suppress it.
        var toolCall = new FunctionCallContent("call-shell-secret", "shell_execute",
            ToolInput.Create("Command", "echo API_KEY=secret123"));
        var context = TestToolExecutionContext.CreateBound("signalr/thread-1", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
        });

        var result = await _executor.ExecuteAsync(toolCall, context, CancellationToken.None);

        Assert.Contains("***REDACTED***", result);
        Assert.DoesNotContain("secret123", result);
    }

    [Fact]
    public async Task File_read_spill_file_is_redacted_even_when_model_result_is_not()
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "nc-disp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDir);
        try
        {
            // Create a file with a secret that exceeds the shell's verbose
            // budget so it spills. file_read uses the content budget (12000)
            // so we need a bigger file. Use a custom executor with a small
            // content budget to force a spill.
            var file = Path.Combine(sessionDir, "big-config.json");
            var bigContent = $$"""{"secretKey": "real-secret-value", "data": "{{new string('x', 15000)}}"}""";
            await File.WriteAllTextAsync(file, bigContent, CancellationToken.None);

            var toolCall = new FunctionCallContent("call-spill-secret", "file_read",
                ToolInput.Create("Path", file));
            var context = TestToolExecutionContext.CreateBound("slack/thread-1", sessionDir, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                InlineOutputBudget = new InlineOutputBudget(500),
            });

            var result = await _executor.ExecuteAsync(toolCall, context, CancellationToken.None);

            // The inline result (model-facing) should NOT contain the redacted sentinel
            Assert.DoesNotContain("***REDACTED***", result);
            // But it should be truncated (spilled)
            Assert.Contains("output saved to", result);

            // The spill file on disk SHOULD be redacted
            var spillPath = Path.Combine(sessionDir, "tool-calls", "call-spill-secret.log");
            Assert.True(File.Exists(spillPath));
            var spillContent = await File.ReadAllTextAsync(spillPath, CancellationToken.None);
            Assert.Contains("***REDACTED***", spillContent);
            Assert.DoesNotContain("real-secret-value", spillContent);
        }
        finally
        {
            Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public async Task Content_tool_under_default_budget_not_spilled()
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), "nc-disp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDir);
        try
        {
            // file_read has no verbose override → the 12000-char content budget; a
            // small file is returned whole with no spill.
            var file = Path.Combine(sessionDir, "note.txt");
            await File.WriteAllTextAsync(file, "hello content", CancellationToken.None);
            var toolCall = new FunctionCallContent("call-content", "file_read",
                ToolInput.Create("Path", file));
            var context = TestToolExecutionContext.CreateBound("slack/thread-1", sessionDir, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
            });

            var result = await _executor.ExecuteAsync(toolCall, context, CancellationToken.None);

            Assert.Contains("hello content", result);
            Assert.DoesNotContain("saved to", result);
            Assert.False(Directory.Exists(Path.Combine(sessionDir, "tool-calls")));
        }
        finally
        {
            Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public async Task Routes_shell_execute()
    {
        var toolCall = new FunctionCallContent(
            "call-1", "shell_execute",
            ToolInput.Create("Command", "echo routed"));

        var context = TestToolExecutionContext.CreateBound("signalr/thread-1", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "signalr"
        });

        var result = await _executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);

        Assert.Contains("routed", result);
        Assert.Contains("Exit code: 0", result);
    }

    [Fact]
    public async Task Routes_file_read_missing_file()
    {
        var toolCall = new FunctionCallContent(
            "call-2", "file_read",
            ToolInput.Create("Path", "/nonexistent/file.txt"));

        var context = TestToolExecutionContext.CreateBound("signalr/thread-1", Path.GetTempPath(), new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "signalr"
        });

        var result = await _executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);

        Assert.Contains("File not found", result);
    }

    [Fact]
    public async Task Shell_execute_is_denied_outside_personal_context()
    {
        var toolCall = new FunctionCallContent(
            "call-deny", "shell_execute",
            ToolInput.Create("Command", "echo denied"));

        var context = TestToolExecutionContext.CreateBound("slack/thread-1", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "slack"
        });

        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(() => _restrictedExecutor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));
        Assert.Equal("shell_requires_personal_context", ex.DenyReason);
    }

    [Fact]
    public async Task Shell_execute_is_denied_when_missing_from_personal_audience_profile()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ToolsMode = ToolProfileMode.Allowlist;
        config.AudienceProfiles.Personal.AllowedTools = ["file_read", "file_write", "attach_file"];

        var commandPolicy = new ShellCommandPolicy(ShellEnvironment);
        var pathPolicy = new ToolPathPolicy(ShellEnvironment, []);
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config, new NetclawPaths(), pathPolicy, commandPolicy);

        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                commandPolicy,
                pathPolicy));

        var toolCall = new FunctionCallContent(
            "call-shell-profile-deny", "shell_execute",
            ToolInput.Create("Command", "echo denied"));

        var context = TestToolExecutionContext.CreateBound("signalr/thread-1", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "signalr"
        });

        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(() => executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));
        Assert.Equal("tool_not_allowed_for_audience_profile", ex.DenyReason);
    }

    [Fact]
    public async Task Shell_execute_is_denied_when_shell_mode_is_off_even_in_personal_context()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.Off };
        config.AudienceProfiles.Personal.ToolsMode = ToolProfileMode.Allowlist;
        config.AudienceProfiles.Personal.AllowedTools.Add("shell_execute");

        var commandPolicy = new ShellCommandPolicy(ShellEnvironment);
        var pathPolicy = new ToolPathPolicy(ShellEnvironment, []);
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config, new NetclawPaths(), pathPolicy, commandPolicy);

        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.Off,
                    UsedStrictFallback: false),
                commandPolicy,
                pathPolicy));

        var toolCall = new FunctionCallContent(
            "call-shell-off", "shell_execute",
            ToolInput.Create("Command", "echo denied"));

        var context = TestToolExecutionContext.CreateBound("signalr/thread-1", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "signalr"
        });

        var decision = await executor.EvaluateAuthorizationAsync(
            toolCall,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("shell_disabled", decision.DenyReason);
        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(() => executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));
        Assert.Equal("shell_disabled", ex.DenyReason);
    }

    [Fact]
    public async Task Shell_execute_is_allowed_in_personal_context()
    {
        var toolCall = new FunctionCallContent(
            "call-allow", "shell_execute",
            ToolInput.Create("Command", "echo allowed"));

        var context = TestToolExecutionContext.CreateBound("signalr/thread-1", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "signalr"
        });

        var result = await _restrictedExecutor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);
        Assert.Contains("allowed", result);
    }

    [Theory]
    [InlineData("echo observable")]
    [InlineData("printf observable")]
    [InlineData(":")]
    [InlineData("true")]
    [InlineData("false")]
    public async Task Approval_exempt_shell_candidates_report_allow_reason(string command)
    {
        var executor = CreateApprovalGatedShellExecutor();
        var call = new FunctionCallContent(
            "call-approval-exempt",
            "shell_execute",
            ToolInput.Create("Command", command));
        var context = CreateInteractivePersonalContext("signalr/thread-approval-exempt");

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Allowed, decision.Outcome);
        Assert.Equal(ToolAllowReason.ApprovalExemptShellCandidates, decision.AllowReason);
        Assert.Empty(decision.ApprovalMatches);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Shell_approval_without_extracted_candidates_fails_closed(string command)
    {
        var executor = CreateApprovalGatedShellExecutor();
        var call = new FunctionCallContent(
            "call-no-approval-candidates",
            "shell_execute",
            ToolInput.Create("Command", command));
        var context = CreateInteractivePersonalContext("signalr/thread-no-approval-candidates");

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        Assert.NotNull(decision.ApprovalContext);
        Assert.Empty(decision.ApprovalContext.Candidates!);
    }

    [Fact]
    public async Task Shell_parser_rejection_fails_closed_without_execution()
    {
        if (OperatingSystem.IsWindows())
            return;

        var markerPath = Path.Combine(Path.GetTempPath(), $"netclaw-approval-{Guid.NewGuid():N}");
        var command = $"touch {markerPath} <(true)";
        var arguments = ToolInput.Create("Command", command);
        Assert.False(ShellTokenizer.IsMessyCompoundCommand(command));
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux));
        Assert.Empty(matcher.ExtractCandidates(new ToolName("shell_execute"), arguments));

        var executor = CreateApprovalGatedShellExecutor();
        var call = new FunctionCallContent(
            "call-parser-rejection",
            "shell_execute",
            arguments);
        var context = CreateInteractivePersonalContext("signalr/thread-parser-rejection");

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        Assert.NotNull(decision.ApprovalContext);
        Assert.Empty(decision.ApprovalContext.Candidates!);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task Authorization_evaluation_preserves_partial_approval_matches()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(
            config,
            new NetclawPaths(),
            new ToolPathPolicy([]),
            new ShellCommandPolicy());
        var approvedMatch = new ToolApprovalMatch("git status", "session", "this chat");
        var approvalService = new FixedApprovalService(
            new ToolApprovalCheckResult(["git push"], [approvedMatch]));
        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])),
            approvalService);
        var call = new FunctionCallContent(
            "call-partial-approval",
            "shell_execute",
            ToolInput.Create("Command", "git status && git push"));
        var context = TestToolExecutionContext.CreateBound(
            "signalr/thread-partial-approval",
            null,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            });

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        Assert.NotNull(decision.ApprovalContext);
        Assert.Equal(["git status", "git push"], decision.ApprovalContext.CandidateVerbs);
        Assert.Empty(decision.ApprovalMatches);
    }

    [Fact]
    public async Task Authorization_evaluation_prompts_only_for_exact_unapproved_candidates()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(
            config,
            new NetclawPaths(),
            new ToolPathPolicy([]),
            new ShellCommandPolicy());
        var approvedMatch = new ToolApprovalMatch("git status", "session", "this chat");
        var approvedCandidate = BashCandidate("git status");
        var unapprovedCandidate = BashCandidate("git push");
        var approvalService = new FixedApprovalService(
            new ToolApprovalCheckResult(
                ["git push"],
                [approvedMatch])
            {
                CandidateChecks =
                [
                    new ToolApprovalCandidateCheck(approvedCandidate, approvedMatch),
                    new ToolApprovalCandidateCheck(unapprovedCandidate, ApprovedMatch: null)
                ]
            });
        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])),
            approvalService);
        var call = new FunctionCallContent(
            "call-exact-partial-approval",
            "shell_execute",
            ToolInput.Create("Command", "git status && git push"));
        var context = CreateInteractivePersonalContext("signalr/thread-exact-partial-approval");

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        var approvalContext = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
        Assert.Equal(["git push"], approvalContext.Patterns);
        Assert.Equal(["git push"], approvalContext.CandidateVerbs);
        Assert.Equal([unapprovedCandidate], approvalContext.Candidates);
        Assert.Equal([approvedMatch], decision.ApprovalMatches);
    }

    [SlopwatchSuppress("SW001", "This test pins the Bash compound-command coverage model used by the Linux approval policy.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only shell coverage semantics")]
    public async Task Authorization_evaluation_composes_session_and_reviewed_safe_coverage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netclaw-coverage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
            config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
            {
                ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
                {
                    ["shell_execute"] = ToolApprovalMode.Approval
                }
            };
            var commandPolicy = new ShellCommandPolicy(ShellEnvironment);
            var pathPolicy = new ToolPathPolicy(ShellEnvironment, []);
            var registry = new ToolRegistry();
            registry.WithFirstPartyTools(config, new NetclawPaths(), pathPolicy, commandPolicy);
            var approvalService = new FixedShellApprovalService(request =>
            {
                var matches = request.Candidates.Select(candidate =>
                {
                    if (!candidate.Candidate.Verb.StartsWith("git status", StringComparison.Ordinal))
                    {
                        return new ShellGrantCandidateMatch(
                            candidate.CandidateId,
                            Match: null,
                            GrantCoverage: null,
                            NearMisses: []);
                    }

                    return new ShellGrantCandidateMatch(
                        candidate.CandidateId,
                        new ToolApprovalMatch(candidate.Candidate.Verb, "session", "this chat"),
                        ShellCoverageKind.Session,
                        []);
                }).ToArray();
                return new ShellApprovalMatchResult(
                    new PersistentGrantStoreStatus.Unavailable(ApprovalStoreFailure.InvalidData),
                    Array.AsReadOnly(matches));
            });
            var executor = new DispatchingToolExecutor(
                registry,
                new ToolAccessPolicy(
                    config,
                    new EffectivePolicyDefaults(
                        DeploymentPosture.Personal,
                        TrustAudience.Personal,
                        ShellExecutionMode.HostAllowed,
                        UsedStrictFallback: false),
                    commandPolicy,
                    pathPolicy,
                    safeVerbs: SafeVerbList.FromVerbs(["head"])),
                approvalService);
            var context = TestToolExecutionContext.CreateBound(
                "signalr/mixed-coverage",
                sessionDirectory: null,
                new TestToolExecutionContextOptions
                {
                    Audience = TrustAudience.Personal,
                    ProjectDirectory = root,
                    InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
                });
            var call = new FunctionCallContent(
                "call-mixed-coverage",
                "shell_execute",
                ToolInput.Create(
                    "Command",
                    "git status && head README.md",
                    "WorkingDirectory",
                    root));

            var decision = await executor.EvaluateAuthorizationAsync(
                call,
                context,
                TestContext.Current.CancellationToken);

            Assert.Equal(ToolAuthorizationOutcome.Allowed, decision.Outcome);
            Assert.Equal(ToolAllowReason.StoredApproval, decision.AllowReason);
            Assert.Equal(1, approvalService.RequestCount);
            var request = Assert.IsType<ShellApprovalMatchRequest>(approvalService.LastRequest);
            Assert.Equal(
                Enumerable.Range(0, request.Candidates.Count),
                request.Candidates.Select(candidate => candidate.CandidateId.Value));
            Assert.Contains(request.Candidates, candidate => candidate.Candidate.Verb == "git status");
            Assert.Contains(request.Candidates, candidate => candidate.Candidate.Verb == "head");
            Assert.Collection(
                decision.ShellPolicyTrace.Rows,
                row =>
                {
                    Assert.Equal(ShellPolicyTraceStage.StoredGrantMatch, row.Stage);
                    Assert.Equal(ShellPolicyTraceOutcome.Covered, row.Outcome);
                    Assert.Equal(ShellPolicyTraceReason.SessionGrant, row.Reason);
                    Assert.Equal("git", row.ExecutableBasename);
                    Assert.Equal(ShellCoverageKind.Session, row.Coverage);
                    Assert.Equal(ShellScopeRelation.ThisChat, row.ScopeRelation);
                },
                row =>
                {
                    Assert.Equal(ShellPolicyTraceStage.StoredGrantMatch, row.Stage);
                    Assert.Equal(ShellPolicyTraceOutcome.Uncovered, row.Outcome);
                    Assert.Equal(ShellPolicyTraceReason.NoGrant, row.Reason);
                    Assert.Equal("head", row.ExecutableBasename);
                    Assert.Equal(ShellCoverageKind.Uncovered, row.Coverage);
                    Assert.Equal(ShellScopeRelation.None, row.ScopeRelation);
                },
                row =>
                {
                    Assert.Equal(ShellPolicyTraceStage.ReviewedSafePolicy, row.Stage);
                    Assert.Equal(ShellPolicyTraceOutcome.Covered, row.Outcome);
                    Assert.Equal(ShellPolicyTraceReason.ReviewedSafePhrase, row.Reason);
                    Assert.Equal("head", row.ExecutableBasename);
                    Assert.Equal(ShellCoverageKind.ReviewedSafePolicy, row.Coverage);
                    Assert.Equal(ShellScopeRelation.UnderRealRoot, row.ScopeRelation);
                },
                row =>
                {
                    Assert.Equal(ShellPolicyTraceStage.Completion, row.Stage);
                    Assert.Equal(ShellPolicyTraceOutcome.Allow, row.Outcome);
                    Assert.Equal(ShellPolicyTraceReason.AllCandidatesCovered, row.Reason);
                });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "This test pins Bash causal approval intent on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only shell directory semantics")]
    public async Task Authorization_evaluation_composes_grants_with_causal_intent_diagnostics()
    {
        var approvalService = new FixedShellApprovalService(request =>
        {
            var matches = request.Candidates.Select(candidate =>
            {
                var shell = Assert.IsType<ApprovalShell>(candidate.Candidate.Shell);
                var tokens = Assert.IsAssignableFrom<IReadOnlyList<string>>(
                    candidate.Candidate.VerbTokens);
                var entry = ApprovalEntry.CreateTokenPrefix(
                    shell,
                    tokens,
                    directory: null,
                    createdAt: null);
                return new ShellGrantCandidateMatch(
                    candidate.CandidateId,
                    new ToolApprovalMatch(
                        candidate.Candidate.Verb,
                        "persistent",
                        entry.FormatScope()),
                    ShellCoverageKind.PersistentGlobal,
                    NearMisses: []);
            }).ToArray();
            return new ShellApprovalMatchResult(
                new PersistentGrantStoreStatus.Ready(),
                Array.AsReadOnly(matches));
        });
        var executor = CreateApprovalGatedShellExecutor(
            approvalService,
            safeVerbs: SafeVerbList.FromVerbs(
                ApprovalShell.Bash,
                ["wc", "head"]));
        var command = "cd /tmp && gh api repos/example/project/actions/jobs/123456/logs "
                      + "> slopwatch.log 2>&1; wc -c slopwatch.log; head -100 slopwatch.log";
        var call = new FunctionCallContent(
            "call-causal-intent",
            "shell_execute",
            ToolInput.Create(
                "Command",
                command,
                "WorkingDirectory",
                "/work"));

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            CreateInteractivePersonalContext("signalr/causal-intent"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Allowed, decision.Outcome);
        Assert.Equal(ToolAllowReason.StoredApproval, decision.AllowReason);
        var request = Assert.IsType<ShellApprovalMatchRequest>(approvalService.LastRequest);
        Assert.All(request.Candidates, candidate =>
            Assert.Contains(candidate.Candidate.Verb, new[] { "cd", "gh api" }));
        Assert.Contains(request.Candidates, candidate => candidate.Candidate.Verb == "cd");
        Assert.Contains(request.Candidates, candidate => candidate.Candidate.Verb == "gh api");
        var intentRows = decision.ShellPolicyTrace.Rows
            .Where(row => row.ScopeRelation == ShellScopeRelation.UnderIntentRoot)
            .ToArray();
        Assert.Equal(2, intentRows.Length);
        Assert.All(intentRows, row =>
        {
            Assert.Equal(ShellPolicyTraceStage.ReviewedSafePolicy, row.Stage);
            Assert.Equal(ShellPolicyTraceReason.ReviewedSafePhrase, row.Reason);
        });
        Assert.Equal(
            new[] { "head", "wc" },
            intentRows
                .Select(row => Assert.IsType<string>(row.ExecutableBasename))
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [SlopwatchSuppress("SW001", "This test pins Bash causal approval intent on POSIX hosts.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only shell directory semantics")]
    [InlineData("Session")]
    [InlineData("PersistentFolder")]
    public async Task Causal_intent_accepts_session_or_real_folder_prerequisite_coverage(
        string prerequisiteCoverageName)
    {
        var prerequisiteCoverage = Enum.Parse<ShellCoverageKind>(prerequisiteCoverageName);
        var approvalService = new FixedShellApprovalService(request =>
        {
            var matches = request.Candidates.Select(candidate =>
            {
                var match = prerequisiteCoverage == ShellCoverageKind.Session
                    ? new ToolApprovalMatch(candidate.Candidate.Verb, "session", "this chat")
                    : new ToolApprovalMatch(
                        candidate.Candidate.Verb,
                        "persistent",
                        ApprovalEntry.CreateTokenPrefix(
                            ApprovalShell.Bash,
                            Assert.IsAssignableFrom<IReadOnlyList<string>>(
                                candidate.Candidate.VerbTokens),
                            "/tmp",
                            createdAt: null).FormatScope());
                return new ShellGrantCandidateMatch(
                    candidate.CandidateId,
                    match,
                    prerequisiteCoverage,
                    NearMisses: []);
            }).ToArray();
            return new ShellApprovalMatchResult(
                new PersistentGrantStoreStatus.Ready(),
                Array.AsReadOnly(matches));
        });
        var executor = CreateApprovalGatedShellExecutor(
            approvalService,
            safeVerbs: SafeVerbList.FromVerbs(ApprovalShell.Bash, ["head"]));
        var call = new FunctionCallContent(
            "call-causal-intent-bounded-grant",
            "shell_execute",
            ToolInput.Create(
                "Command",
                "cd /tmp && inspect; head result.log",
                "WorkingDirectory",
                "/work"));

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            CreateInteractivePersonalContext("signalr/causal-intent-bounded-grant"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Allowed, decision.Outcome);
        var request = Assert.IsType<ShellApprovalMatchRequest>(approvalService.LastRequest);
        Assert.Equal(["cd", "inspect"], request.Candidates
            .Select(candidate => candidate.Candidate.Verb).ToArray());
        Assert.All(request.Candidates, candidate =>
            Assert.Equal("/tmp", candidate.Candidate.Directory));
        Assert.Equal(
            2,
            decision.ShellPolicyTrace.Rows.Count(row =>
                row.Stage == ShellPolicyTraceStage.StoredGrantMatch
                && row.Coverage == prerequisiteCoverage));
        Assert.Contains(
            decision.ShellPolicyTrace.Rows,
            row => row is
            {
                Stage: ShellPolicyTraceStage.ReviewedSafePolicy,
                ScopeRelation: ShellScopeRelation.UnderIntentRoot,
                ExecutableBasename: "head"
            });
    }

    [SlopwatchSuppress("SW001", "This test pins Bash causal approval intent on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only shell directory semantics")]
    public async Task Causal_intent_does_not_rebase_a_folder_grant_to_the_intent_scope()
    {
        var approvalService = new FixedShellApprovalService(request =>
            new ShellApprovalMatchResult(
                new PersistentGrantStoreStatus.Ready(),
                Array.AsReadOnly(request.Candidates.Select(candidate =>
                {
                    var grant = ApprovalEntry.CreateTokenPrefix(
                        ApprovalShell.Bash,
                        Assert.IsAssignableFrom<IReadOnlyList<string>>(
                            candidate.Candidate.VerbTokens),
                        "/work",
                        createdAt: null);
                    return new ShellGrantCandidateMatch(
                        candidate.CandidateId,
                        Match: null,
                        GrantCoverage: null,
                        NearMisses:
                        [
                            new ShellApprovalNearMiss(
                                grant,
                                ShellApprovalNearMissReason.OutsideDirectory)
                        ]);
                }).ToArray())));
        var executor = CreateApprovalGatedShellExecutor(
            approvalService,
            safeVerbs: SafeVerbList.FromVerbs(ApprovalShell.Bash, ["head"]));
        var call = new FunctionCallContent(
            "call-causal-intent-folder-near-miss",
            "shell_execute",
            ToolInput.Create(
                "Command",
                "cd /tmp && inspect; head result.log",
                "WorkingDirectory",
                "/work"));

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            CreateInteractivePersonalContext("signalr/causal-intent-folder-near-miss"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        var request = Assert.IsType<ShellApprovalMatchRequest>(approvalService.LastRequest);
        Assert.All(request.Candidates, candidate =>
            Assert.Equal("/tmp", candidate.Candidate.Directory));
        Assert.DoesNotContain(
            decision.ShellPolicyTrace.Rows,
            row => row.ScopeRelation == ShellScopeRelation.UnderIntentRoot);
    }

    [SlopwatchSuppress("SW001", "This test pins Bash causal approval intent on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only shell directory semantics")]
    public async Task Causal_intent_requires_authority_for_each_prerequisite()
    {
        var approvalService = new FixedShellApprovalService(request =>
            new ShellApprovalMatchResult(
                new PersistentGrantStoreStatus.Ready(),
                Array.AsReadOnly(request.Candidates.Select(candidate =>
                {
                    if (candidate.Candidate.Verb != "cd")
                    {
                        return new ShellGrantCandidateMatch(
                            candidate.CandidateId,
                            Match: null,
                            GrantCoverage: null,
                            NearMisses: []);
                    }

                    var entry = ApprovalEntry.CreateTokenPrefix(
                        ApprovalShell.Bash,
                        ["cd"],
                        directory: null,
                        createdAt: null);
                    return new ShellGrantCandidateMatch(
                        candidate.CandidateId,
                        new ToolApprovalMatch("cd", "persistent", entry.FormatScope()),
                        ShellCoverageKind.PersistentGlobal,
                        NearMisses: []);
                }).ToArray())));
        var executor = CreateApprovalGatedShellExecutor(
            approvalService,
            safeVerbs: SafeVerbList.FromVerbs(
                ApprovalShell.Bash,
                ["gh api", "wc", "head"]));
        var call = new FunctionCallContent(
            "call-causal-intent-missing-prerequisite",
            "shell_execute",
            ToolInput.Create(
                "Command",
                "cd /tmp && gh api repos/example/project > result.log 2>&1; "
                + "wc -c result.log; head result.log",
                "WorkingDirectory",
                "/work"));

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            CreateInteractivePersonalContext("signalr/causal-intent-missing-prerequisite"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        var approval = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
        Assert.True(approval.IsMessy);
        Assert.Empty(approval.Candidates!);
        Assert.Equal(
            [
                Netclaw.Actors.Protocol.ApprovalOptionKeys.ApproveOnce,
                Netclaw.Actors.Protocol.ApprovalOptionKeys.Deny
            ],
            approval.Options.Select(option => option.Key.Value).ToArray());
        Assert.Equal(
            ["cd", "gh api"],
            Assert.IsType<ShellApprovalMatchRequest>(approvalService.LastRequest)
                .Candidates.Select(candidate => candidate.Candidate.Verb).ToArray());
    }

    [SlopwatchSuppress("SW001", "This test pins Bash causal approval intent on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only shell directory semantics")]
    public async Task Exact_one_time_retry_covers_the_original_causal_call()
    {
        var approvalService = new FixedShellApprovalService(request =>
            new ShellApprovalMatchResult(
                new PersistentGrantStoreStatus.Ready(),
                Array.AsReadOnly(request.Candidates.Select(candidate =>
                    new ShellGrantCandidateMatch(
                        candidate.CandidateId,
                        Match: null,
                        GrantCoverage: null,
                        NearMisses: [])).ToArray())));
        var executor = CreateApprovalGatedShellExecutor(
            approvalService,
            safeVerbs: SafeVerbList.FromVerbs(
                ApprovalShell.Bash,
                ["head"]));
        var call = new FunctionCallContent(
            "call-causal-intent-once",
            "shell_execute",
            ToolInput.Create(
                "Command",
                "cd /tmp && inspect; head result.log",
                "WorkingDirectory",
                "/work"));
        var context = CreateInteractivePersonalContext("signalr/causal-intent-once");

        var initial = await executor.EvaluateAuthorizationAsync(
            call,
            context,
            TestContext.Current.CancellationToken);
        var approval = Assert.IsType<ToolApprovalContext>(initial.ApprovalContext);
        context.OneTimeApprovedToolName = call.Name;
        context.SetOneTimeApprovedPatterns(OneTimeApprovalKeys.Create(approval));

        var retry = await executor.EvaluateAuthorizationAsync(
            call,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Allowed, retry.Outcome);
        Assert.Equal(ToolAllowReason.OneTimeApproval, retry.AllowReason);
        Assert.DoesNotContain(
            retry.ShellPolicyTrace.Rows,
            row => row.ScopeRelation == ShellScopeRelation.UnderIntentRoot);
    }

    [SlopwatchSuppress("SW001", "This test pins Bash causal approval intent on POSIX hosts.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only shell directory semantics")]
    [InlineData("command cd /tmp && inspect; head result.log")]
    [InlineData("builtin cd /tmp && inspect; head result.log")]
    public async Task Parser_owned_directory_effect_allows_wrapped_transition(
        string command)
    {
        var approvalService = GrantEveryShellCandidate();
        var executor = CreateApprovalGatedShellExecutor(
            approvalService,
            safeVerbs: SafeVerbList.FromVerbs(
                ApprovalShell.Bash,
                ["head"]));
        var call = new FunctionCallContent(
            "call-causal-intent-wrapped-transition",
            "shell_execute",
            ToolInput.Create(
                "Command",
                command,
                "WorkingDirectory",
                "/work"));

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            CreateInteractivePersonalContext("signalr/causal-intent-wrapper"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Allowed, decision.Outcome);
        Assert.Contains(
            decision.ShellPolicyTrace.Rows,
            row => row is
            {
                Stage: ShellPolicyTraceStage.ReviewedSafePolicy,
                ScopeRelation: ShellScopeRelation.UnderIntentRoot,
                ExecutableBasename: "head"
            });
    }

    [SlopwatchSuppress("SW001", "This test pins Bash causal approval intent on POSIX hosts.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only shell directory semantics")]
    [InlineData("cd /tmp && inspect; pushd /other; head result.log")]
    [InlineData("cd /tmp && inspect; popd; head result.log")]
    [InlineData("cd /tmp && inspect; cd \"$1\"; head result.log")]
    [InlineData("cd /tmp extra && inspect; head result.log")]
    [InlineData("cd -z /tmp && inspect; head result.log")]
    public async Task Unproved_directory_effect_keeps_causal_chain_strict(
        string command)
    {
        var approvalService = GrantEveryShellCandidate();
        var executor = CreateApprovalGatedShellExecutor(
            approvalService,
            safeVerbs: SafeVerbList.FromVerbs(
                ApprovalShell.Bash,
                ["head"]));
        var call = new FunctionCallContent(
            "call-causal-intent-strict-effect",
            "shell_execute",
            ToolInput.Create(
                "Command",
                command,
                "WorkingDirectory",
                "/work"));

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            CreateInteractivePersonalContext("signalr/causal-intent-strict-effect"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        Assert.True(Assert.IsType<ToolApprovalContext>(decision.ApprovalContext).IsMessy);
        Assert.Null(approvalService.LastRequest);
        Assert.DoesNotContain(
            decision.ShellPolicyTrace.Rows,
            row => row.ScopeRelation == ShellScopeRelation.UnderIntentRoot);
    }

    [SlopwatchSuppress("SW001", "This test pins Bash causal approval intent on POSIX hosts.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "POSIX-only shell directory semantics")]
    [InlineData("cd /tmp && inspect; head private.log", "/tmp/private.log", "head")]
    [InlineData("cd /tmp && inspect; head private.log", "/work/private.log", "head")]
    [InlineData("cd /tmp && inspect; grep -f /protected/patterns local.txt", "/protected/patterns", "grep")]
    [InlineData("cd /tmp && inspect; wc -c < private.log", "/tmp/private.log", "wc")]
    public async Task Causal_intent_cannot_bypass_protected_path_policy(
        string command,
        string deniedPath,
        string safeVerb)
    {
        var approvalService = GrantEveryShellCandidate();
        var executor = CreateApprovalGatedShellExecutor(
            approvalService,
            safeVerbs: SafeVerbList.FromVerbs(
                ApprovalShell.Bash,
                [safeVerb]),
            deniedPaths: [deniedPath]);
        var call = new FunctionCallContent(
            "call-causal-intent-protected-path",
            "shell_execute",
            ToolInput.Create(
                "Command",
                command,
                "WorkingDirectory",
                "/work"));

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            CreateInteractivePersonalContext("signalr/causal-intent-protected-path"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("shell_references_protected_path", decision.DenyReason);
        Assert.DoesNotContain(
            decision.ShellPolicyTrace.Rows,
            row => row.ScopeRelation == ShellScopeRelation.UnderIntentRoot);
    }

    [SlopwatchSuppress("SW001", "This test requires native POSIX symbolic-link behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only symbolic-link semantics")]
    public async Task Causal_intent_rejects_a_symbolic_link_transition_target()
    {
        var root = Directory.CreateTempSubdirectory("netclaw-causal-intent-");
        try
        {
            var target = Path.Combine(root.FullName, "target");
            var alias = Path.Combine(root.FullName, "alias");
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(alias, target);
            var approvalService = GrantEveryShellCandidate();
            var executor = CreateApprovalGatedShellExecutor(
                approvalService,
                safeVerbs: SafeVerbList.FromVerbs(
                    ApprovalShell.Bash,
                    ["head"]));
            var call = new FunctionCallContent(
                "call-causal-intent-symlink-target",
                "shell_execute",
                ToolInput.Create(
                    "Command",
                    $"cd {alias} && inspect; head result.log",
                    "WorkingDirectory",
                    "/work"));

            var decision = await executor.EvaluateAuthorizationAsync(
                call,
                CreateInteractivePersonalContext("signalr/causal-intent-symlink-target"),
                TestContext.Current.CancellationToken);

            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
            Assert.True(Assert.IsType<ToolApprovalContext>(decision.ApprovalContext).IsMessy);
            Assert.Null(approvalService.LastRequest);
            Assert.DoesNotContain(
                decision.ShellPolicyTrace.Rows,
                row => row.ScopeRelation == ShellScopeRelation.UnderIntentRoot);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "This test requires native POSIX symbolic-link behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only symbolic-link semantics")]
    public async Task Causal_intent_rejects_a_symbolic_link_fallback_directory()
    {
        var root = Directory.CreateTempSubdirectory("netclaw-causal-fallback-");
        try
        {
            var target = Path.Combine(root.FullName, "target");
            var alias = Path.Combine(root.FullName, "alias");
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(alias, target);
            var approvalService = GrantEveryShellCandidate();
            var executor = CreateApprovalGatedShellExecutor(
                approvalService,
                safeVerbs: SafeVerbList.FromVerbs(
                    ApprovalShell.Bash,
                    ["head"]));
            var call = new FunctionCallContent(
                "call-causal-intent-symlink-fallback",
                "shell_execute",
                ToolInput.Create(
                    "Command",
                    $"cd {alias} && inspect; cd /tmp && collect; head result.log",
                    "WorkingDirectory",
                    "/work"));

            var decision = await executor.EvaluateAuthorizationAsync(
                call,
                CreateInteractivePersonalContext("signalr/causal-intent-symlink-fallback"),
                TestContext.Current.CancellationToken);

            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
            Assert.True(Assert.IsType<ToolApprovalContext>(decision.ApprovalContext).IsMessy);
            Assert.Null(approvalService.LastRequest);
            Assert.DoesNotContain(
                decision.ShellPolicyTrace.Rows,
                row => row.ScopeRelation == ShellScopeRelation.UnderIntentRoot);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "This test requires native POSIX symbolic-link behavior.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only symbolic-link semantics")]
    public async Task Protected_path_denial_precedes_symlink_fallback_rejection()
    {
        var root = Directory.CreateTempSubdirectory("netclaw-causal-denied-fallback-");
        try
        {
            var denied = Path.Combine(root.FullName, "denied");
            var alias = Path.Combine(root.FullName, "alias");
            Directory.CreateDirectory(denied);
            Directory.CreateSymbolicLink(alias, denied);
            var approvalService = GrantEveryShellCandidate();
            var executor = CreateApprovalGatedShellExecutor(
                approvalService,
                safeVerbs: SafeVerbList.FromVerbs(
                    ApprovalShell.Bash,
                    ["head"]),
                deniedPaths: [denied]);
            var call = new FunctionCallContent(
                "call-causal-intent-denied-fallback",
                "shell_execute",
                ToolInput.Create(
                    "Command",
                    "cd /tmp && inspect; head result.log",
                    "WorkingDirectory",
                    alias));

            var decision = await executor.EvaluateAuthorizationAsync(
                call,
                CreateInteractivePersonalContext("signalr/causal-intent-denied-fallback"),
                TestContext.Current.CancellationToken);

            Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
            Assert.Equal("shell_references_protected_path", decision.DenyReason);
            Assert.Null(approvalService.LastRequest);
            Assert.Null(decision.ApprovalContext);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [SlopwatchSuppress("SW001", "This test pins Bash causal approval intent on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only shell directory semantics")]
    public async Task Causal_intent_does_not_grant_reviewed_safe_authority_to_headless_runs()
    {
        var approvalService = GrantEveryShellCandidate();
        var executor = CreateApprovalGatedShellExecutor(
            approvalService,
            safeVerbs: SafeVerbList.FromVerbs(
                ApprovalShell.Bash,
                ["head"]),
            shellTrustZonePolicy: new AllowAllShellTrustZonePolicy());
        var call = new FunctionCallContent(
            "call-causal-intent-headless",
            "shell_execute",
            ToolInput.Create(
                "Command",
                "cd /tmp && inspect; head result.log",
                "WorkingDirectory",
                "/work"));
        var context = TestToolExecutionContext.CreateBound(
            "webhook/causal-intent-headless",
            null,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(false)
            });

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("shell_unresolved_trust_zone_input", decision.DenyReason);
        Assert.Null(approvalService.LastRequest);
        Assert.DoesNotContain(
            decision.ShellPolicyTrace.Rows,
            row => row.ScopeRelation == ShellScopeRelation.UnderIntentRoot);
    }

    [Fact]
    public async Task Native_power_shell_directory_change_does_not_create_causal_approval_scope()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            "C:\\Program Files\\PowerShell\\7\\pwsh.exe",
            PwshDialect.PowerShell7);
        var approvalService = GrantEveryShellCandidate();
        var executor = CreateApprovalGatedShellExecutor(
            environment,
            approvalService,
            safeVerbs: SafeVerbList.FromVerbs(
                ApprovalShell.PowerShell,
                ["Get-Content"]));
        var call = new FunctionCallContent(
            "call-native-power-shell-causal-scope",
            "shell_execute",
            ToolInput.Create(
                "Command",
                "Set-Location C:\\Temp; Get-Content result.log",
                "WorkingDirectory",
                "C:\\work"));

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            CreateInteractivePersonalContext("signalr/native-power-shell-causal-scope"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        Assert.True(Assert.IsType<ToolApprovalContext>(decision.ApprovalContext).IsMessy);
        Assert.Null(approvalService.LastRequest);
        Assert.DoesNotContain(
            decision.ShellPolicyTrace.Rows,
            row => row.ScopeRelation == ShellScopeRelation.UnderIntentRoot);
    }

    [Fact]
    public async Task Authorization_trace_carries_persistent_grant_timestamp()
    {
        var grantTimestamp = new DateTimeOffset(2026, 8, 13, 7, 0, 0, TimeSpan.Zero);
        var approvalService = new FixedShellApprovalService(request =>
        {
            var matches = request.Candidates.Select(candidate =>
            {
                var shell = Assert.IsType<ApprovalShell>(candidate.Candidate.Shell);
                var tokens = Assert.IsAssignableFrom<IReadOnlyList<string>>(candidate.Candidate.VerbTokens);
                var entry = ApprovalEntry.CreateTokenPrefix(
                    shell,
                    tokens,
                    directory: null,
                    grantTimestamp);
                return new ShellGrantCandidateMatch(
                    candidate.CandidateId,
                    new ToolApprovalMatch(candidate.Candidate.Verb, "persistent", entry.FormatScope()),
                    ShellCoverageKind.PersistentGlobal,
                    NearMisses: [])
                {
                    GrantCreatedAt = grantTimestamp
                };
            }).ToArray();
            return new ShellApprovalMatchResult(
                new PersistentGrantStoreStatus.Ready(),
                Array.AsReadOnly(matches));
        });
        var logger = new RecordingLogger<DispatchingToolExecutor>();
        var executor = CreateApprovalGatedShellExecutor(approvalService, logger);
        var call = new FunctionCallContent(
            "call-trace-persistent-grant",
            "shell_execute",
            ToolInput.Create("Command", "git status"));

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            CreateInteractivePersonalContext("signalr/trace-persistent-grant"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Allowed, decision.Outcome);
        var grantRow = Assert.Single(
            decision.ShellPolicyTrace.Rows,
            row => row.Stage == ShellPolicyTraceStage.StoredGrantMatch);
        Assert.Equal(ShellPolicyTraceReason.PersistentGlobalGrant, grantRow.Reason);
        Assert.Equal(grantTimestamp, grantRow.GrantTimestamp);
        Assert.Contains(logger.Entries, entry =>
            Equals(entry.GetValueOrDefault("GrantTimestamp"), grantTimestamp));
    }

    [Fact]
    public async Task Authorization_trace_projects_redacted_near_miss_without_raw_evidence()
    {
        const string rawGrantPath = "/private/ghp_12345678901234567890/path";
        var grantTimestamp = new DateTimeOffset(2026, 8, 13, 7, 30, 0, TimeSpan.Zero);
        var approvalService = new FixedShellApprovalService(request =>
            new ShellApprovalMatchResult(
                new PersistentGrantStoreStatus.Ready(),
                Array.AsReadOnly(request.Candidates.Select(candidate =>
                {
                    var shell = Assert.IsType<ApprovalShell>(candidate.Candidate.Shell);
                    var grant = ApprovalEntry.CreateTokenPrefix(
                        shell,
                        ["git", "push"],
                        rawGrantPath,
                        grantTimestamp);
                    return new ShellGrantCandidateMatch(
                        candidate.CandidateId,
                        Match: null,
                        GrantCoverage: null,
                        NearMisses:
                        [
                            new ShellApprovalNearMiss(
                                grant,
                                ShellApprovalNearMissReason.OutsideDirectory)
                        ]);
                }).ToArray())));
        var logger = new RecordingLogger<DispatchingToolExecutor>();
        var executor = CreateApprovalGatedShellExecutor(approvalService, logger);
        var call = new FunctionCallContent(
            "call-trace-near-miss",
            "shell_execute",
            ToolInput.Create("Command", "git push"));

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            CreateInteractivePersonalContext("signalr/trace-near-miss"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        var nearMissRow = Assert.Single(
            decision.ShellPolicyTrace.Rows,
            row => row.Stage == ShellPolicyTraceStage.StoredGrantMatch);
        Assert.Equal(ShellPolicyTraceOutcome.Uncovered, nearMissRow.Outcome);
        Assert.Equal(ShellPolicyTraceReason.OutsideDirectory, nearMissRow.Reason);
        Assert.Equal(ShellCoverageKind.PersistentFolder, nearMissRow.Coverage);
        Assert.Equal(ShellScopeRelation.OutsideGrantRoot, nearMissRow.ScopeRelation);
        Assert.Equal(grantTimestamp, nearMissRow.GrantTimestamp);
        Assert.NotNull(decision.ApprovalContext);
        Assert.DoesNotContain(
            typeof(ToolApprovalContext).GetProperties(),
            property => property.Name.Contains("Trace", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(Netclaw.Actors.Sessions.SessionProtocol.ToolApprovalRequested).GetProperties(),
            property => property.Name.Contains("Trace", StringComparison.Ordinal));

        var logValues = string.Join(
            '\n',
            logger.Entries.SelectMany(static entry => entry.Values)
                .Select(static value => value?.ToString() ?? string.Empty));
        Assert.DoesNotContain(rawGrantPath, logValues, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_policy_trace_caps_rows_without_authority_change()
    {
        var builder = new ShellPolicyDecisionTraceBuilder();
        for (var index = 0; index < 300; index++)
        {
            builder.AddCoverage(
                ShellPolicyTraceStage.ReviewedSafePolicy,
                new ShellPolicyCandidate(
                    new ShellPolicyCandidateId(index),
                    BashCandidate($"/usr/bin/tool-{index}"),
                    SourceOccurrence: null),
                ShellCoverageKind.ReviewedSafePolicy,
                ShellPolicyReason.ReviewedSafePhrase,
                ShellScopeRelation.UnderRealRoot);
        }

        var decision = ToolAuthorizationDecision.Allow(ToolAllowReason.SafeVerbInTrustedScope);
        var trace = builder.Complete(decision);

        Assert.Equal(ToolAuthorizationOutcome.Allowed, decision.Outcome);
        Assert.Equal(ShellPolicyDecisionTraceBuilder.MaximumRows, trace.Rows.Count);
        Assert.Single(trace.Rows, row => row.Outcome == ShellPolicyTraceOutcome.TraceTruncated);
        var finalRow = trace.Rows[^1];
        Assert.Equal(ShellPolicyTraceStage.Completion, finalRow.Stage);
        Assert.Equal(ShellPolicyTraceOutcome.Allow, finalRow.Outcome);
    }

    [Fact]
    public void Shell_policy_trace_sanitizes_controls_secrets_and_invalid_unicode()
    {
        const string secret = "ghp_12345678901234567890";
        var input = $"{secret}\r\n\u202Ebad\uD800{new string('x', 200)}";

        var sanitized = ShellPolicyDecisionTraceBuilder.SanitizeText(input);

        Assert.DoesNotContain(secret, sanitized, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", sanitized, StringComparison.Ordinal);
        Assert.Contains("\\u000D\\u000A", sanitized, StringComparison.Ordinal);
        Assert.Contains("\\u202E", sanitized, StringComparison.Ordinal);
        Assert.Contains("\\uD800", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', sanitized);
        Assert.DoesNotContain('\n', sanitized);
        Assert.True(sanitized.Length <= ShellPolicyDecisionTraceBuilder.MaximumTextCodeUnits);
    }

    [Fact]
    public void Shell_policy_trace_fails_closed_before_a_long_private_key_can_be_truncated()
    {
        var privateKey = $"-----BEGIN PRIVATE KEY-----\n{new string('A', 600)}\n-----END PRIVATE KEY-----";

        var sanitized = ShellPolicyDecisionTraceBuilder.SanitizeText(privateKey);

        Assert.Equal("***REDACTED***", sanitized);
        Assert.DoesNotContain("PRIVATE KEY", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAAAAAA", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_policy_trace_logs_one_row_for_malicious_executable_text()
    {
        const string secret = "ghp_12345678901234567890";
        var logger = new RecordingLogger<DispatchingToolExecutor>();
        var executor = CreateApprovalGatedShellExecutor(logger: logger);
        var builder = new ShellPolicyDecisionTraceBuilder();
        builder.AddCoverage(
            ShellPolicyTraceStage.ReviewedSafePolicy,
            new ShellPolicyCandidate(
                new ShellPolicyCandidateId(0),
                BashCandidate($"/usr/bin/{secret}\r\n\u202Espoof"),
                SourceOccurrence: null),
            ShellCoverageKind.ReviewedSafePolicy,
            ShellPolicyReason.ReviewedSafePhrase,
            ShellScopeRelation.UnderRealRoot);
        var trace = builder.Complete(
            ToolAuthorizationDecision.Allow(ToolAllowReason.SafeVerbInTrustedScope));

        executor.LogShellPolicyTrace(trace);

        Assert.Equal(trace.Rows.Count, logger.Entries.Count);
        var candidateLog = logger.Entries[0];
        var executable = Assert.IsType<string>(candidateLog["ExecutableBasename"]);
        Assert.DoesNotContain(secret, executable, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", executable, StringComparison.Ordinal);
        Assert.Contains("\\u000D\\u000A", executable, StringComparison.Ordinal);
        Assert.Contains("\\u202E", executable, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', executable);
        Assert.DoesNotContain('\n', executable);
    }

    [Theory]
    [InlineData(nameof(ShellApprovalNearMissReason.TokenMismatch), nameof(ShellPolicyTraceReason.TokenMismatch))]
    [InlineData(nameof(ShellApprovalNearMissReason.ShellMismatch), nameof(ShellPolicyTraceReason.ShellMismatch))]
    public void Shell_policy_trace_maps_typed_phrase_near_misses(
        string nearMissReasonName,
        string traceReasonName)
    {
        var nearMissReason = Enum.Parse<ShellApprovalNearMissReason>(nearMissReasonName);
        var traceReason = Enum.Parse<ShellPolicyTraceReason>(traceReasonName);
        var candidate = new ShellPolicyCandidate(
            new ShellPolicyCandidateId(0),
            BashCandidate("git push"),
            SourceOccurrence: null);
        var grant = ApprovalEntry.CreateTokenPrefix(
            ApprovalShell.Bash,
            ["git", "status"]);
        var actorMatch = new ShellGrantCandidateMatch(
            candidate.Id,
            Match: null,
            GrantCoverage: null,
            NearMisses: [new ShellApprovalNearMiss(grant, nearMissReason)]);
        var builder = new ShellPolicyDecisionTraceBuilder();

        builder.AddActorEvidence(candidate, actorMatch);
        var trace = builder.Complete(
            ToolAuthorizationDecision.Allow(ToolAllowReason.SafeVerbInTrustedScope));

        Assert.Equal(traceReason, trace.Rows[0].Reason);
        Assert.Equal(ShellPolicyTraceOutcome.Uncovered, trace.Rows[0].Outcome);
    }

    [Fact]
    public async Task Authorization_evaluation_denies_duplicate_actor_candidate_id()
    {
        var approvalService = new FixedShellApprovalService(request =>
        {
            var duplicateId = request.Candidates[0].CandidateId;
            return new ShellApprovalMatchResult(
                new PersistentGrantStoreStatus.Ready(),
                Array.AsReadOnly(request.Candidates.Select(candidate =>
                    new ShellGrantCandidateMatch(
                        duplicateId,
                        Match: null,
                        GrantCoverage: null,
                        NearMisses: [])).ToArray()));
        });
        var executor = CreateApprovalGatedShellExecutor(approvalService);
        var call = new FunctionCallContent(
            "call-duplicate-candidate-id",
            "shell_execute",
            ToolInput.Create("Command", "git status && git push"));

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            CreateInteractivePersonalContext("signalr/duplicate-candidate-id"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("internal_policy_failure", decision.DenyReason);
    }

    [Fact]
    public async Task Authorization_evaluation_denies_mismatched_actor_match()
    {
        var approvalService = new FixedShellApprovalService(request =>
            new ShellApprovalMatchResult(
                new PersistentGrantStoreStatus.Ready(),
                Array.AsReadOnly(request.Candidates.Select(candidate =>
                    new ShellGrantCandidateMatch(
                        candidate.CandidateId,
                        new ToolApprovalMatch("unrelated", "persistent", "anywhere"),
                        ShellCoverageKind.Session,
                        NearMisses: [])).ToArray())));
        var executor = CreateApprovalGatedShellExecutor(approvalService);
        var call = new FunctionCallContent(
            "call-mismatched-actor-match",
            "shell_execute",
            ToolInput.Create("Command", "git status"));

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            CreateInteractivePersonalContext("signalr/mismatched-actor-match"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("internal_policy_failure", decision.DenyReason);
    }

    [Theory]
    [InlineData("this chat", false)]
    [InlineData("garbage anywhere", true)]
    public async Task Authorization_evaluation_denies_malformed_persistent_actor_scope(
        string scope,
        bool claimsGlobalScope)
    {
        var coverage = claimsGlobalScope
            ? ShellCoverageKind.PersistentGlobal
            : ShellCoverageKind.PersistentFolder;
        var approvalService = new FixedShellApprovalService(request =>
            new ShellApprovalMatchResult(
                new PersistentGrantStoreStatus.Ready(),
                Array.AsReadOnly(request.Candidates.Select(candidate =>
                    new ShellGrantCandidateMatch(
                        candidate.CandidateId,
                        new ToolApprovalMatch(candidate.Candidate.Verb, "persistent", scope),
                        coverage,
                        NearMisses: [])).ToArray())));
        var executor = CreateApprovalGatedShellExecutor(approvalService);
        var call = new FunctionCallContent(
            "call-malformed-persistent-scope",
            "shell_execute",
            ToolInput.Create("Command", "git status"));

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            CreateInteractivePersonalContext("signalr/malformed-persistent-scope"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("internal_policy_failure", decision.DenyReason);
    }

    [Fact]
    public async Task Authorization_evaluation_denies_invalid_store_failure_enum()
    {
        var approvalService = new FixedShellApprovalService(request =>
            new ShellApprovalMatchResult(
                new PersistentGrantStoreStatus.Unavailable((ApprovalStoreFailure)999),
                Array.AsReadOnly(request.Candidates.Select(candidate =>
                    new ShellGrantCandidateMatch(
                        candidate.CandidateId,
                        new ToolApprovalMatch(candidate.Candidate.Verb, "session", "this chat"),
                        ShellCoverageKind.Session,
                        NearMisses: [])).ToArray())));
        var executor = CreateApprovalGatedShellExecutor(approvalService);
        var call = new FunctionCallContent(
            "call-invalid-store-enum",
            "shell_execute",
            ToolInput.Create("Command", "git status"));

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            CreateInteractivePersonalContext("signalr/invalid-store-enum"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("internal_policy_failure", decision.DenyReason);
    }

    [Fact]
    public async Task Authorization_evaluation_denies_uncovered_candidate_when_store_is_unavailable()
    {
        var approvalService = new FixedShellApprovalService(request =>
            new ShellApprovalMatchResult(
                new PersistentGrantStoreStatus.Unavailable(ApprovalStoreFailure.InvalidData),
                Array.AsReadOnly(request.Candidates.Select(candidate =>
                    new ShellGrantCandidateMatch(
                        candidate.CandidateId,
                        Match: null,
                        GrantCoverage: null,
                        NearMisses: [])).ToArray())));
        var executor = CreateApprovalGatedShellExecutor(approvalService);
        var call = new FunctionCallContent(
            "call-unavailable-store-miss",
            "shell_execute",
            ToolInput.Create("Command", "git push"));

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            CreateInteractivePersonalContext("signalr/unavailable-store-miss"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("approval_store_unavailable", decision.DenyReason);
    }

    [SlopwatchSuppress("SW001", "This test verifies Bash parser directory attribution, which does not apply to the Windows shell parser.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only shell directory semantics")]
    public async Task Authorization_evaluation_preserves_directory_for_duplicate_verb_candidates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netclaw-prompt-scope-{Guid.NewGuid():N}");
        var approvedDirectory = Path.Combine(root, "approved");
        var unapprovedDirectory = Path.Combine(root, "unapproved");
        Directory.CreateDirectory(approvedDirectory);
        Directory.CreateDirectory(unapprovedDirectory);

        try
        {
            var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
            config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
            {
                ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
                {
                    ["shell_execute"] = ToolApprovalMode.Approval
                }
            };
            var registry = new ToolRegistry();
            registry.WithFirstPartyTools(
                config,
                new NetclawPaths(),
                new ToolPathPolicy([]),
                new ShellCommandPolicy());
            var approvedScope = ApprovalEntry.CreateTokenPrefix(
                ApprovalShell.Bash,
                ["git", "push"],
                approvedDirectory).FormatScope();
            var approvedMatch = new ToolApprovalMatch("git push", "persistent", approvedScope);
            var approvedCandidate = BashCandidate("git push", approvedDirectory);
            var unapprovedCandidate = BashCandidate("git push", unapprovedDirectory);
            var approvalService = new FixedApprovalService(
                new ToolApprovalCheckResult(
                    ["git push"],
                    [approvedMatch])
                {
                    CandidateChecks =
                    [
                        new ToolApprovalCandidateCheck(approvedCandidate, approvedMatch),
                        new ToolApprovalCandidateCheck(unapprovedCandidate, ApprovedMatch: null)
                    ]
                });
            var executor = new DispatchingToolExecutor(
                registry,
                new ToolAccessPolicy(
                    config,
                    new EffectivePolicyDefaults(
                        DeploymentPosture.Personal,
                        TrustAudience.Personal,
                        ShellExecutionMode.HostAllowed,
                        UsedStrictFallback: false),
                    new ShellCommandPolicy(),
                    new ToolPathPolicy([])),
                approvalService);
            var call = new FunctionCallContent(
                "call-duplicate-verb-scopes",
                "shell_execute",
                ToolInput.Create(
                    "Command",
                    $"git -C {approvedDirectory} push && git -C {unapprovedDirectory} push"));
            var context = CreateInteractivePersonalContext("signalr/thread-duplicate-verb-scopes");

            var decision = await executor.EvaluateAuthorizationAsync(
                call,
                context,
                TestContext.Current.CancellationToken);

            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
            var approvalContext = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
            Assert.Equal(["git push"], approvalContext.Patterns);
            Assert.Equal(["git push"], approvalContext.CandidateVerbs);
            Assert.Equal([unapprovedCandidate], approvalContext.Candidates);
            Assert.Equal([approvedMatch], decision.ApprovalMatches);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Authorization_evaluation_denies_inconsistent_candidate_result()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(
            config,
            new NetclawPaths(),
            new ToolPathPolicy([]),
            new ShellCommandPolicy());
        var approvalService = new FixedApprovalService(
            new ToolApprovalCheckResult(
                ["git push"],
                [])
            {
                CandidateChecks =
                [
                    new ToolApprovalCandidateCheck(
                        new ApprovalCandidate("git push", Directory: null),
                        ApprovedMatch: null)
                ]
            });
        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])),
            approvalService);
        var call = new FunctionCallContent(
            "call-inconsistent-partial-approval",
            "shell_execute",
            ToolInput.Create("Command", "git status && git push"));
        var context = CreateInteractivePersonalContext("signalr/thread-inconsistent-partial-approval");

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("internal_policy_failure", decision.DenyReason);
    }

    [Fact]
    public async Task Authorization_evaluation_denies_inconsistent_parser_tokens()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(
            config,
            new NetclawPaths(),
            new ToolPathPolicy([]),
            new ShellCommandPolicy());
        var forgedCandidate = new ApprovalCandidate("git status", Directory: null)
        {
            Shell = ApprovalShell.Bash,
            VerbTokens = Array.AsReadOnly(["git", "push"]),
        };
        var approvalService = new FixedApprovalService(
            new ToolApprovalCheckResult(
                ["git status", "git push"],
                [])
            {
                CandidateChecks =
                [
                    new ToolApprovalCandidateCheck(forgedCandidate, ApprovedMatch: null),
                    new ToolApprovalCandidateCheck(BashCandidate("git push"), ApprovedMatch: null)
                ]
            });
        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])),
            approvalService);
        var call = new FunctionCallContent(
            "call-inconsistent-parser-tokens",
            "shell_execute",
            ToolInput.Create("Command", "git status && git push"));
        var context = CreateInteractivePersonalContext("signalr/thread-inconsistent-parser-tokens");

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("internal_policy_failure", decision.DenyReason);
    }

    [Fact]
    public async Task Authorization_evaluation_rejects_inconsistent_all_approved_result()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(
            config,
            new NetclawPaths(),
            new ToolPathPolicy([]),
            new ShellCommandPolicy());
        var approvalService = new FixedApprovalService(
            new ToolApprovalCheckResult(
                [],
                [])
            {
                CandidateChecks =
                [
                    new ToolApprovalCandidateCheck(
                        new ApprovalCandidate("git push", Directory: null),
                        ApprovedMatch: null)
                ]
            });
        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])),
            approvalService);
        var call = new FunctionCallContent(
            "call-inconsistent-all-approved",
            "shell_execute",
            ToolInput.Create("Command", "git status && git push"));
        var context = CreateInteractivePersonalContext("signalr/thread-inconsistent-all-approved");

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal("internal_policy_failure", decision.DenyReason);
    }

    [Fact]
    public async Task Authorization_evaluation_logs_allow_reason_before_execution()
    {
        var config = new ToolConfig();
        var registry = new ToolRegistry();
        var executionCount = 0;
        registry.Register(
            AIFunctionFactory.Create(() =>
            {
                executionCount++;
                return "ok";
            }, "telemetry_probe"),
            "test");
        var logger = new RecordingLogger<DispatchingToolExecutor>();
        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])),
            logger: logger);
        var call = new FunctionCallContent(
            "call-authorization-telemetry",
            "telemetry_probe",
            ToolInput.Empty());
        var context = TestToolExecutionContext.CreateBound(
            "signalr/thread-authorization-telemetry",
            null,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal });

        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, executionCount);
        Assert.Equal(ToolAuthorizationOutcome.Allowed, decision.Outcome);
        Assert.Equal(ToolAllowReason.PolicyAuto, decision.AllowReason);
        var log = Assert.Single(logger.Entries);
        Assert.Equal(nameof(ToolAuthorizationOutcome.Allowed), log["AuthorizationOutcome"]);
        Assert.Equal(nameof(ToolAllowReason.PolicyAuto), log["AuthorizationReason"]);
        Assert.Equal(
            ToolAllowReason.PolicyAuto.GetDescription(),
            log["AuthorizationExplanation"]);
    }

    [Fact]
    public async Task File_read_is_denied_outside_session_directory_in_public_context()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"netclaw-public-read-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(filePath, "secret", TestContext.Current.CancellationToken);

        try
        {
            var toolCall = new FunctionCallContent(
                "call-file-read-deny", "file_read",
                ToolInput.Create("Path", filePath));

            var sessionDir = Path.Combine(Path.GetTempPath(), $"netclaw-public-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(sessionDir);

            var context = TestToolExecutionContext.CreateBound("slack/thread-1", sessionDir, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Public,
                Boundary = TrustBoundary.Public,
                ChannelType = "slack"
            });

            var result = await _restrictedExecutor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);
            Assert.Contains("Public trust context", result);
            Assert.Contains("session directory", result);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task File_write_is_denied_outside_session_directory_in_team_context()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"netclaw-team-write-{Guid.NewGuid():N}.txt");

        try
        {
            var toolCall = new FunctionCallContent(
                "call-file-write-deny", "file_write",
                ToolInput.Create("Path", filePath, "Content", "blocked"));

            var sessionDir = Path.Combine(Path.GetTempPath(), $"netclaw-team-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(sessionDir);

            var context = TestToolExecutionContext.CreateBound("slack/thread-1", sessionDir, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Team,
                Boundary = TrustBoundary.Team,
                ChannelType = "slack"
            });

            var result = await _restrictedExecutor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);
            Assert.Contains("Team trust context", result);
            Assert.Contains("session directory", result);
            Assert.False(File.Exists(filePath));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Routes_file_write()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"netclaw-dispatch-{Guid.NewGuid():N}.txt");
        try
        {
            var toolCall = new FunctionCallContent(
                "call-3", "file_write",
                ToolInput.Create("Path", filePath, "Content", "dispatch test"));

            var sessionDir = Path.Combine(Path.GetTempPath(), $"netclaw-dispatch-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(sessionDir);

            var context = TestToolExecutionContext.CreateBound("signalr/thread-1", sessionDir, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "signalr"
            });

            var result = await _executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);

            Assert.Contains("Successfully wrote", result);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Unknown_tool_returns_error_string()
    {
        var toolCall = new FunctionCallContent(
            "call-4", "unknown_tool",
            ToolInput.Create("arg", "value"));

        var result = await _executor.ExecuteAsync(
            toolCall,
            TestToolExecutionContext.CreateUnbound(),
            TestContext.Current.CancellationToken);

        Assert.Equal("Unknown tool: unknown_tool", result);
    }

    [Fact]
    public void Team_profile_exposes_file_tools_and_hides_shell_and_webhooks()
    {
        // Default Team profile (no explicit AllowedTools override).
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };

        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(),
            new ToolPathPolicy([]));

        var registry = new ToolRegistry();
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-webhook-tools-{Guid.NewGuid():N}"));
        paths.EnsureDirectoriesExist();
        registry.WithFirstPartyTools(config, paths: paths, pathPolicy: new ToolPathPolicy([]), shellCommandPolicy: new ShellCommandPolicy(), toolAccessPolicy: policy, webhookRouteStore: new WebhookRouteStore(paths));

        Assert.True(policy.IsToolExposed(registry.GetByName("file_read")!, TrustAudience.Team));
        Assert.True(policy.IsToolExposed(registry.GetByName("file_list")!, TrustAudience.Team));
        Assert.True(policy.IsToolExposed(registry.GetByName("file_write")!, TrustAudience.Team));
        Assert.True(policy.IsToolExposed(registry.GetByName("file_edit")!, TrustAudience.Team));
        Assert.True(policy.IsToolExposed(registry.GetByName("attach_file")!, TrustAudience.Team));
        Assert.True(policy.IsToolExposed(registry.GetByName("set_working_directory")!, TrustAudience.Team));
        Assert.True(policy.IsToolExposed(registry.GetByName("web_fetch")!, TrustAudience.Team));
        Assert.False(policy.IsToolExposed(registry.GetByName("shell_execute")!, TrustAudience.Team));
        Assert.False(policy.IsToolExposed(registry.GetByName("set_webhook")!, TrustAudience.Team));
        Assert.False(policy.IsToolExposed(registry.GetByName("list_webhooks")!, TrustAudience.Team));
        Assert.False(policy.IsToolExposed(registry.GetByName("delete_webhook")!, TrustAudience.Team));
    }

    [Fact]
    public void Public_profile_exposes_read_tools_and_hides_mutation_tools()
    {
        // Default Public profile — least-trusted: read, enumerate, attach only.
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };

        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(),
            new ToolPathPolicy([]));

        var registry = new ToolRegistry();
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), $"netclaw-public-tools-{Guid.NewGuid():N}"));
        paths.EnsureDirectoriesExist();
        registry.WithFirstPartyTools(config, paths: paths, pathPolicy: new ToolPathPolicy([]), shellCommandPolicy: new ShellCommandPolicy(), toolAccessPolicy: policy, webhookRouteStore: new WebhookRouteStore(paths));

        Assert.True(policy.IsToolExposed(registry.GetByName("file_read")!, TrustAudience.Public));
        Assert.True(policy.IsToolExposed(registry.GetByName("file_list")!, TrustAudience.Public));
        Assert.True(policy.IsToolExposed(registry.GetByName("attach_file")!, TrustAudience.Public));
        Assert.False(policy.IsToolExposed(registry.GetByName("file_write")!, TrustAudience.Public));
        Assert.False(policy.IsToolExposed(registry.GetByName("file_edit")!, TrustAudience.Public));
        Assert.False(policy.IsToolExposed(registry.GetByName("shell_execute")!, TrustAudience.Public));
        Assert.False(policy.IsToolExposed(registry.GetByName("set_working_directory")!, TrustAudience.Public));
        Assert.False(policy.IsToolExposed(registry.GetByName("web_fetch")!, TrustAudience.Public));
    }

    [Fact]
    public async Task Mcp_tool_is_denied_when_server_not_allowed_for_audience()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create(() => "ok", "search_memories"),
            "memorizer",
            "search_memories",
            invoker: new RecordingMcpToolInvoker("ok")));

        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])));

        var toolCall = new FunctionCallContent("call-mcp-deny", "memorizer/search_memories", ToolInput.Empty());
        var context = TestToolExecutionContext.CreateBound("slack/thread-1", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            ChannelType = "slack"
        });

        var ex = await Assert.ThrowsAsync<ToolAccessDeniedException>(() => executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));
        Assert.Equal("mcp_server_not_allowed_for_audience_profile", ex.DenyReason);
    }

    [Fact]
    public async Task One_time_approval_allows_immediate_retry_only()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };

        var commandPolicy = new ShellCommandPolicy(ShellEnvironment);
        var pathPolicy = new ToolPathPolicy(ShellEnvironment, []);
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config, new NetclawPaths(), pathPolicy, commandPolicy);

        var system = ActorSystem.Create($"tool-approval-{Guid.NewGuid():N}");
        try
        {
            var approvalActor = system.ActorOf(ToolApprovalActor.CreateProps(), "tool-approval");
            var approvalService = new AkkaToolApprovalService(new StubRequiredActor(approvalActor), ShellEnvironment);
            var executor = new DispatchingToolExecutor(
                registry,
                new ToolAccessPolicy(
                    config,
                    new EffectivePolicyDefaults(
                        DeploymentPosture.Personal,
                        TrustAudience.Personal,
                        ShellExecutionMode.HostAllowed,
                        UsedStrictFallback: false),
                    commandPolicy,
                    pathPolicy),
                approvalService);

            var toolCall = new FunctionCallContent(
                "call-approve-once",
                "shell_execute",
                // Use a non-side-effect verb (echo/printf/:/true/false
                // auto-allow at the matcher level under v2.1) so the
                // approval flow this test exercises actually triggers.
                ToolInput.Create("Command", "git status"));

            var context = TestToolExecutionContext.CreateBound("signalr/thread-1", null, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "signalr",
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            });

            var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));

            context.OneTimeApprovedToolName = toolCall.Name;
            context.SetOneTimeApprovedPatterns(OneTimeApprovalKeys.Create(firstAttempt.ApprovalContext));

            // The one-time-approval bypass should let the call succeed.
            // Output text varies by test environment (git status); meaningful
            // assertion is that no ToolApprovalRequiredException is thrown.
            _ = await executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);

            context.OneTimeApprovedToolName = null;
            context.SetOneTimeApprovedPatterns([]);

            await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task One_time_approval_bypasses_policy_for_matching_shell_patterns()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };

        var commandPolicy = new ShellCommandPolicy(ShellEnvironment);
        var pathPolicy = new ToolPathPolicy(ShellEnvironment, []);
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config, new NetclawPaths(), pathPolicy, commandPolicy);

        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                commandPolicy,
                pathPolicy));

        var toolCall = new FunctionCallContent(
            "call-approve-once-bypass",
            "shell_execute",
            ToolInput.Create("Command", "echo bypass"));

        var context = TestToolExecutionContext.CreateBound("signalr/thread-1", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "signalr",
            InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
        });

        var initialDecision = await executor.EvaluateAuthorizationAsync(
            toolCall,
            context,
            TestContext.Current.CancellationToken);
        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, initialDecision.Outcome);
        Assert.NotNull(initialDecision.ApprovalContext);

        var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
            executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));

        context.OneTimeApprovedToolName = toolCall.Name;
        context.SetOneTimeApprovedPatterns(OneTimeApprovalKeys.Create(firstAttempt.ApprovalContext));

        var decision = await executor.EvaluateAuthorizationAsync(
            toolCall,
            context,
            TestContext.Current.CancellationToken);
        var retryResult = await executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Allowed, decision.Outcome);
        Assert.Equal(ToolAllowReason.OneTimeApproval, decision.AllowReason);
        Assert.Contains("bypass", retryResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task One_time_approval_remains_valid_when_persistent_store_is_unavailable()
    {
        var initialExecutor = CreateApprovalGatedShellExecutor(new FixedApprovalService(
            new ToolApprovalCheckResult(["git push"], [])));
        var context = CreateInteractivePersonalContext("signalr/store-unavailable");
        var toolCall = new FunctionCallContent(
            "call-store-unavailable-once",
            "shell_execute",
            ToolInput.Create("Command", "git push"));

        var initial = await initialExecutor.EvaluateAuthorizationAsync(
            toolCall,
            context,
            TestContext.Current.CancellationToken);
        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, initial.Outcome);
        Assert.NotNull(initial.ApprovalContext);
        context.OneTimeApprovedToolName = toolCall.Name;
        context.SetOneTimeApprovedPatterns(OneTimeApprovalKeys.Create(initial.ApprovalContext));

        var unavailableExecutor = CreateApprovalGatedShellExecutor(new FixedApprovalService(
            new ToolApprovalCheckResult(["git push"], [])
            {
                PersistentStoreFailure = ApprovalStoreFailure.InvalidData,
            }));
        var retry = await unavailableExecutor.EvaluateAuthorizationAsync(
            toolCall,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolAuthorizationOutcome.Allowed, retry.Outcome);
        Assert.Equal(ToolAllowReason.OneTimeApproval, retry.AllowReason);
    }

    [Fact]
    public async Task One_time_approval_bypasses_policy_for_path_aware_file_patterns()
    {
        var controlPlaneRoot = Path.Combine(Path.GetTempPath(), $"netclaw-control-plane-{Guid.NewGuid():N}");
        var targetPath = Path.Combine(controlPlaneRoot, "netclaw.json");
        var secondPath = Path.Combine(controlPlaneRoot, "devices.json");
        Directory.CreateDirectory(controlPlaneRoot);

        try
        {
            var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
            config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
            {
                ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
                {
                    ["shell_execute"] = ToolApprovalMode.Approval
                }
            };

            var registry = new ToolRegistry();
            registry.WithFirstPartyTools(config, new NetclawPaths(), new ToolPathPolicy([]), new ShellCommandPolicy());

            var executor = new DispatchingToolExecutor(
                registry,
                new ToolAccessPolicy(
                    config,
                    new EffectivePolicyDefaults(
                        DeploymentPosture.Personal,
                        TrustAudience.Personal,
                        ShellExecutionMode.HostAllowed,
                        UsedStrictFallback: false),
                    new ShellCommandPolicy(),
                    new ToolPathPolicy([]),
                    fileApprovalMatcher: new FilePathApprovalMatcher(controlPlaneRoot)));

            var toolCall = new FunctionCallContent(
                "call-file-approve-once-bypass",
                "file_write",
                ToolInput.Create("Path", targetPath, "Content", "approved once"));

            var context = TestToolExecutionContext.CreateBound("signalr/thread-1", null, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "signalr",
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            });

            var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));

            context.OneTimeApprovedToolName = toolCall.Name;
            context.SetOneTimeApprovedPatterns(OneTimeApprovalKeys.Create(firstAttempt.ApprovalContext));

            var retryResult = await executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);
            Assert.Contains("Successfully wrote", retryResult, StringComparison.Ordinal);
            Assert.True(File.Exists(targetPath));

            var secondCall = new FunctionCallContent(
                "call-file-approve-once-bypass-second",
                "file_write",
                ToolInput.Create("Path", secondPath, "Content", "different path"));

            await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(secondCall, context, TestContext.Current.CancellationToken));

            context.OneTimeApprovedToolName = null;
            context.SetOneTimeApprovedPatterns([]);

            await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(controlPlaneRoot))
                Directory.Delete(controlPlaneRoot, recursive: true);
        }
    }

    [Fact]
    public async Task One_time_approval_uses_filtered_unapproved_patterns_on_retry()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };

        var commandPolicy = new ShellCommandPolicy(ShellEnvironment);
        var pathPolicy = new ToolPathPolicy(ShellEnvironment, []);
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config, new NetclawPaths(), pathPolicy, commandPolicy);

        var system = ActorSystem.Create($"tool-approval-filtered-once-{Guid.NewGuid():N}");
        try
        {
            var approvalActor = system.ActorOf(ToolApprovalActor.CreateProps(), "tool-approval");
            var approvalService = new AkkaToolApprovalService(new StubRequiredActor(approvalActor), ShellEnvironment);
            var executor = new DispatchingToolExecutor(
                registry,
                new ToolAccessPolicy(
                    config,
                    new EffectivePolicyDefaults(
                        DeploymentPosture.Personal,
                        TrustAudience.Personal,
                        ShellExecutionMode.HostAllowed,
                        UsedStrictFallback: false),
                    commandPolicy,
                    pathPolicy),
                approvalService);

            var context = TestToolExecutionContext.CreateBound("signalr/thread-filtered", null, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "signalr",
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            });

            var approvedPattern = ShellEnvironment.Grammar == ShellGrammar.PowerShell
                ? "Get-Location"
                : "pwd";
            var unapprovedPattern = ShellEnvironment.Grammar == ShellGrammar.PowerShell
                ? "Get-ChildItem"
                : "ls";
            var command = ShellEnvironment.Grammar == ShellGrammar.PowerShell
                ? "Get-Location; Get-ChildItem"
                : "pwd && ls";

            await approvalService.RecordApprovalAsync(
                "signalr/thread-filtered",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                [approvedPattern],
                persistent: false,
                cwd: null,
                TestContext.Current.CancellationToken);

            var call = new FunctionCallContent(
                "call-filtered-once",
                "shell_execute",
                ToolInput.Create("Command", command));

            var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(call, context, TestContext.Current.CancellationToken));

            Assert.Equal([unapprovedPattern], firstAttempt.ApprovalContext.Patterns);
            Assert.Equal([unapprovedPattern], firstAttempt.ApprovalContext.CandidateVerbs);

            context.OneTimeApprovedToolName = call.Name;
            context.SetOneTimeApprovedPatterns(OneTimeApprovalKeys.Create(firstAttempt.ApprovalContext));

            var retryResult = await executor.ExecuteAsync(call, context, TestContext.Current.CancellationToken);
            Assert.Contains("Exit code: 0", retryResult, StringComparison.Ordinal);

            context.OneTimeApprovedToolName = null;
            context.SetOneTimeApprovedPatterns([]);

            await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(call, context, TestContext.Current.CancellationToken));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Persistent_approval_hit_records_audit_context_without_prompting()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config, new NetclawPaths(), new ToolPathPolicy([]), new ShellCommandPolicy());

        var tempFile = Path.GetTempFileName();
        var system = ActorSystem.Create($"tool-approval-audit-{Guid.NewGuid():N}");
        try
        {
            File.Delete(tempFile);
            var store = new ToolApprovalStore(
                tempFile,
                timeProvider: null,
                migrationContext: new ApprovalStoreMigrationContext(ApprovalShell.Bash),
                lockTimeout: TimeSpan.Zero);
            store.AddApproval(TrustAudience.Personal, "shell_execute",
                ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["git", "status"]));

            var approvalActor = system.ActorOf(ToolApprovalActor.CreateProps(store), "tool-approval");
            var approvalService = new AkkaToolApprovalService(new StubRequiredActor(approvalActor), ShellEnvironment);
            var executor = new DispatchingToolExecutor(
                registry,
                new ToolAccessPolicy(
                    config,
                    new EffectivePolicyDefaults(
                        DeploymentPosture.Personal,
                        TrustAudience.Personal,
                        ShellExecutionMode.HostAllowed,
                        UsedStrictFallback: false),
                    new ShellCommandPolicy(),
                    new ToolPathPolicy([])),
                approvalService);

            var context = TestToolExecutionContext.CreateBound("signalr/thread-audit", null, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "signalr",
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            });

            var call = new FunctionCallContent(
                "call-audit",
                "shell_execute",
                ToolInput.Create("Command", "git status"));

            var decision = await executor.EvaluateAuthorizationAsync(
                call,
                context,
                TestContext.Current.CancellationToken);

            Assert.Equal(ToolAuthorizationOutcome.Allowed, decision.Outcome);
            Assert.Equal(ToolAllowReason.StoredApproval, decision.AllowReason);
            var match = Assert.Single(decision.ApprovalMatches);
            Assert.Equal("git status", match.Pattern);
            Assert.Equal("persistent", match.Source);
            Assert.Equal("Bash token-prefix \"git status\" anywhere", match.Scope);
            Assert.Equal("PreviouslyApproved", context.AppliedApprovalDecision);
            Assert.Equal(
                "git status [persistent: Bash token-prefix \"git status\" anywhere]",
                context.AppliedApprovalPattern);
        }
        finally
        {
            File.Delete(tempFile);
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Session_approval_allows_same_session_but_not_different_session()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config, new NetclawPaths(), new ToolPathPolicy([]), new ShellCommandPolicy());

        var system = ActorSystem.Create($"tool-approval-session-{Guid.NewGuid():N}");
        try
        {
            var approvalActor = system.ActorOf(ToolApprovalActor.CreateProps(), "tool-approval");
            var approvalService = new AkkaToolApprovalService(new StubRequiredActor(approvalActor), ShellEnvironment);
            var executor = new DispatchingToolExecutor(
                registry,
                new ToolAccessPolicy(
                    config,
                    new EffectivePolicyDefaults(
                        DeploymentPosture.Personal,
                        TrustAudience.Personal,
                        ShellExecutionMode.HostAllowed,
                        UsedStrictFallback: false),
                    new ShellCommandPolicy(),
                    new ToolPathPolicy([])),
                approvalService);

            var toolCall = new FunctionCallContent(
                "call-session-approve",
                "shell_execute",
                // Non-side-effect verb so the approval flow under test
                // actually triggers (see same change above for
                // One_time_approval_allows_immediate_retry_only).
                ToolInput.Create("Command", "git status"));

            var firstContext = TestToolExecutionContext.CreateBound("signalr/thread-1", null, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "signalr",
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            });

            var secondContext = TestToolExecutionContext.CreateBound("signalr/thread-2", null, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "signalr",
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            });

            var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, firstContext, TestContext.Current.CancellationToken));

            var reviewedCandidates = Assert.IsAssignableFrom<IReadOnlyList<ApprovalCandidate>>(
                firstAttempt.ApprovalContext.Candidates);
            await approvalService.RecordApprovalCandidatesAsync(
                (ToolApprovalSessionId)"signalr/thread-1",
                TrustAudience.Personal,
                new ToolName(toolCall.Name),
                reviewedCandidates
                    .Select(static candidate => new ToolApprovalGrant(candidate, Directory: null))
                    .ToArray(),
                persistent: false,
                TestContext.Current.CancellationToken);

            // Approved in firstContext's session — call should succeed.
            // The output text varies by test environment (git status may
            // error if not in a repo), but the meaningful assertion is
            // that no ToolApprovalRequiredException was thrown.
            _ = await executor.ExecuteAsync(toolCall, firstContext, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, secondContext, TestContext.Current.CancellationToken));
        }
        finally
        {
            await system.Terminate();
        }
    }

    // Regression for #1133: PR #1134 introduced an Anthropic-safe sanitized
    // alias (`server__tool`) for MCP tool names. The LLM emits tool_use with
    // the sanitized form, but the policy/session actor record approval under
    // the canonical `server/tool`. Looking up by the sanitized form on retry
    // miscounted every approved grant as unapproved and threw
    // ToolApprovalRequiredException on every post-approval call — surfaced
    // in production as "I encountered an error executing a tool" loops on
    // Notion writes.
    [Fact]
    public async Task Mcp_session_approval_recorded_under_canonical_name_authorizes_sanitized_alias_retry()
    {
        const string serverName = "notion";
        const string bareToolName = "notion-create-pages";
        const string canonicalName = $"{serverName}/{bareToolName}";
        const string sanitizedAlias = $"{serverName}__{bareToolName}";

        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            // Override keyed on the canonical name — same form the policy
            // uses when it builds the approval gate for MCP tools.
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                [canonicalName] = ToolApprovalMode.Approval
            }
        };

        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create(() => "ok", bareToolName),
            serverName,
            bareToolName,
            invoker: new RecordingMcpToolInvoker("ok")));

        // Sanity check: the adapter exposes the sanitized alias to the LLM
        // while keeping the canonical name as its primary identity. If this
        // ever changes, the rest of the test loses its meaning.
        var adapter = (McpToolAdapter)registry.GetByName(canonicalName)!;
        Assert.Equal(canonicalName, adapter.Name);
        Assert.Equal(sanitizedAlias, adapter.LlmFacingName.Value);

        var system = ActorSystem.Create($"tool-approval-mcp-{Guid.NewGuid():N}");
        try
        {
            var approvalActor = system.ActorOf(ToolApprovalActor.CreateProps(), "tool-approval");
            var approvalService = new AkkaToolApprovalService(new StubRequiredActor(approvalActor), ShellEnvironment);
            var executor = new DispatchingToolExecutor(
                registry,
                new ToolAccessPolicy(
                    config,
                    new EffectivePolicyDefaults(
                        DeploymentPosture.Personal,
                        TrustAudience.Personal,
                        ShellExecutionMode.HostAllowed,
                        UsedStrictFallback: false),
                    new ShellCommandPolicy(),
                    new ToolPathPolicy([])),
                approvalService);

            // The LLM emits tool_use with the sanitized alias — mirror that
            // here. The registry's two-form lookup (introduced in PR #1134)
            // resolves it back to the same adapter.
            var toolCall = new FunctionCallContent(
                "call-mcp-approve-session",
                sanitizedAlias,
                ToolInput.Empty());

            var context = TestToolExecutionContext.CreateBound("slack/D0/1779", null, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "slack",
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            });

            var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken));

            // The approval context — and the slack prompt the user sees —
            // carry the canonical name, not the sanitized alias.
            Assert.Equal(canonicalName, firstAttempt.ApprovalContext.ToolName);

            // Simulate LlmSessionActor.PersistApprovalCandidatesAsync on an
            // ApprovedSession click: the grant is recorded under the
            // canonical name (pending.ToolName), with the canonical
            // candidate verb extracted by DefaultApprovalMatcher.
            await approvalService.RecordApprovalAsync(
                "slack/D0/1779",
                TrustAudience.Personal,
                new ToolName(canonicalName),
                firstAttempt.ApprovalContext.CandidateVerbs,
                persistent: false,
                cwd: null,
                TestContext.Current.CancellationToken);

            // Retry — still under the sanitized alias the LLM uses. Pre-fix
            // this re-threw ToolApprovalRequiredException because the
            // executor looked up the grant by toolCall.Name (sanitized)
            // while it had been stored under tool.Name (canonical).
            _ = await executor.ExecuteAsync(toolCall, context, TestContext.Current.CancellationToken);

            // Same call dispatched by the canonical name must also resolve
            // — the registry accepts both forms, so the gate should
            // authorize either way.
            var canonicalToolCall = new FunctionCallContent(
                "call-mcp-approve-session-canonical",
                canonicalName,
                ToolInput.Empty());
            _ = await executor.ExecuteAsync(canonicalToolCall, context, TestContext.Current.CancellationToken);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Background_job_control_does_not_contact_approval_service(bool cancel)
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["check_background_job"] = ToolApprovalMode.Approval
            }
        };
        var registry = new ToolRegistry();
        registry.WithBackgroundJobTools(ActorRefs.Nobody);
        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])),
            new UnexpectedApprovalService());
        var context = TestToolExecutionContext.CreateBound(
            "slack/thread-1",
            null,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal });
        var toolCall = new FunctionCallContent(
            $"call-job-{cancel}",
            CheckBackgroundJobTool.ToolName,
            ToolInput.Create("JobId", "abc123", "Cancel", cancel));

        await executor.AuthorizeAsync(toolCall, context, TestContext.Current.CancellationToken);
    }

    private static DispatchingToolExecutor CreateApprovalGatedShellExecutor(
        IToolApprovalService? approvalService = null,
        ILogger<DispatchingToolExecutor>? logger = null,
        SafeVerbList? safeVerbs = null,
        IEnumerable<string>? deniedPaths = null,
        IShellTrustZonePolicy? shellTrustZonePolicy = null)
        => CreateApprovalGatedShellExecutor(
            ShellExecutionEnvironmentDefaults.Bash,
            approvalService,
            logger,
            safeVerbs,
            deniedPaths,
            shellTrustZonePolicy);

    private static DispatchingToolExecutor CreateApprovalGatedShellExecutor(
        ShellExecutionEnvironment environment,
        IToolApprovalService? approvalService = null,
        ILogger<DispatchingToolExecutor>? logger = null,
        SafeVerbList? safeVerbs = null,
        IEnumerable<string>? deniedPaths = null,
        IShellTrustZonePolicy? shellTrustZonePolicy = null)
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };
        var pathPolicy = new ToolPathPolicy(environment, deniedPaths ?? []);
        var commandPolicy = new ShellCommandPolicy(environment);
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(
            config,
            new NetclawPaths(),
            pathPolicy,
            commandPolicy);
        return new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                commandPolicy,
                pathPolicy,
                shellTrustZonePolicy: shellTrustZonePolicy,
                safeVerbs: safeVerbs),
            approvalService ?? new UnexpectedApprovalService(),
            logger: logger);
    }

    private static FixedShellApprovalService GrantEveryShellCandidate()
        => new(request =>
        {
            var matches = request.Candidates.Select(candidate =>
            {
                var shell = Assert.IsType<ApprovalShell>(candidate.Candidate.Shell);
                var tokens = Assert.IsAssignableFrom<IReadOnlyList<string>>(
                    candidate.Candidate.VerbTokens);
                var entry = ApprovalEntry.CreateTokenPrefix(
                    shell,
                    tokens,
                    directory: null,
                    createdAt: null);
                return new ShellGrantCandidateMatch(
                    candidate.CandidateId,
                    new ToolApprovalMatch(
                        candidate.Candidate.Verb,
                        "persistent",
                        entry.FormatScope()),
                    ShellCoverageKind.PersistentGlobal,
                    NearMisses: []);
            }).ToArray();
            return new ShellApprovalMatchResult(
                new PersistentGrantStoreStatus.Ready(),
                Array.AsReadOnly(matches));
        });

    private static ToolExecutionContext CreateInteractivePersonalContext(string sessionId)
        => TestToolExecutionContext.CreateBound(
            sessionId,
            null,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            });

    private sealed class AllowAllShellTrustZonePolicy : IShellTrustZonePolicy
    {
        public bool IsShellWritePathAuthorized(string fullPath, ToolInvocationContext context)
            => true;
    }

    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static ApprovalCandidate BashCandidate(string verb, string? directory = null) =>
        new(verb, directory)
        {
            Shell = ApprovalShell.Bash,
            VerbTokens = Array.AsReadOnly(
                verb.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
        };

    private sealed class UnexpectedApprovalService : IToolApprovalService
    {
        public Task<ToolApprovalCheckResult> CheckApprovalAsync(
            ToolApprovalSessionId? sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<ApprovalCandidate> candidates,
            string? cwd,
            CancellationToken ct = default)
            => throw new InvalidOperationException("The approval-exempt path must not query stored approvals.");

        public Task<IReadOnlyList<string>> GetUnapprovedPatternsAsync(
            ToolApprovalSessionId? sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<string> patterns,
            string? cwd,
            CancellationToken ct = default)
            => throw new InvalidOperationException("The approval-exempt path must not query stored approvals.");

        public Task RecordApprovalAsync(
            ToolApprovalSessionId sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<string> patterns,
            bool persistent,
            string? cwd,
            CancellationToken ct = default)
            => throw new InvalidOperationException("The approval-exempt path must not record an approval.");
    }

    private sealed class FixedApprovalService(ToolApprovalCheckResult result) : IToolApprovalService
    {
        public Task<ToolApprovalCheckResult> CheckApprovalAsync(
            ToolApprovalSessionId? sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<ApprovalCandidate> candidates,
            string? cwd,
            CancellationToken ct = default)
            => Task.FromResult(result);

        public Task<IReadOnlyList<string>> GetUnapprovedPatternsAsync(
            ToolApprovalSessionId? sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<string> patterns,
            string? cwd,
            CancellationToken ct = default)
            => throw new InvalidOperationException("The test does not use the legacy approval check.");

        public Task RecordApprovalAsync(
            ToolApprovalSessionId sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<string> patterns,
            bool persistent,
            string? cwd,
            CancellationToken ct = default)
            => throw new InvalidOperationException("The authorization evaluator must not record an approval.");
    }

    private sealed class FixedShellApprovalService(
        Func<ShellApprovalMatchRequest, ShellApprovalMatchResult> responseFactory)
        : IToolApprovalService, IShellApprovalMatchService
    {
        public int RequestCount { get; private set; }

        public ShellApprovalMatchRequest? LastRequest { get; private set; }

        public Task<ShellApprovalMatchResult> MatchShellCandidatesAsync(
            ShellApprovalMatchRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            return Task.FromResult(responseFactory(request));
        }

        public Task<ToolApprovalCheckResult> CheckApprovalAsync(
            ToolApprovalSessionId? sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<ApprovalCandidate> candidates,
            string? cwd,
            CancellationToken ct = default)
            => throw new InvalidOperationException("The shell coordinator must use the typed batch protocol.");

        public Task<IReadOnlyList<string>> GetUnapprovedPatternsAsync(
            ToolApprovalSessionId? sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<string> patterns,
            string? cwd,
            CancellationToken ct = default)
            => throw new InvalidOperationException("The shell coordinator must use the typed batch protocol.");

        public Task RecordApprovalAsync(
            ToolApprovalSessionId sessionId,
            TrustAudience audience,
            ToolName toolName,
            IReadOnlyList<string> patterns,
            bool persistent,
            string? cwd,
            CancellationToken ct = default)
            => throw new InvalidOperationException("The authorization evaluator must not record an approval.");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<IReadOnlyDictionary<string, object?>> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> properties)
                return;

            Entries.Add(properties.ToDictionary(
                property => property.Key,
                property => property.Value,
                StringComparer.Ordinal));
        }

        private sealed class EmptyScope : IDisposable
        {
            public static readonly EmptyScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class StubRequiredActor : IRequiredActor<ToolApprovalActorKey>
    {
        private readonly IActorRef _actor;

        public StubRequiredActor(IActorRef actor)
        {
            _actor = actor;
        }

        public IActorRef ActorRef => _actor;

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_actor);
    }

}
