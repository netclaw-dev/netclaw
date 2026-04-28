// -----------------------------------------------------------------------
// <copyright file="ChannelAttachmentPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class ChannelAttachmentPolicyTests
{
    [Fact]
    public void Empty_policy_allows_nothing_and_has_zero_caps()
    {
        var empty = ChannelAttachmentPolicy.Empty;

        Assert.Empty(empty.AllowedCategories);
        Assert.Equal(0, empty.MaxFileBytes);
        Assert.Equal(0, empty.MaxFilesPerMessage);
        Assert.False(empty.Allows(AttachmentCategory.Image));
        Assert.False(empty.Allows(AttachmentCategory.Pdf));
        Assert.False(empty.Allows(AttachmentCategory.Document));
    }

    [Fact]
    public void Default_public_profile_allows_only_images()
    {
        var profile = ToolAudienceProfileDefaults.CreatePublic();
        var policy = profile.ChannelAttachments;

        Assert.True(policy.Allows(AttachmentCategory.Image));
        Assert.False(policy.Allows(AttachmentCategory.Pdf));
        Assert.False(policy.Allows(AttachmentCategory.Document));
        Assert.False(policy.Allows(AttachmentCategory.Archive));
        Assert.False(policy.Allows(AttachmentCategory.Media));
        Assert.False(policy.Allows(AttachmentCategory.Other));
        Assert.Equal(ChannelAttachmentPolicy.DefaultMaxFileBytes, policy.MaxFileBytes);
        Assert.Equal(ChannelAttachmentPolicy.DefaultMaxFilesPerMessage, policy.MaxFilesPerMessage);
    }

    [Fact]
    public void Default_team_profile_allows_everything_except_other()
    {
        var profile = ToolAudienceProfileDefaults.CreateTeam();
        var policy = profile.ChannelAttachments;

        Assert.True(policy.Allows(AttachmentCategory.Image));
        Assert.True(policy.Allows(AttachmentCategory.Pdf));
        Assert.True(policy.Allows(AttachmentCategory.Document));
        Assert.True(policy.Allows(AttachmentCategory.Archive));
        Assert.True(policy.Allows(AttachmentCategory.Media));
        Assert.False(policy.Allows(AttachmentCategory.Other));
    }

    [Fact]
    public void Default_personal_profile_allows_every_category_including_other()
    {
        var profile = ToolAudienceProfileDefaults.CreatePersonal();
        var policy = profile.ChannelAttachments;

        Assert.True(policy.Allows(AttachmentCategory.Image));
        Assert.True(policy.Allows(AttachmentCategory.Pdf));
        Assert.True(policy.Allows(AttachmentCategory.Document));
        Assert.True(policy.Allows(AttachmentCategory.Archive));
        Assert.True(policy.Allows(AttachmentCategory.Media));
        Assert.True(policy.Allows(AttachmentCategory.Other));
    }

    [Fact]
    public void Default_caps_are_twentyfive_mib_and_ten_files()
    {
        Assert.Equal(25L * 1024 * 1024, ChannelAttachmentPolicy.DefaultMaxFileBytes);
        Assert.Equal(10, ChannelAttachmentPolicy.DefaultMaxFilesPerMessage);
    }

    [Fact]
    public void Validation_passes_on_default_profiles()
    {
        var profiles = ToolAudienceProfileDefaults.CreateProfiles();

        var errors = profiles.ValidateChannelAttachments();

        Assert.Empty(errors);
    }

    [Fact]
    public void Validation_rejects_allowed_category_with_zero_size_cap()
    {
        var profiles = ToolAudienceProfileDefaults.CreateProfiles();
        profiles.Team.ChannelAttachments.MaxFileBytes = 0;

        var errors = profiles.ValidateChannelAttachments();

        Assert.Contains(errors, e => e.Contains("Team", System.StringComparison.Ordinal)
                                      && e.Contains("MaxFileBytes", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validation_rejects_allowed_category_with_zero_file_count_cap()
    {
        var profiles = ToolAudienceProfileDefaults.CreateProfiles();
        profiles.Personal.ChannelAttachments.MaxFilesPerMessage = 0;

        var errors = profiles.ValidateChannelAttachments();

        Assert.Contains(errors, e => e.Contains("Personal", System.StringComparison.Ordinal)
                                      && e.Contains("MaxFilesPerMessage", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validation_accepts_empty_allowlist_with_zero_caps()
    {
        var profiles = ToolAudienceProfileDefaults.CreateProfiles();
        profiles.Public.ChannelAttachments = new ChannelAttachmentPolicy
        {
            AllowedCategories = [],
            MaxFileBytes = 0,
            MaxFilesPerMessage = 0
        };

        var errors = profiles.ValidateChannelAttachments();

        Assert.Empty(errors);
    }
}

public sealed class AttachmentCategoriesTests
{
    [Theory]
    [InlineData("image/png", AttachmentCategory.Image)]
    [InlineData("image/jpeg", AttachmentCategory.Image)]
    [InlineData("IMAGE/WEBP", AttachmentCategory.Image)]
    [InlineData("image/gif;charset=binary", AttachmentCategory.Image)]
    [InlineData("application/pdf", AttachmentCategory.Pdf)]
    [InlineData("APPLICATION/PDF", AttachmentCategory.Pdf)]
    [InlineData("application/msword", AttachmentCategory.Document)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", AttachmentCategory.Document)]
    [InlineData("text/plain", AttachmentCategory.Document)]
    [InlineData("text/markdown", AttachmentCategory.Document)]
    [InlineData("application/json", AttachmentCategory.Document)]
    [InlineData("application/zip", AttachmentCategory.Archive)]
    [InlineData("application/x-tar", AttachmentCategory.Other)]
    [InlineData("application/gzip", AttachmentCategory.Archive)]
    [InlineData("application/x-7z-compressed", AttachmentCategory.Archive)]
    [InlineData("video/mp4", AttachmentCategory.Media)]
    [InlineData("audio/mpeg", AttachmentCategory.Media)]
    [InlineData("application/octet-stream", AttachmentCategory.Other)]
    [InlineData("wibble/wobble", AttachmentCategory.Other)]
    public void FromMime_classifies_known_types(string mime, AttachmentCategory expected)
    {
        Assert.Equal(expected, AttachmentCategories.FromMime(mime));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void FromMime_returns_other_for_missing_input(string? mime)
    {
        Assert.Equal(AttachmentCategory.Other, AttachmentCategories.FromMime(mime));
    }
}
