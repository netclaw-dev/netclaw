// -----------------------------------------------------------------------
// <copyright file="MattermostAttachmentUrlTrustTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Mattermost;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class MattermostAttachmentUrlTrustTests
{
    [Fact]
    public void Allows_url_matching_server_url()
    {
        Assert.True(MattermostAttachmentUrlTrust.IsAllowedAttachmentUrl(
            "https://mm.example.com/api/v4/files/abc123",
            "https://mm.example.com"));
    }

    [Fact]
    public void Rejects_url_from_different_domain()
    {
        Assert.False(MattermostAttachmentUrlTrust.IsAllowedAttachmentUrl(
            "https://evil.com/api/v4/files/abc123",
            "https://mm.example.com"));
    }

    [Fact]
    public void Rejects_subdomain_bypass()
    {
        Assert.False(MattermostAttachmentUrlTrust.IsAllowedAttachmentUrl(
            "https://mm.example.com.evil.com/api/v4/files/abc123",
            "https://mm.example.com"));
    }

    [Fact]
    public void Handles_trailing_slash_on_server_url()
    {
        Assert.True(MattermostAttachmentUrlTrust.IsAllowedAttachmentUrl(
            "https://mm.example.com/api/v4/files/abc123",
            "https://mm.example.com/"));
    }

    [Fact]
    public void Case_insensitive_comparison()
    {
        Assert.True(MattermostAttachmentUrlTrust.IsAllowedAttachmentUrl(
            "HTTPS://MM.EXAMPLE.COM/api/v4/files/abc123",
            "https://mm.example.com"));
    }
}
