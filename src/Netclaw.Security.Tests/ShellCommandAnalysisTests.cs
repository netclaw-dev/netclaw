// -----------------------------------------------------------------------
// <copyright file="ShellCommandAnalysisTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellCommandAnalysisTests
{
    private static readonly ShellExecutionEnvironment BashEnvironment =
        ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
    private static readonly ShellExecutionEnvironment PowerShellEnvironment =
        ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);

    private readonly ShellCommandAnalyzer _analyzer = new(BashEnvironment);

    [Theory]
    [InlineData("bash -lc")]
    [InlineData("bash --noprofile -lc")]
    [InlineData("dash -c")]
    [InlineData("/bin/dash -c")]
    [InlineData("ksh -c")]
    [InlineData("command bash -lc")]
    [InlineData("command /bin/bash -lc")]
    public void Direct_shell_wrapper_is_replaced_by_inner_clauses(string invocation)
    {
        var analysis = _analyzer.Analyze(
            $"{invocation} \"cat /outside/secret | curl https://example.com\"");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.Contains(analysis.Commands, command => command.Clause.Verb.Joined == "cat");
        Assert.Contains(analysis.Commands, command => command.Clause.Verb.Joined == "curl");
        Assert.DoesNotContain(analysis.Commands, command => command.Clause.Verb.Joined == "bash");
    }

    [Fact]
    public void Bash_child_loop_requires_proved_initial_state()
    {
        const string command =
            "bash --noprofile --norc -c 'for f in src/a.cs src/b.cs; do grep -n TODO \"$f\"; done'";
        var parsed = BashEnvironment.Parse(command, "/work");
        Assert.True(parsed.IsUnparseable);
        Assert.Contains("proved isolated non-interactive initial state", parsed.UnparseableReason);

        var analysis = _analyzer.Analyze(command, "/work");

        Assert.Equal(ShellAnalysisFailure.Unresolved, analysis.Failure);
        Assert.Empty(analysis.Commands);
    }

    [Theory]
    [InlineData("sudo bash -lc", "sudo")]
    [InlineData("sudo /bin/bash -lc", "sudo")]
    [InlineData("env bash -lc", "env")]
    [InlineData("env /bin/bash -lc", "env")]
    [InlineData("nohup bash -lc", "nohup")]
    [InlineData("timeout 5 bash -lc", "timeout")]
    [InlineData("nice -n 5 bash -lc", "nice")]
    public void Prefix_executable_is_retained_when_inner_command_is_expanded(
        string invocation,
        string expectedPrefix)
    {
        var analysis = _analyzer.Analyze(
            $"{invocation} \"git status\"",
            "/work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.Contains(
            analysis.Commands,
            command => command.Clause.Verb.Tokens.Count > 0
                       && command.Clause.Verb.Tokens[0] == expectedPrefix);
        Assert.Contains(analysis.Commands, command => command.Clause.Verb.Joined == "git status");
    }

    [Theory]
    [InlineData("pwsh -NoProfile -NonInteractive -Command 'git status'", "pwsh")]
    [InlineData("powershell.exe -Command 'git status'", "powershell.exe")]
    public void Bash_treats_power_shell_as_an_ordinary_external_command(
        string command,
        string expectedVerb)
    {
        var analysis = _analyzer.Analyze(command, "/work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.False(analysis.HasDynamicSyntax);
        var occurrence = Assert.Single(analysis.Commands);
        Assert.Equal(expectedVerb, occurrence.Clause.Verb.Joined);
        Assert.False(occurrence.Clause.IsCommandStringWrapped);
    }

    [Fact]
    public void Bash_does_not_interpret_a_power_shell_command_payload()
    {
        var analysis = _analyzer.Analyze(
            "pwsh -NoProfile -Command 'Write-Output ok; netclaw daemon stop'",
            "/work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        var occurrence = Assert.Single(analysis.Commands);
        Assert.Equal("pwsh", occurrence.Clause.Verb.Joined);
        Assert.DoesNotContain(
            analysis.Commands,
            command => command.Clause.Verb.Joined == "netclaw daemon stop");
    }

    [Theory]
    [InlineData("PWSH -NoProfile -Command 'git status'")]
    [InlineData("pwsh.exe -File script.ps1")]
    [InlineData("powershell -EncodedCommand RwBlAHQALQBEAGEAdABlAA==")]
    public void Bash_does_not_apply_power_shell_wrapper_rules(string command)
    {
        var analysis = _analyzer.Analyze(command, "/work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.Single(analysis.Commands);
    }

    [Theory]
    [InlineData("echo pwsh")]
    [InlineData("rg pwsh .")]
    [InlineData("printf '%s\\n' pwsh")]
    [InlineData("git commit -m pwsh")]
    public void Power_shell_host_token_used_as_data_is_not_special(string command)
    {
        var analysis = _analyzer.Analyze(command, "/work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.Single(analysis.Commands);
    }

    [Fact]
    public void Bash_dynamic_argument_to_power_shell_stays_dynamic()
    {
        var analysis = _analyzer.Analyze(
            "pwsh -NoProfile -NonInteractive -Command \"git $operation\"",
            "/work");

        Assert.Equal(ShellAnalysisFailure.Unresolved, analysis.Failure);
        Assert.Empty(analysis.Commands);
    }

    [Fact]
    public void Bash_does_not_decode_a_power_shell_payload()
    {
        var analysis = _analyzer.Analyze(
            "pwsh -NoProfile -NonInteractive -Command 'Write-Output '' ; netclaw daemon stop; #'''",
            "/work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.Equal("pwsh", Assert.Single(analysis.Commands).Clause.Verb.Joined);
        Assert.DoesNotContain(
            analysis.Commands,
            command => command.Clause.Verb.Joined == "netclaw daemon stop");
    }

    [Fact]
    public void Power_shell_dynamic_child_stays_dynamic()
    {
        var analyzer = new ShellCommandAnalyzer(PowerShellEnvironment);
        var analysis = analyzer.Analyze(
            "pwsh -NoProfile -NonInteractive -Command 'git $operation'",
            @"C:\work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.True(
            analysis.HasDynamicSyntax,
            string.Join(" | ", analysis.Commands.Select(command =>
                $"{command.Clause.Verb.Joined}:{command.IsComplete}:" +
                string.Join(",", command.Clause.Args.Select(arg => $"{arg.Raw}={arg.Kind}")))));
        Assert.Equal("git", analysis.Commands[0].Clause.Verb.Joined);
    }

    [Fact]
    public void Power_shell_proved_execution_region_is_complete()
    {
        var analyzer = new ShellCommandAnalyzer(PowerShellEnvironment);
        var analysis = analyzer.Analyze(
            "pwsh -NoProfile -NonInteractive -Command '& { git push }'",
            @"C:\work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.Equal(
            ["git push"],
            analysis.Commands.Select(command => command.Clause.Verb.Joined));
        Assert.False(
            analysis.HasDynamicSyntax,
            Describe(analysis));
    }

    [Fact]
    public void Power_shell_child_loop_does_not_inherit_initial_state_proof()
    {
        var analyzer = new ShellCommandAnalyzer(PowerShellEnvironment);
        var analysis = analyzer.Analyze(
            "pwsh -NoProfile -NonInteractive -Command 'foreach ($f in @(\"a.txt\", \"b.txt\")) { Get-Content -LiteralPath $f }'",
            @"C:\work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.True(analysis.HasDynamicSyntax, Describe(analysis));
        var occurrence = Assert.Single(analysis.Commands);
        Assert.Equal("Get-Content", occurrence.Clause.Verb.Joined);
        Assert.True(occurrence.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(occurrence.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Power_shell_treats_bash_as_an_ordinary_external_command()
    {
        var analyzer = new ShellCommandAnalyzer(PowerShellEnvironment);
        var analysis = analyzer.Analyze(
            "bash -c 'Remove-Item victim.txt'",
            @"C:\work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        var occurrence = Assert.Single(analysis.Commands);
        Assert.Equal("bash", occurrence.Clause.Verb.Joined);
        Assert.DoesNotContain(
            analysis.Commands,
            command => command.Clause.Verb.Joined == "Remove-Item");
    }

    [Fact]
    public void Command_inspection_option_is_not_treated_as_transparent_shell_dispatch()
    {
        var analysis = _analyzer.Analyze(
            "command -v bash -lc \"git status\"",
            "/work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.Contains(
            analysis.Commands,
            command => command.Clause.Verb.Tokens.Count > 0
                       && command.Clause.Verb.Tokens[0] == "command");
    }

    [Fact]
    public void Bundled_shell_wrapper_inherits_proven_cwd()
    {
        var analysis = _analyzer.Analyze(
            "cd /tmp && bash -lc \"cat relative.txt\"",
            "/work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        var inner = Assert.Single(
            analysis.Commands,
            command => command.Clause.Verb.Joined == "cat");
        Assert.Contains(
            inner.Clause.Args,
            arg => arg.Raw == "relative.txt" && arg.Resolved == "/tmp/relative.txt");
    }

    [Fact]
    public void Bundled_shell_wrapper_with_uncertain_cwd_fails_closed()
    {
        var analysis = _analyzer.Analyze(
            "cd /tmp\nbash -lc \"git status\"",
            "/work");

        Assert.Equal(ShellAnalysisFailure.Unresolved, analysis.Failure);
    }

    [Theory]
    [InlineData("echo $(git push)")]
    public void Dynamic_command_syntax_is_explicit(string command)
    {
        var analysis = _analyzer.Analyze(command);

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.True(analysis.HasDynamicSyntax);
    }

    [Fact]
    public void Unsupported_legacy_backticks_fail_closed()
    {
        var analysis = _analyzer.Analyze("echo `$command`");

        Assert.Equal(ShellAnalysisFailure.Unresolved, analysis.Failure);
        Assert.Empty(analysis.Commands);
    }

    [Theory]
    [InlineData("git status 2>&1", RedirectOperation.DescriptorDuplicate, 1)]
    [InlineData("git status 2>&123", RedirectOperation.DescriptorDuplicate, 123)]
    [InlineData("git status 2>&1-", RedirectOperation.DescriptorMove, 1)]
    [InlineData("git status 2>&-", RedirectOperation.DescriptorClose, null)]
    public void Static_file_descriptor_redirect_is_not_dynamic(
        string command,
        RedirectOperation expectedOperation,
        int? expectedTargetDescriptor)
    {
        var analysis = _analyzer.Analyze(command);

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.False(analysis.HasDynamicSyntax);
        var occurrence = Assert.Single(analysis.Commands);
        var redirect = Assert.Single(occurrence.Redirects);
        Assert.Equal(expectedOperation, redirect.Operation);
        Assert.Equal(expectedTargetDescriptor, redirect.TargetDescriptor);
        Assert.False(redirect.IsPathRelevant);
        Assert.True(redirect.IsComplete);
    }

    [Theory]
    [InlineData("git status 2>&$FD")]
    [InlineData("git status >&$FD")]
    [InlineData("git status 2>&${FD}")]
    public void Dynamic_file_descriptor_redirect_stays_dynamic(string command)
    {
        var analysis = _analyzer.Analyze(command);

        Assert.True(
            analysis.Failure == ShellAnalysisFailure.Unresolved
            || analysis.HasDynamicSyntax);
    }

    [Fact]
    public void Background_list_fails_closed_when_parser_omits_its_tail()
    {
        var analysis = _analyzer.Analyze("git status & git push");

        Assert.Equal(ShellAnalysisFailure.Unresolved, analysis.Failure);
        Assert.Empty(analysis.Commands);
    }

    [Fact]
    public void Exact_file_redirect_has_bounded_path_target()
    {
        var analysis = _analyzer.Analyze("git status > result.log", "/work");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.False(analysis.HasDynamicSyntax);
        var redirect = Assert.Single(Assert.Single(analysis.Commands).Redirects);
        Assert.Equal(RedirectOperation.FileOutput, redirect.Operation);
        Assert.Equal(ShellValueDomainKind.Exact, redirect.Target.Kind);
        Assert.Equal("/work/result.log", Assert.Single(redirect.Target.Values));
        Assert.True(redirect.IsPathRelevant);
    }

    [Theory]
    [InlineData("cat <<'EOF'\nbody\nEOF", RedirectOperation.HereDocument)]
    [InlineData("cat <<< \"body\"", RedirectOperation.HereString)]
    [InlineData("cat 0<<< \"body\"", RedirectOperation.HereString)]
    public void Bounded_data_only_stdin_is_not_dynamic(
        string command,
        RedirectOperation expectedOperation)
    {
        var analysis = _analyzer.Analyze(command);

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.False(analysis.HasDynamicSyntax);
        var redirect = Assert.Single(Assert.Single(analysis.Commands).Redirects);
        Assert.Equal(expectedOperation, redirect.Operation);
        Assert.False(redirect.IsPathRelevant);
        Assert.True(redirect.IsComplete);
    }

    [Fact]
    public void Finite_here_string_data_for_argument_free_cat_is_not_dynamic()
    {
        var occurrence = new CommandOccurrence
        {
            Clause = new Clause
            {
                Verb = new VerbChain { Tokens = ["cat"] }
            },
            ImmediateRole = CommandOccurrenceRole.Ordinary,
            Redirects =
            [
                new RedirectAnalysis
                {
                    Source = new RedirectSource { Kind = RedirectSourceKind.Default },
                    Operation = RedirectOperation.HereString,
                    Target = new ShellValueDomain
                    {
                        Kind = ShellValueDomainKind.FiniteSet,
                        Values = ["alpha\n", "beta\n"]
                    },
                    IsComplete = true
                }
            ],
            IsComplete = true
        };
        var analysis = CreateAnalysis(occurrence);

        Assert.False(analysis.HasDynamicSyntax);
    }

    [Fact]
    public void Transparent_shell_dispatch_keeps_bounded_inner_cat()
    {
        var analysis = _analyzer.Analyze("bash -c \"cat <<< 'body'\"");

        Assert.Equal(ShellAnalysisFailure.None, analysis.Failure);
        Assert.False(analysis.HasDynamicSyntax);
        var occurrence = Assert.Single(analysis.Commands);
        Assert.Equal("cat", occurrence.Clause.Verb.Joined);
        Assert.True(occurrence.Clause.IsCommandStringWrapped);
    }

    [Theory]
    [MemberData(nameof(MalformedStdinRedirects))]
    public void Malformed_stdin_facts_for_cat_fail_closed(RedirectAnalysis redirect)
    {
        var occurrence = new CommandOccurrence
        {
            Clause = new Clause
            {
                Verb = new VerbChain { Tokens = ["cat"] }
            },
            ImmediateRole = CommandOccurrenceRole.Ordinary,
            Redirects = [redirect],
            IsComplete = true
        };
        var analysis = CreateAnalysis(occurrence);

        Assert.True(analysis.HasDynamicSyntax);
    }

    [Theory]
    [InlineData("cat <<< \"$value\"")]
    [InlineData("cat <<EOF\nbody\nEOF")]
    [InlineData("cat <<EOF\n$value\nEOF")]
    [InlineData("cat -n <<< \"body\"")]
    [InlineData("command cat <<< \"body\"")]
    [InlineData("bash <<< \"echo ok\"")]
    [InlineData("cat <<< \"$(printf payload)\"")]
    public void Unproved_or_unsupported_stdin_receiver_stays_dynamic(string command)
    {
        var analysis = _analyzer.Analyze(command);

        Assert.True(
            analysis.Failure == ShellAnalysisFailure.Unresolved
            || analysis.HasDynamicSyntax);
    }

    [Theory]
    [MemberData(nameof(MalformedRedirects))]
    public void Malformed_or_future_redirect_facts_fail_closed(RedirectAnalysis redirect)
    {
        var occurrence = new CommandOccurrence
        {
            Clause = new Clause
            {
                Verb = new VerbChain { Tokens = ["command"] }
            },
            ImmediateRole = CommandOccurrenceRole.Ordinary,
            Redirects = [redirect],
            IsComplete = true
        };
        var analysis = CreateAnalysis(occurrence);

        Assert.True(analysis.HasDynamicSyntax);
    }

    [Fact]
    public void Unknown_ancestry_fails_closed()
    {
        var occurrence = new CommandOccurrence
        {
            Clause = new Clause
            {
                Verb = new VerbChain { Tokens = ["command"] }
            },
            ImmediateRole = CommandOccurrenceRole.Ordinary,
            Ancestry =
            [
                new CommandAncestryFrame
                {
                    AncestorKind = ShellSyntaxKind.Unknown,
                    Region = CommandAncestryRegion.Root
                }
            ],
            IsComplete = true
        };
        var analysis = CreateAnalysis(occurrence);

        Assert.True(analysis.HasDynamicSyntax);
    }

    [Theory]
    [InlineData(ShellValueDomainKind.FiniteSet)]
    [InlineData(ShellValueDomainKind.Pattern)]
    [InlineData((ShellValueDomainKind)999)]
    public void Unsupported_working_directory_domain_fails_closed(
        ShellValueDomainKind workingDirectoryKind)
    {
        var occurrence = new CommandOccurrence
        {
            Clause = new Clause
            {
                Verb = new VerbChain { Tokens = ["command"] }
            },
            ImmediateRole = CommandOccurrenceRole.Ordinary,
            WorkingDirectory = new ShellValueDomain { Kind = workingDirectoryKind },
            IsComplete = true
        };
        var analysis = CreateAnalysis(occurrence);

        Assert.True(analysis.HasDynamicSyntax);
    }

    public static TheoryData<RedirectAnalysis> MalformedRedirects => new()
    {
        new RedirectAnalysis
        {
            Source = new RedirectSource { Kind = RedirectSourceKind.Descriptor },
            Operation = RedirectOperation.DescriptorDuplicate,
            TargetDescriptor = 1,
            IsComplete = true
        },
        new RedirectAnalysis
        {
            Source = new RedirectSource { Kind = RedirectSourceKind.Default },
            Operation = RedirectOperation.DescriptorDuplicate,
            IsComplete = true
        },
        new RedirectAnalysis
        {
            Source = new RedirectSource { Kind = RedirectSourceKind.Default },
            Operation = RedirectOperation.DescriptorClose,
            TargetDescriptor = 1,
            IsComplete = true
        },
        new RedirectAnalysis
        {
            Source = new RedirectSource { Kind = RedirectSourceKind.Default },
            Operation = (RedirectOperation)999,
            IsComplete = true
        },
        new RedirectAnalysis
        {
            Source = new RedirectSource { Kind = RedirectSourceKind.Default },
            Operation = RedirectOperation.FileOutput,
            Target = new ShellValueDomain
            {
                Kind = ShellValueDomainKind.Exact,
                Values = ["/work/result.log"]
            },
            IsComplete = true
        }
    };

    public static TheoryData<RedirectAnalysis> MalformedStdinRedirects => new()
    {
        HereDocumentRedirect(new HereDocumentAnalysis
        {
            Delimiter = new ShellSourceFragment { Raw = "EOF" },
            Body = new ShellSourceFragment { Raw = "body\n" },
            ExpansionMode = HereDocumentExpansionMode.Literal,
            IsComplete = false
        }),
        HereDocumentRedirect(new HereDocumentAnalysis
        {
            Delimiter = new ShellSourceFragment { Raw = "EOF" },
            Body = new ShellSourceFragment { Raw = "$value\n" },
            ExpansionMode = HereDocumentExpansionMode.Expand,
            IsComplete = true
        }),
        HereDocumentRedirect(new HereDocumentAnalysis
        {
            Delimiter = new ShellSourceFragment { Raw = "EOF" },
            Body = new ShellSourceFragment { Raw = "body\n" },
            ExpansionMode = (HereDocumentExpansionMode)999,
            IsComplete = true
        }),
        HereDocumentRedirect(new HereDocumentAnalysis
        {
            ExpansionMode = HereDocumentExpansionMode.Literal,
            IsComplete = true
        }),
        HereDocumentRedirect(new HereDocumentAnalysis
        {
            Delimiter = null!,
            Body = new ShellSourceFragment { Raw = "body\n" },
            ExpansionMode = HereDocumentExpansionMode.Literal,
            IsComplete = true
        }),
        HereDocumentRedirect(new HereDocumentAnalysis
        {
            Delimiter = new ShellSourceFragment { Raw = "EOF" },
            Body = null!,
            ExpansionMode = HereDocumentExpansionMode.Literal,
            IsComplete = true
        }),
        HereDocumentRedirect(new HereDocumentAnalysis
        {
            Delimiter = new ShellSourceFragment
            {
                Raw = "EOF",
                SourceStart = 1
            },
            Body = new ShellSourceFragment { Raw = "body\n" },
            ExpansionMode = HereDocumentExpansionMode.Literal,
            IsComplete = true
        }),
        HereDocumentRedirect(null),
        HereDocumentRedirect(
            new HereDocumentAnalysis
            {
                Delimiter = new ShellSourceFragment { Raw = "EOF" },
                Body = new ShellSourceFragment { Raw = "body\n" },
                ExpansionMode = HereDocumentExpansionMode.Literal,
                IsComplete = true
            },
            target: new ShellValueDomain
            {
                Kind = ShellValueDomainKind.Exact,
                Values = ["body\n"]
            }),
        HereStringRedirect(ShellValueDomain.Unknown),
        HereStringRedirect(null!),
        HereStringRedirect(new ShellValueDomain
        {
            Kind = ShellValueDomainKind.Exact,
            Values = ["one", "two"]
        }),
        HereStringRedirect(new ShellValueDomain
        {
            Kind = ShellValueDomainKind.Exact,
            Values = [null!]
        }),
        HereStringRedirect(new ShellValueDomain
        {
            Kind = ShellValueDomainKind.FiniteSet,
            Values = ["one"]
        }),
        HereStringRedirect(new ShellValueDomain
        {
            Kind = ShellValueDomainKind.FiniteSet,
            Values = ["same", "same"]
        }),
        HereStringRedirect(new ShellValueDomain
        {
            Kind = ShellValueDomainKind.FiniteSet,
            Values = Enumerable.Range(0, 33).Select(static index => index.ToString()).ToArray()
        }),
        HereStringRedirect(new ShellValueDomain
        {
            Kind = ShellValueDomainKind.Pattern,
            Pattern = "*",
            CoveringDirectory = "/work"
        }),
        HereStringRedirect(
            new ShellValueDomain
            {
                Kind = ShellValueDomainKind.Exact,
                Values = ["body\n"]
            },
            source: new RedirectSource
            {
                Kind = RedirectSourceKind.Descriptor,
                Descriptor = 2
            }),
        HereStringRedirect(
            new ShellValueDomain
            {
                Kind = ShellValueDomainKind.Exact,
                Values = ["body\n"]
            },
            isPathRelevant: true),
        HereStringRedirect(
            new ShellValueDomain
            {
                Kind = ShellValueDomainKind.Exact,
                Values = ["body\n"]
            },
            targetDescriptor: 0),
        HereStringRedirect(
            new ShellValueDomain
            {
                Kind = ShellValueDomainKind.Exact,
                Values = ["body\n"]
            },
            hereDocument: new HereDocumentAnalysis
            {
                Delimiter = new ShellSourceFragment { Raw = "EOF" },
                Body = new ShellSourceFragment { Raw = "body\n" },
                ExpansionMode = HereDocumentExpansionMode.Literal,
                IsComplete = true
            })
    };

    private static ShellCommandAnalysis CreateAnalysis(CommandOccurrence occurrence)
        => new(
            BashEnvironment,
            source: string.Empty,
            workingDirectory: null,
            commands: [occurrence],
            ShellAnalysisFailure.None);

    private static string Describe(ShellCommandAnalysis analysis)
        => string.Join(" | ", analysis.Commands.Select(command =>
            $"{command.Clause.Verb.Joined}:complete={command.IsComplete}:" +
            $"role={command.ImmediateRole}:cwd={command.WorkingDirectory.Kind}:" +
            $"ancestry={string.Join(',', command.Ancestry.Select(frame => $"{frame.AncestorKind}/{frame.Region}"))}:" +
            $"args={string.Join(',', command.Clause.Args.Select(arg => $"{arg.Raw}/{arg.Kind}/{arg.Resolved}"))}:" +
            $"effective={string.Join(',', command.EffectiveArguments.Select(arg => $"{arg.ClauseElementIndex}/{arg.Value.Kind}/{string.Join(';', arg.Value.Values)}"))}"));

    private static RedirectAnalysis HereDocumentRedirect(
        HereDocumentAnalysis? hereDocument,
        ShellValueDomain? target = null)
        => new()
        {
            Source = new RedirectSource { Kind = RedirectSourceKind.Default },
            Operation = RedirectOperation.HereDocument,
            Target = target ?? ShellValueDomain.Unknown,
            HereDocument = hereDocument,
            IsComplete = true
        };

    private static RedirectAnalysis HereStringRedirect(
        ShellValueDomain target,
        RedirectSource? source = null,
        bool isPathRelevant = false,
        HereDocumentAnalysis? hereDocument = null,
        int? targetDescriptor = null)
        => new()
        {
            Source = source ?? new RedirectSource { Kind = RedirectSourceKind.Default },
            Operation = RedirectOperation.HereString,
            Target = target,
            TargetDescriptor = targetDescriptor,
            HereDocument = hereDocument,
            IsPathRelevant = isPathRelevant,
            IsComplete = true
        };
}
