// -----------------------------------------------------------------------
// <copyright file="ApprovalsListView.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Serialization;
using Netclaw.Configuration;

namespace Netclaw.Cli.Approvals;

/// <summary>
/// Stable JSON output shape for <c>netclaw approvals list --json</c>. Uses the
/// same audience/tool/entries layout as <c>tool-approvals.json</c> so scripts
/// can reuse parsers across both surfaces. Entries preserve the typed
/// <see cref="ApprovalEntry"/> shape (<c>verb</c> + nullable <c>directory</c>).
/// </summary>
internal sealed class ApprovalsListView
{
    [JsonPropertyName("audiences")]
    public SortedDictionary<string, SortedDictionary<string, List<ApprovalEntry>>> Audiences { get; }
        = new(StringComparer.Ordinal);
}
