// -----------------------------------------------------------------------
// <copyright file="GitSkillPluginSourceValidatorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class GitSkillPluginSourceValidatorTests
{
    [Theory]
    [InlineData("Aaronontheweb/dotnet-skills", "Aaronontheweb/dotnet-skills")]
    [InlineData("https://github.com/Aaronontheweb/dotnet-skills", "Aaronontheweb/dotnet-skills")]
    [InlineData("https://github.com/Aaronontheweb/dotnet-skills.git", "Aaronontheweb/dotnet-skills")]
    public void Repository_normalization_accepts_public_GitHub_forms(string value, string expected)
    {
        Assert.True(GitSkillPluginSourceValidator.TryNormalizeRepository(value, out var actual, out _));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("https://user@github.com/owner/repo")]
    [InlineData("https://github.com:8443/owner/repo")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://github.com/owner/repo?token=secret")]
    [InlineData("https://github.com/owner/repo#readme")]
    [InlineData("--upload-pack=bad")]
    public void Repository_normalization_rejects_unsafe_transport_forms(string value)
    {
        Assert.False(GitSkillPluginSourceValidator.TryNormalizeRepository(value, out _, out _));
    }

    [Fact]
    public void Source_validation_requires_a_full_commit_identity()
    {
        var source = Source();
        source.ReferenceKind = GitSkillPluginReferenceKind.Commit;
        source.Reference = "abc123";

        Assert.False(GitSkillPluginSourceValidator.TryValidateSource(source, out var error));
        Assert.Contains("full 40-character", error);
    }

    [Fact]
    public void Fingerprint_changes_for_each_source_semantic()
    {
        var source = Source();
        var original = GitSkillPluginSourceValidator.Fingerprint(source);

        source.Subdirectory = "plugin";

        Assert.NotEqual(original, GitSkillPluginSourceValidator.Fingerprint(source));
    }

    [Fact]
    public void Collection_validation_rejects_duplicate_names()
    {
        var first = Source();
        var second = Source();
        second.Repository = "owner/other";

        Assert.False(GitSkillPluginSourceValidator.TryValidateSources([first, second], out var error));
        Assert.Contains("occurs more than once", error);
    }

    [Fact]
    public void Collection_validation_rejects_more_than_twenty_sources()
    {
        var sources = Enumerable.Range(0, 21).Select(index =>
        {
            var source = Source();
            source.Name = $"source-{index}";
            return source;
        }).ToArray();

        Assert.False(GitSkillPluginSourceValidator.TryValidateSources(sources, out var error));
        Assert.Contains("No more than 20", error);
    }

    [Theory]
    [InlineData("./plugin/")]
    [InlineData("/plugin")]
    [InlineData("plugin/../other")]
    [InlineData("plugin/file:stream")]
    [InlineData("plugin/CON.md")]
    [InlineData("plugin/CON .md")]
    [InlineData("plugin/COM¹.md")]
    [InlineData("plugin/LPT³.md")]
    [InlineData("plugin/trailing.")]
    [InlineData("plugin/trailing ")]
    public void Source_validation_rejects_a_noncanonical_subdirectory(string subdirectory)
    {
        var source = Source();
        source.Subdirectory = subdirectory;

        Assert.False(GitSkillPluginSourceValidator.TryValidateSource(source, out _));
    }

    [Theory]
    [InlineData("owner/.")]
    [InlineData("owner/..")]
    [InlineData("./repository")]
    public void Repository_normalization_rejects_dot_components(string repository)
    {
        Assert.False(GitSkillPluginSourceValidator.TryNormalizeRepository(repository, out _, out _));
    }

    [Fact]
    public void Source_validation_rejects_an_unknown_reference_type()
    {
        var source = Source();
        source.ReferenceKind = (GitSkillPluginReferenceKind)99;

        Assert.False(GitSkillPluginSourceValidator.TryValidateSource(source, out _));
    }

    private static GitSkillPluginSource Source() => new()
    {
        Name = "dotnet-skills",
        Repository = "Aaronontheweb/dotnet-skills",
        Format = "codex",
        ReferenceKind = GitSkillPluginReferenceKind.Branch,
        Reference = "main",
    };
}
