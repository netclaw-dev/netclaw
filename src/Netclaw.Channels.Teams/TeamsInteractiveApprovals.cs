// -----------------------------------------------------------------------
// <copyright file="TeamsInteractiveApprovals.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels.Teams;

/// <summary>
/// Builds the SDK-free Adaptive Card contract for Teams tool approvals.
/// Descriptive request fields never become callback authorization data.
/// </summary>
public static class TeamsApprovalCardRenderer
{
    internal const int MaxOptionCount = 16;
    internal const int MaxOptionLabelLength = 128;
    internal const int MaxToolNameChars = 128;
    internal const int MaxRequestDisplayChars = 2_048;
    internal const int MaxSummaryChars = 512;
    internal const string ApprovalWindowDescription = "15 minutes";

    public static TeamsApprovalCard CreatePending(
        ToolInteractionRequest request,
        string correlationId,
        string nonce)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TeamsApprovalAction.IsBoundedOpaqueValue(correlationId, TeamsApprovalAction.MaxCorrelationLength))
            throw new ArgumentException("The approval correlation must be bounded and opaque.", nameof(correlationId));
        if (!TeamsApprovalAction.IsBoundedOpaqueValue(nonce, TeamsApprovalAction.MaxNonceLength))
            throw new ArgumentException("The approval nonce must be bounded and opaque.", nameof(nonce));

        ValidateOptions(request.Options);
        var fields = BuildPendingFields(request);
        var card = new TeamsApprovalCard(
            "Approval Required",
            BuildPendingBody(fields, request),
            request.Options
                .Select(option => new TeamsApprovalCardAction(
                    option.Label,
                    option.Key.Value,
                    correlationId,
                    nonce,
                    GetActionStyle(option.Key.Value)))
                .ToArray(),
            TeamsApprovalCardTone.Accent)
        {
            IconName = "ShieldLock",
            Banner = "Netclaw wants to run a command or tool operation.",
            Fields = fields,
            Summary = BuildPendingSummary(request),
            Speak = "Approval required for a Netclaw tool operation."
        };
        EnsureBounded(card);
        return card;
    }

    /// <summary>
    /// Creates the elevated visual variant without choosing when it applies.
    /// The caller must supply a canonical transport-neutral risk signal.
    /// </summary>
    public static TeamsApprovalCard CreateElevatedPending(
        ToolInteractionRequest request,
        string correlationId,
        string nonce,
        string riskLevel,
        string impact)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(riskLevel) || string.IsNullOrWhiteSpace(impact))
            throw new ArgumentException("The elevated card requires canonical risk and impact text.");

        var card = CreatePending(request, correlationId, nonce) with
        {
            Title = "Elevated Approval Required",
            Tone = TeamsApprovalCardTone.Warning,
            IconName = "Warning",
            Banner = "Netclaw detected a potentially destructive action.",
            Speak = "Elevated approval required for a potentially destructive Netclaw operation.",
            Fields =
            [
                .. BuildPrimaryFields(request),
                new TeamsApprovalCardField("Risk Level", Truncate(riskLevel, MaxSummaryChars)),
                new TeamsApprovalCardField("Impact", Truncate(impact, MaxSummaryChars))
            ]
        };
        EnsureBounded(card);
        return card;
    }

    public static IReadOnlyList<string> GetOfferedOptionKeys(ToolInteractionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOptions(request.Options);
        return request.Options.Select(static option => option.Key.Value).ToArray();
    }

    public static TeamsApprovalCard CreateGranted(
        string toolName,
        string requestDisplayText,
        string selectedKey,
        DateTimeOffset approvedAt,
        bool isMcpTool = false,
        string? operatorDisplayName = null)
    {
        var fields = BuildPrimaryFields(toolName, requestDisplayText, isMcpTool)
            .Append(new TeamsApprovalCardField("Approved By", PresenterLabel(operatorDisplayName)))
            .Append(new TeamsApprovalCardField("Approval Scope", ApprovalScopeDescription(selectedKey, isMcpTool)))
            .Append(new TeamsApprovalCardField("Approved At", FormatTimestamp(approvedAt)))
            .Append(new TeamsApprovalCardField("Execution State", "Execution Approved"))
            .ToArray();
        return CreateTerminalCard(
            "Approval Granted",
            "ShieldCheckmark",
            TeamsApprovalCardTone.Good,
            "Approval was recorded. The requested operation is authorized.",
            "STATUS: EXECUTION AUTHORIZED",
            "Approval granted. The requested operation is authorized but has not been confirmed as executed.",
            fields);
    }

    public static TeamsApprovalCard CreateDenied(
        string toolName,
        string requestDisplayText,
        DateTimeOffset deniedAt,
        bool isMcpTool = false,
        string? operatorDisplayName = null)
    {
        var fields = BuildPrimaryFields(toolName, requestDisplayText, isMcpTool)
            .Append(new TeamsApprovalCardField("Denied By", PresenterLabel(operatorDisplayName)))
            .Append(new TeamsApprovalCardField("Denied At", FormatTimestamp(deniedAt)))
            .Append(new TeamsApprovalCardField("Reason", "User rejected the request"))
            .ToArray();
        return CreateTerminalCard(
            "Approval Denied",
            "ShieldDismiss",
            TeamsApprovalCardTone.Attention,
            "The request was rejected. The command or operation was not executed.",
            "STATUS: EXECUTION BLOCKED",
            "Approval denied. The request was rejected and was not executed.",
            fields);
    }

    public static TeamsApprovalCard CreateExpired(
        string toolName,
        string requestDisplayText,
        DateTimeOffset expiredAt,
        bool isMcpTool = false)
    {
        var fields = BuildPrimaryFields(toolName, requestDisplayText, isMcpTool)
            .Append(new TeamsApprovalCardField("Approval Window", ApprovalWindowDescription))
            .Append(new TeamsApprovalCardField("Expired At", FormatTimestamp(expiredAt)))
            .ToArray();
        return CreateTerminalCard(
            "Approval Card Expired",
            "ClockDismiss",
            TeamsApprovalCardTone.Warning,
            "This approval card expired before a decision was received. No approval decision was recorded. A replacement approval card was issued.",
            "STATUS: NO DECISION RECORDED",
            "Approval card expired. No approval decision was recorded. A replacement card was issued.",
            fields);
    }

    public static TeamsApprovalCard CreateTerminal(string message)
        => CreateTerminal(string.Empty, string.Empty, message);

    public static TeamsApprovalCard CreateTerminal(
        string toolName,
        string requestDisplayText,
        string message,
        bool isMcpTool = false)
    {
        if (string.Equals(message, "Denied.", StringComparison.Ordinal))
            return CreateDenied(toolName, requestDisplayText, DateTimeOffset.UnixEpoch, isMcpTool);

        if (string.Equals(message, "This approval has expired.", StringComparison.Ordinal))
            return CreateExpired(toolName, requestDisplayText, DateTimeOffset.UnixEpoch, isMcpTool);

        if (message.StartsWith("Approved:", StringComparison.Ordinal))
        {
            return CreateGranted(
                toolName,
                requestDisplayText,
                ApprovalOptionKeys.ApproveOnce,
                DateTimeOffset.UnixEpoch,
                isMcpTool);
        }

        var unavailable = string.Equals(message, "This approval is no longer available.", StringComparison.Ordinal);
        var title = unavailable ? "Approval Unavailable" : "Approval Already Resolved";
        var footer = unavailable ? "STATUS: ACTION UNAVAILABLE" : "STATUS: ACTION ALREADY PROCESSED";
        var fields = BuildPrimaryFields(toolName, requestDisplayText, isMcpTool);
        return CreateTerminalCard(
            title,
            unavailable ? "Warning" : "Info",
            TeamsApprovalCardTone.Default,
            Truncate(message, MaxSummaryChars),
            footer,
            Truncate(message, MaxSummaryChars),
            fields);
    }

    public static string CreateNonce()
        => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string CreateCorrelationId()
        => Base64Url(RandomNumberGenerator.GetBytes(24));

    public static string HashNonce(string nonce)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)));
    }

    public static bool NonceMatches(string expectedHash, string nonce)
    {
        if (string.IsNullOrWhiteSpace(expectedHash)
            || !TeamsApprovalAction.IsBoundedOpaqueValue(nonce, TeamsApprovalAction.MaxNonceLength))
        {
            return false;
        }

        var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
        try
        {
            var expected = Convert.FromHexString(expectedHash);
            return expected.Length == candidate.Length
                && CryptographicOperations.FixedTimeEquals(expected, candidate);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static TeamsApprovalCard CreateTerminalCard(
        string title,
        string iconName,
        TeamsApprovalCardTone tone,
        string banner,
        string footer,
        string speak,
        IReadOnlyList<TeamsApprovalCardField> fields)
    {
        var card = new TeamsApprovalCard(title, banner, [], tone)
        {
            IconName = iconName,
            Banner = banner,
            Footer = footer,
            Fields = fields,
            Speak = speak,
            Summary = banner
        };
        EnsureBounded(card);
        return card;
    }

    private static void EnsureBounded(TeamsApprovalCard card)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(card).Length > TeamsApprovalCard.MaxSerializedBytes)
            throw new InvalidOperationException("The Teams approval card exceeds the outbound payload limit.");
    }

    private static string Truncate(string value, int maximumLength)
        => value.Length <= maximumLength ? value : value[..maximumLength];

    private static string PresenterLabel(string? operatorDisplayName) =>
        TeamsApprovalAction.NormalizeOperatorDisplayName(operatorDisplayName) ?? "Authorized operator";

    private static TeamsApprovalCardField[] BuildPendingFields(ToolInteractionRequest request)
    {
        var fields = BuildPrimaryFields(request).ToList();
        var candidates = request.CandidateVerbs.Count > 0 ? request.CandidateVerbs : request.Patterns;
        if (candidates.Count > 0)
        {
            fields.Add(new TeamsApprovalCardField(
                "Candidates",
                ApprovalDisplayTextFormatter.Truncate(string.Join(", ", candidates), MaxSummaryChars)));
        }

        if (!string.IsNullOrWhiteSpace(request.Cwd))
        {
            fields.Add(new TeamsApprovalCardField(
                "Working Directory",
                ApprovalDisplayTextFormatter.Truncate(request.Cwd, MaxSummaryChars)));
        }

        return [.. fields];
    }

    private static IReadOnlyList<TeamsApprovalCardField> BuildPrimaryFields(ToolInteractionRequest request)
        => BuildPrimaryFields(request.ToolName.Value, request.DisplayText, request.ToolName.IsMcp);

    private static TeamsApprovalCardField[] BuildPrimaryFields(
        string toolName,
        string requestDisplayText,
        bool isMcpTool)
    {
        if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(requestDisplayText))
            return [];

        return
        [
            new TeamsApprovalCardField("Tool", Truncate(toolName, MaxToolNameChars)),
            new TeamsApprovalCardField(RequestLabel(toolName, isMcpTool), Truncate(requestDisplayText, MaxRequestDisplayChars))
        ];
    }

    private static string BuildPendingBody(
        IReadOnlyList<TeamsApprovalCardField> fields,
        ToolInteractionRequest request)
    {
        var lines = fields.Select(field => $"{field.Label}: {field.Value}").ToList();
        if (request.IsMessy)
            lines.Add("Reusable approval is unavailable for this request.");
        if (request.HasAdoptedContext)
        {
            lines.Add("Adopted context: present.");
            if (request.AdoptedSpeakerIds.Count > 0)
            {
                lines.Add(
                    $"Speakers: {ApprovalDisplayTextFormatter.Truncate(string.Join(", ", request.AdoptedSpeakerIds), MaxSummaryChars)}");
            }
        }

        return string.Join("\n", lines);
    }

    private static string? BuildPendingSummary(ToolInteractionRequest request)
    {
        var lines = new List<string>();
        if (request.IsMessy)
            lines.Add("Reusable approval is unavailable for this request.");
        if (request.HasAdoptedContext)
        {
            lines.Add("Adopted context: present.");
            if (request.AdoptedSpeakerIds.Count > 0)
            {
                lines.Add(
                    $"Speakers: {ApprovalDisplayTextFormatter.Truncate(string.Join(", ", request.AdoptedSpeakerIds), MaxSummaryChars)}");
            }
        }

        return lines.Count is 0 ? null : string.Join("\n", lines);
    }

    private static string RequestLabel(string toolName, bool isMcpTool) =>
        isMcpTool ? "Invocation" : string.Equals(toolName, "shell_execute", StringComparison.Ordinal) ? "Command" : "Request";

    private static string ApprovalScopeDescription(string selectedKey, bool isMcpTool) => selectedKey switch
    {
        ApprovalOptionKeys.ApproveOnce => "One-time approval",
        ApprovalOptionKeys.ApproveSession => "Session approval",
        ApprovalOptionKeys.ApproveAlways => "Always here",
        ApprovalOptionKeys.ApproveEverywhere => ApprovalOptionKeys.LabelFor(selectedKey, isMcpTool),
        _ => ApprovalOptionKeys.LabelFor(selectedKey, isMcpTool)
    };

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static void ValidateOptions(IReadOnlyList<ToolInteractionOption> options)
    {
        if (options.Count is 0 or > MaxOptionCount)
            throw new ArgumentException("The approval option count is invalid.", nameof(options));

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options)
        {
            if (!TeamsApprovalAction.IsSupportedAction(option.Key.Value)
                || !keys.Add(option.Key.Value)
                || string.IsNullOrWhiteSpace(option.Label)
                || option.Label.Length > MaxOptionLabelLength
                || option.Label.Any(char.IsControl))
            {
                throw new ArgumentException("The approval options are invalid.", nameof(options));
            }
        }
    }

    private static TeamsApprovalActionStyle GetActionStyle(string optionKey)
    {
        if (optionKey == ApprovalOptionKeys.Deny)
            return TeamsApprovalActionStyle.Destructive;

        return optionKey == ApprovalOptionKeys.ApproveOnce
            ? TeamsApprovalActionStyle.Positive
            : TeamsApprovalActionStyle.Default;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
