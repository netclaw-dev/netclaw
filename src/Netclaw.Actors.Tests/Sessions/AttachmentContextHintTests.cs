using Netclaw.Actors.Channels;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Canonical shape assertions for <see cref="SessionMessageAssembler.AttachmentContextHint"/>.
/// The hint is injected into the system prompt of every file_read-granted
/// session and is the agent's only documentation of how to interpret
/// <c>[attachment]</c> announcement lines. Eval regressions usually trace
/// back to drift in this string, so it's pinned here as a bear-trap test.
/// </summary>
public sealed class AttachmentContextHintTests
{
    [Fact]
    public void Hint_names_the_inbox_subdirectory()
    {
        Assert.Contains("inbox/", SessionMessageAssembler.AttachmentContextHint, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Hint_documents_the_inlined_field_and_both_values()
    {
        Assert.Contains("inlined=\"true|false\"", SessionMessageAssembler.AttachmentContextHint, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Hint_explains_the_model_missing_note_class()
    {
        Assert.Contains("current model has no", SessionMessageAssembler.AttachmentContextHint, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Hint_explains_the_format_not_inlineable_note_class()
    {
        Assert.Contains("format not inlineable", SessionMessageAssembler.AttachmentContextHint, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Hint_forbids_silently_ignoring_attachments()
    {
        Assert.Contains("Never silently ignore", SessionMessageAssembler.AttachmentContextHint, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Hint_note_prefixes_match_AttachmentNotes_constants()
    {
        Assert.StartsWith("current model has no image modality", AttachmentNotes.ModelMissingImage, System.StringComparison.Ordinal);
        Assert.StartsWith("current model has no native PDF support", AttachmentNotes.ModelMissingPdf, System.StringComparison.Ordinal);
        Assert.StartsWith("format not inlineable", AttachmentNotes.FormatNotInlineable, System.StringComparison.Ordinal);
    }
}
