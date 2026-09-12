// -----------------------------------------------------------------------
// <copyright file="GitSkillPluginAcquirerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Security.Skills;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class GitSkillPluginAcquirerTests : IDisposable
{
    private const string Commit = "13e26d39ed01d97ea592235d041304d289f4ba07";
    private readonly DisposableTempDir _temp = new();

    public void Dispose()
    {
        GitSkillPluginAcquirer.DeleteDirectory(new NetclawPaths(_temp.Path).ManagedGitSkillsDirectory);
        _temp.Dispose();
    }

    [Fact]
    public async Task Acquire_imports_only_declared_skill_folders_and_resources()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.2.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile),
            ("repo/skills/alpha/references/exact.md", "exact resource", TarEntryType.RegularFile),
            ("repo/scripts/install.sh", "must not import", TarEntryType.RegularFile));
        var candidate = await CreateAcquirer(archive).AcquireAsync(Source(), TestContext.Current.CancellationToken);

        Assert.Equal(Commit, candidate.Commit);
        Assert.Equal("1.2.0", candidate.Version);
        Assert.Equal("alpha", Assert.Single(candidate.Skills).Name);
        Assert.Equal("exact resource", await File.ReadAllTextAsync(
            Path.Combine(candidate.Directory, "alpha", "references", "exact.md"),
            TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(candidate.Directory, "scripts", "install.sh")));
    }

    [Fact]
    public async Task Acquire_ignores_an_unselected_root_link()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile),
            ("repo/CLAUDE.md", "AGENTS.md", TarEntryType.SymbolicLink));

        var candidate = await CreateAcquirer(archive).AcquireAsync(Source(), TestContext.Current.CancellationToken);

        Assert.Equal("alpha", Assert.Single(candidate.Skills).Name);
        Assert.False(File.Exists(Path.Combine(candidate.Directory, "CLAUDE.md")));
    }

    [Fact]
    public async Task Acquire_ignores_archive_wide_PAX_metadata()
    {
        var archive = CreateArchive(
            ("pax_global_header", "", TarEntryType.GlobalExtendedAttributes),
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));

        var candidate = await CreateAcquirer(archive).AcquireAsync(Source(), TestContext.Current.CancellationToken);

        Assert.Equal("alpha", Assert.Single(candidate.Skills).Name);
    }

    [Fact]
    public async Task Acquire_counts_archive_wide_PAX_metadata_against_the_entry_limit()
    {
        var archive = CreateArchive(
            ("pax_global_header", "", TarEntryType.GlobalExtendedAttributes),
            ("pax_global_header", "", TarEntryType.GlobalExtendedAttributes),
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));
        var limits = Limits() with { MaximumArchiveEntries = 3 };

        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(archive, limits: limits).AcquireAsync(Source(), TestContext.Current.CancellationToken));

        Assert.Contains("entry-count limit", error.Message);
    }

    [Fact]
    public async Task Acquire_rejects_a_link_without_importing_any_candidate()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile),
            ("repo/skills/alpha/escape", "target", TarEntryType.SymbolicLink));

        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(archive).AcquireAsync(Source(), TestContext.Current.CancellationToken));

        Assert.False(error.SecurityRejection);
        var source = Source();
        Assert.False(Directory.Exists(new NetclawPaths(_temp.Path).ManagedGitSkillCommitDirectory(
            source.Id,
            ManagedPluginSourceValidator.Fingerprint(source),
            Commit)));
    }

    [Theory]
    [InlineData(ManagedPluginSourceValidator.AgentPluginFormat)]
    [InlineData(ManagedPluginSourceValidator.CodexFormat)]
    public async Task Acquire_marks_only_a_confirmed_scanner_rejection_as_security_rejection(string format)
    {
        var manifest = format == ManagedPluginSourceValidator.AgentPluginFormat
            ? ("repo/plugin.json", AgentManifest("fixture", "1.0.0"), TarEntryType.RegularFile)
            : ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile);
        var archive = CreateArchive(
            manifest,
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));

        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(archive, new RejectScanner()).AcquireAsync(
                Source(format), TestContext.Current.CancellationToken));

        Assert.True(error.SecurityRejection);
    }

    [Fact]
    public async Task Acquire_surfaces_a_scanner_failure_as_a_retryable_error()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));

        var error = await Assert.ThrowsAsync<GitSkillPluginScannerUnavailableException>(() =>
            CreateAcquirer(archive, new FailedScanner()).AcquireAsync(
                Source(), TestContext.Current.CancellationToken));

        Assert.Contains("scanner", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Acquire_surfaces_a_thrown_scanner_error_as_a_retryable_error()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));

        await Assert.ThrowsAsync<GitSkillPluginScannerUnavailableException>(() =>
            CreateAcquirer(archive, new ThrowingScanner()).AcquireAsync(
                Source(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Acquire_uses_the_default_skills_folder_when_the_manifest_omits_skills()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", "{ \"name\": \"fixture\", \"version\": \"1.0.0\" }", TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile),
            ("repo/skills/category/nested/SKILL.md", Skill("nested"), TarEntryType.RegularFile));

        var candidate = await CreateAcquirer(archive).AcquireAsync(Source(), TestContext.Current.CancellationToken);

        Assert.Equal("alpha", Assert.Single(candidate.Skills).Name);
        Assert.False(Directory.Exists(Path.Combine(candidate.Directory, "nested")));
    }

    [Fact]
    public async Task Acquire_accepts_an_Agent_Plugins_manifest_with_portable_metadata()
    {
        var archive = CreateArchive(
            ("repo/plugin.json", AgentManifest("acme.tools", "release-2026-09"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));
        var source = Source(ManagedPluginSourceValidator.AgentPluginFormat);

        var candidate = await CreateAcquirer(archive).AcquireAsync(
            source,
            TestContext.Current.CancellationToken);

        Assert.Equal(ManagedPluginSourceValidator.AgentPluginFormat, candidate.Format);
        Assert.Equal("acme.tools", candidate.Name);
        Assert.Equal("release-2026-09", candidate.Version);
        Assert.Equal("alpha", Assert.Single(candidate.Skills).Name);
    }

    [Fact]
    public async Task Acquire_auto_prefers_a_recognized_Agent_Plugins_manifest()
    {
        var archive = CreateArchive(
            ("repo/plugin.json", AgentManifest("portable.plugin", "portable-version"), TarEntryType.RegularFile),
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));

        var candidate = await CreateAcquirer(archive).AcquireAsync(
            Source(ManagedPluginSourceValidator.AutoFormat),
            TestContext.Current.CancellationToken);

        Assert.Equal(ManagedPluginSourceValidator.AgentPluginFormat, candidate.Format);
        Assert.Equal("portable.plugin", candidate.Name);
    }

    [Fact]
    public async Task Acquire_auto_does_not_fall_through_after_portable_selection()
    {
        var archive = CreateArchive(
            ("repo/plugin.json", AgentManifest("Bad_Name", "1.0.0"), TarEntryType.RegularFile),
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));

        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(archive).AcquireAsync(
                Source(ManagedPluginSourceValidator.AutoFormat),
                TestContext.Current.CancellationToken));

        Assert.Contains("Agent Plugins manifest name", error.Message);
    }

    [Fact]
    public async Task Acquire_portable_skips_an_invalid_skill_and_keeps_a_valid_sibling()
    {
        var archive = CreateArchive(
            ("repo/plugin.json", AgentManifest("portable.plugin", "1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile),
            ("repo/skills/invalid/SKILL.md", "# Missing frontmatter", TarEntryType.RegularFile));

        var candidate = await CreateAcquirer(archive).AcquireAsync(
            Source(ManagedPluginSourceValidator.AgentPluginFormat),
            TestContext.Current.CancellationToken);

        Assert.Equal("alpha", Assert.Single(candidate.Skills).Name);
        Assert.NotEmpty(candidate.Notices);
    }

    [Fact]
    public async Task Acquire_portable_reports_unknown_fields_and_unsupported_MCP()
    {
        var manifest = AgentManifest("portable.plugin", "1.0.0")
            .Replace("}", ", \"futureField\": true, \"extensions\": [] }");
        var archive = CreateArchive(
            ("repo/plugin.json", manifest, TarEntryType.RegularFile),
            ("repo/mcp.json", "{}", TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));

        var candidate = await CreateAcquirer(archive).AcquireAsync(
            Source(ManagedPluginSourceValidator.AgentPluginFormat),
            TestContext.Current.CancellationToken);

        Assert.Contains(candidate.Notices, notice => notice.Contains("futureField", StringComparison.Ordinal));
        Assert.Contains(candidate.Notices, notice => notice.Contains("extensions", StringComparison.Ordinal));
        Assert.Contains(candidate.Notices, notice => notice.Contains("MCP", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Acquire_portable_ignores_unimplemented_extension_content()
    {
        var manifest = AgentManifest("portable.plugin", "1.0.0")
            .Replace("}", ", \"extensions\": { \"org.example.client\": 42 } }");
        var archive = CreateArchive(
            ("repo/plugin.json", manifest, TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));

        var candidate = await CreateAcquirer(archive).AcquireAsync(
            Source(ManagedPluginSourceValidator.AgentPluginFormat),
            TestContext.Current.CancellationToken);

        Assert.Equal("alpha", Assert.Single(candidate.Skills).Name);
    }

    [Fact]
    public async Task Acquire_portable_accepts_a_package_without_supported_components()
    {
        var archive = CreateArchive(
            ("repo/plugin.json", AgentManifest("portable.plugin", "release-label"), TarEntryType.RegularFile),
            ("repo/mcp.json", "{}", TarEntryType.RegularFile));

        var candidate = await CreateAcquirer(archive).AcquireAsync(
            Source(ManagedPluginSourceValidator.AgentPluginFormat),
            TestContext.Current.CancellationToken);

        Assert.Empty(candidate.Skills);
        Assert.Contains(candidate.Notices, notice => notice.Contains("MCP", StringComparison.Ordinal));
        Assert.Contains(candidate.Notices, notice => notice.Contains("no supported skills", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Acquire_portable_skips_an_invalid_skills_component()
    {
        var archive = CreateArchive(
            ("repo/plugin.json", AgentManifest("portable.plugin", "1.0.0"), TarEntryType.RegularFile),
            ("repo/skills", "not a directory", TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));

        var candidate = await CreateAcquirer(archive).AcquireAsync(
            Source(ManagedPluginSourceValidator.AgentPluginFormat),
            TestContext.Current.CancellationToken);

        Assert.Empty(candidate.Skills);
        Assert.Contains(candidate.Notices, notice => notice.Contains("invalid Agent Plugins skills", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-01")]
    public async Task Acquire_rejects_a_non_strict_plugin_version(string version)
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest(version), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));

        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(archive).AcquireAsync(Source(), TestContext.Current.CancellationToken));

        Assert.Contains("strict SemVer", error.Message);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.0-beta.1")]
    [InlineData("1.0.0-beta-name.1")]
    [InlineData("1.0.0+build.7")]
    [InlineData("1.0.0-beta.1+build.7")]
    public async Task Acquire_accepts_strict_SemVer_versions(string version)
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest(version), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));

        var candidate = await CreateAcquirer(archive).AcquireAsync(Source(), TestContext.Current.CancellationToken);

        Assert.Equal(version, candidate.Version);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("42")]
    public async Task Acquire_rejects_a_present_invalid_plugin_version(string version)
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", $"{{ \"name\": \"fixture\", \"version\": {version} }}", TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));

        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(archive).AcquireAsync(Source(), TestContext.Current.CancellationToken));

        Assert.Contains("strict SemVer", error.Message);
    }

    [Fact]
    public async Task Acquire_rejects_an_invalid_manifest_name()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", "{ \"name\": \"Bad Name\" }", TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));

        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(archive).AcquireAsync(Source(), TestContext.Current.CancellationToken));

        Assert.Contains("manifest name", error.Message);
    }

    [Fact]
    public async Task Acquire_reports_ignored_executable_components()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", """
                { "name": "fixture", "hooks": {}, "mcpServers": {}, "apps": {}, "agents": {} }
                """, TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));

        var candidate = await CreateAcquirer(archive).AcquireAsync(Source(), TestContext.Current.CancellationToken);

        Assert.Equal(4, candidate.Notices.Count);
        Assert.Contains(candidate.Notices, notice => notice.Contains("hooks", StringComparison.Ordinal));
        Assert.Contains(candidate.Notices, notice => notice.Contains("mcpServers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Acquire_rejects_the_complete_candidate_when_a_text_resource_is_rejected()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile),
            ("repo/skills/alpha/references/risk.md", "blocked resource", TarEntryType.RegularFile));

        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(archive, new ResourceRejectScanner()).AcquireAsync(
                Source(), TestContext.Current.CancellationToken));

        Assert.True(error.SecurityRejection);
        Assert.Contains("alpha:references/risk.md", error.Message);
    }

    [Fact]
    public async Task Acquire_does_not_replace_an_existing_immutable_commit_directory()
    {
        var firstArchive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile),
            ("repo/skills/alpha/reference.md", "first", TarEntryType.RegularFile));
        var first = await CreateAcquirer(firstArchive).AcquireAsync(Source(), TestContext.Current.CancellationToken);
        var secondArchive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile),
            ("repo/skills/alpha/reference.md", "second", TarEntryType.RegularFile));

        var second = await CreateAcquirer(secondArchive).AcquireAsync(Source(), TestContext.Current.CancellationToken);

        Assert.Equal(first.Directory, second.Directory);
        Assert.Equal("first", await File.ReadAllTextAsync(
            Path.Combine(second.Directory, "alpha", "reference.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Acquire_uses_a_separate_directory_for_each_source_fingerprint()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile),
            ("repo/plugin/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/plugin/skills/beta/SKILL.md", Skill("beta"), TarEntryType.RegularFile));
        var firstSource = Source();
        var secondSource = Source();
        secondSource.Subdirectory = "plugin";

        var first = await CreateAcquirer(archive).AcquireAsync(firstSource, TestContext.Current.CancellationToken);
        var second = await CreateAcquirer(archive).AcquireAsync(secondSource, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Directory, second.Directory);
        Assert.Equal("alpha", Assert.Single(first.Skills).Name);
        Assert.Equal("beta", Assert.Single(second.Skills).Name);
    }

    [Theory]
    [InlineData("repo/skills/alpha/../escape/SKILL.md")]
    [InlineData("repo/skills\\alpha/SKILL.md")]
    [InlineData("repo/skills/alpha/reference.md:payload")]
    [InlineData("repo/skills/alpha/CON.md")]
    [InlineData("repo/skills/alpha/trailing.")]
    public async Task Acquire_rejects_an_unsafe_archive_path(string path)
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            (path, Skill("alpha"), TarEntryType.RegularFile));

        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(archive).AcquireAsync(Source(), TestContext.Current.CancellationToken));

        Assert.Contains("unsafe", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Acquire_rejects_overlapping_declared_skill_roots()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json",
                "{ \"name\": \"fixture\", \"version\": \"1.0.0\", \"skills\": \"./skills/alpha/\" }",
                TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile),
            ("repo/skills/alpha/beta/SKILL.md", Skill("beta"), TarEntryType.RegularFile));

        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(archive).AcquireAsync(Source(), TestContext.Current.CancellationToken));

        Assert.Contains("cannot overlap", error.Message);
    }

    [Fact]
    public async Task Acquire_rejects_invalid_UTF8_in_a_skill_file()
    {
        var skillBytes = Encoding.UTF8.GetBytes(Skill("alpha")).Concat([byte.MaxValue]).ToArray();
        var archive = CreateBinaryArchive(
            ("repo/.codex-plugin/plugin.json", Encoding.UTF8.GetBytes(Manifest("1.0.0")), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", skillBytes, TarEntryType.RegularFile));

        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(archive).AcquireAsync(Source(), TestContext.Current.CancellationToken));

        Assert.Contains("not valid UTF-8", error.Message);
    }

    [Fact]
    public async Task Acquire_rejects_a_skill_file_with_noncanonical_case()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/skill.md", Skill("alpha"), TarEntryType.RegularFile));

        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(archive).AcquireAsync(Source(), TestContext.Current.CancellationToken));

        Assert.Contains("no skills", error.Message);
    }

    [Fact]
    public async Task Acquire_rejects_an_unsafe_archive_redirect()
    {
        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(new UnsafeRedirectHandler()).AcquireAsync(Source(), TestContext.Current.CancellationToken));

        Assert.Contains("unsafe archive redirect", error.Message);
    }

    [Fact]
    public void CreateHttpHandler_disables_automatic_redirects()
    {
        using var handler = GitSkillPluginAcquirer.CreateHttpHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task Acquire_rejects_a_selected_file_that_exceeds_the_limit()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha") + new string('x', 200), TarEntryType.RegularFile));
        var limits = Limits() with { MaximumSelectedFileBytes = 128 };

        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(archive, limits: limits).AcquireAsync(Source(), TestContext.Current.CancellationToken));

        Assert.Contains("per-file limit", error.Message);
        Assert.False(error.SecurityRejection);
    }

    [Fact]
    public async Task Acquire_rejects_an_archive_that_exceeds_the_decompressed_limit()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha") + new string('x', 2_000), TarEntryType.RegularFile));
        var limits = Limits() with { MaximumDecompressedArchiveBytes = 512 };

        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(archive, limits: limits).AcquireAsync(Source(), TestContext.Current.CancellationToken));

        Assert.Contains("decompressed-size", error.Message);
        Assert.False(error.SecurityRejection);
    }

    [Fact]
    public async Task ResolveCommit_streams_the_GitHub_JSON_response_through_the_size_limit()
    {
        var limits = Limits() with { MaximumManifestBytes = 32 };
        var handler = new StreamingMetadataHandler(
            $"{{\"sha\":\"{Commit}\",\"padding\":\"{new string('x', 100)}\"}}");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateAcquirer(handler, limits: limits)
                .ResolveCommitAsync(Source(), TestContext.Current.CancellationToken));

        Assert.False(handler.Content.WasSerialized);
    }

    [Fact]
    public async Task ResolveDefaultBranch_streams_the_GitHub_JSON_response_through_the_size_limit()
    {
        var limits = Limits() with { MaximumManifestBytes = 32 };
        var handler = new StreamingMetadataHandler(
            $"{{\"default_branch\":\"main\",\"padding\":\"{new string('x', 100)}\"}}");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateAcquirer(handler, limits: limits)
                .ResolveDefaultBranchAsync("owner/repository", TestContext.Current.CancellationToken));

        Assert.False(handler.Content.WasSerialized);
    }

    [Fact]
    public async Task Acquire_sends_the_Netclaw_user_agent_on_each_GitHub_request()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));
        var handler = new GitHubHandler(archive);

        await CreateAcquirer(handler).AcquireAsync(Source(), TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.UserAgents.Count);
        Assert.All(handler.UserAgents, userAgent => Assert.Equal(NetclawUserAgent.Value, userAgent));
    }

    [Theory]
    [InlineData(Commit)]
    [InlineData("13e26d39ed01d97ea592235d041304d289f4ba0713e26d39ed01d97ea592235d")]
    public async Task Acquire_uses_a_fixed_commit_without_a_GitHub_commit_lookup(string commit)
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));
        var handler = new GitHubHandler(archive);
        var source = Source();
        source.ReferenceKind = ManagedPluginReferenceKind.Commit;
        source.Reference = commit;

        var candidate = await CreateAcquirer(handler).AcquireAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(commit, candidate.Commit);
        Assert.Equal(0, handler.CommitRequestCount);
        Assert.Single(handler.UserAgents);
    }

    [Fact]
    public async Task Acquire_accepts_a_64_character_fixed_commit()
    {
        var archive = CreateArchive(
            ("repo/.codex-plugin/plugin.json", Manifest("1.0.0"), TarEntryType.RegularFile),
            ("repo/skills/alpha/SKILL.md", Skill("alpha"), TarEntryType.RegularFile));
        var handler = new GitHubHandler(archive);
        var commit = new string('a', 64);
        var source = Source();
        source.ReferenceKind = ManagedPluginReferenceKind.Commit;
        source.Reference = commit;

        var candidate = await CreateAcquirer(handler).AcquireAsync(
            source, commit, TestContext.Current.CancellationToken);

        Assert.Equal(commit, candidate.Commit);
        Assert.Equal(0, handler.CommitRequestCount);
    }

    [Theory]
    [MemberData(nameof(CorruptArchives))]
    public async Task Acquire_rejects_malformed_archive_content(byte[] archive)
    {
        var error = await Assert.ThrowsAsync<GitSkillPluginRejectedException>(() =>
            CreateAcquirer(archive).AcquireAsync(Source(), TestContext.Current.CancellationToken));

        Assert.Contains("invalid GZip or tar", error.Message);
    }

    public static IEnumerable<object[]> CorruptArchives()
    {
        yield return [Encoding.UTF8.GetBytes("not a gzip archive")];
        yield return [CreateCorruptTarArchive()];
    }

    private GitSkillPluginAcquirer CreateAcquirer(
        byte[] archive,
        ISkillContentScanner? scanner = null,
        GitSkillPluginArchiveLimits? limits = null)
    {
        return CreateAcquirer(new GitHubHandler(archive), scanner, limits);
    }

    private GitSkillPluginAcquirer CreateAcquirer(
        HttpMessageHandler handler,
        ISkillContentScanner? scanner = null,
        GitSkillPluginArchiveLimits? limits = null)
    {
        var client = new HttpClient(handler);
        return new GitSkillPluginAcquirer(
            client,
            new NetclawPaths(_temp.Path),
            TimeProvider.System,
            scanner ?? new NoOpSkillContentScanner(),
            limits ?? Limits());
    }

    private static GitSkillPluginArchiveLimits Limits() => GitSkillPluginArchiveLimits.Default;

    private static ManagedPluginSource Source(
        string format = ManagedPluginSourceValidator.CodexFormat) => new()
    {
        Id = "fixture",
        Repository = "owner/repository",
        Format = format,
        ReferenceKind = ManagedPluginReferenceKind.Branch,
        Reference = "main",
    };

    private static string Manifest(string version) => $$"""
        { "name": "fixture", "version": "{{version}}", "skills": "./skills/" }
        """;

    private static string AgentManifest(string name, string version) => $$"""
        {
          "$schema": "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json",
          "name": "{{name}}",
          "version": "{{version}}"
        }
        """;

    private static string Skill(string name) => $$"""
        ---
        name: {{name}}
        description: A fixture skill.
        ---

        # Fixture
        """;

    private static byte[] CreateArchive(params (string Path, string Content, TarEntryType Type)[] entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (var (path, content, type) in entries)
            {
                if (type == TarEntryType.GlobalExtendedAttributes)
                {
                    writer.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(
                        [new KeyValuePair<string, string>("comment", "fixture")]));
                    continue;
                }

                var entry = new PaxTarEntry(type, path) { Mode = (UnixFileMode)0x1A4 };
                if (type is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                else if (type == TarEntryType.SymbolicLink)
                    entry.LinkName = content;
                writer.WriteEntry(entry);
            }
        }
        return output.ToArray();
    }

    private static byte[] CreateBinaryArchive(params (string Path, byte[] Content, TarEntryType Type)[] entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (var (path, content, type) in entries)
            {
                var entry = new PaxTarEntry(type, path) { Mode = (UnixFileMode)0x1A4 };
                entry.DataStream = new MemoryStream(content);
                writer.WriteEntry(entry);
            }
        }
        return output.ToArray();
    }

    private static byte[] CreateCorruptTarArchive()
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var invalidTar = Encoding.UTF8.GetBytes("this is not a tar archive");
            gzip.Write(invalidTar);
        }
        return output.ToArray();
    }

    private sealed class GitHubHandler(byte[] archive) : HttpMessageHandler
    {
        public List<string?> UserAgents { get; } = [];
        public int CommitRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            UserAgents.Add(request.Headers.UserAgent.ToString());
            var response = request.RequestUri!.AbsolutePath.Contains("/commits/", StringComparison.Ordinal)
                ? CommitResponse()
                : ArchiveResponse();
            return Task.FromResult(response);
        }

        private HttpResponseMessage CommitResponse()
        {
            CommitRequestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"sha\":\"{Commit}\"}}", Encoding.UTF8, "application/json"),
            };
        }

        private HttpResponseMessage ArchiveResponse() => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive),
        };
    }

    private sealed class UnsafeRedirectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/commits/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"sha\":\"{Commit}\"}}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://example.test/archive.tar.gz") },
            });
        }
    }

    private sealed class StreamingMetadataHandler(string json) : HttpMessageHandler
    {
        public StreamingJsonContent Content { get; } = new(json);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Content,
            });
    }

    private sealed class StreamingJsonContent(string json) : HttpContent
    {
        private readonly byte[] _content = Encoding.UTF8.GetBytes(json);

        public bool WasSerialized { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasSerialized = true;
            throw new InvalidOperationException("The HTTP client tried to buffer the complete metadata response.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new MemoryStream(_content));

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => Task.FromResult<Stream>(new MemoryStream(_content));
    }

    private sealed class RejectScanner : ISkillContentScanner
    {
        public Task<Netclaw.Security.Skills.SkillScanResult> ScanAsync(
            string skillName,
            string content,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Netclaw.Security.Skills.SkillScanResult.Reject("fixture rejection"));
    }

    private sealed class FailedScanner : ISkillContentScanner
    {
        public Task<Netclaw.Security.Skills.SkillScanResult> ScanAsync(
            string skillName,
            string content,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Netclaw.Security.Skills.SkillScanResult.Fail("scanner unavailable"));
    }

    private sealed class ResourceRejectScanner : ISkillContentScanner
    {
        public Task<Netclaw.Security.Skills.SkillScanResult> ScanAsync(
            string skillName,
            string content,
            CancellationToken cancellationToken = default)
            => Task.FromResult(skillName.Contains(':', StringComparison.Ordinal)
                ? Netclaw.Security.Skills.SkillScanResult.Reject("fixture resource rejection")
                : Netclaw.Security.Skills.SkillScanResult.Allow());
    }

    private sealed class ThrowingScanner : ISkillContentScanner
    {
        public Task<Netclaw.Security.Skills.SkillScanResult> ScanAsync(
            string skillName,
            string content,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("fixture scanner error");
    }
}
