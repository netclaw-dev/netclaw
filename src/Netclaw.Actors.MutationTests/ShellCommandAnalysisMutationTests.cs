// -----------------------------------------------------------------------
// <copyright file="ShellCommandAnalysisMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;
using System.Reflection;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class ShellCommandAnalysisMutationTests
{
    private static readonly ShellExecutionEnvironment PowerShellEnvironment =
        ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);
    private static readonly ShellExecutionEnvironment WindowsPowerShellEnvironment =
        ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            PwshDialect.WindowsPowerShell51);

    [Fact]
    public void Known_and_unknown_execution_regions_keep_distinct_analysis_results()
    {
        var analyzer = new ShellCommandAnalyzer(PowerShellEnvironment);

        var known = analyzer.Analyze(
            "Get-ChildItem | ForEach-Object { $_.FullName }",
            @"C:\work");
        var unknown = analyzer.Analyze(
            @"Invoke-Custom { Remove-Item .\victim.txt }",
            @"C:\work");
        var ordinaryDynamicArgument = analyzer.Analyze(
            "Get-Content $path",
            @"C:\work");

        Assert.Equal(ShellAnalysisFailure.None, known.Failure);
        Assert.False(known.HasDynamicSyntax);
        Assert.Equal(ShellAnalysisFailure.None, unknown.Failure);
        Assert.True(unknown.HasDynamicSyntax);
        Assert.True(ordinaryDynamicArgument.HasDynamicSyntax);
    }

    [Fact]
    public void Deny_only_facts_enforce_a_static_deny_and_reject_a_dynamic_operand()
    {
        var policy = new ShellCommandPolicy(
            PowerShellEnvironment,
            additionalDenyPatterns: null,
            overrideRules:
            [
                new HardDenyRule
                {
                    Verb = ["custom-tool"],
                    FirstPath = new PathConstraint { OneOf = ["$path"] },
                    Reason = "test_rule"
                }
            ]);

        var clauses = Collect("netclaw daemon stop");
        var analysis = new ShellCommandAnalysis(
            PowerShellEnvironment,
            source: "Write-Output ok",
            @"C:\work",
            commands: [],
            clauses,
            ShellAnalysisFailure.Unresolved,
            new HashSet<ClauseElement>(ReferenceEqualityComparer.Instance),
            syntaxProofComplete: false);

        Assert.False(policy.Evaluate(analysis).Allowed);
        Assert.False(policy.EvaluateDenyOnlyClauses(
            Collect("Start-Process pwsh -Verb R`unAs")).Allowed);
        Assert.True(policy.EvaluateDenyOnlyClauses(
            Collect("custom-tool $path")).Allowed);
    }

    [Fact]
    public void Tree_traversal_policy_separates_reusable_and_exact_only_effects()
    {
        const string command = @"Get-ChildItem -Path C:\work -Recurse";
        var windows = new ShellCommandAnalyzer(WindowsPowerShellEnvironment)
            .Analyze(command, @"C:\work");
        var powerShell7 = new ShellCommandAnalyzer(PowerShellEnvironment)
            .Analyze(command, @"C:\work");

        Assert.True(windows.RequiresExactTreeApproval);
        Assert.False(powerShell7.RequiresExactTreeApproval);
        Assert.False(ShellFileSystemTreeAccessPolicy.IsReusableTraversal(
            ShellTreeTraversalMode.Unknown));
        Assert.False(ShellFileSystemTreeAccessPolicy.IsReusableTraversal(
            (ShellTreeTraversalMode)int.MaxValue));
        Assert.True(ShellFileSystemTreeAccessPolicy.IsReusableTraversal(
            ShellTreeTraversalMode.DirectChildren));
        Assert.True(ShellFileSystemTreeAccessPolicy.IsReusableTraversal(
            ShellTreeTraversalMode.RecursiveWithoutFollowingLinks));
        Assert.False(ShellFileSystemTreeAccessPolicy.IsReusableTraversal(
            ShellTreeTraversalMode.RecursiveMayFollowLinks));

        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            WindowsPowerShellEnvironment,
            [windows.Commands[0], powerShell7.Commands[0]]));
        Assert.False(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.Bash,
            Assert.Single(
                new ShellCommandAnalyzer(
                    ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux))
                    .Analyze("Get-ChildItem", "/work")
                    .Commands)));
    }

    [Fact]
    public void Tree_traversal_policy_rejects_missing_multiple_and_malformed_facts()
    {
        var occurrence = Assert.Single(
            new ShellCommandAnalyzer(PowerShellEnvironment)
                .Analyze(
                    @"Get-ChildItem -Path C:\work -Recurse",
                    @"C:\work")
                .Commands);
        var access = Assert.Single(occurrence.FileSystemTreeAccesses);

        SetProperty(occurrence, nameof(CommandOccurrence.FileSystemTreeAccesses),
            Array.Empty<ShellFileSystemTreeAccess>());
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            occurrence));

        SetProperty(occurrence, nameof(CommandOccurrence.FileSystemTreeAccesses),
            Array.AsReadOnly([access, access]));
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            occurrence));

        SetProperty(occurrence, nameof(CommandOccurrence.FileSystemTreeAccesses),
            Array.AsReadOnly(Enumerable.Repeat(access, 33).ToArray()));
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            occurrence));

        SetProperty(access, nameof(ShellFileSystemTreeAccess.Traversal),
            (ShellTreeTraversalMode)int.MaxValue);
        SetProperty(occurrence, nameof(CommandOccurrence.FileSystemTreeAccesses),
            Array.AsReadOnly([access]));
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            occurrence));

        var alias = Assert.Single(
            new ShellCommandAnalyzer(PowerShellEnvironment)
                .Analyze("gci", @"C:\work")
                .Commands);
        SetProperty(alias, nameof(CommandOccurrence.FileSystemTreeAccesses),
            Array.Empty<ShellFileSystemTreeAccess>());
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            alias));

        var implicitAccess = Assert.Single(
            Assert.Single(
                    new ShellCommandAnalyzer(PowerShellEnvironment)
                        .Analyze("Get-ChildItem", @"C:\work")
                        .Commands)
                .FileSystemTreeAccesses);
        var unknownAccess = Assert.Single(
            Assert.Single(
                    new ShellCommandAnalyzer(WindowsPowerShellEnvironment)
                        .Analyze(
                            @"Get-ChildItem -Path C:\work -Recurse:$flag",
                            @"C:\work")
                        .Commands)
                .FileSystemTreeAccesses);
        var ordinary = Assert.Single(
            new ShellCommandAnalyzer(PowerShellEnvironment)
                .Analyze("Write-Output ok", @"C:\work")
                .Commands);
        SetProperty(ordinary, nameof(CommandOccurrence.FileSystemTreeAccesses),
            Array.AsReadOnly(Enumerable.Repeat(implicitAccess, 32).ToArray()));
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            ordinary));

        SetProperty(ordinary, nameof(CommandOccurrence.FileSystemTreeAccesses),
            Array.AsReadOnly([implicitAccess, unknownAccess]));
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            ordinary));
    }

    [Fact]
    public void Tree_root_policy_rejects_corrupted_reference_and_domain_facts()
    {
        Assert.True(ShellFileSystemTreeAccessPolicy.IsReusableLeafPattern(@"C:\*.cs"));
        Assert.True(ShellFileSystemTreeAccessPolicy.IsReusableLeafPattern(
            @"\\server\share\*.cs"));
        Assert.True(ShellFileSystemTreeAccessPolicy.IsReusableLeafPattern("*.cs"));
        Assert.False(ShellFileSystemTreeAccessPolicy.IsReusableLeafPattern(@"C:\work"));
        Assert.False(ShellFileSystemTreeAccessPolicy.IsReusableLeafPattern(
            @"C:\work\*\child"));
        Assert.False(ShellFileSystemTreeAccessPolicy.IsReusableLeafPattern("C:*"));
        Assert.False(ShellFileSystemTreeAccessPolicy.IsReusableLeafPattern("C:*.cs"));
        Assert.False(ShellFileSystemTreeAccessPolicy.IsReusableLeafPattern(
            @"C:folder\*.cs"));
        Assert.Throws<ArgumentNullException>(() =>
            ShellFileSystemTreeAccessPolicy.IsReusableLeafPattern(null!));
        Assert.False(ShellFileSystemTreeAccessPolicy.IsReusableLeafPattern(@"\*.cs"));
        Assert.False(ShellFileSystemTreeAccessPolicy.IsReusableLeafPattern("/*.cs"));
        Assert.False(ShellFileSystemTreeAccessPolicy.IsReusableLeafPattern(
            @"\\server\*.cs"));
        Assert.False(ShellFileSystemTreeAccessPolicy.IsReusableLeafPattern(@"\\*.cs"));
        Assert.False(ShellFileSystemTreeAccessPolicy.IsReusableLeafPattern(
            @"\\?\C:\*.cs"));

        foreach (var source in new[]
                 {
                     @"Get-ChildItem -Path C:\*.cs",
                     @"Get-ChildItem -Path \\server\share\*.cs"
                 })
        {
            var boundedPattern = Assert.Single(
                new ShellCommandAnalyzer(PowerShellEnvironment)
                    .Analyze(source, @"C:\work")
                    .Commands);
            Assert.False(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
                ShellGrammar.PowerShell,
                boundedPattern));
        }

        foreach (var invalidPattern in new[]
                 {
                     @"\*.cs",
                     "/*.cs",
                     @"\\server\*.cs",
                     @"\\*.cs",
                     @"\\?\C:\*.cs",
                     "C:*.cs",
                     @"C:folder\*.cs"
                 })
        {
            var corruptedPattern = Assert.Single(
                new ShellCommandAnalyzer(PowerShellEnvironment)
                    .Analyze(@"Get-ChildItem -Path C:\work\*.cs", @"C:\work")
                    .Commands);
            var corruptedAccess = Assert.Single(corruptedPattern.FileSystemTreeAccesses);
            var corruptedArgument = Assert.Single(
                corruptedPattern.Arguments,
                static argument => argument.Argument.IsPath);
            SetProperty(
                corruptedArgument.Element,
                nameof(ClauseElement.Value),
                invalidPattern);
            SetProperty(
                corruptedAccess,
                nameof(ShellFileSystemTreeAccess.Root),
                CreateDomain<ShellValueDomain.PathPattern>(invalidPattern, @"C:\work"));
            Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
                ShellGrammar.PowerShell,
                corruptedPattern));
        }

        var inline = Assert.Single(
            new ShellCommandAnalyzer(PowerShellEnvironment)
                .Analyze(@"Get-ChildItem -Path:C:\work\*.cs", @"C:\work")
                .Commands);
        Assert.False(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            inline));

        var inlineAccess = Assert.Single(inline.FileSystemTreeAccesses);
        var inlineRoot = Assert.IsType<ShellValueDomain.PathPattern>(inlineAccess.Root);
        SetProperty(
            inlineAccess,
            nameof(ShellFileSystemTreeAccess.Root),
            CreateDomain<ShellValueDomain.PathPattern>(
                inlineRoot.Pattern,
                @"C:\other"));
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            inline));

        var resolvedOnly = Assert.Single(
            new ShellCommandAnalyzer(PowerShellEnvironment)
                .Analyze(@"Get-ChildItem -Path C:\work", @"C:\work")
                .Commands);
        var resolvedOnlyArgument = Assert.Single(
            resolvedOnly.Arguments,
            static argument => argument.Argument.IsPath);
        SetProperty(
            resolvedOnlyArgument,
            nameof(AnalyzedArgument.AuthoredFileSystemValue),
            CreateDomain<ShellValueDomain.Unknown>());
        Assert.False(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            resolvedOnly));

        var first = Assert.Single(
            new ShellCommandAnalyzer(PowerShellEnvironment)
                .Analyze(@"Get-ChildItem -Path C:\work", @"C:\work")
                .Commands);
        var second = Assert.Single(
            new ShellCommandAnalyzer(PowerShellEnvironment)
                .Analyze(@"Get-ChildItem -Path C:\work", @"C:\work")
                .Commands);
        var access = Assert.Single(first.FileSystemTreeAccesses);
        var originalRoot = access.Root;
        var firstArgument = Assert.Single(
            first.Arguments,
            static argument => argument.Argument.IsPath);
        var foreignArgument = Assert.Single(second.Arguments,
            static argument => argument.Argument.IsPath);

        SetProperty(
            access,
            nameof(ShellFileSystemTreeAccess.Root),
            CreateDomain<ShellValueDomain.Exact>(@"C:\other"));
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            first));
        SetProperty(access, nameof(ShellFileSystemTreeAccess.Root), originalRoot);

        SetProperty(access, nameof(ShellFileSystemTreeAccess.RootArgument), foreignArgument);
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            first));

        var elementMismatch = Assert.Single(
            new ShellCommandAnalyzer(PowerShellEnvironment)
                .Analyze(@"Get-ChildItem -Path C:\work", @"C:\work")
                .Commands);
        var elementMismatchArgument = Assert.Single(
            elementMismatch.Arguments,
            static argument => argument.Argument.IsPath);
        SetProperty(
            elementMismatchArgument,
            nameof(AnalyzedArgument.Element),
            foreignArgument.Element);
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            elementMismatch));

        var flagMismatch = Assert.Single(
            new ShellCommandAnalyzer(PowerShellEnvironment)
                .Analyze(@"Get-ChildItem -Path C:\work", @"C:\work")
                .Commands);
        var flagMismatchArgument = Assert.Single(
            flagMismatch.Arguments,
            static argument => argument.Argument.IsPath);
        SetProperty(flagMismatchArgument.Argument, nameof(Arg.Raw), "-Path");
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            flagMismatch));

        var cwdMismatch = Assert.Single(
            new ShellCommandAnalyzer(PowerShellEnvironment)
                .Analyze(@"Get-ChildItem -Path C:\work", @"C:\work")
                .Commands);
        var cwdMismatchArgument = Assert.Single(
            cwdMismatch.Arguments,
            static argument => argument.Argument.IsPath);
        SetProperty(
            cwdMismatchArgument.Argument,
            nameof(Arg.IsCwdAttribution),
            true);
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            cwdMismatch));

        var pathMismatch = Assert.Single(
            new ShellCommandAnalyzer(PowerShellEnvironment)
                .Analyze(@"Get-ChildItem -Path C:\work", @"C:\work")
                .Commands);
        var pathMismatchArgument = Assert.Single(
            pathMismatch.Arguments,
            static argument => argument.Argument.IsPath);
        SetProperty(pathMismatchArgument.Argument, nameof(Arg.IsPath), false);
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            pathMismatch));

        SetProperty(access, nameof(ShellFileSystemTreeAccess.RootArgument), null!);
        SetProperty(access, nameof(ShellFileSystemTreeAccess.Root),
            CreateDomain<ShellValueDomain.Exact>(" "));
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            first));

        SetProperty(access, nameof(ShellFileSystemTreeAccess.Root),
            CreateDomain<ShellValueDomain.PathPattern>(@"C:\work\*.cs", ""));
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            first));

        SetProperty(access, nameof(ShellFileSystemTreeAccess.RootArgument), firstArgument);
        SetProperty(
            access,
            nameof(ShellFileSystemTreeAccess.Root),
            CreateDomain<ShellValueDomain.Concatenation>(
                (object)new ShellValueDomain[]
                {
                    CreateDomain<ShellValueDomain.Exact>(@"C:\wo"),
                    CreateDomain<ShellValueDomain.Exact>("rk")
                }));
        Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            ShellGrammar.PowerShell,
            first));

        foreach (var invalidImplicitRoot in new[] { string.Empty, "C:\\work\nchild" })
        {
            var invalidImplicit = Assert.Single(
                new ShellCommandAnalyzer(PowerShellEnvironment)
                    .Analyze("Get-ChildItem", @"C:\work")
                    .Commands);
            var invalidAccess = Assert.Single(invalidImplicit.FileSystemTreeAccesses);
            var invalidDomain = CreateDomain<ShellValueDomain.Exact>(invalidImplicitRoot);
            SetProperty(invalidAccess, nameof(ShellFileSystemTreeAccess.Root), invalidDomain);
            SetProperty(
                invalidImplicit,
                nameof(CommandOccurrence.WorkingDirectory),
                invalidDomain);
            Assert.True(ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
                ShellGrammar.PowerShell,
                invalidImplicit));
        }
    }

    [Fact]
    public void Audited_nonfilesystem_policy_accepts_only_bounded_typed_facts()
    {
        var reusable = new ShellCommandAnalyzer(WindowsPowerShellEnvironment).Analyze(
            "Get-Content C:\\work\\a.cs | Select-Object -Index (113..145); "
            + "Get-Process | Select-Object Name,Name",
            @"C:\work");
        var dynamic = new ShellCommandAnalyzer(WindowsPowerShellEnvironment).Analyze(
            "Get-Process | Select-Object Name,$property",
            @"C:\work");
        var hugeRange = new ShellCommandAnalyzer(WindowsPowerShellEnvironment).Analyze(
            "Get-Content C:\\work\\a.cs | Select-Object -Index (0..2147483647)",
            @"C:\work");
        var tooWideBoundary = new ShellCommandAnalyzer(WindowsPowerShellEnvironment).Analyze(
            "Get-Content C:\\work\\a.cs | Select-Object -Index (0..4096)",
            @"C:\work");
        var boundaryRanges = new ShellCommandAnalyzer(WindowsPowerShellEnvironment).Analyze(
            "Get-Content C:\\work\\a.cs | Select-Object -Index (0..0); "
            + "Get-Content C:\\work\\a.cs | Select-Object -Index (0..4095); "
            + "Get-Content C:\\work\\a.cs | Select-Object -Index (2147483646..2147483647)",
            @"C:\work");
        var range = reusable.Commands
            .SelectMany(static command => command.Arguments)
            .Single(static argument =>
                argument.AuthoredValue is ShellValueDomain.IntegerRange);
        var ordered = reusable.Commands
            .SelectMany(static command => command.Arguments)
            .Single(static argument =>
                argument.AuthoredNonFileSystemValue is ShellValueDomain.OrderedList);
        var exact = Assert.Single(
            new ShellCommandAnalyzer(WindowsPowerShellEnvironment)
                .Analyze(
                    "Get-Process | Select-Object -ExpandProperty FullName",
                    @"C:\work")
                .Commands[1]
                .Arguments,
            static argument =>
                argument.AuthoredNonFileSystemValue is ShellValueDomain.Exact);
        var path = Assert.Single(
            new ShellCommandAnalyzer(WindowsPowerShellEnvironment)
                .Analyze("Get-Content C:\\work\\a.cs", @"C:\work")
                .Commands[0]
                .Arguments,
            static argument => argument.Argument.IsPath);
        var unproved = dynamic.Commands
            .SelectMany(static command => command.Arguments)
            .Single(static argument => argument.Argument.Raw.Contains(',', StringComparison.Ordinal));

        Assert.True(ShellCommandAnalysis.HasAuditedNonFileSystemValue(range));
        Assert.True(ShellCommandAnalysis.HasAuditedNonFileSystemValue(ordered));
        Assert.True(ShellCommandAnalysis.HasAuditedNonFileSystemValue(exact));
        Assert.False(ShellCommandAnalysis.HasAuditedNonFileSystemValue(path));
        Assert.False(ShellCommandAnalysis.HasAuditedNonFileSystemValue(unproved));

        var reversed = CreateDomain<ShellValueDomain.IntegerRange>(0L, 100L);
        SetField(reversed, "<MinimumInclusive>k__BackingField", 100L);
        SetField(reversed, "<MaximumInclusive>k__BackingField", 0L);
        SetProperty(range, nameof(AnalyzedArgument.Value), reversed);
        SetProperty(range, nameof(AnalyzedArgument.AuthoredValue), reversed);
        Assert.False(ShellCommandAnalysis.HasAuditedNonFileSystemValue(range));

        var exactDomain = exact.AuthoredNonFileSystemValue;
        SetProperty(
            exact,
            nameof(AnalyzedArgument.AuthoredNonFileSystemValue),
            CreateDomain<ShellValueDomain.Concatenation>(
                (object)new ShellValueDomain[]
                {
                    CreateDomain<ShellValueDomain.Exact>("Full"),
                    CreateDomain<ShellValueDomain.Exact>("Name")
                }));
        Assert.False(ShellCommandAnalysis.HasAuditedNonFileSystemValue(exact));
        SetProperty(exact, nameof(AnalyzedArgument.AuthoredNonFileSystemValue), exactDomain);

        // Model a corrupted or future producer that publishes conflicting
        // filesystem and nonfilesystem proof on one immutable argument.
        SetProperty(
            exact,
            nameof(AnalyzedArgument.AuthoredFileSystemValue),
            path.AuthoredFileSystemValue);
        Assert.False(ShellCommandAnalysis.HasAuditedNonFileSystemValue(exact));

        Assert.False(reusable.HasDynamicSyntax);
        Assert.True(dynamic.HasDynamicSyntax);
        Assert.True(hugeRange.HasDynamicSyntax);
        Assert.True(tooWideBoundary.HasDynamicSyntax);
        Assert.False(boundaryRanges.HasDynamicSyntax);
    }

    [Fact]
    public void Exact_tree_mode_and_reviewed_safe_guards_remain_independent()
    {
        Assert.Equal(
            ToolApprovalMode.Approval,
            ToolAccessPolicy.ResolveShellApprovalMode(
                ToolApprovalMode.Auto,
                requiresExactTreeApproval: true));
        Assert.Equal(
            ToolApprovalMode.Auto,
            ToolAccessPolicy.ResolveShellApprovalMode(
                ToolApprovalMode.Auto,
                requiresExactTreeApproval: false));
        Assert.Equal(
            ToolApprovalMode.Deny,
            ToolAccessPolicy.ResolveShellApprovalMode(
                ToolApprovalMode.Deny,
                requiresExactTreeApproval: true));

        var occurrence = Assert.Single(
            new ShellCommandAnalyzer(WindowsPowerShellEnvironment)
                .Analyze(
                    @"Get-ChildItem -Path C:\work -Recurse",
                    @"C:\work")
                .Commands);
        var candidate = new ApprovalCandidate("Get-ChildItem", @"C:\work")
        {
            Shell = ApprovalShell.PowerShell,
            VerbTokens = ["Get-ChildItem"],
            SourceOccurrence = occurrence
        };
        var projected = new ShellPolicyCandidate(
            new ShellPolicyCandidateId(0),
            candidate,
            occurrence);
        var pathFacts = Assert.Single(ShellPolicyPathFacts.Create(
            [projected],
            ShellPathStyle.Windows));
        Assert.Contains(
            pathFacts.Real.Facts,
            static fact => fact.Source.Origin == ShellPolicyPathOrigin.FileSystemTreeRoot
                           && fact.State == ShellPolicyPathResolutionState.Known);

        var policy = new ReviewedSafeShellPolicy(
            SafeVerbList.FromVerbs(ApprovalShell.PowerShell, ["Get-ChildItem"]),
            new PathAccessPolicy(
                new ToolConfig(),
                new NetclawPaths(),
                new ToolPathPolicy(WindowsPowerShellEnvironment, [])));
        Assert.False(policy.ShortCircuits(
            projected,
            pathFacts,
            PersonalContext()));
        Assert.False(InvokeReviewedSyntax(policy, candidate, occurrence, pathFacts.Real));

        var reusableOccurrence = Assert.Single(
            new ShellCommandAnalyzer(PowerShellEnvironment)
                .Analyze("Get-ChildItem", @"C:\work")
                .Commands);
        var reusableTreeAccesses = reusableOccurrence.FileSystemTreeAccesses;
        var reusableCandidate = candidate with { SourceOccurrence = reusableOccurrence };
        Assert.True(InvokeReviewedSyntax(
            policy,
            reusableCandidate,
            reusableOccurrence,
            resolvedPaths: null));

        SetProperty(
            reusableOccurrence,
            nameof(CommandOccurrence.FileSystemTreeAccesses),
            Array.Empty<ShellFileSystemTreeAccess>());
        Assert.False(InvokeReviewedSyntax(
            policy,
            reusableCandidate,
            reusableOccurrence,
            resolvedPaths: null));

        var bashOccurrence = Assert.Single(
            new ShellCommandAnalyzer(
                    ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux))
                .Analyze("Get-ChildItem", "/work")
                .Commands);
        var bashCandidate = new ApprovalCandidate("Get-ChildItem", "/work")
        {
            Shell = ApprovalShell.Bash,
            VerbTokens = ["Get-ChildItem"],
            SourceOccurrence = bashOccurrence
        };
        var bashPolicy = new ReviewedSafeShellPolicy(
            SafeVerbList.FromVerbs(ApprovalShell.Bash, ["Get-ChildItem"]),
            new PathAccessPolicy(
                new ToolConfig(),
                new NetclawPaths(),
                new ToolPathPolicy([])));
        Assert.True(InvokeReviewedSyntax(
            bashPolicy,
            bashCandidate,
            bashOccurrence,
            resolvedPaths: null));

        var originalForgedAnalysis = new ShellCommandAnalyzer(PowerShellEnvironment)
            .Analyze("Write-Output ok", @"C:\work");
        var forgedOccurrence = Assert.Single(originalForgedAnalysis.Commands);
        SetProperty(
            forgedOccurrence,
            nameof(CommandOccurrence.FileSystemTreeAccesses),
            reusableTreeAccesses);
        var forgedAnalysis = new ShellCommandAnalysis(
            PowerShellEnvironment,
            "Write-Output ok",
            @"C:\work",
            [forgedOccurrence],
            [],
            ShellAnalysisFailure.None,
            new HashSet<ClauseElement>(ReferenceEqualityComparer.Instance),
            syntaxProofComplete: true);
        var forgedCandidate = new ApprovalCandidate("Write-Output", @"C:\work")
        {
            Shell = ApprovalShell.PowerShell,
            VerbTokens = ["Write-Output"],
            SourceOccurrence = forgedOccurrence
        };
        var forgedMatcherResult = new ShellApprovalMatcher(PowerShellEnvironment)
            .AnalyzeInvocation(
                new ToolName("shell_execute"),
                new Dictionary<string, object?>
                {
                    ["Command"] = "Write-Output ok",
                    ["WorkingDirectory"] = @"C:\work"
                },
                forgedAnalysis);

        Assert.True(forgedMatcherResult.IsMessy);
        Assert.Empty(forgedMatcherResult.Candidates);
        Assert.False(InvokeReviewedSyntax(
            policy,
            forgedCandidate,
            forgedOccurrence,
            resolvedPaths: null));
    }

    [Fact]
    public void Matcher_keeps_exact_tree_out_of_reusable_candidates()
    {
        const string command = @"Get-ChildItem -Path C:\work -Recurse";
        var windowsMatcher = new ShellApprovalMatcher(WindowsPowerShellEnvironment);
        var powerShell7Matcher = new ShellApprovalMatcher(PowerShellEnvironment);
        var arguments = new Dictionary<string, object?>
        {
            ["Command"] = command,
            ["WorkingDirectory"] = @"C:\work"
        };

        var exact = windowsMatcher.AnalyzeInvocation(
            new ToolName("shell_execute"),
            arguments);
        var reusable = powerShell7Matcher.AnalyzeInvocation(
            new ToolName("shell_execute"),
            arguments);
        var dynamic = windowsMatcher.AnalyzeInvocation(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "Get-Process | Select-Object Name,$property",
                ["WorkingDirectory"] = @"C:\work"
            });

        Assert.True(exact.IsMessy);
        Assert.Empty(exact.Candidates);
        Assert.NotEmpty(exact.Patterns);
        Assert.False(reusable.IsMessy);
        Assert.NotEmpty(reusable.Candidates);
        Assert.True(dynamic.IsMessy);
        Assert.Empty(dynamic.Candidates);
    }

    private static IReadOnlyList<Clause> Collect(string source)
    {
        var parsed = PowerShellEnvironment.Parse(source, @"C:\work");
        var clauses = new List<Clause>();
        ShellCommandAnalysis.CollectSourceAuthenticDenyOnlyClauses(
            parsed.Syntax,
            source,
            clauses);
        return clauses;
    }

    private static ToolInvocationContext PersonalContext()
        => new(
            new ToolRunScope
            {
                Session = new ToolSessionScope.Sessionless(),
                Audience = TrustAudience.Personal,
                InlineOutputBudget = InlineOutputBudget.Default,
                InteractiveApproval = new InteractiveApprovalCapability.Unavailable()
            },
            ToolExecutionTimeout.Default);

    private static void SetProperty(object target, string name, object value)
        => target.GetType().GetProperty(name)!.SetValue(target, value);

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static T CreateDomain<T>(params object[] arguments)
        where T : ShellValueDomain
        => (T)(Activator.CreateInstance(
            typeof(T),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: arguments,
            culture: null)
            ?? throw new InvalidOperationException("The domain constructor returned null."));

    private static bool InvokeReviewedSyntax(
        ReviewedSafeShellPolicy policy,
        ApprovalCandidate candidate,
        CommandOccurrence occurrence,
        ShellPolicyResolvedPathView? resolvedPaths)
    {
        var arguments = new object?[] { candidate, occurrence, resolvedPaths, null };
        return (bool)(typeof(ReviewedSafeShellPolicy)
            .GetMethod(
                "IsReviewedDiagnosticSyntax",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(policy, arguments)
            ?? throw new InvalidOperationException("The syntax policy returned null."));
    }
}
