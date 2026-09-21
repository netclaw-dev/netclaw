// -----------------------------------------------------------------------
// <copyright file="TeamsSdkAttachmentDownloader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Schema;
using Netclaw.Channels;
using Netclaw.Channels.Teams;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Keeps short-lived SDK attachment URLs at the authenticated HTTP boundary.
/// Actors receive only a downloader interface and sanitized attachment metadata.
/// </summary>
internal sealed class TeamsSdkAttachmentDownloader(
    IHttpClientFactory httpClientFactory,
    IAuthorizationHeaderProvider authorizationHeaderProvider,
    IOptionsMonitor<ManagedIdentityOptions> managedIdentityOptions,
    TimeProvider timeProvider,
    ILogger<TeamsSdkAttachmentDownloader> log) : ITeamsAttachmentDownloader
{
    internal static readonly HttpRequestOptionsKey<TeamsAttachmentTransfer> StageObserver = new("TeamsAttachmentStage");
    private const int MaximumCaptures = 1_024;
    private const int MaximumUrlLength = 4_096;
    private static readonly TimeSpan CaptureLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<CaptureKey, CapturedActivity> _captures = new();

    internal void Capture(MessageActivity source, TeamsInboundActivity activity)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(activity);
        if (activity.Attachments.Length == 0 || source.Attachments is null)
            return;

        RemoveExpiredCaptures();
        var captured = new ConcurrentDictionary<int, CapturedAttachment>();
        foreach (var attachment in activity.Attachments)
        {
            if (attachment.Kind == TeamsInboundAttachmentKind.Unknown
                || attachment.SourceIndex < 0
                || attachment.SourceIndex >= source.Attachments.Count)
            {
                continue;
            }

            var sdkAttachment = source.Attachments[attachment.SourceIndex];
            if (sdkAttachment is not null
                && TryGetDownloadUrl(sdkAttachment, attachment.Kind, out var downloadUri))
            {
                captured.TryAdd(attachment.SourceIndex, new CapturedAttachment(
                    downloadUri.AbsoluteUri,
                    RequiresBotConnectorAuthentication(downloadUri)));
            }
        }

        if (captured.IsEmpty)
            return;

        while (_captures.Count >= MaximumCaptures)
        {
            var oldest = _captures.FirstOrDefault();
            if (oldest.Key == default || !_captures.TryRemove(oldest.Key, out _))
                break;
        }

        _captures[CaptureKey.Create(activity)] = new CapturedActivity(
            timeProvider.GetUtcNow() + CaptureLifetime,
            captured);
    }

    public async Task<AttachmentDownloadResult> DownloadAsync(
        TeamsInboundActivity activity,
        TeamsAttachmentMetadata attachment,
        string stagingDirectory,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(attachment);
        if (!_captures.TryGetValue(CaptureKey.Create(activity), out var captured)
            || captured.ExpiresAt <= timeProvider.GetUtcNow()
            || !captured.Attachments.TryRemove(attachment.SourceIndex, out var source))
        {
            throw new InvalidDataException("The authenticated Teams attachment download is unavailable.");
        }

        if (captured.Attachments.IsEmpty)
            _captures.TryRemove(CaptureKey.Create(activity), out _);

        var hostClass = source.RequiresBotConnectorAuthentication ? "bot_connector" : "signed_url";
        var authenticated = false;
        var stage = "token";
        try
        {
            var authorizationHeader = source.RequiresBotConnectorAuthentication
                ? await CreateBotConnectorAuthorizationHeaderAsync(cancellationToken).ConfigureAwait(false)
                : null;
            using var client = httpClientFactory.CreateClient("teams-attachments");
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var transfer = new TeamsAttachmentTransfer(timeProvider);
                stage = "request";
                try
                {
                    var result = await StreamingAttachmentDownloader.DownloadToFileAsync(
                        client,
                        source.DownloadUrl,
                        configureRequest: request =>
                        {
                            request.Options.Set(StageObserver, transfer);
                            if (authorizationHeader is not null)
                            {
                                request.Headers.Authorization = CreateBearerHeader(authorizationHeader);
                                authenticated = true;
                            }
                        },
                        targetDirectory: stagingDirectory,
                        maxBytes: maximumBytes,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    RecordAttempt("completed");
                    return result;
                }
                catch (TeamsAttachmentReadTimeoutException) when (attempt == 1 && !cancellationToken.IsCancellationRequested)
                {
                    // Retry only a body idle timeout. Both attempts share the caller's absolute deadline.
                    RecordAttempt("body-idle-retry");
                }
                catch (TeamsAttachmentReadTimeoutException)
                {
                    RecordAttempt("body-idle-exhausted");
                    throw;
                }
                catch
                {
                    RecordAttempt("failed");
                    throw;
                }
                finally
                {
                    stage = transfer.Stage;
                }

                void RecordAttempt(string outcome) => log.LogInformation(
                    "attachment_transfer outcome={Outcome} attempt={Attempt} host_class={HostClass} authenticated={Authenticated} stage={Stage} bytes_received={BytesReceived} content_length={ContentLength} elapsed_ms={ElapsedMs} headers_ms={HeadersMs} last_read_age_ms={LastReadAgeMs}",
                    outcome, attempt, hostClass, authenticated, transfer.Stage, transfer.BytesRead,
                    transfer.ContentLength, transfer.ElapsedMilliseconds, transfer.HeadersMilliseconds, transfer.LastReadAgeMilliseconds);
            }
        }
        catch (AttachmentTooLargeException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // SDK and HTTP exceptions can contain credentials or URLs. Export only these bounded facts.
            throw new TeamsAttachmentDownloadException(
                hostClass, authenticated, stage,
                exception is OperationCanceledException || cancellationToken.IsCancellationRequested,
                exception is HttpRequestException,
                exception is TeamsAttachmentReadTimeoutException);
        }
    }

    internal sealed class ResponseStageHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.Options.TryGetValue(StageObserver, out var transfer))
                transfer.Observe(response);
            return response;
        }
    }

    private async Task<string> CreateBotConnectorAuthorizationHeaderAsync(CancellationToken cancellationToken)
    {
        var options = new AuthorizationHeaderProviderOptions
        {
            AcquireTokenOptions = new AcquireTokenOptions
            {
                AuthenticationOptionsName = TeamsActivityEndpointExtensions.AuthenticationScheme
            }
        };
        var managedIdentity = managedIdentityOptions.Get(TeamsActivityEndpointExtensions.AuthenticationScheme);
        if (!string.IsNullOrEmpty(managedIdentity.UserAssignedClientId))
            options.AcquireTokenOptions.ManagedIdentity = managedIdentity;

        var authorizationHeader = await authorizationHeaderProvider.CreateAuthorizationHeaderForAppAsync(
            "https://api.botframework.com/.default",
            options,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            throw new InvalidDataException(
                "The authenticated Teams attachment download is unavailable.");
        }

        return authorizationHeader;
    }

    private static AuthenticationHeaderValue CreateBearerHeader(string authorizationHeader)
    {
        var token = authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader["Bearer ".Length..]
            : authorizationHeader;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidDataException(
                "The authenticated Teams attachment download is unavailable.");
        }

        return new AuthenticationHeaderValue("Bearer", token);
    }

    private void RemoveExpiredCaptures()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var entry in _captures)
        {
            if (entry.Value.ExpiresAt <= now)
                _captures.TryRemove(entry.Key, out _);
        }
    }

    private static bool TryGetDownloadUrl(
        TeamsAttachment attachment,
        TeamsInboundAttachmentKind kind,
        out Uri downloadUri)
    {
        downloadUri = null!;
        var candidate = kind == TeamsInboundAttachmentKind.PersonalFile
            ? TryGetPersonalDownloadUrl(attachment.Content) ?? attachment.ContentUrl?.ToString()
            : attachment.ContentUrl?.ToString();
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Length > MaximumUrlLength
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !IsTrustedAttachmentHost(uri.Host))
        {
            return false;
        }

        downloadUri = uri;
        return true;
    }

    private static bool RequiresBotConnectorAuthentication(Uri uri)
        => uri.Host.Equals("smba.trafficmanager.net", StringComparison.OrdinalIgnoreCase)
           || uri.Host.EndsWith(".botframework.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrustedAttachmentHost(string host)
        => host.EndsWith(".teams.microsoft.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".botframework.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("smba.trafficmanager.net", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".sharepoint.us", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".onedrive.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("onedrive.live.com", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetPersonalDownloadUrl(object? content)
    {
        if (content is JsonElement { ValueKind: JsonValueKind.Object } json
            && json.TryGetProperty("downloadUrl", out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private readonly record struct CaptureKey(
        string TenantId,
        string ConversationId,
        string ActivityId,
        TeamsConversationScope Scope)
    {
        public static CaptureKey Create(TeamsInboundActivity activity) => new(
            activity.Trust.TenantId,
            activity.Trust.ConversationId,
            activity.Trust.ActivityId,
            activity.Trust.Scope);
    }

    private sealed record CapturedActivity(
        DateTimeOffset ExpiresAt,
        ConcurrentDictionary<int, CapturedAttachment> Attachments);

    private sealed record CapturedAttachment(string DownloadUrl, bool RequiresBotConnectorAuthentication);
}
