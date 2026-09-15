// -----------------------------------------------------------------------
// <copyright file="TeamsChannelFoundationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using Akka.Actor;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Identity.Abstractions;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Apps.Schema.Entities;
using Microsoft.Teams.Core.Schema;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Security;
using Xunit;
using TeamsAccount = Microsoft.Teams.Apps.Schema.TeamsChannelAccount;
using TeamsAttachment = Microsoft.Teams.Apps.Schema.TeamsAttachment;
using TeamsChannel = Microsoft.Teams.Apps.Schema.TeamsChannel;
using TeamsChannelData = Microsoft.Teams.Apps.Schema.TeamsChannelData;
using TeamsConversation = Microsoft.Teams.Apps.Schema.TeamsConversation;
using TeamsConversationType = Microsoft.Teams.Apps.Schema.ConversationType;
using TeamsTeam = Microsoft.Teams.Apps.Schema.Team;

namespace Netclaw.Daemon.Tests.Configuration;

[Collection("TeamsTelemetry")]
public sealed class TeamsChannelFoundationTests
{
    private static class TeamsContentType
    {
        public static readonly AttachmentContentType Html = new("text/html");
        public static readonly AttachmentContentType Text = new("text/plain");
    }

    [Fact]
    public void Options_default_to_disabled_and_fail_closed()
    {
        var options = new TeamsChannelOptions();

        Assert.False(options.Enabled);
        Assert.False(options.AllowDirectMessages);
        Assert.False(options.AllowGroupChats);
        Assert.True(options.MentionOnly);
        Assert.Equal(TeamsAuthenticationMode.ClientSecret, options.AuthenticationMode);
        Assert.Empty(options.AllowedTeamIds);
        Assert.Empty(options.AllowedChannelIds);
        Assert.Empty(options.AllowedGroupChatIds);
        Assert.Empty(options.AllowedUserIds);
        Assert.Empty(options.ChannelAudiences);
        Assert.Empty(options.ChannelAudienceOverrides);
    }

    [Fact]
    public void Structured_channel_audience_override_binds_delimiter_bearing_canonical_ids()
    {
        const string json = """
            {
              "Teams": {
                "ChannelAudienceOverrides": [
                  {
                    "TeamId": "19:team-id@thread.tacv2",
                    "ChannelId": "19:channel-id@thread.tacv2",
                    "Audience": "team"
                  }
                ]
              }
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        var options = configuration.GetSection("Teams").Get<TeamsChannelOptions>();

        Assert.NotNull(options);
        var audienceOverride = Assert.Single(options.ChannelAudienceOverrides);
        Assert.Equal("19:team-id@thread.tacv2", audienceOverride.TeamId);
        Assert.Equal("19:channel-id@thread.tacv2", audienceOverride.ChannelId);
        Assert.Equal("team", audienceOverride.Audience);
    }

    [Fact]
    public void Secret_overlay_binds_effective_secret_without_serializing_it()
    {
        const string secret = "teams-pr1-synthetic-sentinel";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Teams:Enabled"] = "true",
                ["Teams:TenantId"] = "tenant",
                ["Teams:ClientId"] = "client",
                ["Teams:ClientSecret"] = secret,
            })
            .Build();

        var options = configuration.GetSection("Teams").Get<TeamsChannelOptions>();

        Assert.NotNull(options);
        Assert.Equal(secret, options.ClientSecret!.Value);
        var serialized = JsonSerializer.Serialize(options);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(TeamsChannelOptions.ClientSecret), serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Environment_secret_overlay_binds_the_teams_client_secret()
    {
        const string variableName = "NETCLAW_Teams__ClientSecret";
        const string secret = "teams-pr1-environment-sentinel";
        var previousValue = Environment.GetEnvironmentVariable(variableName);
        Environment.SetEnvironmentVariable(variableName, secret);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables("NETCLAW_")
                .Build();

            var options = configuration.GetSection("Teams").Get<TeamsChannelOptions>();

            Assert.NotNull(options);
            Assert.Equal(secret, options.ClientSecret!.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previousValue);
        }
    }

    [Fact]
    public async Task Disabled_teams_is_descriptor_visible_without_runtime_side_effects()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().Build();

        services.AddChannelIntegrations(configuration);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IChannel));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IChannelRegistry>();
        var descriptor = registry.GetChannel(ChannelDescriptorKey.FromChannelType(ChannelType.Teams));
        var snapshot = await registry.GetSnapshotAsync(descriptor.Key, TestContext.Current.CancellationToken);

        Assert.False(descriptor.IsEnabled);
        Assert.Equal("Teams", descriptor.DisplayName);
        Assert.False(snapshot.IsEnabled);
        Assert.Contains("disabled", snapshot.HealthDetail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enabled_teams_has_no_transport_registration_or_secret_diagnostic_disclosure()
    {
        const string secret = "teams-pr1-synthetic-sentinel";
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Teams:Enabled"] = "true",
                ["Teams:TenantId"] = "tenant",
                ["Teams:ClientId"] = "client",
                ["Teams:ClientSecret"] = secret,
            })
            .Build();

        services.AddChannelIntegrations(configuration);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IChannel));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IChannelRegistry>();
        var snapshot = await registry.GetSnapshotAsync(ChannelDescriptorKey.FromChannelType(ChannelType.Teams), TestContext.Current.CancellationToken);

        Assert.True(snapshot.IsEnabled);
        Assert.DoesNotContain(secret, snapshot.HealthDetail!, StringComparison.Ordinal);
    }

    [Fact]
    public void Channel_type_teams_uses_a_stable_wire_value_without_changing_existing_values()
    {
        Assert.Equal(0, (int)ChannelType.Slack);
        Assert.Equal(1, (int)ChannelType.Tui);
        Assert.Equal(2, (int)ChannelType.Headless);
        Assert.Equal(3, (int)ChannelType.SignalR);
        Assert.Equal(4, (int)ChannelType.Reminder);
        Assert.Equal(5, (int)ChannelType.Webhook);
        Assert.Equal(6, (int)ChannelType.Discord);
        Assert.Equal(7, (int)ChannelType.Mattermost);
        Assert.Equal(8, (int)ChannelType.Teams);
        Assert.Equal("teams", ChannelType.Teams.ToWireValue());
        Assert.True(ChannelTypeExtensions.TryFromWireValue("teams", out var teams));
        Assert.Equal(ChannelType.Teams, teams);
        Assert.False(ChannelTypeExtensions.TryFromWireValue("not-a-channel", out _));
        Assert.True(ChannelType.Teams.SupportsInteractiveApproval());
        Assert.Equal("slack", ChannelType.Slack.ToWireValue());
        Assert.Equal("discord", ChannelType.Discord.ToWireValue());
        Assert.Equal("mattermost", ChannelType.Mattermost.ToWireValue());
    }

    [Fact]
    public void Personal_identifier_round_trips_canonically()
    {
        Assert.True(TeamsSessionIdentifierCodec.TryCreatePersonal("tenant/α", "conversation/β", out var sessionId, out var createError));
        Assert.Equal(TeamsIdentifierValidationError.None, createError);
        Assert.True(TeamsSessionIdentifierCodec.TryParse(sessionId, out var parsed, out var parseError));

        Assert.Equal(TeamsIdentifierValidationError.None, parseError);
        Assert.Equal("tenant/α", parsed.TenantId);
        Assert.Equal(TeamsConversationScope.Personal, parsed.Scope);
        Assert.Equal("conversation/β", parsed.ConversationId);
        Assert.Equal("conversation", parsed.ThreadKey);
        Assert.Null(parsed.RootActivityId);
    }

    [Fact]
    public void Channel_identifier_separates_tenants_and_roots()
    {
        Assert.True(TeamsSessionIdentifierCodec.TryCreateChannel("tenant-a", "conversation", "root-a", out var first, out _));
        Assert.True(TeamsSessionIdentifierCodec.TryCreateChannel("tenant-a", "conversation", "root-b", out var second, out _));
        Assert.True(TeamsSessionIdentifierCodec.TryCreateChannel("tenant-b", "conversation", "root-a", out var third, out _));
        Assert.True(TeamsSessionIdentifierCodec.TryCreateChannel("tenant-a", "conversation", "root-a", out var repeat, out _));

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, third);
        Assert.Equal(first, repeat);
    }

    [Fact]
    public void Group_chat_identifier_round_trips_as_a_flat_conversation()
    {
        Assert.True(TeamsSessionIdentifierCodec.TryCreateGroupChat(
            "tenant-a",
            "19:group-chat@thread.v2",
            out var sessionId,
            out var createError));
        Assert.Equal(TeamsIdentifierValidationError.None, createError);
        Assert.True(TeamsSessionIdentifierCodec.TryParse(sessionId, out var parsed, out var parseError));

        Assert.Equal(TeamsIdentifierValidationError.None, parseError);
        Assert.Equal(TeamsConversationScope.GroupChat, parsed.Scope);
        Assert.Equal("19:group-chat@thread.v2", parsed.ConversationId);
        Assert.Equal("conversation", parsed.ThreadKey);
        Assert.Null(parsed.RootActivityId);
    }

    [Fact]
    public void Group_chat_identifier_rejects_an_oversized_conversation()
    {
        Assert.False(TeamsSessionIdentifierCodec.TryCreateGroupChat(
            "tenant-a",
            new string('g', TeamsSessionIdentifierCodec.MaxRawIdentifierBytes + 1),
            out _,
            out var error));

        Assert.Equal(TeamsIdentifierValidationError.OversizedIdentifier, error);
    }

    [Fact]
    public void Group_chat_identifier_rejects_a_noncanonical_conversation()
    {
        Assert.False(TeamsSessionIdentifierCodec.TryCreateGroupChat(
            "tenant-a",
            "group-chat-name",
            out _,
            out var error));

        Assert.Equal(TeamsIdentifierValidationError.InvalidSessionId, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Identifier_rejects_blank_tenant(string tenant)
    {
        Assert.False(TeamsSessionIdentifierCodec.TryCreatePersonal(tenant, "conversation", out _, out var error));
        Assert.Equal(TeamsIdentifierValidationError.MissingTenantId, error);
    }

    [Fact]
    public void Channel_identifier_rejects_missing_root()
    {
        Assert.False(TeamsSessionIdentifierCodec.TryCreateChannel("tenant", "conversation", null, out _, out var error));
        Assert.Equal(TeamsIdentifierValidationError.MissingActivityId, error);
    }

    [Fact]
    public void Identifier_parser_rejects_padded_noncanonical_invalid_and_oversized_components()
    {
        var conversation = Convert.ToBase64String(Encoding.UTF8.GetBytes("conversation")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var padded = new Netclaw.Actors.Protocol.SessionId($"teams~dGVuYW50=~personal~{conversation}/conversation");
        var nonCanonical = new Netclaw.Actors.Protocol.SessionId($"teams~Zh~personal~{conversation}/conversation");
        var invalid = new Netclaw.Actors.Protocol.SessionId($"teams~tenant!~personal~{conversation}/conversation");
        var oversizedTenant = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('a', TeamsSessionIdentifierCodec.MaxRawIdentifierBytes + 1))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var oversized = new Netclaw.Actors.Protocol.SessionId($"teams~{oversizedTenant}~personal~{conversation}/conversation");

        Assert.False(TeamsSessionIdentifierCodec.TryParse(padded, out _, out _));
        Assert.False(TeamsSessionIdentifierCodec.TryParse(nonCanonical, out _, out _));
        Assert.False(TeamsSessionIdentifierCodec.TryParse(invalid, out _, out _));
        Assert.False(TeamsSessionIdentifierCodec.TryParse(oversized, out _, out var oversizedError));
        Assert.Equal(TeamsIdentifierValidationError.OversizedIdentifier, oversizedError);
    }

    [Fact]
    public void Identifier_rejects_oversized_raw_values_without_truncation()
    {
        var oversized = new string('a', TeamsSessionIdentifierCodec.MaxRawIdentifierBytes + 1);

        Assert.False(TeamsSessionIdentifierCodec.TryCreatePersonal(oversized, "conversation", out _, out var error));
        Assert.Equal(TeamsIdentifierValidationError.OversizedIdentifier, error);
    }

    [Theory]
    [InlineData(1023, true)]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public void Identifier_uses_utf8_byte_limits_for_ascii_components(int byteCount, bool shouldSucceed)
    {
        var value = new string('a', byteCount);

        var result = TeamsSessionIdentifierCodec.TryCreatePersonal(value, "conversation", out _, out var error);

        Assert.Equal(shouldSucceed, result);
        Assert.Equal(
            shouldSucceed ? TeamsIdentifierValidationError.None : TeamsIdentifierValidationError.OversizedIdentifier,
            error);
    }

    [Theory]
    [InlineData(511, true)]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public void Identifier_uses_utf8_byte_limits_for_multibyte_components(int characterCount, bool shouldSucceed)
    {
        var value = new string('β', characterCount);

        var result = TeamsSessionIdentifierCodec.TryCreatePersonal(value, "conversation", out _, out var error);

        Assert.Equal(shouldSucceed, result);
        Assert.Equal(
            shouldSucceed ? TeamsIdentifierValidationError.None : TeamsIdentifierValidationError.OversizedIdentifier,
            error);
    }

    [Fact]
    public void Identifier_parser_enforces_encoded_component_boundaries_and_canonical_base64url()
    {
        var conversation = EncodeForSession("conversation");
        var encoded1024Bytes = EncodeForSession(new string('a', TeamsSessionIdentifierCodec.MaxRawIdentifierBytes));
        var encoded1365Characters = new string('A', TeamsSessionIdentifierCodec.MaxEncodedIdentifierLength - 1);
        var encoded1367Characters = new string('A', TeamsSessionIdentifierCodec.MaxEncodedIdentifierLength + 1);
        var invalidLengthModuloFour = "A";

        Assert.Equal(TeamsSessionIdentifierCodec.MaxEncodedIdentifierLength, encoded1024Bytes.Length);
        Assert.False(TeamsSessionIdentifierCodec.TryParse(
            new Netclaw.Actors.Protocol.SessionId($"teams~{encoded1365Characters}~personal~{conversation}/conversation"),
            out _, out _));
        Assert.True(TeamsSessionIdentifierCodec.TryParse(
            new Netclaw.Actors.Protocol.SessionId($"teams~{encoded1024Bytes}~personal~{conversation}/conversation"),
            out _, out var acceptedError));
        Assert.Equal(TeamsIdentifierValidationError.None, acceptedError);
        Assert.False(TeamsSessionIdentifierCodec.TryParse(
            new Netclaw.Actors.Protocol.SessionId($"teams~{encoded1367Characters}~personal~{conversation}/conversation"),
            out _, out var oversizedError));
        Assert.Equal(TeamsIdentifierValidationError.OversizedIdentifier, oversizedError);
        Assert.False(TeamsSessionIdentifierCodec.TryParse(
            new Netclaw.Actors.Protocol.SessionId($"teams~{invalidLengthModuloFour}~personal~{conversation}/conversation"),
            out _, out _));
    }

    [Fact]
    public void Identifier_parser_rejects_extra_separators_and_invalid_thread_forms()
    {
        var tenant = EncodeForSession("tenant");
        var conversation = EncodeForSession("conversation");
        var root = EncodeForSession("root");

        Assert.False(TeamsSessionIdentifierCodec.TryParse(
            new Netclaw.Actors.Protocol.SessionId($"teams~{tenant}~personal~{conversation}/conversation/extra"),
            out _, out _));
        Assert.False(TeamsSessionIdentifierCodec.TryParse(
            new Netclaw.Actors.Protocol.SessionId($"teams~{tenant}~personal~{conversation}/other"),
            out _, out _));
        Assert.False(TeamsSessionIdentifierCodec.TryParse(
            new Netclaw.Actors.Protocol.SessionId($"teams~{tenant}~channel~{conversation}/"),
            out _, out _));
        Assert.True(TeamsSessionIdentifierCodec.TryParse(
            new Netclaw.Actors.Protocol.SessionId($"teams~{tenant}~channel~{conversation}/{root}"),
            out var parsed,
            out _));
        Assert.Equal("root", parsed.RootActivityId);
    }

    [Fact]
    public void Contracts_reject_missing_or_invalid_trust_and_destination_data()
    {
        Assert.Throws<ArgumentException>(() => new TeamsIngressTrustContext(
            TrustAudience.Team,
            PrincipalClassification.TrustedInternal,
            TrustBoundary.Team,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            "sender",
            "tenant",
            "conversation",
            TeamsConversationScope.Personal,
            "activity",
            default));
        Assert.Throws<ArgumentException>(() => new TeamsOutboundDestination(
            "tenant",
            " ",
            TeamsConversationScope.Personal,
            "https://service.invalid/"));
        Assert.Throws<ArgumentException>(() => new TeamsOutboundDestination(
            "tenant",
            "conversation",
            TeamsConversationScope.Channel,
            "https://service.invalid/"));
        Assert.Throws<ArgumentException>(() => new TeamsOutboundDestination(
            "tenant",
            "conversation",
            TeamsConversationScope.Personal,
            "https://service.invalid/"));
        Assert.Throws<ArgumentException>(() => new TeamsOutboundDestination(
            "tenant",
            "conversation",
            TeamsConversationScope.Personal,
            "http://service.invalid/",
            userId: "user"));
        Assert.Throws<ArgumentException>(() => new TeamsOutboundDestination(
            "tenant",
            "conversation",
            TeamsConversationScope.Personal,
            "https://service.invalid/" + new string('x', TeamsOutboundDestination.MaxServiceUrlLength),
            userId: "user"));
        Assert.Throws<ArgumentException>(() => new TeamsOutboundDestination(
            "tenant",
            "conversation",
            TeamsConversationScope.Channel,
            "https://service.invalid/",
            "root",
            "team"));
        Assert.Throws<ArgumentException>(() => new TeamsOutboundDestination(
            "tenant",
            "conversation",
            TeamsConversationScope.GroupChat,
            "https://service.invalid/",
            rootActivityId: "root"));
        Assert.Throws<ArgumentException>(() => new TeamsOutboundDestination(
            "tenant",
            "conversation",
            TeamsConversationScope.GroupChat,
            "https://service.invalid/",
            userId: "user"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TeamsAttachmentMetadata("file.txt", null, -1));
    }

    [Fact]
    public void Group_chat_destination_requires_only_its_flat_routing_values()
    {
        var destination = new TeamsOutboundDestination(
            "tenant",
            "19:group-chat@thread.v2",
            TeamsConversationScope.GroupChat,
            "https://service.invalid/");

        Assert.Equal(TeamsConversationScope.GroupChat, destination.Scope);
        Assert.Null(destination.RootActivityId);
        Assert.Null(destination.TeamId);
        Assert.Null(destination.ChannelId);
        Assert.Null(destination.UserId);
    }

    [Fact]
    public void Channel_output_message_requires_its_canonical_root_and_bounded_activity_ids()
    {
        var destination = new TeamsOutboundDestination(
            "tenant",
            "conversation",
            TeamsConversationScope.Channel,
            "https://service.invalid/",
            "root",
            "team",
            "channel");

        Assert.Throws<ArgumentException>(() => new TeamsOutboundMessage(
            destination,
            "reply",
            "idempotency",
            "correlation"));
        Assert.Throws<ArgumentException>(() => new TeamsOutboundMessage(
            destination,
            "reply",
            "idempotency",
            "correlation",
            "other-root"));
        Assert.Throws<ArgumentException>(() => new TeamsOutboundMessage(
            destination,
            "reply",
            "idempotency",
            "correlation",
            "root",
            new string('x', TeamsSessionIdentifierCodec.MaxRawIdentifierBytes + 1)));

        var message = new TeamsOutboundMessage(destination, "reply", "idempotency", "correlation", "root");
        Assert.Equal("root", message.ReplyToActivityId);
    }

    [Fact]
    public void Output_renderer_normalizes_empty_text_and_preserves_unicode_chunks()
    {
        var renderer = new TeamsOutputRenderer();

        Assert.Empty(renderer.Render(" \r\n ").Chunks);
        var output = renderer.Render("one\r\ntwo \U0001f469\u200d\U0001f4bb");

        var chunk = Assert.Single(output.Chunks);
        Assert.Equal("one\ntwo \U0001f469\u200d\U0001f4bb", chunk);
        Assert.False(output.IsRejectedTooLarge);
    }

    [Fact]
    public void Output_renderer_rejects_content_that_exceeds_the_bounded_chunk_budget()
    {
        var renderer = new TeamsOutputRenderer();
        var output = renderer.Render(new string('x', TeamsOutputRenderer.MaxSerializedPayloadBytes * (TeamsOutputRenderer.MaxChunkCount + 1)));

        Assert.True(output.IsRejectedTooLarge);
        Assert.Empty(output.Chunks);
    }

    [Fact]
    public void Output_renderer_accepts_the_exact_payload_boundary_and_preserves_one_byte_overflow()
    {
        var renderer = new TeamsOutputRenderer();
        var envelopeBytes = TeamsOutputRenderer.GetSerializedPayloadBytes(string.Empty);
        var exactText = new string('x', TeamsOutputRenderer.MaxSerializedPayloadBytes - envelopeBytes);

        var exact = renderer.Render(exactText);
        var overflow = renderer.Render(exactText + "y");

        Assert.Equal(TeamsOutputRenderer.MaxSerializedPayloadBytes, TeamsOutputRenderer.GetSerializedPayloadBytes(exactText));
        Assert.Equal(exactText, Assert.Single(exact.Chunks));
        Assert.False(exact.IsRejectedTooLarge);
        Assert.Equal(exactText + "y", string.Concat(overflow.Chunks));
        Assert.All(overflow.Chunks, chunk =>
            Assert.InRange(TeamsOutputRenderer.GetSerializedPayloadBytes(chunk), 1, TeamsOutputRenderer.MaxSerializedPayloadBytes));
    }

    [Fact]
    public void Output_renderer_counts_the_channel_root_reply_metadata()
    {
        var renderer = new TeamsOutputRenderer();
        var rootActivityId = new string('r', TeamsSessionIdentifierCodec.MaxRawIdentifierBytes);
        var envelopeBytes = TeamsOutputRenderer.GetSerializedPayloadBytes(string.Empty, rootActivityId);
        var exactText = new string('x', TeamsOutputRenderer.MaxSerializedPayloadBytes - envelopeBytes);

        var exact = renderer.Render(exactText, rootActivityId);
        var overflow = renderer.Render(exactText + "y", rootActivityId);

        Assert.Equal(TeamsOutputRenderer.MaxSerializedPayloadBytes, TeamsOutputRenderer.GetSerializedPayloadBytes(exactText, rootActivityId));
        Assert.Equal(exactText, Assert.Single(exact.Chunks));
        Assert.Equal(exactText + "y", string.Concat(overflow.Chunks));
        Assert.All(overflow.Chunks, chunk =>
            Assert.InRange(TeamsOutputRenderer.GetSerializedPayloadBytes(chunk, rootActivityId), 1, TeamsOutputRenderer.MaxSerializedPayloadBytes));
    }

    [Fact]
    public void Output_renderer_has_a_deterministic_multibyte_unicode_boundary()
    {
        var renderer = new TeamsOutputRenderer();
        const string emoji = "\U0001f9ea";
        var envelopeBytes = TeamsOutputRenderer.GetSerializedPayloadBytes(string.Empty);
        var emojiBytes = TeamsOutputRenderer.GetSerializedPayloadBytes(emoji) - envelopeBytes;
        var emojiCount = (TeamsOutputRenderer.MaxSerializedPayloadBytes - envelopeBytes) / emojiBytes;
        var remainingAsciiBytes = TeamsOutputRenderer.MaxSerializedPayloadBytes - envelopeBytes - (emojiCount * emojiBytes);
        var exactText = new string('x', remainingAsciiBytes) + string.Concat(Enumerable.Repeat(emoji, emojiCount));

        var exact = renderer.Render(exactText);
        var overflow = renderer.Render(exactText + emoji);

        Assert.Equal(TeamsOutputRenderer.MaxSerializedPayloadBytes, TeamsOutputRenderer.GetSerializedPayloadBytes(exactText));
        Assert.Equal(exactText, Assert.Single(exact.Chunks));
        Assert.Equal(exactText + emoji, string.Concat(overflow.Chunks));
        Assert.All(overflow.Chunks, chunk =>
            Assert.InRange(TeamsOutputRenderer.GetSerializedPayloadBytes(chunk), 1, TeamsOutputRenderer.MaxSerializedPayloadBytes));
    }

    [Fact]
    public void Output_renderer_does_not_split_a_zero_width_joiner_sequence()
    {
        var renderer = new TeamsOutputRenderer();
        const string woman = "\U0001f469";
        const string joinedEmoji = "\U0001f469\u200d\U0001f4bb";
        var prefix = new string('x', TeamsOutputRenderer.MaxSerializedPayloadBytes - TeamsOutputRenderer.GetSerializedPayloadBytes(woman));

        var output = renderer.Render(prefix + joinedEmoji);

        Assert.Equal([prefix, joinedEmoji], output.Chunks);
        Assert.Equal(prefix + joinedEmoji, string.Concat(output.Chunks));
    }

    [Fact]
    public void Output_renderer_preserves_markdown_unicode_and_line_boundaries_when_chunking()
    {
        var renderer = new TeamsOutputRenderer();
        var input = string.Join("\n", Enumerable.Repeat("[docs](https://example.invalid/path) \U0001f469\u200d\U0001f4bb", 4_000));

        var output = renderer.Render(input);

        Assert.False(output.IsRejectedTooLarge);
        Assert.InRange(output.Chunks.Count, 2, TeamsOutputRenderer.MaxChunkCount);
        Assert.Equal(input, string.Concat(output.Chunks));
        Assert.All(output.Chunks, chunk =>
            Assert.InRange(TeamsOutputRenderer.GetSerializedPayloadBytes(chunk), 1, TeamsOutputRenderer.MaxSerializedPayloadBytes));
    }

    [Fact]
    public void Output_renderer_rejects_an_oversized_markdown_link_instead_of_corrupting_it()
    {
        var renderer = new TeamsOutputRenderer();
        var input = "[label](https://example.invalid/" + new string('x', TeamsOutputRenderer.MaxSerializedPayloadBytes) + ")";

        var output = renderer.Render(input);

        Assert.True(output.IsRejectedTooLarge);
        Assert.Empty(output.Chunks);
    }

    [Fact]
    public void Teams_contract_assembly_has_no_microsoft_teams_dependency()
    {
        var assembly = typeof(TeamsInboundActivity).Assembly;

        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name => name.Name!.StartsWith("Microsoft.Teams", StringComparison.Ordinal));
        Assert.All(
            new[] { typeof(TeamsIngressTrustContext), typeof(TeamsInboundActivity), typeof(TeamsOutboundMessage) },
            type => Assert.DoesNotContain(type.GetProperties(), property => property.PropertyType.Namespace?.StartsWith("Microsoft.Teams", StringComparison.Ordinal) == true));
    }

    [Fact]
    public void Ingress_registration_requires_the_complete_client_secret_credential_set()
    {
        var missingSecret = TeamsIngressRegistration.Evaluate(new TeamsChannelOptions
        {
            Enabled = true,
            TenantId = "tenant",
            ClientId = "client"
        });
        var complete = TeamsIngressRegistration.Evaluate(new TeamsChannelOptions
        {
            Enabled = true,
            TenantId = "tenant",
            ClientId = "client",
            ClientSecret = new SensitiveString("synthetic-secret")
        });

        Assert.False(missingSecret.CanActivateSdk);
        Assert.Equal("missing_client_secret", missingSecret.ReasonCode);
        Assert.True(complete.CanActivateSdk);
    }

    [Fact]
    public async Task Teams_health_stays_degraded_and_not_ready_until_tenant_validation()
    {
        var disabled = await SnapshotAsync(new TeamsChannelOptions());
        var incomplete = await SnapshotAsync(new TeamsChannelOptions { Enabled = true, TenantId = "tenant" });
        var complete = await SnapshotAsync(new TeamsChannelOptions
        {
            Enabled = true,
            TenantId = "tenant",
            ClientId = "client",
            ClientSecret = new SensitiveString("secret")
        });

        Assert.All(new[] { disabled, incomplete, complete }, snapshot =>
        {
            Assert.Equal(ChannelHealthStatus.Degraded, snapshot.Health);
            Assert.False(snapshot.IsReady);
            Assert.False(snapshot.IsConnected);
        });
    }

    [Fact]
    public void Translator_accepts_complete_personal_and_group_chat_messages()
    {
        var translator = new TeamsSdkActivityTranslator(
            new TeamsChannelOptions { TenantId = "tenant" },
            new FakeTimeProvider());
        var activity = CreateSdkMessage();

        var accepted = translator.Translate(activity, "tenant");
        var missingTenant = translator.Translate(activity, null);
        activity.Conversation!.ConversationType = TeamsConversationType.GroupChat;
        var groupChat = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, accepted.Disposition);
        Assert.Equal("tenant", accepted.Activity!.Trust.TenantId);
        Assert.Equal(TeamsConversationScope.Personal, accepted.Activity.Trust.Scope);
        Assert.Equal(TeamsTranslationDisposition.RejectedPendingTenantEvidence, missingTenant.Disposition);
        Assert.Equal(TeamsTranslationDisposition.Accepted, groupChat.Disposition);
        Assert.Equal(TeamsConversationScope.GroupChat, groupChat.Activity!.Trust.Scope);
        Assert.Null(groupChat.Activity.Reply!.RootActivityId);
    }

    [Fact]
    public void Translator_recognizes_only_a_structured_bot_mention_in_group_chat()
    {
        var translator = new TeamsSdkActivityTranslator(
            new TeamsChannelOptions { TenantId = "tenant", BotId = "bot" },
            TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.GroupChat);
        activity.Text = "<at>Netclaw</at> review this";
        ((CoreActivity)activity).Recipient = new TeamsAccount { Id = "28:bot" };
        activity.Entities = [new MentionEntity
        {
            Type = "mention",
            Mentioned = new TeamsAccount { Id = "28:bot" },
            Text = "<at>Netclaw</at>"
        }];

        var mentioned = translator.Translate(activity, "tenant");
        activity.Text = "@Netclaw review this";
        activity.Entities = [];
        var literal = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, mentioned.Disposition);
        Assert.Equal(TeamsConversationScope.GroupChat, mentioned.Activity!.Trust.Scope);
        Assert.True(mentioned.Activity.IsMentioned);
        Assert.Equal(" review this", mentioned.Activity.Text);
        Assert.False(literal.Activity!.IsMentioned);
        Assert.Equal("@Netclaw review this", literal.Activity.Text);
    }

    [Theory]
    [InlineData(ApprovalOptionKeys.ApproveOnce)]
    [InlineData(ApprovalOptionKeys.ApproveSession)]
    [InlineData(ApprovalOptionKeys.ApproveAlways)]
    [InlineData(ApprovalOptionKeys.ApproveEverywhere)]
    [InlineData(ApprovalOptionKeys.Deny)]
    public void Translator_accepts_each_bounded_canonical_approval_key(string optionKey)
    {
        var translator = new TeamsSdkActivityTranslator(
            new TeamsChannelOptions { TenantId = "tenant" },
            new FakeTimeProvider());

        var result = translator.Translate(CreateSdkApprovalAction(optionKey), "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.NotNull(result.ApprovalAction);
        Assert.Equal(optionKey, result.ApprovalAction.Action);
    }

    [Fact]
    public void Translator_carries_a_safe_presenter_label_without_changing_canonical_sender_identity()
    {
        var translator = new TeamsSdkActivityTranslator(
            new TeamsChannelOptions { TenantId = "tenant" },
            new FakeTimeProvider());

        var result = translator.Translate(
            CreateSdkApprovalAction(
                ApprovalOptionKeys.ApproveOnce,
                senderId: "teams-transport-sender",
                aadObjectId: "operator-aad-object",
                displayName: " Ada Lovelace "),
            "tenant");

        var action = Assert.IsType<TeamsApprovalAction>(result.ApprovalAction);
        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal("operator-aad-object", action.Trust.SenderId);
        Assert.Equal("Ada Lovelace", action.OperatorDisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("operator-aad-object")]
    [InlineData("d73d8cc1-50d1-4ed6-9ebd-dce8ad6cfd69")]
    [InlineData("Ada\u202eLovelace")]
    public void Translator_discards_missing_or_unsafe_presenter_labels(string? displayName)
    {
        var translator = new TeamsSdkActivityTranslator(
            new TeamsChannelOptions { TenantId = "tenant" },
            new FakeTimeProvider());

        var result = translator.Translate(
            CreateSdkApprovalAction(
                ApprovalOptionKeys.Deny,
                senderId: "teams-transport-sender",
                aadObjectId: "operator-aad-object",
                displayName: displayName),
            "tenant");

        var action = Assert.IsType<TeamsApprovalAction>(result.ApprovalAction);
        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal("operator-aad-object", action.Trust.SenderId);
        Assert.Null(action.OperatorDisplayName);
    }

    [Fact]
    public void Translator_discards_a_long_presenter_label_that_matches_the_raw_sender_identifier()
    {
        var longSenderId = new string('s', TeamsApprovalAction.MaxOperatorDisplayNameLength + 40);
        var translator = new TeamsSdkActivityTranslator(
            new TeamsChannelOptions { TenantId = "tenant" },
            new FakeTimeProvider());

        var result = translator.Translate(
            CreateSdkApprovalAction(
                ApprovalOptionKeys.Deny,
                senderId: longSenderId,
                displayName: longSenderId),
            "tenant");

        var action = Assert.IsType<TeamsApprovalAction>(result.ApprovalAction);
        Assert.Equal(longSenderId, action.Trust.SenderId);
        Assert.Null(action.OperatorDisplayName);
    }

    [Fact]
    public void Translator_enforces_configured_and_activity_tenant_boundaries_without_identifier_disclosure()
    {
        var translator = new TeamsSdkActivityTranslator(
            new TeamsChannelOptions { TenantId = "configured-tenant" },
            new FakeTimeProvider());
        var activity = CreateSdkMessage();
        activity.Conversation!.TenantId = "configured-tenant";

        var configuredMismatch = translator.Translate(activity, "authenticated-other-tenant");
        activity.Conversation!.TenantId = "conversation-other-tenant";
        var activityMismatch = translator.Translate(activity, "configured-tenant");
        activity.Conversation!.TenantId = "configured-tenant";
        var missingAuthenticatedTenant = translator.Translate(activity, null);

        Assert.Equal("configured_tenant_mismatch", configuredMismatch.ReasonCode);
        Assert.DoesNotContain("authenticated-other-tenant", configuredMismatch.ReasonCode, StringComparison.Ordinal);
        Assert.Equal("tenant_mismatch", activityMismatch.ReasonCode);
        Assert.Equal("missing_authenticated_tenant_id", missingAuthenticatedTenant.ReasonCode);
    }

    [Fact]
    public void Translator_rejects_a_missing_configured_tenant_before_constructing_an_activity()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions(), new FakeTimeProvider());

        var result = translator.Translate(CreateSdkMessage(), "authenticated-tenant");

        Assert.Equal(TeamsTranslationDisposition.RejectedPendingTenantEvidence, result.Disposition);
        Assert.Equal("missing_configured_tenant_id", result.ReasonCode);
        Assert.Null(result.Activity);
        Assert.DoesNotContain("authenticated-tenant", result.ReasonCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Tenant_resolver_prefers_the_sdk_authenticated_tenant()
    {
        var activity = CreateSdkMessage();
        activity.Conversation!.TenantId = "activity-tenant";

        var tenantId = TeamsActivityEndpointExtensions.ResolveTenantId(activity, "sdk-tenant");

        Assert.Equal("sdk-tenant", tenantId);
    }

    [Fact]
    public void Tenant_resolver_uses_the_platform_conversation_tenant_when_the_sdk_omits_one()
    {
        var activity = CreateSdkMessage();
        activity.Conversation!.TenantId = "activity-tenant";

        var tenantId = TeamsActivityEndpointExtensions.ResolveTenantId(activity, null);

        Assert.Equal("activity-tenant", tenantId);
    }

    [Fact]
    public void Incomplete_configuration_never_maps_the_activity_route()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Teams:Enabled"] = "true",
            ["Teams:TenantId"] = "tenant",
            ["Teams:ClientId"] = "client"
        });
        builder.Services.AddChannelIntegrations(builder.Configuration);
        builder.AddTeamsIngress();
        var app = builder.Build();

        app.MapTeamsActivityEndpoint();

        Assert.DoesNotContain(
            ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            endpoint => string.Equals(endpoint.RoutePattern.RawText, TeamsActivityEndpointExtensions.ActivityPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Complete_configuration_maps_exactly_one_authenticated_activity_route()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Teams:Enabled"] = "true",
            ["Teams:TenantId"] = "tenant",
            ["Teams:ClientId"] = "client",
            ["Teams:ClientSecret"] = "synthetic-secret"
        });
        builder.Services.AddChannelIntegrations(builder.Configuration);
        builder.AddTeamsIngress();
        var app = builder.Build();

        app.MapTeamsActivityEndpoint();

        var route = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            endpoint => string.Equals(endpoint.RoutePattern.RawText, TeamsActivityEndpointExtensions.ActivityPath, StringComparison.Ordinal));
        Assert.Contains(
            route.Metadata.OfType<AuthorizeAttribute>(),
            attribute => attribute.Policy == TeamsActivityEndpointExtensions.AuthorizationPolicy);
        Assert.Contains(route.Metadata, metadata => metadata is EnableRateLimitingAttribute);

        var policy = await app.Services.GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync(TeamsActivityEndpointExtensions.AuthorizationPolicy);
        Assert.NotNull(policy);
        Assert.Equal([TeamsActivityEndpointExtensions.AuthenticationScheme], policy.AuthenticationSchemes);
    }

    [Fact]
    public void Complete_legacy_configuration_populates_sdk_inbound_and_outbound_authentication()
    {
        const string tenantId = "synthetic-tenant";
        const string clientId = "synthetic-client";
        const string clientSecret = "synthetic-secret";
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Teams:Enabled"] = "true",
            ["Teams:TenantId"] = tenantId,
            ["Teams:ClientId"] = clientId,
            ["Teams:ClientSecret"] = clientSecret
        });
        builder.Services.AddChannelIntegrations(builder.Configuration);

        Assert.False(builder.Configuration.GetSection("AzureAd").Exists());

        builder.AddTeamsIngress();
        using var app = builder.Build();

        var bot = app.Services.GetRequiredService<TeamsBotApplication>();
        var inbound = app.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(TeamsActivityEndpointExtensions.AuthenticationScheme);
        var outbound = app.Services.GetRequiredService<IOptionsMonitor<MicrosoftIdentityApplicationOptions>>()
            .Get(TeamsActivityEndpointExtensions.AuthenticationScheme);
        var credential = Assert.Single(outbound.ClientCredentials!);

        Assert.Equal(clientId, bot.AppId);
        Assert.Contains(clientId, inbound.TokenValidationParameters.ValidAudiences!);
        Assert.Equal(tenantId, outbound.TenantId);
        Assert.Equal(clientId, outbound.ClientId);
        Assert.Equal(CredentialSource.ClientSecret, credential.SourceType);
        Assert.Equal(clientSecret, credential.ClientSecret);
        Assert.NotNull(app.Services.GetRequiredService<Microsoft.Teams.Core.ConversationClient>());
        Assert.NotNull(app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("BotConversationClient"));
    }

    [Fact]
    public void Complete_teams_configuration_rejects_a_conflicting_azuread_client_id()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Teams:Enabled"] = "true",
            ["Teams:TenantId"] = "tenant",
            ["Teams:ClientId"] = "teams-client",
            ["Teams:ClientSecret"] = "synthetic-secret",
            ["AzureAd:ClientId"] = "different-client"
        });
        builder.Services.AddChannelIntegrations(builder.Configuration);

        var error = Assert.Throws<InvalidOperationException>(builder.AddTeamsIngress);

        Assert.Contains("AzureAd:ClientId", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_teams_does_not_change_generic_authentication_or_authorization()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddChannelIntegrations(builder.Configuration);
        builder.AddTeamsIngress();
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        using var app = builder.Build();

        var schemes = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        var policy = await app.Services.GetRequiredService<IAuthorizationPolicyProvider>()
            .GetDefaultPolicyAsync();

        Assert.Equal("AuthSelector", (await schemes.GetDefaultAuthenticateSchemeAsync())!.Name);
        Assert.Null(await schemes.GetSchemeAsync(TeamsActivityEndpointExtensions.AuthenticationScheme));
        Assert.Empty(policy.AuthenticationSchemes);
    }

    [Fact]
    public async Task Teams_policy_uses_azuread_without_a_device_bearer_attempt()
    {
        await using var app = await BuildTeamsAuthorizationTestHostAsync();
        var authorization = app.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var defaultPolicy = await authorization.GetDefaultPolicyAsync();
        var teamsPolicy = await authorization.GetPolicyAsync(TeamsActivityEndpointExtensions.AuthorizationPolicy);
        var client = app.GetTestClient();

        Assert.Empty(defaultPolicy.AuthenticationSchemes);
        Assert.NotNull(teamsPolicy);
        Assert.Equal([TeamsActivityEndpointExtensions.AuthenticationScheme], teamsPolicy.AuthenticationSchemes);

        var loopbackRequest = new HttpRequestMessage(HttpMethod.Get, "/teams-auth-test/operator");
        loopbackRequest.Headers.Add("X-Test-Loopback", "true");
        var loopbackResponse = await client.SendAsync(loopbackRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, loopbackResponse.StatusCode);
        Assert.Equal(
            LoopbackAuthenticationHandler.SchemeName,
            await loopbackResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var rejectedDeviceRequest = new HttpRequestMessage(HttpMethod.Get, "/teams-auth-test/operator");
        rejectedDeviceRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "invalid-device-token");
        var rejectedDeviceResponse = await client.SendAsync(rejectedDeviceRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, rejectedDeviceResponse.StatusCode);

        FailingDeviceAuthenticationHandler.ResetAttempts();
        var teamsRequest = new HttpRequestMessage(HttpMethod.Post, TeamsActivityEndpointExtensions.ActivityPath)
        {
            Content = new StringContent("{}")
        };
        teamsRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "invalid-device-token");
        teamsRequest.Headers.Add(TeamsPolicyAuthenticationHandler.HeaderName, TeamsPolicyAuthenticationHandler.HeaderValue);
        var teamsResponse = await client.SendAsync(teamsRequest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, teamsResponse.StatusCode);
        Assert.Equal(
            TeamsActivityEndpointExtensions.AuthenticationScheme,
            await teamsResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, FailingDeviceAuthenticationHandler.Attempts);
    }

    [Fact]
    public async Task Complete_host_rejects_anonymous_teams_activity()
    {
        await using var app = await BuildTeamsTestHostAsync();

        var response = await app.GetTestClient().PostAsync(
            TeamsActivityEndpointExtensions.ActivityPath,
            new StringContent("{}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ingress_actor_deduplicates_only_the_process_local_fast_path()
    {
        var actorSystem = ActorSystem.Create($"teams-ingress-{Guid.NewGuid():N}");
        try
        {
            var sink = new RecordingIngressSink();
            var actor = actorSystem.ActorOf(Props.Create(() => new TeamsIngressActor(sink, TimeProvider.System)));
            var activity = CreateInboundActivity();

            var first = await actor.Ask<TeamsIngressRouteResult>(
                new TeamsIngressReceived(activity, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);
            var duplicate = await actor.Ask<TeamsIngressRouteResult>(
                new TeamsIngressReceived(activity, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);

            Assert.Equal(TeamsIngressRouteDisposition.Routed, first.Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.Duplicate, duplicate.Disposition);
            Assert.Equal(1, sink.Count);
        }
        finally
        {
            await actorSystem.Terminate();
        }
    }

    [Fact]
    public async Task Ingress_actor_failure_and_unavailable_result_do_not_poison_a_retry()
    {
        var actorSystem = ActorSystem.Create($"teams-ingress-retry-{Guid.NewGuid():N}");
        try
        {
            var sink = new SequencedIngressSink(TeamsIngressSinkResult.Unavailable, TeamsIngressSinkResult.Accepted);
            var actor = actorSystem.ActorOf(Props.Create(() => new TeamsIngressActor(sink, TimeProvider.System)));
            var activity = CreateInboundActivity();

            var unavailable = await actor.Ask<TeamsIngressRouteResult>(new TeamsIngressReceived(activity, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            var retry = await actor.Ask<TeamsIngressRouteResult>(new TeamsIngressReceived(activity, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            var duplicate = await actor.Ask<TeamsIngressRouteResult>(new TeamsIngressReceived(activity, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            Assert.Equal(TeamsIngressRouteDisposition.Unavailable, unavailable.Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.Routed, retry.Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.Duplicate, duplicate.Disposition);
        }
        finally
        {
            await actorSystem.Terminate();
        }
    }

    [Fact]
    public async Task Ingress_host_reports_unavailable_before_start_and_after_stop()
    {
        var actorSystem = ActorSystem.Create($"teams-ingress-host-{Guid.NewGuid():N}");
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(actorSystem);
            using var provider = services.BuildServiceProvider();
            var host = new TeamsIngressActorHost(provider);

            var beforeStart = await host.SubmitAsync(CreateInboundActivity(), TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
            var afterStop = await host.SubmitAsync(CreateInboundActivity(), TestContext.Current.CancellationToken);

            Assert.Equal(TeamsIngressRouteDisposition.Unavailable, beforeStart.Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.Unavailable, afterStop.Disposition);
        }
        finally
        {
            await actorSystem.Terminate();
        }
    }

    [Fact]
    public async Task Ingress_actor_failure_does_not_poison_a_retry_or_consume_a_cache_entry()
    {
        var actorSystem = ActorSystem.Create($"teams-ingress-failure-{Guid.NewGuid():N}");
        try
        {
            var sink = new ThrowThenAcceptIngressSink();
            var actor = actorSystem.ActorOf(Props.Create(() => new TeamsIngressActor(sink, TimeProvider.System)));
            var activity = CreateInboundActivity();

            Assert.Equal(TeamsIngressRouteDisposition.RouteFailed, (await RouteAsync(actor, activity)).Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.Routed, (await RouteAsync(actor, activity)).Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.Duplicate, (await RouteAsync(actor, activity)).Disposition);
        }
        finally
        {
            await actorSystem.Terminate();
        }
    }

    [Fact]
    public async Task Ingress_actor_cancellation_during_sink_routing_remains_retryable()
    {
        var actorSystem = ActorSystem.Create($"teams-ingress-cancel-{Guid.NewGuid():N}");
        try
        {
            using var cancellation = new CancellationTokenSource();
            var sink = new CancelThenAcceptIngressSink(cancellation);
            var actor = actorSystem.ActorOf(Props.Create(() => new TeamsIngressActor(sink, TimeProvider.System)));
            var activity = CreateInboundActivity();

            Assert.Equal(
                TeamsIngressRouteDisposition.Cancelled,
                (await actor.Ask<TeamsIngressRouteResult>(
                    new TeamsIngressReceived(activity, cancellation.Token),
                    TestContext.Current.CancellationToken)).Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.Routed, (await RouteAsync(actor, activity)).Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.Duplicate, (await RouteAsync(actor, activity)).Disposition);
        }
        finally
        {
            await actorSystem.Terminate();
        }
    }

    [Fact]
    public async Task Deferred_sink_is_unavailable_and_cancelled_host_submission_is_cancelled()
    {
        var sink = new DeferredTeamsConversationIngressSink();
        Assert.Equal(
            TeamsIngressSinkResult.Unavailable,
            await sink.RouteAsync(CreateInboundActivity(), TestContext.Current.CancellationToken));

        var actorSystem = ActorSystem.Create($"teams-ingress-cancelled-host-{Guid.NewGuid():N}");
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(actorSystem);
            using var provider = services.BuildServiceProvider();
            var host = new TeamsIngressActorHost(provider);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Equal(
                TeamsIngressRouteDisposition.Cancelled,
                (await host.SubmitAsync(CreateInboundActivity(), cancellation.Token)).Disposition);
        }
        finally
        {
            await actorSystem.Terminate();
        }
    }

    [Fact]
    public async Task Ingress_host_maps_an_ask_timeout_to_unavailable()
    {
        var telemetry = ChannelTelemetry.For(ChannelType.Teams);
        var initial = telemetry.GetSnapshot();
        var actorSystem = ActorSystem.Create($"teams-ingress-timeout-{Guid.NewGuid():N}");
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(actorSystem);
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddSingleton<ITeamsConversationIngressSink, NeverCompletingIngressSink>();
            using var provider = services.BuildServiceProvider();
            var host = new TeamsIngressActorHost(provider);
            await host.StartAsync(TestContext.Current.CancellationToken);

            var result = await host.SubmitAsync(CreateInboundActivity(), TestContext.Current.CancellationToken);

            Assert.Equal(TeamsIngressRouteDisposition.Unavailable, result.Disposition);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await actorSystem.Terminate();
        }

        Assert.Equal(initial.EventsRouted, telemetry.GetSnapshot().EventsRouted);
    }

    [Fact]
    public async Task Teams_activity_rate_limit_rejects_the_thirty_first_request_and_isolates_sources()
    {
        await using var app = await BuildTeamsRateLimitHostAsync();
        var client = app.GetTestClient();

        for (var request = 0; request < 30; request++)
            Assert.Equal(System.Net.HttpStatusCode.OK, (await SendRateLimitedRequestAsync(client, "192.0.2.10")).StatusCode);

        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, (await SendRateLimitedRequestAsync(client, "192.0.2.10")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await SendRateLimitedRequestAsync(client, "192.0.2.11")).StatusCode);
    }

    [Fact]
    public async Task Rate_limiter_composition_retains_all_inbound_policies()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Teams:Enabled"] = "true",
            ["Teams:TenantId"] = "tenant",
            ["Teams:ClientId"] = "client",
            ["Teams:ClientSecret"] = "secret"
        });
        builder.AddTeamsIngress();
        builder.Services.AddRateLimiter(options =>
            options.AddPolicy("pairing-exchange", _ => RateLimitPartition.GetNoLimiter("pairing")));
        builder.Services.AddMattermostActionEndpointRateLimiting();
        builder.Services.AddRateLimiter(options => options.RejectionStatusCode = StatusCodes.Status429TooManyRequests);
        builder.Services.RemoveAll<IHostedService>();

        await using var app = builder.Build();
        app.UseRateLimiter();
        app.MapPost("/rate/teams", () => Results.Ok()).RequireRateLimiting(TeamsActivityEndpointExtensions.RateLimitPolicy);
        app.MapPost("/rate/pairing", () => Results.Ok()).RequireRateLimiting("pairing-exchange");
        app.MapPost("/rate/mattermost", () => Results.Ok()).RequireRateLimiting(MattermostActionEndpointExtensions.CallbackRateLimitPolicy);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/rate/teams", new StringContent("{}"), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/rate/pairing", new StringContent("{}"), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/rate/mattermost", new StringContent("{}"), TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Teams_rate_limit_uses_one_bounded_partition_when_remote_ip_is_absent()
    {
        var endpoint = new RateLimitedEndpoint();
        await using var app = await BuildTeamsRateLimitHostAsync(endpoint);
        var client = app.GetTestClient();

        for (var request = 0; request < 30; request++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/teams-rate-limit-test", new StringContent("{}"), TestContext.Current.CancellationToken)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/teams-rate-limit-test", new StringContent("{}"), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(30, endpoint.Count);
    }

    [Fact]
    public async Task Ingress_dispositions_record_routed_telemetry_only_after_sink_acceptance()
    {
        var telemetry = ChannelTelemetry.For(ChannelType.Teams);
        var initial = telemetry.GetSnapshot();
        var actorSystem = ActorSystem.Create($"teams-ingress-telemetry-{Guid.NewGuid():N}");
        try
        {
            var accepted = actorSystem.ActorOf(Props.Create(() => new TeamsIngressActor(new RecordingIngressSink(), TimeProvider.System)));
            var unavailable = actorSystem.ActorOf(Props.Create(() => new TeamsIngressActor(new SequencedIngressSink(TeamsIngressSinkResult.Unavailable), TimeProvider.System)));
            var failed = actorSystem.ActorOf(Props.Create(() => new TeamsIngressActor(new ThrowThenAcceptIngressSink(), TimeProvider.System)));

            Assert.Equal(TeamsIngressRouteDisposition.Routed, (await RouteAsync(accepted, CreateInboundActivity())).Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.Duplicate, (await RouteAsync(accepted, CreateInboundActivity())).Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.Unavailable, (await RouteAsync(unavailable, CreateInboundActivity(activityId: "unavailable"))).Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.RouteFailed, (await RouteAsync(failed, CreateInboundActivity(activityId: "failed"))).Disposition);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Equal(
                TeamsIngressRouteDisposition.Cancelled,
                (await accepted.Ask<TeamsIngressRouteResult>(
                    new TeamsIngressReceived(CreateInboundActivity(activityId: "cancelled"), cancellation.Token),
                    TestContext.Current.CancellationToken)).Disposition);
        }
        finally
        {
            await actorSystem.Terminate();
        }

        var final = telemetry.GetSnapshot();
        Assert.Equal(initial.EventsRouted + 1, final.EventsRouted);
        Assert.Equal(initial.EventsFiltered + 1, final.EventsFiltered);
        Assert.True(final.EventsDropped >= initial.EventsDropped + 2);
    }

    [Fact]
    public async Task Teams_activity_body_guard_returns_semantic_4xx_and_never_calls_the_endpoint_for_oversized_bodies()
    {
        var endpoint = new RecordingBodyEndpoint();
        await using var app = await BuildTeamsBodyGuardHostAsync(endpoint);
        var client = app.GetTestClient();

        var empty = await client.PostAsync(TeamsActivityEndpointExtensions.ActivityPath, new ByteArrayContent([]), TestContext.Current.CancellationToken);
        var malformed = await client.PostAsync(TeamsActivityEndpointExtensions.ActivityPath, new StringContent("{"), TestContext.Current.CancellationToken);
        var exactLimit = await client.PostAsync(
            TeamsActivityEndpointExtensions.ActivityPath,
            new ByteArrayContent(Encoding.UTF8.GetBytes(new string('a', TeamsActivityEndpointExtensions.MaxActivityBodyBytes))),
            TestContext.Current.CancellationToken);
        var oversized = await client.PostAsync(
            TeamsActivityEndpointExtensions.ActivityPath,
            new ByteArrayContent(Encoding.UTF8.GetBytes(new string('a', TeamsActivityEndpointExtensions.MaxActivityBodyBytes + 1))),
            TestContext.Current.CancellationToken);
        var chunkedOversized = await client.PostAsync(
            TeamsActivityEndpointExtensions.ActivityPath,
            new ChunkedContent(Encoding.UTF8.GetBytes(new string('a', TeamsActivityEndpointExtensions.MaxActivityBodyBytes + 1))),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.InRange((int)malformed.StatusCode, 400, 499);
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, exactLimit.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunkedOversized.StatusCode);
        Assert.Equal(2, endpoint.Count);
    }

    [Fact]
    public async Task Teams_safe_outputs_do_not_disclose_ingress_secrets_or_tenant_values()
    {
        const string configuredTenant = "configured-tenant-sentinel";
        const string authenticatedTenant = "authenticated-tenant-sentinel";
        const string activityTenant = "activity-tenant-sentinel";
        const string secret = "client-secret-sentinel";
        const string authorization = "authorization-sentinel";
        const string rawBody = "raw-body-sentinel";
        var translator = new TeamsSdkActivityTranslator(
            new TeamsChannelOptions { TenantId = configuredTenant, ClientSecret = new SensitiveString(secret) },
            TimeProvider.System);
        var configuredMismatch = translator.Translate(CreateSdkMessage(), authenticatedTenant);
        var activity = CreateSdkMessage();
        activity.Conversation!.TenantId = activityTenant;
        var activityMismatch = translator.Translate(activity, configuredTenant);
        var snapshot = await SnapshotAsync(new TeamsChannelOptions
        {
            Enabled = true,
            TenantId = configuredTenant,
            ClientId = "client",
            ClientSecret = new SensitiveString(secret)
        });
        await using var app = await BuildTeamsTestHostAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, TeamsActivityEndpointExtensions.ActivityPath)
        {
            Content = new StringContent(rawBody)
        };
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        var safeOutputs = string.Join("\n", new[]
        {
            configuredMismatch.ReasonCode,
            activityMismatch.ReasonCode,
            snapshot.HealthDetail,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("configured_tenant_mismatch", configuredMismatch.ReasonCode);
        Assert.Equal("tenant_mismatch", activityMismatch.ReasonCode);
        Assert.DoesNotContain(configuredTenant, safeOutputs, StringComparison.Ordinal);
        Assert.DoesNotContain(authenticatedTenant, safeOutputs, StringComparison.Ordinal);
        Assert.DoesNotContain(activityTenant, safeOutputs, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, safeOutputs, StringComparison.Ordinal);
        Assert.DoesNotContain(authorization, safeOutputs, StringComparison.Ordinal);
        Assert.DoesNotContain(rawBody, safeOutputs, StringComparison.Ordinal);
    }

    [Fact]
    public void Translator_rejects_each_required_message_boundary_without_synthesizing_identifiers()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-01T12:00:00Z"));
        var translator = new TeamsSdkActivityTranslator(
            new TeamsChannelOptions { TenantId = "tenant" },
            clock);
        var activity = CreateSdkMessage();

        var missingConversation = CreateSdkMessage();
        missingConversation.Text = "text";
        ((CoreActivity)missingConversation).Conversation = new TeamsConversation
        {
            Id = "",
            ConversationType = TeamsConversationType.Personal
        };
        Assert.Equal("missing_conversation_id", translator.Translate(missingConversation, "tenant").ReasonCode);

        var missingActivity = CreateSdkMessage();
        missingActivity.Text = "text";
        missingActivity.Id = null;
        Assert.Equal("missing_activity_id", translator.Translate(missingActivity, "tenant").ReasonCode);

        var missingText = CreateSdkMessage();
        missingText.Text = "";
        Assert.Equal("missing_message_text", translator.Translate(missingText, "tenant").ReasonCode);

        var missingSender = CreateSdkMessage();
        missingSender.Text = "text";
        ((CoreActivity)missingSender).From = null;
        Assert.Equal("missing_sender_id", translator.Translate(missingSender, "tenant").ReasonCode);

        var accepted = translator.Translate(activity, "tenant");
        Assert.Equal(TeamsTranslationDisposition.Accepted, accepted.Disposition);
        Assert.Equal(clock.GetUtcNow(), accepted.Activity!.Trust.ReceivedAtUtc);
        Assert.Null(accepted.Activity.Trust.PlatformTimestampUtc);
        Assert.Equal("activity", accepted.Activity.Trust.ActivityId);
        Assert.Equal("conversation", accepted.Activity.Trust.ConversationId);
    }

    [Fact]
    public void Translator_preserves_only_the_platform_timestamp_and_rejects_pending_activity_kinds()
    {
        var receivedAt = DateTimeOffset.Parse("2026-07-01T12:00:00Z");
        var clock = new FakeTimeProvider(receivedAt);
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, clock);
        var activity = CreateSdkMessage();
        activity.Timestamp = "2026-06-30T12:00:00+02:00";

        var accepted = translator.Translate(activity, "tenant");
        Assert.Equal(receivedAt, accepted.Activity!.Trust.ReceivedAtUtc);
        Assert.Equal(DateTimeOffset.Parse(activity.Timestamp), accepted.Activity.Trust.PlatformTimestampUtc);
        Assert.Equal("invalid_channel_root_identity", translator.Translate(CreateSdkMessage(TeamsConversationType.Channel), "tenant").ReasonCode);
        Assert.Equal("missing_conversation_id", translator.Translate(CreateSdkActivity<MessageUpdateActivity>("messageUpdate"), "tenant").ReasonCode);
        Assert.Equal("missing_conversation_id", translator.Translate(CreateSdkActivity<MessageDeleteActivity>("messageDelete"), "tenant").ReasonCode);
        Assert.Equal(TeamsTranslationDisposition.Ignored, translator.Translate(CreateSdkActivity<ConversationUpdateActivity>("conversationUpdate"), "tenant").Disposition);
        Assert.Equal(TeamsIngressActivityKind.Unknown, translator.Translate(CreateSdkActivity<TeamsActivity>("typing"), "tenant").ActivityKind);
    }

    [Fact]
    public void Translator_maps_tenant_proven_channel_root_and_qualified_bot_mentions()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions
        {
            TenantId = "tenant",
            BotId = "bot",
            AllowedTeamIds = ["team"],
            AllowedChannelIds = ["channel"]
        }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Channel);
        activity.Id = "root";
        activity.Text = "<at>renamed bot</at> hello <at>renamed bot</at>";
        ((CoreActivity)activity).From = new TeamsAccount { Id = "sender" };
        ((CoreActivity)activity).Recipient = new TeamsAccount { Id = "28:bot" };
        activity.Conversation!.Id = "conversation;messageid=root";
        activity.ChannelData = new TeamsChannelData
        {
            Team = new TeamsTeam { Id = "team" },
            Channel = new TeamsChannel { Id = "channel" }
        };
        activity.Entities =
        [
            new MentionEntity { Type = "mention", Mentioned = new TeamsAccount { Id = "28:bot" }, Text = "<at>renamed bot</at>" },
            new MentionEntity { Type = "mention", Mentioned = new TeamsAccount { Id = "28:bot" }, Text = "<at>renamed bot</at>" },
            new MentionEntity { Type = "mention", Mentioned = new TeamsAccount { Id = "28:user" }, Text = "<at>user</at>" }
        ];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.True(result.Activity!.IsMentioned);
        Assert.Equal(" hello ", result.Activity.Text);
        Assert.Equal("root", result.Activity.Reply!.RootActivityId);
        Assert.Equal("team", result.Activity.TeamId);
        Assert.Equal("channel", result.Activity.ChannelId);
    }

    [Fact]
    public void Translator_uses_entra_object_id_for_channel_user_acl_identity()
    {
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant",
            BotId = "bot",
            AllowedTeamIds = ["team"],
            AllowedChannelIds = ["channel"],
            AllowedUserIds = ["operator-aad-object"],
            ChannelAudienceOverrides = [new TeamsChannelAudienceOverride
            {
                TeamId = "team",
                ChannelId = "channel",
                Audience = "Team"
            }]
        };
        var translator = new TeamsSdkActivityTranslator(options, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Channel);
        activity.Id = "root";
        ((CoreActivity)activity).From = new TeamsAccount { Id = "opaque-transport-sender", AadObjectId = "operator-aad-object" };
        ((CoreActivity)activity).Recipient = new TeamsAccount { Id = "28:bot" };
        activity.Conversation!.Id = "conversation;messageid=root";
        activity.ChannelData = new TeamsChannelData
        {
            Team = new TeamsTeam { Id = "team" },
            Channel = new TeamsChannel { Id = "channel" }
        };
        activity.Entities = [new MentionEntity
        {
            Type = "mention",
            Mentioned = new TeamsAccount { Id = "28:bot" },
            Text = "<at>Netclaw</at>"
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal("operator-aad-object", result.Activity!.Trust.SenderId);
        Assert.Equal(TeamsChannelPolicyDisposition.Allowed, TeamsChannelAclPolicy.Evaluate(result.Activity, options).Disposition);
    }

    [Fact]
    public void Translator_retains_transport_sender_id_when_entra_object_id_is_unavailable()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage();
        ((CoreActivity)activity).From = new TeamsAccount { Id = "opaque-transport-sender" };

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal("opaque-transport-sender", result.Activity!.Trust.SenderId);
    }

    [Fact]
    public void Translator_accepts_plain_text_without_a_wrapper_before_channel_routing()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Channel);
        activity.Conversation!.Id = "conversation;messageid=root";

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal("plain_text_accepted", result.ReasonCode);
        Assert.Equal("hello", result.Activity!.Text);
    }

    [Fact]
    public void Translator_rejects_embedded_graph_references_in_html_attachment_content_before_routing()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Html,
            Content = "<a href=\"https://graph.microsoft.com/v1.0/me/drive/items/1\">file</a>"
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.RejectedMalformed, result.Disposition);
        Assert.Equal("graph_backed_attachment_unsupported", result.ReasonCode);
        Assert.Null(result.Activity);
    }

    [Fact]
    public void Translator_accepts_formatted_text_rendering_wrapper_without_exposing_its_markup()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Html,
            Content = "<pre>line one\nline two \ud83d\ude80</pre>"
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal("teams_text_rendering_wrapper_ignored", result.ReasonCode);
        Assert.Equal("hello", result.Activity!.Text);
    }

    [Fact]
    public void Translator_accepts_a_json_string_formatted_text_rendering_wrapper_from_the_sdk()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Html,
            Content = JsonDocument.Parse("\"<pre>line one\\nline two \\ud83d\\ude80</pre>\"").RootElement.Clone()
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal("teams_text_rendering_wrapper_ignored", result.ReasonCode);
        Assert.Equal("hello", result.Activity!.Text);
    }

    [Fact]
    public void Translator_accepts_a_parameterized_html_rendering_wrapper_for_a_bot_mentioned_channel_root()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant", BotId = "bot" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Channel);
        activity.Id = "root";
        activity.Text = "<at>synthetic bot</at> hello";
        activity.Conversation!.Id = "conversation;messageid=root";
        ((CoreActivity)activity).Recipient = new TeamsAccount { Id = "28:bot" };
        activity.Entities = [new MentionEntity
        {
            Type = "mention",
            Mentioned = new TeamsAccount { Id = "28:bot" },
            Text = "<at>synthetic bot</at>"
        }];
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = new AttachmentContentType("text/html; charset=utf-8"),
            Content = JsonDocument.Parse("\"<div>synthetic rendering metadata</div>\"").RootElement.Clone()
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal("teams_text_rendering_wrapper_ignored", result.ReasonCode);
        Assert.Equal(" hello", result.Activity!.Text);
        Assert.True(result.Activity.IsMentioned);
        Assert.Equal("root", result.Activity.Reply!.RootActivityId);
        Assert.Empty(result.Activity.Attachments);
    }

    [Fact]
    public void Translator_does_not_treat_a_user_only_mention_as_a_bot_mention()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant", BotId = "bot" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Channel);
        activity.Conversation!.Id = "conversation;messageid=root";
        ((CoreActivity)activity).Recipient = new TeamsAccount { Id = "28:bot" };
        activity.Text = "<at>synthetic user</at> hello";
        activity.Entities = [new MentionEntity
        {
            Type = "mention",
            Mentioned = new TeamsAccount { Id = "28:user" },
            Text = "<at>synthetic user</at>"
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.False(result.Activity!.IsMentioned);
        Assert.Equal("<at>synthetic user</at> hello", result.Activity.Text);
    }

    [Fact]
    public void Translator_maps_a_group_chat_inline_image_to_safe_metadata()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.GroupChat);
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = new AttachmentContentType("image/png"),
            ContentUrl = new Uri("https://smba.trafficmanager.net/emea/attachments/image"),
            Name = "diagram.png"
        }];

        var result = translator.Translate(activity, "tenant");

        var attachment = Assert.Single(result.Activity!.Attachments);
        Assert.Equal(TeamsInboundAttachmentKind.InlineImage, attachment.Kind);
        Assert.Equal("diagram.png", attachment.Name);
        Assert.Equal("image/png", attachment.ContentType);
        Assert.Equal(0, attachment.SourceIndex);
        Assert.DoesNotContain("Url", string.Join(',', attachment.GetType().GetProperties().Select(property => property.Name)));
    }

    [Fact]
    public void Translator_accepts_a_live_inline_image_with_its_html_rendering_companion()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions
        {
            TenantId = "tenant",
            BotId = "bot"
        }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Channel);
        activity.Id = "root";
        activity.Conversation!.Id = "conversation;messageid=root";
        activity.Text = "<at>Netclaw</at> inspect this image";
        ((CoreActivity)activity).Recipient = new TeamsAccount { Id = "28:bot" };
        activity.Entities = [new MentionEntity
        {
            Type = "mention",
            Mentioned = new TeamsAccount { Id = "28:bot" },
            Text = "<at>Netclaw</at>"
        }];
        activity.Attachments =
        [
            new TeamsAttachment
            {
                ContentType = new AttachmentContentType("image/png"),
                ContentUrl = new Uri("https://smba.trafficmanager.net/amer/v3/attachments/image"),
                Name = "inline.png"
            },
            new TeamsAttachment
            {
                ContentType = TeamsContentType.Html,
                Content = "<div><img src=\"https://rendering.invalid/inline\" /></div>"
            }
        ];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal(" inspect this image", result.Activity!.Text);
        var attachment = Assert.Single(result.Activity.Attachments);
        Assert.Equal(TeamsInboundAttachmentKind.InlineImage, attachment.Kind);
        Assert.Equal(0, attachment.SourceIndex);
    }

    [Fact]
    public void Translator_treats_a_bounded_image_transport_mime_as_an_image_candidate()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.GroupChat);
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = new AttachmentContentType("image/bmp"),
            ContentUrl = new Uri("https://smba.trafficmanager.net/emea/attachments/image"),
            Name = "diagram.bmp"
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal(TeamsInboundAttachmentKind.InlineImage, Assert.Single(result.Activity!.Attachments).Kind);
    }

    [Fact]
    public void Translator_accepts_an_attachment_only_message()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Text = " ";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = new AttachmentContentType("image/png"),
            ContentUrl = new Uri("https://smba.trafficmanager.net/amer/v3/attachments/image"),
            Name = "diagram.png"
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.True(string.IsNullOrWhiteSpace(result.Activity!.Text));
        Assert.Equal(TeamsInboundAttachmentKind.InlineImage, Assert.Single(result.Activity.Attachments).Kind);
    }

    [Fact]
    public void Translator_maps_a_personal_file_download_notice_to_safe_metadata()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = new AttachmentContentType("application/vnd.microsoft.teams.file.download.info"),
            ContentUrl = new Uri("https://contoso.sharepoint.com/personal/user/Documents/report.pdf"),
            Name = "report.pdf",
            Content = JsonDocument.Parse("{\"downloadUrl\":\"https://contoso.sharepoint.com/download?token=opaque\"}").RootElement.Clone()
        }];

        var result = translator.Translate(activity, "tenant");

        var attachment = Assert.Single(result.Activity!.Attachments);
        Assert.Equal(TeamsInboundAttachmentKind.PersonalFile, attachment.Kind);
        Assert.Equal("report.pdf", attachment.Name);
        Assert.Equal(0, attachment.SourceIndex);
    }

    [Fact]
    public void Translator_defers_non_image_group_chat_files_without_exposing_their_url()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.GroupChat);
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = new AttachmentContentType("application/vnd.microsoft.teams.file.download.info"),
            ContentUrl = new Uri("https://contoso.sharepoint.com/personal/user/Documents/report.pdf"),
            Name = "report.pdf"
        }];

        var result = translator.Translate(activity, "tenant");

        var attachment = Assert.Single(result.Activity!.Attachments);
        Assert.Equal(TeamsInboundAttachmentKind.Unknown, attachment.Kind);
        Assert.Equal("report.pdf", attachment.Name);
    }

    [Theory]
    [InlineData("text/html; charset=utf-16")]
    [InlineData("text/html; profile=untrusted")]
    public void Attachment_classifier_rejects_html_rendering_parameters_other_than_utf8_charset(string contentType)
    {
        var result = TeamsTenantEvidenceMappings.ClassifyAttachment(new TeamsAttachmentEvidence(
            contentType,
            HasName: false,
            ContentUrl: null,
            HasContentUrl: false,
            ContentKind: TeamsAttachmentContentKind.NonEmptyText));

        Assert.Equal(TeamsAttachmentClassification.UnsupportedAttachmentShape, result.Classification);
        Assert.Equal("unsupported_attachment_shape", result.ReasonCode);
    }

    [Fact]
    public void Translator_does_not_duplicate_canonical_text_when_it_has_a_formatted_text_wrapper()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Text = "canonical\ntext \ud83d\ude80";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Html,
            Content = "<pre>canonical\ntext \ud83d\ude80</pre>"
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal("canonical\ntext \ud83d\ude80", result.Activity!.Text);
    }

    [Fact]
    public void Translator_rejects_empty_html_upload_shell_before_channel_routing()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Html,
            Content = string.Empty
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.RejectedMalformed, result.Disposition);
        Assert.Equal("graph_backed_attachment_unsupported", result.ReasonCode);
        Assert.Null(result.Activity);
    }

    [Fact]
    public void Translator_rejects_structured_html_attachment_content_before_channel_routing()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Html,
            Content = new { rendering = "text" }
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.RejectedMalformed, result.Disposition);
        Assert.Equal("attachment_malformed_rejected", result.ReasonCode);
        Assert.Null(result.Activity);
    }

    [Fact]
    public void Translator_rejects_personal_messages_with_graph_backed_attachment_references_before_routing()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Html,
            ContentUrl = new Uri("https://graph.microsoft.com/v1.0/me/drive/items/1")
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.RejectedMalformed, result.Disposition);
        Assert.Equal("graph_backed_attachment_unsupported", result.ReasonCode);
    }

    [Fact]
    public void Translator_maps_a_bounded_unknown_attachment_as_non_executable()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Text,
            Content = "dGVzdA==",
            ContentUrl = new Uri("https://example.test/file.txt")
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        var attachment = Assert.Single(result.Activity!.Attachments);
        Assert.Equal(TeamsInboundAttachmentKind.Unknown, attachment.Kind);
        Assert.Equal(0, attachment.SourceIndex);
    }

    [Fact]
    public void Translator_preserves_text_when_a_bounded_unknown_companion_is_not_accepted()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Text = "safe text";
        activity.Attachments =
        [
            new TeamsAttachment { ContentType = TeamsContentType.Html, Content = "<div>rendering metadata</div>" },
            new TeamsAttachment { ContentType = TeamsContentType.Text, Content = "untrusted inline attachment" }
        ];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal("safe text", result.Activity!.Text);
        var attachment = Assert.Single(result.Activity.Attachments);
        Assert.Equal(TeamsInboundAttachmentKind.Unknown, attachment.Kind);
        Assert.Equal(1, attachment.SourceIndex);
    }

    [Fact]
    public void Translator_rejects_an_html_wrapper_with_a_thumbnail_url_before_routing()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Html,
            Content = "<pre>normal text</pre>",
            ThumbnailUrl = new Uri("https://rendering.invalid/thumbnail")
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.RejectedMalformed, result.Disposition);
        Assert.Equal("attachment_malformed_rejected", result.ReasonCode);
        Assert.Null(result.Activity);
    }

    [Fact]
    public void Translator_rejects_an_html_wrapper_with_an_embedded_graph_reference_before_routing()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Html,
            Content = "<pre><a href=\"https://graph.microsoft.com/v1.0/synthetic\">normal text</a></pre>"
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.RejectedMalformed, result.Disposition);
        Assert.Equal("graph_backed_attachment_unsupported", result.ReasonCode);
        Assert.Null(result.Activity);
    }

    [Fact]
    public void Translator_maps_a_bounded_channel_upload_reference_as_non_executable()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant", BotId = "bot" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Channel);
        activity.Id = "root";
        activity.Conversation!.Id = "conversation;messageid=root";
        ((CoreActivity)activity).Recipient = new TeamsAccount { Id = "28:bot" };
        activity.ChannelData = new TeamsChannelData();
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Html,
            Content = JsonDocument.Parse("\"https://rendering.invalid/opaque\"").RootElement.Clone()
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal(TeamsInboundAttachmentKind.Unknown, Assert.Single(result.Activity!.Attachments).Kind);
    }

    [Fact]
    public void Translator_maps_a_bounded_channel_html_reference_as_non_executable()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant", BotId = "bot" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Channel);
        activity.Id = "root";
        activity.Conversation!.Id = "conversation;messageid=root";
        ((CoreActivity)activity).Recipient = new TeamsAccount { Id = "28:bot" };
        activity.ChannelData = new TeamsChannelData();
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Html,
            Content = JsonDocument.Parse("\"<span><a href=\\\"https://rendering.invalid/opaque\\\">synthetic</a></span>\"").RootElement.Clone()
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal(TeamsInboundAttachmentKind.Unknown, Assert.Single(result.Activity!.Attachments).Kind);
    }

    [Fact]
    public void Translator_maps_inline_attachment_content_as_non_executable()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Text,
            Content = "untrusted inline attachment"
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal(TeamsInboundAttachmentKind.Unknown, Assert.Single(result.Activity!.Attachments).Kind);
    }

    [Fact]
    public void Translator_maps_a_bounded_non_graph_url_as_non_executable()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Text,
            ContentUrl = new Uri("https://example.test/file.txt")
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal(TeamsInboundAttachmentKind.Unknown, Assert.Single(result.Activity!.Attachments).Kind);
    }

    [Fact]
    public void Translator_rejects_null_attachment_entries_before_routing()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Attachments = new List<TeamsAttachment> { null! };

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.RejectedMalformed, result.Disposition);
        Assert.Equal("attachment_malformed_rejected", result.ReasonCode);
        Assert.Null(result.Activity);
    }

    [Fact]
    public void Attachment_classifier_rejects_known_file_download_info_as_graph_backed()
    {
        var result = TeamsTenantEvidenceMappings.ClassifyAttachment(new TeamsAttachmentEvidence(
            ContentType: "application/vnd.microsoft.teams.file.download.info",
            HasName: false,
            ContentUrl: null,
            HasContentUrl: false));

        Assert.Equal(TeamsAttachmentClassification.GraphBackedUnsupported, result.Classification);
        Assert.Equal("graph_backed_attachment_unsupported", result.ReasonCode);
    }

    [Fact]
    public void Translator_rejects_oversized_attachment_metadata_before_routing()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Text,
            Name = new string('a', 16_384),
            Content = "untrusted inline attachment"
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.RejectedMalformed, result.Disposition);
        Assert.Equal("attachment_malformed_rejected", result.ReasonCode);
        Assert.Null(result.Activity);
    }

    [Fact]
    public void Translator_rejects_an_html_wrapper_with_an_oversized_url_before_routing()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Html,
            Content = "<pre>normal text</pre>",
            ContentUrl = new Uri($"https://example.test/{new string('a', 2_048)}")
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.RejectedMalformed, result.Disposition);
        Assert.Equal("attachment_malformed_rejected", result.ReasonCode);
        Assert.Null(result.Activity);
    }

    [Fact]
    public void Translator_rejects_an_html_wrapper_with_an_empty_url_field_before_routing()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Personal);
        activity.Conversation!.Id = "conversation";
        activity.Attachments = [new TeamsAttachment
        {
            ContentType = TeamsContentType.Html,
            Content = "<pre>normal text</pre>",
            ContentUrl = new Uri(string.Empty, UriKind.Relative)
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.RejectedMalformed, result.Disposition);
        Assert.Equal("attachment_malformed_rejected", result.ReasonCode);
        Assert.Null(result.Activity);
    }

    [Fact]
    public void Translator_describes_each_rejected_channel_attachment_without_payload_data()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions
        {
            TenantId = "tenant",
            BotId = "bot",
            AllowedTeamIds = ["team"],
            AllowedChannelIds = ["channel"],
            AllowedUserIds = ["user"],
            ChannelAudienceOverrides = [new TeamsChannelAudienceOverride
            {
                TeamId = "team",
                ChannelId = "channel",
                Audience = "Team"
            }]
        }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Channel);
        activity.Id = "root";
        activity.Conversation!.Id = "conversation;messageid=root";
        ((CoreActivity)activity).From = new TeamsAccount { Id = "user" };
        ((CoreActivity)activity).Recipient = new TeamsAccount { Id = "28:bot" };
        activity.ChannelData = new TeamsChannelData
        {
            Team = new TeamsTeam { Id = "team" },
            Channel = new TeamsChannel { Id = "channel" }
        };
        activity.Entities = [new MentionEntity
        {
            Type = "mention",
            Mentioned = new TeamsAccount { Id = "28:bot" },
            Text = "<at>Netclaw</at>"
        }];
        activity.Attachments =
        [
            new TeamsAttachment
            {
                ContentType = new AttachmentContentType("image/png"),
                ContentUrl = new Uri("https://smba.trafficmanager.net/amer/v3/attachments/image"),
                Name = "inline.png"
            },
            new TeamsAttachment
            {
                ContentType = TeamsContentType.Html,
                Content = JsonDocument.Parse("{}").RootElement.Clone()
            }
        ];

        var result = translator.Translate(activity, "tenant");
        var diagnostic = translator.DescribeRejectedAttachment(activity, "tenant", result);

        Assert.NotNull(diagnostic);
        Assert.Equal("channel", diagnostic.Scope);
        Assert.True(diagnostic.TenantMatch);
        Assert.True(diagnostic.TeamMatch);
        Assert.True(diagnostic.ChannelMatch);
        Assert.True(diagnostic.SenderMatch);
        Assert.True(diagnostic.Mentioned);
        Assert.True(diagnostic.RootActivityValid);
        Assert.True(diagnostic.AudienceValid);
        Assert.Equal("attachment_malformed_rejected", diagnostic.PolicyReason);
        Assert.Equal(2, diagnostic.AttachmentCount);
        var image = diagnostic.Attachments[0];
        Assert.Equal(0, image.Index);
        Assert.Equal("image", image.ContentType);
        Assert.Equal(nameof(TeamsAttachmentContentKind.Missing), image.ContentKind);
        Assert.True(image.HasContentUrl);
        Assert.True(image.HasName);
        Assert.False(image.HasThumbnail);
        Assert.Equal(nameof(TeamsAttachmentClassification.UnsupportedAttachmentShape), image.ClassifierDisposition);
        Assert.Equal(nameof(TeamsInboundAttachmentKind.InlineImage), image.ResolvedInboundAttachmentKind);
        var hostile = diagnostic.Attachments[1];
        Assert.Equal(1, hostile.Index);
        Assert.Equal("text_html", hostile.ContentType);
        Assert.Equal(nameof(TeamsAttachmentContentKind.Structured), hostile.ContentKind);
        Assert.False(hostile.HasContentUrl);
        Assert.False(hostile.HasName);
        Assert.False(hostile.HasThumbnail);
        Assert.Equal(nameof(TeamsAttachmentClassification.UnsupportedAttachmentShape), hostile.ClassifierDisposition);
        Assert.Equal(nameof(TeamsInboundAttachmentKind.Unknown), hostile.ResolvedInboundAttachmentKind);
        Assert.Equal(1, diagnostic.MentionCount);
        Assert.False(diagnostic.ReplyToIdExists);
    }

    [Fact]
    public void Translation_telemetry_uses_safe_wrapper_and_attachment_reason_codes()
    {
        var telemetry = ChannelTelemetry.For(ChannelType.Teams);
        var initial = telemetry.GetSnapshot();

        TeamsActivityEndpointExtensions.RecordTranslationTelemetry(TeamsTranslationResult.Accepted(
            CreateInboundActivity(), "teams_text_rendering_wrapper_ignored"));
        TeamsActivityEndpointExtensions.RecordTranslationTelemetry(TeamsTranslationResult.Accepted(
            CreateInboundActivity(), "teams_attachment_received"));
        TeamsActivityEndpointExtensions.RecordTranslationTelemetry(TeamsTranslationResult.Rejected(
            TeamsTranslationDisposition.RejectedMalformed,
            TeamsIngressActivityKind.Message,
            "graph_backed_attachment_unsupported"));
        TeamsActivityEndpointExtensions.RecordTranslationTelemetry(TeamsTranslationResult.Rejected(
            TeamsTranslationDisposition.RejectedMalformed,
            TeamsIngressActivityKind.Message,
            "attachment_malformed_rejected"));

        var extras = telemetry.GetSnapshot().Extras;
        Assert.Equal((initial.Extras.TryGetValue("teams_text_rendering_wrapper_ignored", out var wrapper) ? wrapper : 0) + 1,
            extras["teams_text_rendering_wrapper_ignored"]);
        Assert.Equal((initial.Extras.TryGetValue("attachment_received", out var attachment) ? attachment : 0) + 1,
            extras["attachment_received"]);
        Assert.Equal((initial.Extras.TryGetValue("attachment_graph_backed_rejected", out var graph) ? graph : 0) + 1,
            extras["attachment_graph_backed_rejected"]);
        Assert.Equal((initial.Extras.TryGetValue("attachment_malformed_rejected", out var malformed) ? malformed : 0) + 1,
            extras["attachment_malformed_rejected"]);
        Assert.DoesNotContain(extras.Keys, key => key.Contains("hello", StringComparison.Ordinal));
    }

    [Fact]
    public void Translator_does_not_treat_a_malformed_mention_entity_as_a_bot_mention()
    {
        var translator = new TeamsSdkActivityTranslator(new TeamsChannelOptions { TenantId = "tenant", BotId = "bot" }, TimeProvider.System);
        var activity = CreateSdkMessage(TeamsConversationType.Channel);
        activity.Id = "root";
        activity.Text = "literal bot text";
        ((CoreActivity)activity).Recipient = new TeamsAccount { Id = "28:bot" };
        activity.Conversation!.Id = "conversation;messageid=root";
        activity.Entities = [new MentionEntity
        {
            Type = "mention",
            Mentioned = new TeamsAccount { Id = "28:bot" },
            Text = "bot"
        }];

        var result = translator.Translate(activity, "tenant");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.False(result.Activity!.IsMentioned);
        Assert.Equal("literal bot text", result.Activity.Text);
    }

    [Fact]
    public async Task Ingress_actor_retains_tenant_and_conversation_isolation_and_evicts_deterministically()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var actorSystem = ActorSystem.Create($"teams-ingress-capacity-{Guid.NewGuid():N}");
        try
        {
            var sink = new RecordingIngressSink();
            var actor = actorSystem.ActorOf(Props.Create(() => new TeamsIngressActor(sink, clock)));
            for (var index = 0; index < TeamsIngressActor.DuplicateCapacity; index++)
                Assert.Equal(TeamsIngressRouteDisposition.Routed, (await RouteAsync(actor, CreateInboundActivity(activityId: $"activity-{index}"))).Disposition);

            Assert.Equal(TeamsIngressRouteDisposition.Routed, (await RouteAsync(actor, CreateInboundActivity(activityId: "activity-1024"))).Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.Routed, (await RouteAsync(actor, CreateInboundActivity(activityId: "activity-0"))).Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.Duplicate, (await RouteAsync(actor, CreateInboundActivity(activityId: "activity-1024"))).Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.Routed, (await RouteAsync(actor, CreateInboundActivity(tenantId: "other-tenant", activityId: "activity-1024"))).Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.Routed, (await RouteAsync(actor, CreateInboundActivity(conversationId: "other-conversation", activityId: "activity-1024"))).Disposition);
        }
        finally
        {
            await actorSystem.Terminate();
        }
    }

    [Fact]
    public async Task Ingress_actor_expires_accepted_entries_and_never_caches_failure_or_cancellation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var actorSystem = ActorSystem.Create($"teams-ingress-expiry-{Guid.NewGuid():N}");
        try
        {
            var sink = new SequencedIngressSink(TeamsIngressSinkResult.Unavailable, TeamsIngressSinkResult.Accepted, TeamsIngressSinkResult.Accepted);
            var actor = actorSystem.ActorOf(Props.Create(() => new TeamsIngressActor(sink, clock)));
            var activity = CreateInboundActivity();
            Assert.Equal(TeamsIngressRouteDisposition.Unavailable, (await RouteAsync(actor, activity)).Disposition);
            Assert.Equal(TeamsIngressRouteDisposition.Routed, (await RouteAsync(actor, activity)).Disposition);
            clock.Advance(TeamsIngressActor.DuplicateRetention);
            Assert.Equal(TeamsIngressRouteDisposition.Routed, (await RouteAsync(actor, activity)).Disposition);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Equal(TeamsIngressRouteDisposition.Cancelled, (await actor.Ask<TeamsIngressRouteResult>(
                new TeamsIngressReceived(CreateInboundActivity(activityId: "cancelled"), cancellation.Token),
                TestContext.Current.CancellationToken)).Disposition);
        }
        finally
        {
            await actorSystem.Terminate();
        }
    }

    private static async Task<TeamsIngressRouteResult> RouteAsync(IActorRef actor, TeamsInboundActivity activity)
        => await actor.Ask<TeamsIngressRouteResult>(
            new TeamsIngressReceived(activity, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

    private static async Task<ChannelRuntimeSnapshot> SnapshotAsync(TeamsChannelOptions options)
    {
        var descriptor = ChannelDescriptor.CreateRemoteChat(ChannelType.Teams, "Teams", options.Enabled, options.AllowDirectMessages);
        var provider = new TeamsChannelRuntimeSnapshotProvider(descriptor, TeamsIngressRegistration.Evaluate(options));
        return await provider.GetSnapshotAsync(TestContext.Current.CancellationToken);
    }

    private static MessageActivity CreateSdkMessage(TeamsConversationType? type = null) =>
        MessageActivity.FromActivity(CoreActivity.FromJsonString(JsonSerializer.Serialize(new
        {
            type = "message",
            text = "hello",
            id = "activity",
            from = new { id = "sender" },
            serviceUrl = "https://service.invalid/",
            conversation = new
            {
                id = "conversation",
                tenantId = "tenant",
                conversationType = (type ?? TeamsConversationType.Personal).Value
            }
        })));

    private static TActivity CreateSdkActivity<TActivity>(string type)
        where TActivity : TeamsActivity
    {
        var activity = new CoreActivity(type);
        var typedActivity = type switch
        {
            "messageUpdate" => MessageUpdateActivity.FromActivity(activity),
            "messageDelete" => MessageDeleteActivity.FromActivity(activity),
            "conversationUpdate" => ConversationUpdateActivity.FromActivity(activity),
            _ => TeamsActivity.FromActivity(activity)
        };

        return typedActivity as TActivity
            ?? throw new InvalidOperationException($"The SDK did not create {typeof(TActivity).Name} for '{type}'.");
    }

    private static InvokeActivity CreateSdkApprovalAction(
        string action,
        string senderId = "sender",
        string? aadObjectId = null,
        string? displayName = null)
    {
        var coreActivity = CoreActivity.FromJsonString(JsonSerializer.Serialize(new
        {
            type = "invoke",
            name = "adaptiveCard/action",
            id = "approval-action",
            replyToId = "approval-prompt",
            from = new { id = senderId, aadObjectId, name = displayName },
            serviceUrl = "https://service.invalid/",
            conversation = new
            {
                id = "conversation",
                tenantId = "tenant",
                conversationType = TeamsConversationType.Personal.Value
            },
            value = new
            {
                action = new
                {
                    type = "Action.Execute",
                    data = new Dictionary<string, object>
                    {
                        ["correlation"] = "correlation_123",
                        ["nonce"] = "nonce_123",
                        ["action"] = action
                    }
                }
            }
        }));
        var activity = InvokeActivity.FromActivity(coreActivity);
        ((CoreActivity)activity).Properties[TeamsSdkActivityTranslator.PreservedReplyToActivityIdProperty] = coreActivity.ReplyToId;
        return activity;
    }

    private static TeamsInboundActivity CreateInboundActivity(string tenantId = "tenant", string conversationId = "conversation", string activityId = "activity")
        => new(
            new TeamsIngressTrustContext(
                TrustAudience.Public,
                PrincipalClassification.UntrustedExternal,
                TrustBoundary.Public,
                new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
                "sender",
                tenantId,
                conversationId,
                TeamsConversationScope.Personal,
                activityId,
                DateTimeOffset.UnixEpoch),
            "hello");

    private static async Task<WebApplication> BuildTeamsTestHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Teams:Enabled"] = "true",
            ["Teams:TenantId"] = "synthetic-tenant",
            ["Teams:ClientId"] = "synthetic-client",
            ["Teams:ClientSecret"] = "synthetic-secret"
        });
        builder.Services.AddChannelIntegrations(builder.Configuration);
        builder.AddTeamsIngress();
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options => options.RejectionStatusCode = StatusCodes.Status429TooManyRequests);

        // The real daemon starts Akka before this Teams hosted service. The
        // HTTP-only fixture intentionally removes it because anonymous auth
        // rejection must occur before the translator or actor is reachable.
        builder.Services.RemoveAll<IHostedService>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseTeamsIngress();
        app.MapTeamsActivityEndpoint();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<WebApplication> BuildTeamsAuthorizationTestHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Teams:Enabled"] = "true",
            ["Teams:TenantId"] = "synthetic-tenant",
            ["Teams:ClientId"] = "synthetic-client",
            ["Teams:ClientSecret"] = "synthetic-secret"
        });
        builder.Services.AddChannelIntegrations(builder.Configuration);
        builder.AddTeamsIngress();
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.PostConfigure<AuthenticationOptions>(options =>
        {
            options.SchemeMap[TeamsActivityEndpointExtensions.AuthenticationScheme].HandlerType = typeof(TeamsPolicyAuthenticationHandler);
            options.SchemeMap[DeviceTokenAuthenticationHandler.SchemeName].HandlerType = typeof(FailingDeviceAuthenticationHandler);
        });
        builder.Services.RemoveAll<IHostedService>();

        var app = builder.Build();
        app.Use((context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Test-Loopback"))
                context.Connection.RemoteIpAddress = IPAddress.Loopback;

            return next(context);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/teams-auth-test/operator", (HttpContext context) =>
            Results.Text(context.User.Identity?.AuthenticationType ?? "none"))
            .RequireAuthorization();
        app.MapPost(TeamsActivityEndpointExtensions.ActivityPath, (HttpContext context) =>
            Results.Text(context.User.Identity?.AuthenticationType ?? "none"))
            .RequireAuthorization(TeamsActivityEndpointExtensions.AuthorizationPolicy);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<WebApplication> BuildTeamsRateLimitHostAsync(RateLimitedEndpoint? endpoint = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Teams:Enabled"] = "true",
            ["Teams:TenantId"] = "synthetic-tenant",
            ["Teams:ClientId"] = "synthetic-client",
            ["Teams:ClientSecret"] = "synthetic-secret"
        });
        builder.Services.AddChannelIntegrations(builder.Configuration);
        builder.AddTeamsIngress();
        builder.Services.AddRateLimiter(options => options.RejectionStatusCode = StatusCodes.Status429TooManyRequests);
        builder.Services.RemoveAll<IHostedService>();

        var app = builder.Build();
        app.Use((context, next) =>
        {
            var testRemote = context.Request.Headers["X-Test-Remote"].ToString();
            if (!string.IsNullOrWhiteSpace(testRemote))
                context.Connection.RemoteIpAddress = IPAddress.Parse(testRemote);
            return next(context);
        });
        app.UseRateLimiter();
        app.MapPost("/teams-rate-limit-test", () => endpoint is null ? Results.Ok() : endpoint.Handle())
            .RequireRateLimiting(TeamsActivityEndpointExtensions.RateLimitPolicy);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<WebApplication> BuildTeamsBodyGuardHostAsync(RecordingBodyEndpoint endpoint)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.UseTeamsActivityBodyGuard();
        app.MapPost(TeamsActivityEndpointExtensions.ActivityPath, endpoint.HandleAsync);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static Task<HttpResponseMessage> SendRateLimitedRequestAsync(HttpClient client, string source)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/teams-rate-limit-test")
        {
            Content = new StringContent("{}")
        };
        request.Headers.Add("X-Test-Remote", source);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class TeamsPolicyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string HeaderName = "X-Test-Teams-Auth";
        public const string HeaderValue = "valid";

        public TeamsPolicyAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers[HeaderName] != HeaderValue)
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "teams-test-user")],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class FailingDeviceAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private static int _attempts;

        public FailingDeviceAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        public static int Attempts => Volatile.Read(ref _attempts);

        public static void ResetAttempts() => Interlocked.Exchange(ref _attempts, 0);

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Interlocked.Increment(ref _attempts);
            return Task.FromResult(AuthenticateResult.Fail("test device authentication failure"));
        }
    }

    private sealed class RecordingIngressSink : ITeamsConversationIngressSink
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public ValueTask<TeamsIngressSinkResult> RouteAsync(TeamsInboundActivity activity, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(TeamsIngressSinkResult.Accepted);
        }
    }

    private sealed class SequencedIngressSink(params TeamsIngressSinkResult[] results) : ITeamsConversationIngressSink
    {
        private readonly Queue<TeamsIngressSinkResult> _results = new(results);

        public ValueTask<TeamsIngressSinkResult> RouteAsync(TeamsInboundActivity activity, CancellationToken cancellationToken)
            => ValueTask.FromResult(_results.Dequeue());
    }

    private sealed class ThrowThenAcceptIngressSink : ITeamsConversationIngressSink
    {
        private bool _shouldThrow = true;

        public ValueTask<TeamsIngressSinkResult> RouteAsync(TeamsInboundActivity activity, CancellationToken cancellationToken)
        {
            if (_shouldThrow)
            {
                _shouldThrow = false;
                throw new InvalidOperationException("synthetic sink failure");
            }

            return ValueTask.FromResult(TeamsIngressSinkResult.Accepted);
        }
    }

    private sealed class CancelThenAcceptIngressSink(CancellationTokenSource cancellation) : ITeamsConversationIngressSink
    {
        private bool _shouldCancel = true;

        public ValueTask<TeamsIngressSinkResult> RouteAsync(TeamsInboundActivity activity, CancellationToken cancellationToken)
        {
            if (_shouldCancel)
            {
                _shouldCancel = false;
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }

            return ValueTask.FromResult(TeamsIngressSinkResult.Accepted);
        }
    }

    private sealed class NeverCompletingIngressSink : ITeamsConversationIngressSink
    {
        public ValueTask<TeamsIngressSinkResult> RouteAsync(TeamsInboundActivity activity, CancellationToken cancellationToken)
            => new(new TaskCompletionSource<TeamsIngressSinkResult>().Task);
    }

    private sealed class RecordingBodyEndpoint
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public async Task<IResult> HandleAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            try
            {
                await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
                return Results.Ok();
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }
        }
    }

    private sealed class RateLimitedEndpoint
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public IResult Handle()
        {
            Interlocked.Increment(ref _count);
            return Results.Ok();
        }
    }

    private sealed class ChunkedContent(byte[] content) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(content).AsTask();
    }

    private static string EncodeForSession(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
