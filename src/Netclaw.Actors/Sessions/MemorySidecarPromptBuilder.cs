// -----------------------------------------------------------------------
// <copyright file="MemorySidecarPromptBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Sessions;

public static class MemorySidecarPromptBuilder
{
    internal static string BuildClassificationRules() => """
        Operation-to-class mapping (STRICT — always follow these):
        - durable_fact -> operation MUST be "upsert_document" (stable knowledge, merged on write)
        - evidence -> operation MUST be "append_record" (point-in-time observation, immutable)
        - trace -> operation MUST be "append_record" (execution breadcrumb, immutable)

        Why: evidence captures temporal observations ("PR review findings on March 26",
        "build failure at 2:30pm"). Using upsert_document would silently merge distinct
        observations into one, destroying the historical record. Only durable_fact
        (standing truths like user preferences) should be updatable.
        """;
}
