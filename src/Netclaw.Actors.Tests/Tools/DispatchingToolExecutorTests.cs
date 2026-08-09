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
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class DispatchingToolExecutorTests
{
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

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(baseConfig, new NetclawPaths(), new ToolPathPolicy([]), new ShellCommandPolicy());
        _executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                baseConfig,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])));

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
        var restrictedRegistry = new ToolRegistry();
        restrictedRegistry.WithFirstPartyTools(restrictedConfig, new NetclawPaths(), new ToolPathPolicy([]), new ShellCommandPolicy());
        _restrictedExecutor = new DispatchingToolExecutor(
            restrictedRegistry,
            new ToolAccessPolicy(
                restrictedConfig,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])));
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
                new ToolPathPolicy([])));

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

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config, new NetclawPaths(), new ToolPathPolicy([]), new ShellCommandPolicy());

        var executor = new DispatchingToolExecutor(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.Off,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])));

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
        Assert.Empty(ShellApprovalMatcher.Instance.ExtractCandidates(new ToolName("shell_execute"), arguments));

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
        Assert.Equal([approvedMatch], decision.ApprovalMatches);
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
        var approvedCandidate = new ApprovalCandidate("git status", Directory: null);
        var unapprovedCandidate = new ApprovalCandidate("git push", Directory: null);
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
            var approvedMatch = new ToolApprovalMatch("git push", "persistent", approvedDirectory);
            var approvedCandidate = new ApprovalCandidate("git push", approvedDirectory);
            var unapprovedCandidate = new ApprovalCandidate("git push", unapprovedDirectory);
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
    public async Task Authorization_evaluation_keeps_broad_prompt_for_inconsistent_candidate_result()
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

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        Assert.Equal(["git status", "git push"], decision.ApprovalContext!.CandidateVerbs);
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

        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        Assert.Equal(["git status", "git push"], decision.ApprovalContext!.CandidateVerbs);
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

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config, new NetclawPaths(), new ToolPathPolicy([]), new ShellCommandPolicy());

        var system = ActorSystem.Create($"tool-approval-{Guid.NewGuid():N}");
        try
        {
            var approvalActor = system.ActorOf(ToolApprovalActor.CreateProps(), "tool-approval");
            var approvalService = new AkkaToolApprovalService(new StubRequiredActor(approvalActor));
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
                new ToolPathPolicy([])));

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

        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config, new NetclawPaths(), new ToolPathPolicy([]), new ShellCommandPolicy());

        var system = ActorSystem.Create($"tool-approval-filtered-once-{Guid.NewGuid():N}");
        try
        {
            var approvalActor = system.ActorOf(ToolApprovalActor.CreateProps(), "tool-approval");
            var approvalService = new AkkaToolApprovalService(new StubRequiredActor(approvalActor));
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

            var context = TestToolExecutionContext.CreateBound("signalr/thread-filtered", null, new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance,
                ChannelType = "signalr",
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            });

            await approvalService.RecordApprovalAsync(
                "signalr/thread-filtered",
                TrustAudience.Personal,
                new ToolName("shell_execute"),
                ["pwd"],
                persistent: false,
                cwd: null,
                TestContext.Current.CancellationToken);

            var call = new FunctionCallContent(
                "call-filtered-once",
                "shell_execute",
                ToolInput.Create("Command", "pwd && ls"));

            var firstAttempt = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() =>
                executor.ExecuteAsync(call, context, TestContext.Current.CancellationToken));

            Assert.Equal(["ls"], firstAttempt.ApprovalContext.Patterns);
            Assert.Equal(["ls"], firstAttempt.ApprovalContext.CandidateVerbs);

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
            var store = new ToolApprovalStore(tempFile);
            store.AddApproval(TrustAudience.Personal, "shell_execute",
                new ApprovalEntry("git status") { Directory = null });

            var approvalActor = system.ActorOf(ToolApprovalActor.CreateProps(store), "tool-approval");
            var approvalService = new AkkaToolApprovalService(new StubRequiredActor(approvalActor));
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
            Assert.Equal("git status anywhere", match.Scope);
            Assert.Equal("PreviouslyApproved", context.AppliedApprovalDecision);
            Assert.Equal("git status [persistent: git status anywhere]", context.AppliedApprovalPattern);
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
            var approvalService = new AkkaToolApprovalService(new StubRequiredActor(approvalActor));
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

            await approvalService.RecordApprovalAsync(
                "signalr/thread-1",
                TrustAudience.Personal,
                new ToolName(toolCall.Name),
                firstAttempt.ApprovalContext.CandidateVerbs,
                persistent: false,
                cwd: null,
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
            var approvalService = new AkkaToolApprovalService(new StubRequiredActor(approvalActor));
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

    private static DispatchingToolExecutor CreateApprovalGatedShellExecutor()
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
        return new DispatchingToolExecutor(
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
    }

    private static ToolExecutionContext CreateInteractivePersonalContext(string sessionId)
        => TestToolExecutionContext.CreateBound(
            sessionId,
            null,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            });

    public static bool IsPosix => !OperatingSystem.IsWindows();

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
