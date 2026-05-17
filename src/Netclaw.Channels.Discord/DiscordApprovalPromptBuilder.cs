// -----------------------------------------------------------------------
// <copyright file="DiscordApprovalPromptBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Discord;

internal static class DiscordApprovalPromptBuilder
{
    private const string ComplexCommandHint = "_complex command — only one-shot approval available_";

    public static string BuildTextPrompt(ToolInteractionRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Netclaw approval required:");
        sb.Append("Tool: ").AppendLine(request.ToolName.Value);
        sb.Append("Action: ").AppendLine(request.DisplayText);
        sb.AppendLine(BuildApproveHeader(request));

        var verbs = ResolveDisplayVerbs(request);
        if (verbs.Count > 1)
        {
            foreach (var verb in verbs)
                sb.Append("  • ").AppendLine(verb);
        }

        if (request.IsMessy)
            sb.AppendLine(ComplexCommandHint);

        AppendAdoptedContextSummary(sb, request);

        sb.AppendLine();
        sb.AppendLine("Reply with:");
        AppendReplyOptions(sb, request.Options);
        return sb.ToString().TrimEnd();
    }

    public static (string Text, IReadOnlyList<DiscordButtonSpec> Buttons) BuildButtonPrompt(
        ToolInteractionRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine(":lock: **Tool approval required**");
        AppendToolSummary(sb, request);

        sb.AppendLine();
        sb.Append("You can also reply with ").Append(FormatReplyLetters(request.Options)).Append(" in this thread.");

        // Discord hard-caps button labels at 80 characters. Labels MUST come from the
        // fixed `ApprovalOptionKeys` constants — do not interpolate runtime values
        // (paths, commands, tool names) into `option.Label` upstream.
        var buttons = request.Options
            .Select(option => new DiscordButtonSpec(
                CustomId: BuildButtonValue(request, option),
                Label: option.Label,
                Style: GetButtonStyle(option.Key.Value)))
            .ToList();

        return (sb.ToString().TrimEnd(), buttons);
    }

    public static string BuildDecisionStatus(string selectedKey)
    {
        var label = GetDecisionLabel(selectedKey);
        return $"Recorded approval decision: {label}.";
    }

    public static string BuildResolvedPromptText(
        ToolInteractionRequest request,
        string selectedKey,
        string senderId)
    {
        var statusEmoji = selectedKey == ApprovalOptionKeys.Deny
            ? ":no_entry:"
            : ":white_check_mark:";

        var sb = new StringBuilder();
        sb.Append(statusEmoji).AppendLine(" **Tool approval resolved**");
        sb.Append("**Tool:** `").Append(request.ToolName).AppendLine("`");
        sb.Append("**Action:** `").Append(request.DisplayText).AppendLine("`");
        sb.Append("**").Append(BuildResolutionLine(request, selectedKey)).Append("**");
        sb.Append(" (by <@").Append(senderId).Append(">)");

        if (request.HasAdoptedContext)
        {
            sb.AppendLine();
            sb.Append("**Adopted context:** present").AppendLine();
            sb.Append("**Speakers:** `").Append(string.Join(", ", request.AdoptedSpeakerIds)).Append('`');
        }

        return sb.ToString();
    }

    private static void AppendToolSummary(StringBuilder sb, ToolInteractionRequest request)
    {
        sb.Append("**Tool:** `").Append(request.ToolName).AppendLine("`");
        sb.Append("**Action:** `").Append(request.DisplayText).AppendLine("`");
        sb.Append("**").Append(BuildApproveHeader(request)).AppendLine("**");

        var verbs = ResolveDisplayVerbs(request);
        if (verbs.Count > 1)
        {
            foreach (var verb in verbs)
                sb.Append("  • `").Append(verb).AppendLine("`");
        }

        if (request.IsMessy)
            sb.AppendLine(ComplexCommandHint);

        AppendAdoptedContextSummary(sb, request);
    }

    /// <summary>
    /// Header line mirroring the Slack builder. The "in &lt;dir&gt;" portion
    /// shows the most meaningful target directory the operator is being
    /// asked to trust — see SlackApprovalBlockBuilder.BuildApproveHeader
    /// for the priority order.
    /// </summary>
    private static string BuildApproveHeader(ToolInteractionRequest request)
    {
        var verbs = ResolveDisplayVerbs(request);
        var location = ResolveHeaderLocation(request);

        return verbs.Count == 1
            ? $"Approve {verbs[0]} in {location}?"
            : $"Approve in {location}?";
    }

    /// <summary>
    /// Single-line resolution message identical in form to the Slack builder
    /// — see SlackApprovalBlockBuilder.BuildResolutionLine for the spec
    /// reference.
    /// </summary>
    private static string BuildResolutionLine(ToolInteractionRequest request, string selectedKey)
    {
        var verbs = string.Join(", ", ResolveDisplayVerbs(request));
        var location = ResolveHeaderLocation(request);

        return selectedKey switch
        {
            ApprovalOptionKeys.ApproveAlways => $"Saved: {verbs} in {location}",
            ApprovalOptionKeys.ApproveEverywhere => $"Saved: {verbs} anywhere",
            ApprovalOptionKeys.ApproveSession => $"Saved for this chat: {verbs} in {location}",
            ApprovalOptionKeys.ApproveOnce => "Approved (no save)",
            ApprovalOptionKeys.Deny => "Denied",
            _ => "Resolved"
        };
    }

    private static string ResolveHeaderLocation(ToolInteractionRequest request)
    {
        var distinctDirs = request.Candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Directory))
            .Select(c => c.Directory!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (distinctDirs.Count == 1)
            return distinctDirs[0];

        if (distinctDirs.Count > 1)
            return $"{distinctDirs.Count} directories";

        if (string.IsNullOrWhiteSpace(request.Cwd))
            return "(no working directory)";

        return IsSessionScratchPath(request.Cwd) ? "this session" : request.Cwd;
    }

    private static bool IsSessionScratchPath(string cwd)
    {
        var normalized = cwd.Replace('\\', '/');
        return normalized.Contains("/.netclaw/sessions/", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ResolveDisplayVerbs(ToolInteractionRequest request)
        => request.CandidateVerbs.Count > 0
            ? request.CandidateVerbs
            : request.Patterns;

    private static void AppendAdoptedContextSummary(StringBuilder sb, ToolInteractionRequest request)
    {
        if (!request.HasAdoptedContext)
            return;

        sb.Append("**Adopted context:** present").AppendLine();
        sb.Append("**Speakers:** `").Append(string.Join(", ", request.AdoptedSpeakerIds)).AppendLine("`");
    }

    private static void AppendReplyOptions(StringBuilder sb, IReadOnlyList<ToolInteractionOption> options)
    {
        for (var i = 0; i < options.Count; i++)
            sb.Append(GetReplyLetter(i)).Append(") ").AppendLine(options[i].Label);
    }

    private static string FormatReplyLetters(IReadOnlyList<ToolInteractionOption> options)
        => string.Join(", ", Enumerable.Range(0, options.Count).Select(i => $"`{GetReplyLetter(i)}`"));

    private static string GetReplyLetter(int index)
        => ((char)('A' + index)).ToString();

    private static string GetDecisionLabel(string selectedKey)
        => selectedKey switch
        {
            ApprovalOptionKeys.ApproveOnce => ApprovalOptionKeys.ApproveOnceLabel,
            ApprovalOptionKeys.ApproveSession => ApprovalOptionKeys.ApproveSessionLabel,
            ApprovalOptionKeys.ApproveAlways => ApprovalOptionKeys.ApproveAlwaysLabel,
            ApprovalOptionKeys.ApproveEverywhere => ApprovalOptionKeys.ApproveEverywhereLabel,
            ApprovalOptionKeys.Deny => ApprovalOptionKeys.DenyLabel,
            _ => selectedKey
        };

    internal static string BuildButtonValue(ToolInteractionRequest request, ToolInteractionOption option)
        => ApprovalButtonValueCodec.Encode(request, option);

    internal static bool TryParseButtonValue(string? value, out string? callId, out string? selectedKey, out string? requesterSenderId)
        => ApprovalButtonValueCodec.TryDecode(value, out callId, out selectedKey, out requesterSenderId);

    private static DiscordButtonStyle GetButtonStyle(string optionKey)
    {
        if (ApprovalOptionKeys.IsDangerStyled(optionKey))
            return DiscordButtonStyle.Danger;
        if (optionKey == ApprovalOptionKeys.ApproveOnce)
            return DiscordButtonStyle.Success;
        return DiscordButtonStyle.Secondary;
    }
}
