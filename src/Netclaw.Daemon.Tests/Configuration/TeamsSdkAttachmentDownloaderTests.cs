// -----------------------------------------------------------------------
// <copyright file="TeamsSdkAttachmentDownloaderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Net;
using System.Security.Claims;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Akka.Event;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Identity.Abstractions;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Core.Schema;
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using SkiaSharp;
using Xunit;
using MicrosoftLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class TeamsSdkAttachmentDownloaderTests
{
    [Theory]
    [InlineData("http://smba.trafficmanager.net/attachments/image")]
    [InlineData("https://user@smba.trafficmanager.net/attachments/image")]
    [InlineData("https://files.example.test/attachments/image")]
    [InlineData("https://smba.trafficmanager.net.example.test/attachments/image")]
    [InlineData("https://attacker.trafficmanager.net/attachments/image")]
    public async Task Capture_rejects_untrusted_attachment_urls(string url)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var handler = new RecordingHandler(_ => Success("content"));
        var downloader = CreateDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();

        downloader.Capture(CreateSdkMessage(url), activity);

        using var temp = new DisposableTempDir();
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Capture_allows_the_documented_smba_attachment_host()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var handler = new RecordingHandler(_ => Success("content"));
        var downloader = CreateDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();

        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);

        using var temp = new DisposableTempDir();
        var result = await downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result.BytesWritten);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Bot_connector_download_uses_the_sdk_app_token_and_keeps_it_at_the_daemon_boundary()
    {
        const string token = "synthetic-bot-token";
        var authorizationHeaders = new RecordingAuthorizationHeaderProvider($"Bearer {token}");
        var logs = new CapturingLoggerProvider();
        var handler = new RecordingHandler(
            request =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal(token, request.Headers.Authorization?.Parameter);
                Assert.DoesNotContain(token, request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
                return Success("content");
            });
        using var app = BuildBotConnectorDownloadHost(handler, authorizationHeaders, logs);
        var downloader = app.Services.GetRequiredService<TeamsSdkAttachmentDownloader>();
        var activity = CreateActivity();
        var attachment = CreateAttachment();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);

        using var temp = new DisposableTempDir();
        var result = await downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result.BytesWritten);
        Assert.Equal(["https://api.botframework.com/.default"], authorizationHeaders.AppScopes);
        Assert.Equal([TeamsActivityEndpointExtensions.AuthenticationScheme], authorizationHeaders.AppAuthenticationOptionNames);
        Assert.Equal(1, handler.CallCount);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(new { activity, attachment, result }), StringComparison.Ordinal);
        var ingress = new TeamsBindingIngress(activity, CancellationToken.None);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(ingress.Activity), StringComparison.Ordinal);
        Assert.DoesNotContain(token, string.Join('\n', handler.RequestUris), StringComparison.Ordinal);
        Assert.DoesNotContain(token, string.Join('\n', logs.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain("smba.trafficmanager.net", string.Join('\n', logs.Messages), StringComparison.Ordinal);
        Assert.Equal(Timeout.InfiniteTimeSpan, app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("teams-attachments").Timeout);
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/team/image")]
    [InlineData("https://contoso.sharepoint.us/sites/team/image")]
    [InlineData("https://contoso.onedrive.com/personal/user/image")]
    public async Task Signed_non_bot_connector_urls_do_not_receive_the_bot_token(string url)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var signedUrlHandler = new RecordingHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            return Success("content");
        });
        var clients = new TestHttpClientFactory(signedUrlHandler);
        var authorizationHeaders = new RecordingAuthorizationHeaderProvider("Bearer synthetic-bot-token");
        var downloader = CreateDownloader(clients, authorizationHeaders, clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();
        downloader.Capture(CreateSdkMessage(url), activity);

        using var temp = new DisposableTempDir();
        await downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken);

        Assert.Equal(["teams-attachments"], clients.CreatedClientNames);
        Assert.Empty(authorizationHeaders.AppScopes);
        Assert.Equal(1, signedUrlHandler.CallCount);
    }

    [Fact]
    public async Task Bot_connector_401_uses_the_existing_stable_attachment_rejection()
    {
        var authorizationHeaders = new RecordingAuthorizationHeaderProvider("Bearer synthetic-bot-token");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var app = BuildBotConnectorDownloadHost(handler, authorizationHeaders, new CapturingLoggerProvider());
        var downloader = app.Services.GetRequiredService<TeamsSdkAttachmentDownloader>();
        var activity = CreateActivity();
        var attachment = CreateProvisionalAttachment();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);

        using var inbox = new DisposableTempDir();
        using var staging = new DisposableTempDir();
        var log = new AttachmentLog();
        var outcome = await TeamsProvisionalInlineImageIngress.IngestAsync(
            activity,
            attachment,
            TrustAudience.Public,
            ImageAttachmentPolicy(),
            inlineImages: true,
            inbox.Path,
            staging.Path,
            new object(),
            TimeProvider.System,
            new NullContentScanner(),
            log,
            downloader,
            TestContext.Current.CancellationToken);

        var rejection = Assert.IsType<AttachmentIngestOutcome.Rejected>(outcome);
        Assert.Equal("Couldn't download `attachment-1.png` — please try again later.", rejection.UserFacingReason);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("reason=download-http-error", Assert.Single(log.Messages), StringComparison.Ordinal);
        Assert.Contains("stage=response_headers", Assert.Single(log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bot_token_acquisition_failure_has_a_bounded_attachment_rejection()
    {
        const string token = "synthetic-bot-token";
        var authorizationHeaders = new RecordingAuthorizationHeaderProvider(
            $"Bearer {token}",
            new InvalidOperationException($"Token acquisition failed for {token}."));
        var handler = new RecordingHandler(_ => throw new Xunit.Sdk.XunitException("The request must not start after token acquisition fails."));
        using var app = BuildBotConnectorDownloadHost(handler, authorizationHeaders, new CapturingLoggerProvider());
        var downloader = app.Services.GetRequiredService<TeamsSdkAttachmentDownloader>();
        var activity = CreateActivity();
        var attachment = CreateProvisionalAttachment();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);

        using var inbox = new DisposableTempDir();
        using var staging = new DisposableTempDir();
        var log = new AttachmentLog();
        var outcome = await TeamsProvisionalInlineImageIngress.IngestAsync(
            activity,
            attachment,
            TrustAudience.Public,
            ImageAttachmentPolicy(),
            inlineImages: true,
            inbox.Path,
            staging.Path,
            new object(),
            TimeProvider.System,
            new NullContentScanner(),
            log,
            downloader,
            TestContext.Current.CancellationToken);

        var rejection = Assert.IsType<AttachmentIngestOutcome.Rejected>(outcome);
        Assert.Equal("Couldn't download `attachment-1.png` — please try again later.", rejection.UserFacingReason);
        Assert.DoesNotContain(token, rejection.UserFacingReason, StringComparison.Ordinal);
        Assert.Equal(["https://api.botframework.com/.default"], authorizationHeaders.AppScopes);
        Assert.Equal(0, handler.CallCount);
        Assert.Contains("stage=token", Assert.Single(log.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain(token, Assert.Single(log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_does_not_follow_a_redirect()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://files.example.test/redirected") }
        });
        var downloader = CreateDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);

        using var temp = new DisposableTempDir();
        await Assert.ThrowsAsync<TeamsAttachmentDownloadException>(() => downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Captures_expire_after_the_short_lifetime()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var handler = new RecordingHandler(_ => Success("content"));
        var downloader = CreateDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);
        clock.Advance(TimeSpan.FromMinutes(6));

        using var temp = new DisposableTempDir();
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Captured_attachment_url_is_single_use()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var handler = new RecordingHandler(_ => Success("content"));
        var downloader = CreateDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);

        using var temp = new DisposableTempDir();
        await downloader.DownloadAsync(activity, attachment, temp.Path, 1_024, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public void Capture_keeps_no_more_than_1024_activities()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var downloader = CreateDownloader(
            new TestHttpClientFactory(new RecordingHandler(_ => Success("content"))),
            clock);

        for (var index = 0; index < 1_025; index++)
        {
            var activity = CreateActivity($"activity-{index}");
            downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);
        }

        var capturesField = typeof(TeamsSdkAttachmentDownloader).GetField("_captures", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var captures = capturesField!.GetValue(downloader)!;
        var count = Assert.IsType<int>(captures.GetType().GetProperty("Count")!.GetValue(captures));
        Assert.Equal(1_024, count);
    }

    [Fact]
    public async Task Capture_rejects_an_overlong_url()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var handler = new RecordingHandler(_ => Success("content"));
        var downloader = CreateDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();
        var url = "https://smba.trafficmanager.net/" + new string('a', 4_097);
        downloader.Capture(CreateSdkMessage(url), activity);

        using var temp = new DisposableTempDir();
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Bot_connector_download_keeps_the_existing_streaming_byte_limit()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var handler = new RecordingHandler(_ => Success(new string('x', 1_025)));
        var downloader = CreateDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);

        using var temp = new DisposableTempDir();
        await Assert.ThrowsAsync<AttachmentTooLargeException>(() => downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(temp.Path));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Download_removes_the_partial_file_when_cancelled()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var stream = new PartialThenCancellationStream();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        });
        var downloader = CreateDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);

        using var temp = new DisposableTempDir();
        using var cancellation = new CancellationTokenSource();
        var download = downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            cancellation.Token);
        await stream.WaitingForCancellation.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var failure = await Assert.ThrowsAsync<TeamsAttachmentDownloadException>(() => download);
        Assert.True(failure.Cancelled);
        Assert.Equal("body", failure.Stage);
        Assert.Equal(1, handler.CallCount);
        Assert.Empty(Directory.EnumerateFiles(temp.Path));
    }

    [Fact]
    public void Attachment_url_does_not_enter_actor_or_persistence_contracts()
    {
        const string rawUrl = "https://smba.trafficmanager.net/amer/v3/attachments/sensitive";
        var attachment = CreateAttachment();
        var activity = CreateActivity(attachments: [attachment]);
        var ingress = new TeamsBindingIngress(activity, CancellationToken.None);
        var conversationIngress = new TeamsConversationIngress(activity, CancellationToken.None);

        Assert.DoesNotContain("Url", string.Join(',', typeof(TeamsAttachmentMetadata).GetProperties().Select(property => property.Name)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rawUrl, JsonSerializer.Serialize(activity), StringComparison.Ordinal);
        Assert.Same(activity, ingress.Activity);
        Assert.Same(activity, conversationIngress.Activity);
    }

    [Fact]
    public async Task Authenticated_download_can_exceed_the_old_deadline_and_verify_png_bytes()
    {
        var clock = new FakeTimeProvider();
        using var handler = new GatedHandler();
        var downloader = CreateDownloader(new TestHttpClientFactory(handler), clock);
        var attachment = CreateProvisionalAttachment();
        var activity = CreateActivity(attachments: [attachment]);
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/private-id"), activity);
        using var inbox = new DisposableTempDir();
        using var staging = new DisposableTempDir();
        var log = new AttachmentLog();
        var ingest = TeamsProvisionalInlineImageIngress.IngestAsync(
            activity, attachment, TrustAudience.Public, ImageAttachmentPolicy(), true,
            inbox.Path, staging.Path, new object(), clock,
            new MagicByteContentScanner(new ContentPolicy()), log, downloader, TestContext.Current.CancellationToken);
        await handler.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(handler.Token.IsCancellationRequested);
        handler.Response.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(TestImages.SmallPng())
        });
        Assert.IsType<AttachmentIngestOutcome.Accepted>(await ingest);
        Assert.Single(Directory.GetFiles(inbox.Path));
        Assert.Contains(log.Messages, message => message.Contains("verifiedMime=image/png", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false, "download-deadline")]
    [InlineData(true, false, "ingress-cancelled")]
    [InlineData(true, true, "ingress-cancelled")]
    public async Task Download_cancellation_identifies_the_owner(bool cancelIngress, bool expireBoth, string reason)
    {
        var clock = new FakeTimeProvider();
        using var handler = new GatedHandler();
        var downloader = CreateDownloader(new TestHttpClientFactory(handler), clock);
        var attachment = CreateProvisionalAttachment();
        var activity = CreateActivity(attachments: [attachment]);
        const string url = "https://smba.trafficmanager.net/amer/v3/attachments/private-id";
        downloader.Capture(CreateSdkMessage(url), activity);
        using var inbox = new DisposableTempDir();
        using var staging = new DisposableTempDir();
        using var outer = new CancellationTokenSource();
        var log = new AttachmentLog();
        var ingest = TeamsProvisionalInlineImageIngress.IngestAsync(
            activity, attachment, TrustAudience.Public, ImageAttachmentPolicy(), true,
            inbox.Path, staging.Path, new object(), clock,
            new NullContentScanner(), log, downloader, outer.Token);
        await handler.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        if (cancelIngress)
            outer.Cancel();
        if (!cancelIngress || expireBoth)
            clock.Advance(TeamsIngressTimeouts.InlineImageDownload);
        handler.ReleaseCancellation.SetResult();
        if (cancelIngress)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ingest);
        else
            Assert.IsType<AttachmentIngestOutcome.Rejected>(await ingest);
        var message = Assert.Single(log.Messages);
        Assert.Contains($"reason={reason}", message, StringComparison.Ordinal);
        Assert.Contains("host_class=bot_connector", message, StringComparison.Ordinal);
        Assert.Contains("authenticated=True", message, StringComparison.Ordinal);
        Assert.Contains("configured_deadline_ms=240000", message, StringComparison.Ordinal);
        Assert.Contains($"outer_cancellation_requested={cancelIngress}", message, StringComparison.Ordinal);
        Assert.Contains("stage=request", message, StringComparison.Ordinal);
        Assert.DoesNotContain(url, message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-id", message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-bot-token", message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(staging.Path));
        Assert.Empty(Directory.GetFiles(inbox.Path));
    }

    [Fact]
    public async Task Unrelated_cancellation_exception_is_a_failure_not_a_deadline()
    {
        var clock = new FakeTimeProvider();
        var handler = new RecordingHandler(_ => throw new OperationCanceledException("private-id synthetic-bot-token"));
        var downloader = CreateDownloader(new TestHttpClientFactory(handler), clock);
        var attachment = CreateProvisionalAttachment();
        var activity = CreateActivity(attachments: [attachment]);
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/private-id"), activity);
        using var inbox = new DisposableTempDir();
        using var staging = new DisposableTempDir();
        var log = new AttachmentLog();
        var outcome = await TeamsProvisionalInlineImageIngress.IngestAsync(
            activity, attachment, TrustAudience.Public, ImageAttachmentPolicy(), true,
            inbox.Path, staging.Path, new object(), clock,
            new NullContentScanner(), log, downloader, TestContext.Current.CancellationToken);
        Assert.IsType<AttachmentIngestOutcome.Rejected>(outcome);
        var message = Assert.Single(log.Messages);
        Assert.Contains("reason=download-failed", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-id", message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-bot-token", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Slow_body_obeys_the_download_budget_and_cleans_up_on_cancellation(bool cancelIngress)
    {
        var clock = new FakeTimeProvider();
        using var stream = new GatedPngStream(TestImages.SmallPng());
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        });
        var downloader = CreateDownloader(new TestHttpClientFactory(handler), clock);
        var attachment = CreateProvisionalAttachment();
        var activity = CreateActivity(attachments: [attachment]);
        const string url = "https://smba.trafficmanager.net/amer/v3/attachments/private-id";
        downloader.Capture(CreateSdkMessage(url), activity);
        using var inbox = new DisposableTempDir();
        using var staging = new DisposableTempDir();
        using var outer = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var log = new AttachmentLog();
        var ingest = TeamsProvisionalInlineImageIngress.IngestAsync(
            activity, attachment, TrustAudience.Public, ImageAttachmentPolicy(), true,
            inbox.Path, staging.Path, new object(), clock,
            new MagicByteContentScanner(new ContentPolicy()), log, downloader, outer.Token);

        await stream.WaitingForBody.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Single(Directory.GetFiles(staging.Path));
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.False(ingest.IsCompleted);
        Assert.False(stream.ReadToken.IsCancellationRequested);

        if (cancelIngress)
            outer.Cancel();
        else
            stream.Release.SetResult();

        if (cancelIngress)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ingest);
        else
            Assert.IsType<AttachmentIngestOutcome.Accepted>(await ingest);

        Assert.Equal(1, handler.CallCount);
        Assert.Empty(Directory.GetFiles(staging.Path));
        if (cancelIngress)
        {
            Assert.Empty(Directory.GetFiles(inbox.Path));
            var message = Assert.Single(log.Messages);
            Assert.Contains(cancelIngress ? "reason=ingress-cancelled" : "reason=download-deadline", message, StringComparison.Ordinal);
            Assert.Contains("stage=body", message, StringComparison.Ordinal);
            Assert.Contains($"outer_cancellation_requested={cancelIngress}", message, StringComparison.Ordinal);
            Assert.Contains("configured_deadline_ms=240000", message, StringComparison.Ordinal);
            Assert.Contains("host_class=bot_connector authenticated=True", message, StringComparison.Ordinal);
        }
        else
        {
            var file = Assert.Single(Directory.GetFiles(inbox.Path));
            Assert.Equal(TestImages.SmallPng(), await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
            Assert.Contains(log.Messages, message => message.Contains("attachment_download_completed elapsed_ms=20000 configured_deadline_ms=240000", StringComparison.Ordinal));
            Assert.Contains(log.Messages, message => message.Contains("verifiedMime=image/png", StringComparison.Ordinal));
        }
        foreach (var message in log.Messages)
        {
            Assert.DoesNotContain(url, message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-id", message, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-bot-token", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Multi_megabyte_body_with_progress_survives_the_old_total_deadline()
    {
        var clock = new FakeTimeProvider();
        var bytes = LargePng();
        Assert.True(bytes.Length > 2 * 1_024 * 1_024);
        using var stream = new ProgressGatedStream(bytes);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        });
        var downloader = CreateDownloader(new TestHttpClientFactory(handler), clock);
        var attachment = new TeamsAttachmentMetadata("large.png", "image/*", bytes.Length)
        {
            Kind = TeamsInboundAttachmentKind.InlineImage,
            SourceIndex = 0
        };
        var activity = CreateActivity(attachments: [attachment]);
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);
        using var staging = new DisposableTempDir();
        using var inbox = new DisposableTempDir();
        var policy = ImageAttachmentPolicy();
        policy.MaxFileBytes = bytes.Length;
        using var deadline = new CancellationTokenSource(TeamsIngressTimeouts.InlineImageDownload, clock);
        var download = TeamsProvisionalInlineImageIngress.IngestAsync(
            activity, attachment, TrustAudience.Personal, policy, true,
            inbox.Path, staging.Path, new object(), clock,
            new MagicByteContentScanner(new ContentPolicy()), new AttachmentLog(), downloader, deadline.Token);

        for (var index = 0; index < 4; index++)
        {
            var release = await stream.Reads.Reader.ReadAsync(TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(20));
            Assert.False(deadline.IsCancellationRequested);
            Assert.False(download.IsCompleted);
            release.SetResult();
        }

        Assert.IsType<AttachmentIngestOutcome.Accepted>(await download);
        var file = Assert.Single(Directory.GetFiles(inbox.Path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(staging.Path));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Partial_body_stall_retries_once_and_discards_the_first_file()
    {
        var clock = new FakeTimeProvider();
        var bytes = TestImages.SmallPng();
        using var first = new GatedPngStream(bytes);
        using var staging = new DisposableTempDir();
        var attempts = 0;
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal("synthetic-bot-token", request.Headers.Authorization?.Parameter);
            Assert.Empty(Directory.GetFiles(staging.Path));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = ++attempts == 1 ? new StreamContent(first) : new ByteArrayContent(bytes)
            };
        });
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var downloader = new TeamsSdkAttachmentDownloader(
            new TestHttpClientFactory(handler),
            new RecordingAuthorizationHeaderProvider("Bearer synthetic-bot-token"),
            new StaticOptionsMonitor<ManagedIdentityOptions>(new ManagedIdentityOptions()),
            clock, loggerFactory.CreateLogger<TeamsSdkAttachmentDownloader>());
        var activity = CreateActivity();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);
        var download = downloader.DownloadAsync(activity, CreateAttachment(), staging.Path, 1_024, TestContext.Current.CancellationToken);

        await first.WaitingForBody.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Single(Directory.GetFiles(staging.Path));
        clock.Advance(TimeSpan.FromSeconds(30));
        var result = await download;

        Assert.Equal(2, logs.Messages.Count);
        Assert.Contains("outcome=body-idle-retry attempt=1", logs.Messages[0], StringComparison.Ordinal);
        Assert.Contains("bytes_received=8", logs.Messages[0], StringComparison.Ordinal);
        Assert.Contains("outcome=completed attempt=2", logs.Messages[1], StringComparison.Ordinal);
        Assert.Contains($"bytes_received={bytes.Length}", logs.Messages[1], StringComparison.Ordinal);
        foreach (var message in logs.Messages)
        {
            Assert.DoesNotContain("smba.trafficmanager.net", message, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-bot-token", message, StringComparison.Ordinal);
            Assert.DoesNotContain("/attachments/image", message, StringComparison.Ordinal);
        }
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(handler.RequestUris[0], handler.RequestUris[1]);
        Assert.Equal(bytes.Length, result.BytesWritten);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result.FilePath, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(staging.Path));
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            activity, CreateAttachment(), staging.Path, 1_024, TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Two_body_stalls_stop_after_two_attempts_and_remove_partial_files()
    {
        var clock = new FakeTimeProvider();
        using var first = new GatedPngStream(TestImages.SmallPng());
        using var second = new GatedPngStream(TestImages.SmallPng());
        using var staging = new DisposableTempDir();
        var attempts = 0;
        var handler = new RecordingHandler(_ =>
        {
            Assert.Empty(Directory.GetFiles(staging.Path));
            Assert.True(++attempts <= 2, "A body stall permits only one retry.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(attempts == 1 ? first : second)
            };
        });
        var downloader = CreateDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);
        var download = downloader.DownloadAsync(activity, CreateAttachment(), staging.Path, 1_024, TestContext.Current.CancellationToken);

        await first.WaitingForBody.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(30));
        await second.WaitingForBody.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(30));
        var failure = await Assert.ThrowsAsync<TeamsAttachmentDownloadException>(() => download);

        Assert.Equal("body", failure.Stage);
        Assert.Equal(2, handler.CallCount);
        Assert.Empty(Directory.GetFiles(staging.Path));
    }

    [Fact]
    public async Task Body_byte_limit_does_not_retry_an_attachment_without_a_length_header()
    {
        var clock = new FakeTimeProvider();
        using var stream = new PartialThenCancellationStream();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        });
        var downloader = CreateDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);
        using var staging = new DisposableTempDir();

        await Assert.ThrowsAsync<AttachmentTooLargeException>(() => downloader.DownloadAsync(
            activity, CreateAttachment(), staging.Path, 1, TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.CallCount);
        Assert.Empty(Directory.GetFiles(staging.Path));
    }

    private static byte[] LargePng()
    {
        const int size = 1_024;
        var pixels = new byte[size * size * 4];
        new Random(42).NextBytes(pixels);
        for (var index = 3; index < pixels.Length; index += 4)
            pixels[index] = 255;
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private sealed class ProgressGatedStream(byte[] bytes) : MemoryStream(bytes)
    {
        private long _nextGate;
        public System.Threading.Channels.Channel<TaskCompletionSource> Reads { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<TaskCompletionSource>();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= _nextGate && Position < Length)
            {
                _nextGate += (Length + 3) / 4;
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await Reads.Writer.WriteAsync(release, cancellationToken);
                await release.Task.WaitAsync(cancellationToken);
            }

            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 32 * 1_024)], cancellationToken);
        }
    }

    private sealed class GatedPngStream(byte[] bytes) : MemoryStream(bytes)
    {
        private bool _returnedHeader;
        public TaskCompletionSource WaitingForBody { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReadToken { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_returnedHeader)
            {
                _returnedHeader = true;
                return await base.ReadAsync(buffer[..8], cancellationToken);
            }

            ReadToken = cancellationToken;
            WaitingForBody.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class GatedHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("synthetic-bot-token", request.Headers.Authorization?.Parameter);
            Token = cancellationToken;
            Started.SetResult();
            try
            {
                return await Response.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await ReleaseCancellation.Task.WaitAsync(TestContext.Current.CancellationToken);
                throw;
            }
        }
    }

    private sealed class AttachmentLog() : LoggingAdapterBase(SemanticLogMessageFormatter.Instance)
    {
        public List<string> Messages { get; } = [];
        public override bool IsDebugEnabled => true;
        public override bool IsInfoEnabled => true;
        public override bool IsWarningEnabled => true;
        public override bool IsErrorEnabled => true;
        protected override void NotifyLog(Akka.Event.LogLevel logLevel, object message, Exception? cause = null)
        {
            Assert.Null(cause);
            Messages.Add(message.ToString()!);
        }
    }

    private static TeamsInboundActivity CreateActivity(
        string activityId = "activity-a",
        ImmutableArray<TeamsAttachmentMetadata> attachments = default) => new(
        new TeamsIngressTrustContext(
            TrustAudience.Personal,
            PrincipalClassification.TrustedInternal,
            TrustBoundary.Personal,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            "user-a",
            "tenant-a",
            "conversation-a",
            TeamsConversationScope.Personal,
            activityId,
            DateTimeOffset.Parse("2026-09-02T10:00:00Z")),
        "attachment input",
        attachments: attachments.IsDefault ? [CreateAttachment()] : attachments);

    private static TeamsAttachmentMetadata CreateAttachment() => new("image.png", "image/png", 7)
    {
        Kind = TeamsInboundAttachmentKind.InlineImage,
        SourceIndex = 0
    };

    private static TeamsAttachmentMetadata CreateProvisionalAttachment() => new("attachment-1.png", "image/*", 7)
    {
        Kind = TeamsInboundAttachmentKind.InlineImage,
        SourceIndex = 0
    };

    private static ChannelAttachmentPolicy ImageAttachmentPolicy() => new()
    {
        AllowedCategories = [AttachmentCategory.Image],
        MaxFileBytes = 1_024
    };

    private static MessageActivity CreateSdkMessage(string url)
    {
        var message = MessageActivity.FromActivity(CoreActivity.FromJsonString("{\"type\":\"message\"}"));
        message.Attachments =
        [
            new TeamsAttachment
            {
                ContentType = new AttachmentContentType("image/png"),
                ContentUrl = new Uri(url),
                Name = "image.png"
            }
        ];
        return message;
    }

    private static HttpResponseMessage Success(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/octet-stream")
    };

    private static TeamsSdkAttachmentDownloader CreateDownloader(
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider) => CreateDownloader(
        httpClientFactory,
        new RecordingAuthorizationHeaderProvider("Bearer synthetic-bot-token"),
        timeProvider);

    private static TeamsSdkAttachmentDownloader CreateDownloader(
        IHttpClientFactory httpClientFactory,
        RecordingAuthorizationHeaderProvider authorizationHeaders,
        TimeProvider timeProvider) => new(
        httpClientFactory,
        authorizationHeaders,
        new StaticOptionsMonitor<ManagedIdentityOptions>(new ManagedIdentityOptions()),
        timeProvider,
        NullLogger<TeamsSdkAttachmentDownloader>.Instance);

    private static WebApplication BuildBotConnectorDownloadHost(
        RecordingHandler botConnectorHandler,
        RecordingAuthorizationHeaderProvider authorizationHeaders,
        CapturingLoggerProvider logs)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Teams:Enabled"] = "true",
            ["Teams:TenantId"] = "tenant",
            ["Teams:ClientId"] = "client",
            ["Teams:ClientSecret"] = "synthetic-secret"
        });
        builder.Services.AddChannelIntegrations(builder.Configuration);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.AddTeamsIngress();
        builder.Services.RemoveAll<IAuthorizationHeaderProvider>();
        builder.Services.AddSingleton<IAuthorizationHeaderProvider>(authorizationHeaders);
        builder.Services.AddHttpClient("teams-attachments")
            .ConfigurePrimaryHttpMessageHandler(() => botConnectorHandler);

        return builder.Build();
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler attachmentHandler) : IHttpClientFactory
    {
        public List<string> CreatedClientNames { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("teams-attachments", name);
            CreatedClientNames.Add(name);
            return new HttpClient(new TeamsSdkAttachmentDownloader.ResponseStageHandler { InnerHandler = attachmentHandler }, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        Action<HttpRequestMessage>? inspectRequest = null) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUris.Add(request.RequestUri!.AbsoluteUri);
            inspectRequest?.Invoke(request);
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class RecordingAuthorizationHeaderProvider(
        string authorizationHeader,
        Exception? failure = null) : IAuthorizationHeaderProvider
    {
        public List<string> AppScopes { get; } = [];
        public List<string?> AppAuthenticationOptionNames { get; } = [];

        public Task<string> CreateAuthorizationHeaderForUserAsync(
            IEnumerable<string> scopes,
            AuthorizationHeaderProviderOptions? authorizationHeaderProviderOptions = null,
            ClaimsPrincipal? claimsPrincipal = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> CreateAuthorizationHeaderForAppAsync(
            string scopes,
            AuthorizationHeaderProviderOptions? downstreamApiOptions = null,
            CancellationToken cancellationToken = default)
        {
            AppScopes.Add(scopes);
            AppAuthenticationOptionNames.Add(downstreamApiOptions?.AcquireTokenOptions?.AuthenticationOptionsName);
            return failure is null
                ? Task.FromResult(authorizationHeader)
                : Task.FromException<string>(failure);
        }

        public Task<string> CreateAuthorizationHeaderAsync(
            IEnumerable<string> scopes,
            AuthorizationHeaderProviderOptions? options = null,
            ClaimsPrincipal? claimsPrincipal = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StaticOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
        where TOptions : class
    {
        public TOptions CurrentValue => value;

        public TOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

        public void Dispose() { }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(MicrosoftLogLevel logLevel) => logLevel >= MicrosoftLogLevel.Debug;

            public void Log<TState>(
                MicrosoftLogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
        }
    }

    private sealed class PartialThenCancellationStream : Stream
    {
        private bool _returnedInitialBytes;

        public TaskCompletionSource WaitingForCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_returnedInitialBytes)
            {
                _returnedInitialBytes = true;
                buffer.Span[..4].Fill(1);
                return ValueTask.FromResult(4);
            }

            WaitingForCancellation.TrySetResult();
            var pendingRead = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => pendingRead.TrySetCanceled(cancellationToken));
            return new ValueTask<int>(pendingRead.Task);
        }
    }
}
