// -----------------------------------------------------------------------
// <copyright file="ConsentAnswer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Security.Authorization.Consent;

namespace Netclaw.Actors.Authorization.Consent;

/// <summary>
/// The operator's answer to one consent request: one retry, a grant with a
/// scope, or a refusal. The parent session and its sub-agents use this one type.
/// </summary>
internal abstract record ConsentAnswer
{
    private ConsentAnswer()
    {
    }

    /// <summary>Gets the refusal for an explicit "Deny".</summary>
    public static Refused Denied { get; } = new(RefusalKind.Denied);

    /// <summary>Gets the refusal for a prompt that nobody answered in time.</summary>
    public static Refused TimedOut { get; } = new(RefusalKind.TimedOut);

    /// <summary>
    /// Gets the refusal for a prompt that the channel could not show. Nobody
    /// saw the prompt, so this is not a user decision.
    /// </summary>
    public static Refused PromptUnavailable { get; } = new(RefusalKind.PromptUnavailable);

    /// <summary>Allow the blocked call one time. Nothing is stored.</summary>
    public sealed record Once : ConsentAnswer
    {
        private Once()
        {
        }

        public static Once Instance { get; } = new();
    }

    /// <summary>
    /// Allow the blocked call and store a grant. The grant builder resolves the
    /// kind to one <see cref="GrantScope"/> for each candidate.
    /// </summary>
    public sealed record Grant(GrantScopeKind Scope) : ConsentAnswer;

    /// <summary>Do not run the blocked call.</summary>
    public sealed record Refused(RefusalKind Kind) : ConsentAnswer;
}

/// <summary>Why the operator did not allow a call.</summary>
internal enum RefusalKind
{
    Denied,
    TimedOut,
    PromptUnavailable,
}

/// <summary>
/// The tool result text for a refused call. The model reads this text, so each
/// reason must be honest: only <see cref="RefusalKind.Denied"/> says that the
/// user declined.
/// </summary>
internal static class ConsentRefusalText
{
    public const string TimedOut = "Tool access denied: approval_timed_out";

    public const string PromptUnavailable =
        "Tool access denied: approval_prompt_unavailable "
        + "(Netclaw could not show the approval prompt in this channel. The call did not run.)";

    /// <summary>Returns the result text for a refusal.</summary>
    /// <param name="kind">Why the call did not run.</param>
    /// <param name="deniedText">The text for an explicit "Deny" answer.</param>
    public static string For(RefusalKind kind, string deniedText) => kind switch
    {
        RefusalKind.TimedOut => TimedOut,
        RefusalKind.PromptUnavailable => PromptUnavailable,
        _ => deniedText,
    };
}

/// <summary>
/// Translates a <see cref="ConsentAnswer"/> to and from the two wire forms that
/// must stay unchanged: the approval button keys and the journal text of
/// <c>ToolApprovalResolved.Decision</c>.
/// </summary>
internal static class ConsentAnswerCodec
{
    // Journal text. These are the names of the retired ApprovalDecision enum,
    // which the session wrote with ToString(). Recovery of an old journal reads
    // them, so the text must never change.
    private const string ApprovedOnceText = "ApprovedOnce";
    private const string ApprovedSessionText = "ApprovedSession";
    private const string ApprovedAlwaysText = "ApprovedAlways";
    private const string ApprovedRepositoryText = "ApprovedRepository";
    private const string ApprovedEverywhereText = "ApprovedEverywhere";
    private const string DeniedText = "Denied";
    private const string TimedOutText = "TimedOut";
    private const string PromptUnavailableText = "PromptUnavailable";

    /// <summary>
    /// Parses a selected option key. It returns false when the prompt did not
    /// offer the key. An offered key that names no approval maps to a refusal.
    /// </summary>
    public static bool TryParseOption(
        string selectedKey,
        IReadOnlyList<string> offeredKeys,
        string? repositoryCommonDirectory,
        out ConsentAnswer answer)
    {
        // SECURITY: no prompt offers this key. Only a channel binding sends it,
        // when it could not post the prompt. It maps to a refusal, so to accept
        // it without an offer cannot allow a call.
        if (selectedKey == ApprovalOptionKeys.PromptUnavailable)
        {
            answer = ConsentAnswer.PromptUnavailable;
            return true;
        }

        // Legacy journal entries lack offered option keys. They cannot prove
        // that the repository scope appeared in the original prompt.
        if (!IsOffered(offeredKeys, selectedKey, repositoryCommonDirectory))
        {
            answer = ConsentAnswer.Denied;
            return false;
        }

        answer = FromOptionKey(selectedKey);
        return true;
    }

    /// <summary>
    /// True when the prompt offered <paramref name="selectedKey"/>. An assignment
    /// variant and the repository scope need an explicit offer; a legacy prompt
    /// with no recorded keys accepts only the earlier keys.
    /// </summary>
    internal static bool IsOffered(
        IReadOnlyList<string> optionKeys,
        string selectedKey,
        string? repositoryCommonDirectory)
        => ApprovalOptionKeys.IsAssignmentVariant(selectedKey)
            ? optionKeys.Contains(selectedKey, StringComparer.Ordinal)
              && (!ApprovalOptionKeys.IsRepository(selectedKey)
                  || repositoryCommonDirectory is not null)
            : ApprovalOptionKeys.IsRepository(selectedKey)
            ? repositoryCommonDirectory is not null
              && optionKeys.Contains(selectedKey, StringComparer.Ordinal)
            : optionKeys.Count == 0 || optionKeys.Contains(selectedKey, StringComparer.Ordinal);

    /// <summary>Maps an option key to its answer. An unknown key fails closed to a refusal.</summary>
    internal static ConsentAnswer FromOptionKey(string optionKey)
        => ApprovalOptionKeys.CanonicalDecisionKey(optionKey) switch
        {
            ApprovalOptionKeys.ApproveOnce => ConsentAnswer.Once.Instance,
            ApprovalOptionKeys.ApproveSession => new ConsentAnswer.Grant(GrantScopeKind.Session),
            ApprovalOptionKeys.ApproveAlways => new ConsentAnswer.Grant(GrantScopeKind.Folder),
            ApprovalOptionKeys.ApproveRepository => new ConsentAnswer.Grant(GrantScopeKind.Repository),
            ApprovalOptionKeys.ApproveEverywhere => new ConsentAnswer.Grant(GrantScopeKind.Everywhere),
            _ => ConsentAnswer.Denied,
        };

    /// <summary>
    /// Adds one line to an approved tool result. The line tells the model
    /// which approval the person gave, so the model can tell the person that
    /// "once" and "this chat only" do not carry over to a later session, for
    /// example a scheduled reminder run. A refusal adds no line, because the
    /// refusal result already states the reason.
    /// </summary>
    public static string AppendResultNote(string resultText, ConsentAnswer answer)
    {
        var note = answer switch
        {
            ConsentAnswer.Once => "[approval: once]",
            ConsentAnswer.Grant { Scope: GrantScopeKind.Session } => "[approval: this chat only]",
            ConsentAnswer.Grant { Scope: GrantScopeKind.Folder } => "[approval: always in this folder]",
            ConsentAnswer.Grant { Scope: GrantScopeKind.Repository } => "[approval: always in this repo]",
            ConsentAnswer.Grant { Scope: GrantScopeKind.Everywhere } => "[approval: always anywhere]",
            _ => null,
        };
        return note is null ? resultText : $"{resultText}\n{note}";
    }

    /// <summary>Formats an answer as its journal text.</summary>
    public static string ToJournalText(ConsentAnswer answer) => answer switch
    {
        ConsentAnswer.Once => ApprovedOnceText,
        ConsentAnswer.Grant { Scope: GrantScopeKind.Session } => ApprovedSessionText,
        ConsentAnswer.Grant { Scope: GrantScopeKind.Folder } => ApprovedAlwaysText,
        ConsentAnswer.Grant { Scope: GrantScopeKind.Repository } => ApprovedRepositoryText,
        ConsentAnswer.Grant { Scope: GrantScopeKind.Everywhere } => ApprovedEverywhereText,
        ConsentAnswer.Refused { Kind: RefusalKind.Denied } => DeniedText,
        ConsentAnswer.Refused { Kind: RefusalKind.TimedOut } => TimedOutText,
        ConsentAnswer.Refused { Kind: RefusalKind.PromptUnavailable } => PromptUnavailableText,
        _ => throw new ArgumentOutOfRangeException(nameof(answer), answer, "Unknown consent answer."),
    };

    /// <summary>
    /// Reads journal text. The comparison ignores case, as the earlier enum
    /// parse did. Unknown text fails closed to a refusal. An earlier binary
    /// reads <c>PromptUnavailable</c> as <c>Denied</c>, which also refuses.
    /// </summary>
    public static ConsentAnswer FromJournalText(string? text)
    {
        if (Is(text, ApprovedOnceText))
            return ConsentAnswer.Once.Instance;
        if (Is(text, ApprovedSessionText))
            return new ConsentAnswer.Grant(GrantScopeKind.Session);
        if (Is(text, ApprovedAlwaysText))
            return new ConsentAnswer.Grant(GrantScopeKind.Folder);
        if (Is(text, ApprovedRepositoryText))
            return new ConsentAnswer.Grant(GrantScopeKind.Repository);
        if (Is(text, ApprovedEverywhereText))
            return new ConsentAnswer.Grant(GrantScopeKind.Everywhere);
        if (Is(text, PromptUnavailableText))
            return ConsentAnswer.PromptUnavailable;
        return Is(text, TimedOutText) ? ConsentAnswer.TimedOut : ConsentAnswer.Denied;

        static bool Is(string? text, string expected)
            => string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);
    }
}
