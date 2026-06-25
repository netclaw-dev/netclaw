// -----------------------------------------------------------------------
// <copyright file="SlackApprovalBlockBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using SlackNet.Blocks;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels.Slack;

internal static class SlackApprovalBlockBuilder
{
    public const string ApprovalActionId = "tool_approval";

    private const string ComplexCommandHint = "_complex command — only one-shot approval available_";

    /// <summary>
    /// Display-text budget sized to stay under Slack's hard 3000-char
    /// SectionBlock text cap after accounting for the surrounding markdown
    /// scaffolding. Exceeding the cap causes Slack to reject the post with
    /// <c>invalid_blocks</c>, which today triggers an auto-deny the model
    /// misreads as the user declining.
    /// </summary>
    internal const int MaxDisplayTextChars = 2500;

    public static string BuildApprovalText(ToolInteractionRequest request)
    {
        var lines = new List<string>
        {
            ":lock: *Tool approval required*",
            $"> `{request.ToolName}`: `{ApprovalDisplayTextFormatter.Truncate(request.DisplayText, MaxDisplayTextChars)}`",
            BuildApproveHeader(request)
        };

        var verbs = ResolveDisplayVerbs(request);
        if (verbs.Count > 1)
        {
            foreach (var verb in verbs)
                lines.Add($"  • `{verb}`");
        }

        if (request.IsMessy)
            lines.Add(ComplexCommandHint);

        AppendAdoptedContextSummary(lines, request);

        lines.Add("");
        lines.Add("Reply with:");
        foreach (var replyOption in EnumerateReplyOptions(request.Options))
            lines.Add($"  *{replyOption.Letter})* {replyOption.Option.Label}");

        return string.Join("\n", lines);
    }

    public static IReadOnlyList<Block> BuildApprovalBlocks(ToolInteractionRequest request)
    {
        var blocks = new List<Block>
        {
            new SectionBlock
            {
                Text = new Markdown(":lock: *Tool approval required*")
            },
            new SectionBlock
            {
                Text = new Markdown($"*Tool:* `{EscapeMarkdown(request.ToolName.Value)}`\n*Request:* `{EscapeMarkdown(ApprovalDisplayTextFormatter.Truncate(request.DisplayText, MaxDisplayTextChars))}`"),
                Expand = true
            },
            new SectionBlock
            {
                Text = new Markdown($"*{EscapeMarkdown(BuildApproveHeader(request))}*")
            }
        };

        var verbs = ResolveDisplayVerbs(request);
        if (verbs.Count > 1)
        {
            var verbLines = verbs.Select(v => $"• `{EscapeMarkdown(v)}`");
            blocks.Add(new SectionBlock
            {
                Text = new Markdown(string.Join("\n", verbLines))
            });
        }

        if (request.IsMessy)
        {
            blocks.Add(new SectionBlock
            {
                Text = new Markdown(ComplexCommandHint)
            });
        }

        if (request.HasAdoptedContext)
        {
            blocks.Add(new SectionBlock
            {
                Text = new Markdown(BuildAdoptedContextMarkdown(request))
            });
        }

        // Slack hard-caps PlainText button text at 76 characters; oversized labels are
        // rejected with `invalid_blocks` and the post fails. Labels MUST come from the
        // fixed `ApprovalOptionKeys` constants — do not interpolate runtime values
        // (paths, commands, tool names) into `option.Label` upstream.
        blocks.Add(new ActionsBlock
        {
            Elements = [.. request.Options
                .Select(option => (IActionElement)new Button
                {
                    ActionId = BuildActionId(option.Key.Value),
                    Text = new PlainText(option.Label),
                    Value = BuildButtonValue(request, option),
                    Style = GetButtonStyle(option.Key.Value),
                    AccessibilityLabel = option.Label
                })]
        });

        blocks.Add(new SectionBlock
        {
            Text = new Markdown($"You can also reply with {FormatReplyLetters(request.Options)} in this thread.")
        });

        return blocks;
    }

    public static string BuildResolvedApprovalText(
        ToolInteractionRequest request,
        string selectedKey,
        string senderId)
    {
        var statusPrefix = selectedKey == ApprovalOptionKeys.Deny
            ? ":no_entry:"
            : ":white_check_mark:";

        return string.Join("\n", new[]
        {
            $"{statusPrefix} *Tool approval resolved* by <@{EscapeMarkdown(senderId)}>",
            $"> `{request.ToolName}`: `{ApprovalDisplayTextFormatter.Truncate(request.DisplayText, MaxDisplayTextChars)}`",
            BuildResolutionLine(request, selectedKey)
        });
    }

    public static IReadOnlyList<Block> BuildResolvedApprovalBlocks(
        ToolInteractionRequest request,
        string selectedKey,
        string senderId)
    {
        var statusPrefix = selectedKey == ApprovalOptionKeys.Deny
            ? ":no_entry:"
            : ":white_check_mark:";
        var resolutionLine = BuildResolutionLine(request, selectedKey);

        var blocks = new List<Block>
        {
            new SectionBlock
            {
                Text = new Markdown($"{statusPrefix} *Tool approval resolved* by <@{EscapeMarkdown(senderId)}>")
            },
            new SectionBlock
            {
                Text = new Markdown(
                    $"*Tool:* `{EscapeMarkdown(request.ToolName.Value)}`\n"
                    + $"*Request:* `{EscapeMarkdown(ApprovalDisplayTextFormatter.Truncate(request.DisplayText, MaxDisplayTextChars))}`\n"
                    + $"*{EscapeMarkdown(resolutionLine)}*"),
                Expand = true
            }
        };

        if (request.HasAdoptedContext)
        {
            blocks.Add(new SectionBlock
            {
                Text = new Markdown(BuildAdoptedContextMarkdown(request))
            });
        }

        return blocks;
    }

    /// <summary>
    /// Builds a resolved-state message body when the original
    /// <see cref="ToolInteractionRequest"/> is no longer available — e.g. the
    /// binding actor was passivated between posting the prompt and the user
    /// clicking a button. The button payload still carries enough state to
    /// redraw the prompt with a decision banner. When the persisted
    /// <see cref="Netclaw.Actors.Channels.PendingApprovalPromptTracked"/>
    /// carried <paramref name="toolName"/> + <paramref name="displayText"/>,
    /// the redraw includes the original tool name and request text; otherwise
    /// it falls back to a generic banner (pre-field journal entries). See
    /// issue #939.
    /// </summary>
    public static string BuildResolvedApprovalTextWithoutRequest(
        string selectedKey,
        string senderId,
        string? toolName = null,
        string? displayText = null)
    {
        // Match the hot-path text variant (BuildResolvedApprovalText) formatting
        // so the same approval renders identically pre-passivation and post-
        // recovery: no escape on the tool/request code-fenced fields, no bold or
        // escape on the resolution line. Block-Kit variants escape because they
        // render inside markdown SectionBlocks; the text variant is the
        // notification body which Slack already treats as code-fence-safe inside
        // the backticks.
        var statusPrefix = selectedKey == ApprovalOptionKeys.Deny
            ? ":no_entry:"
            : ":white_check_mark:";

        var lines = new List<string>(3)
        {
            $"{statusPrefix} *Tool approval resolved* by <@{EscapeMarkdown(senderId)}>"
        };

        if (!string.IsNullOrEmpty(toolName) && !string.IsNullOrEmpty(displayText))
        {
            lines.Add(
                $"> `{toolName}`: `{ApprovalDisplayTextFormatter.Truncate(displayText, MaxDisplayTextChars)}`");
        }

        lines.Add(BuildGenericResolutionLine(selectedKey));

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Block-Kit variant of <see cref="BuildResolvedApprovalTextWithoutRequest"/>.
    /// Renders without buttons so the prompt UI clears on the cold-spawn path.
    /// Includes the persisted tool name + request text when supplied.
    /// </summary>
    public static IReadOnlyList<Block> BuildResolvedApprovalBlocksWithoutRequest(
        string selectedKey,
        string senderId,
        string? toolName = null,
        string? displayText = null)
    {
        var statusPrefix = selectedKey == ApprovalOptionKeys.Deny
            ? ":no_entry:"
            : ":white_check_mark:";

        var blocks = new List<Block>(3)
        {
            new SectionBlock
            {
                Text = new Markdown($"{statusPrefix} *Tool approval resolved* by <@{EscapeMarkdown(senderId)}>")
            }
        };

        if (!string.IsNullOrEmpty(toolName) && !string.IsNullOrEmpty(displayText))
        {
            blocks.Add(new SectionBlock
            {
                Text = new Markdown(
                    $"*Tool:* `{EscapeMarkdown(toolName)}`\n"
                    + $"*Request:* `{EscapeMarkdown(ApprovalDisplayTextFormatter.Truncate(displayText, MaxDisplayTextChars))}`\n"
                    + $"*{EscapeMarkdown(BuildGenericResolutionLine(selectedKey))}*"),
                Expand = true
            });
        }
        else
        {
            blocks.Add(new SectionBlock
            {
                Text = new Markdown($"*{EscapeMarkdown(BuildGenericResolutionLine(selectedKey))}*")
            });
        }

        return blocks;
    }

    private static string BuildGenericResolutionLine(string selectedKey)
        => selectedKey switch
        {
            ApprovalOptionKeys.ApproveAlways => "Saved: always here",
            ApprovalOptionKeys.ApproveEverywhere => "Saved: always anywhere",
            ApprovalOptionKeys.ApproveSession => "Saved for this chat",
            ApprovalOptionKeys.ApproveOnce => "Approved (no save)",
            ApprovalOptionKeys.Deny => "Denied",
            _ => "Resolved"
        };

    /// <summary>
    /// Builds the prompt's header line. Single-verb invocations collapse the
    /// verb into the header (<c>Approve git status in ~/repos/foo/?</c>);
    /// multi-verb invocations use the generic <c>Approve in ~/repos/foo/?</c>
    /// form and let the bullet list below name the verbs.
    /// </summary>
    /// <remarks>
    /// The "in &lt;dir&gt;" portion shows the most meaningful target directory
    /// the operator is being asked to trust. Priority order:
    /// <list type="number">
    /// <item>The single distinct path argument extracted across the
    /// candidates (e.g. <c>cd /repo &amp;&amp; git status</c> shows
    /// <c>/repo</c>).</item>
    /// <item><c>cwd</c> if no path arguments are present.</item>
    /// <item><c>this session</c> when the cwd is the per-session ephemeral
    /// scratch directory — that path won't recur, so calling it out by name
    /// would be misleading.</item>
    /// </list>
    /// </remarks>
    private static string BuildApproveHeader(ToolInteractionRequest request)
    {
        var verbs = ResolveDisplayVerbs(request);
        var location = ResolveHeaderLocation(request);

        return verbs.Count == 1
            ? $"Approve {verbs[0]} in {location}?"
            : $"Approve in {location}?";
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

        // No path arguments — fall back to cwd, but prefer "this session" when
        // the cwd is the session's ephemeral scratch directory so operators
        // know an "Always here" grant would be a no-op.
        if (string.IsNullOrWhiteSpace(request.Cwd))
            return "(no working directory)";

        return IsSessionScratchPath(request.Cwd) ? "this session" : request.Cwd;
    }

    private static bool IsSessionScratchPath(string cwd)
    {
        // Session directories live under "{netclaw-home}/sessions/{id}/" with
        // {id} of the form "<channelId>_<unix-ts>_<random>" or a uuid. We use
        // a path-segment check rather than full path equality so the helper
        // works without access to NetclawPaths.SessionsBaseDirectory.
        var normalized = cwd.Replace('\\', '/');
        return normalized.Contains("/.netclaw/sessions/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Single-line resolution message replacing v1's dual <c>Patterns</c> /
    /// <c>Directory Roots</c> sections. The format mirrors the spec's
    /// "tool-approval-gates: Resolution message single-line format" requirement:
    /// <list type="bullet">
    /// <item><c>Saved: &lt;verbs&gt; in &lt;dir&gt;</c> for <c>Always here</c></item>
    /// <item><c>Saved: &lt;verbs&gt; anywhere</c> for <c>Always anywhere</c></item>
    /// <item><c>Saved for this chat: &lt;verbs&gt; in &lt;dir&gt;</c> for <c>This chat</c></item>
    /// <item><c>Approved (no save)</c> for <c>Once</c></item>
    /// <item><c>Denied</c> for <c>Deny</c></item>
    /// </list>
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

    /// <summary>
    /// Returns the verbs to display in the prompt body / resolution message.
    /// Prefers <see cref="ToolInteractionRequest.CandidateVerbs"/> (the v2
    /// matcher's verb-chain extraction); falls back to <c>Patterns</c> for
    /// legacy callers and for messy commands where the matcher returned
    /// nothing.
    /// </summary>
    private static IReadOnlyList<string> ResolveDisplayVerbs(ToolInteractionRequest request)
        => request.CandidateVerbs.Count > 0
            ? request.CandidateVerbs
            : request.Patterns;

    private static void AppendAdoptedContextSummary(List<string> lines, ToolInteractionRequest request)
    {
        if (!request.HasAdoptedContext)
            return;

        lines.Add($"Adopted context: present ({string.Join(", ", request.AdoptedSpeakerIds)})");
    }

    private static string BuildAdoptedContextMarkdown(ToolInteractionRequest request)
        => $"*Adopted context:* present\n*Speakers:* `{EscapeMarkdown(string.Join(", ", request.AdoptedSpeakerIds))}`";

    private static IEnumerable<(string Letter, ToolInteractionOption Option)> EnumerateReplyOptions(IReadOnlyList<ToolInteractionOption> options)
    {
        for (var i = 0; i < options.Count; i++)
            yield return (GetReplyLetter(i), options[i]);
    }

    private static string FormatReplyLetters(IReadOnlyList<ToolInteractionOption> options)
        => string.Join(", ", EnumerateReplyOptions(options).Select(static x => $"`{x.Letter}`"));

    private static string GetReplyLetter(int index)
        => ((char)('A' + index)).ToString();

    internal static string BuildButtonValue(ToolInteractionRequest request, ToolInteractionOption option)
        => ApprovalButtonValueCodec.Encode(request, option);

    internal static bool TryParseButtonValue(string? value, out string? callId, out string? selectedKey, out string? requesterSenderId)
        => ApprovalButtonValueCodec.TryDecode(value, out callId, out selectedKey, out requesterSenderId);

    private static ButtonStyle GetButtonStyle(string optionKey)
    {
        if (ApprovalOptionKeys.IsDangerStyled(optionKey))
            return ButtonStyle.Danger;
        if (optionKey == ApprovalOptionKeys.ApproveOnce)
            return ButtonStyle.Primary;
        return ButtonStyle.Default;
    }

    internal static bool IsApprovalActionId(string? actionId)
        => !string.IsNullOrWhiteSpace(actionId)
           && actionId.StartsWith($"{ApprovalActionId}_", StringComparison.Ordinal);

    private static string BuildActionId(string optionKey)
        => $"{ApprovalActionId}_{optionKey}";

    private static string EscapeMarkdown(string value)
        => value.Replace("`", "'", StringComparison.Ordinal);
}
