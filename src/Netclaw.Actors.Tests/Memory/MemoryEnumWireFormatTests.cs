// -----------------------------------------------------------------------
// <copyright file="MemoryEnumWireFormatTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Memory;

/// <summary>
/// Pass 7e guards: the memory/checkpoint records carry typed enums in memory
/// but must keep their snake/kebab-case discriminator strings on the JSON wire
/// so already-persisted documents and live sidecar output stay readable.
/// </summary>
public sealed class MemoryEnumWireFormatTests
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void MemoryProposal_deserializes_wire_strings_into_typed_enums()
    {
        const string wireJson = """
            {
              "operation": "upsert_document",
              "memoryClass": "durable_fact",
              "subjectKind": "project",
              "subjectValue": "netclaw",
              "anchor": { "canonicalName": "deploy-region", "anchorType": "project" },
              "title": "Deployment region",
              "content": "Netclaw deploys in us-east-2.",
              "aliases": ["deployment region"],
              "facets": ["project_fact"],
              "recallMode": "auto",
              "sensitivity": "secret",
              "confidence": 0.9
            }
            """;

        var proposal = JsonSerializer.Deserialize<MemoryProposal>(wireJson, ParseOptions)!;

        Assert.Equal(MemoryProposalOperation.UpsertDocument, proposal.Operation);
        Assert.Equal(MemoryClass.DurableFact, proposal.MemoryClass);
        Assert.Equal(MemoryRecallMode.Auto, proposal.RecallMode);
        Assert.Equal(MemorySensitivity.Secret, proposal.Sensitivity);
        // SubjectKind stays a free-form string — the wire value "project" has no
        // matching SubjectKind enum member and must not be lost.
        Assert.Equal("project", proposal.SubjectKind);
    }

    [Fact]
    public void MemoryProposal_serializes_enums_back_to_wire_strings()
    {
        var proposal = new MemoryProposal(
            MemoryProposalOperation.AppendRecord,
            MemoryClass.Evidence,
            "event",
            "travel",
            new MemoryAnchor("trip", "event"),
            "Hotel options",
            "Found hotels.",
            ["hotel"],
            ["trip_planning"],
            null,
            null,
            MemoryRecallMode.Searchable,
            MemorySensitivity.Normal,
            0.8,
            null,
            null,
            null,
            null);

        var json = JsonSerializer.Serialize(proposal);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("append_record", root.GetProperty("Operation").GetString());
        Assert.Equal("evidence", root.GetProperty("MemoryClass").GetString());
        Assert.Equal("searchable", root.GetProperty("RecallMode").GetString());
        Assert.Equal("normal", root.GetProperty("Sensitivity").GetString());
    }

    [Fact]
    public void MemoryProposal_round_trips_byte_identically()
    {
        var original = new MemoryProposal(
            MemoryProposalOperation.UpsertDocument,
            MemoryClass.Trace,
            "user",
            "self",
            new MemoryAnchor("trace-step", "event"),
            "Trace",
            "Called a tool.",
            null,
            null,
            null,
            null,
            MemoryRecallMode.Never,
            MemorySensitivity.Secret,
            0.6,
            123L,
            456L,
            "identity_profile",
            "trace rationale");

        var firstPass = JsonSerializer.Serialize(original);
        var rehydrated = JsonSerializer.Deserialize<MemoryProposal>(firstPass, ParseOptions)!;
        var secondPass = JsonSerializer.Serialize(rehydrated);

        Assert.Equal(firstPass, secondPass);
        Assert.Equal(original, rehydrated);
    }

    [Fact]
    public void MemoryProposal_deserializes_unknown_enum_wire_value_for_gate_rejection()
    {
        const string badJson = """
            {
              "operation": "not_a_real_operation",
              "memoryClass": "durable_fact",
              "subjectKind": "user",
              "subjectValue": "self",
              "title": "Bad",
              "content": "Bad",
              "recallMode": "auto",
              "sensitivity": "normal",
              "confidence": 0.5
            }
            """;

        var proposal = JsonSerializer.Deserialize<MemoryProposal>(badJson, ParseOptions)!;

        Assert.Equal(MemoryProposalOperation.Unknown, proposal.Operation);
    }

    [Fact]
    public void MemoryProposalGate_rejects_bad_proposal_without_dropping_valid_siblings()
    {
        const string sidecarJson = """
            {
              "proposals": [
                {
                  "operation": "not_a_real_operation",
                  "memoryClass": "durable_fact",
                  "subjectKind": "user",
                  "subjectValue": "self",
                  "anchor": { "canonicalName": "bad-op", "anchorType": "user" },
                  "title": "Bad operation",
                  "content": "This proposal should be rejected individually.",
                  "aliases": ["bad op"],
                  "facets": ["test"],
                  "recallMode": "auto",
                  "sensitivity": "normal",
                  "confidence": 0.5
                },
                {
                  "operation": "upsert_document",
                  "memoryClass": "durable_fact",
                  "subjectKind": "user",
                  "subjectValue": "self",
                  "anchor": { "canonicalName": "preferred-editor", "anchorType": "user" },
                  "title": "Preferred editor",
                  "content": "The user prefers Vim keybindings.",
                  "aliases": ["editor preference"],
                  "facets": ["development_tools"],
                  "recallMode": "auto",
                  "sensitivity": "normal",
                  "confidence": 0.9
                }
              ]
            }
            """;

        var response = JsonSerializer.Deserialize<DistillationResponseFixture>(sidecarJson, ParseOptions)!;
        var result = new MemoryProposalGate().Evaluate(response.Proposals!, nowMs: 123L);

        Assert.Equal(2, result.Summary.Total);
        Assert.Equal(1, result.Summary.Accepted);
        Assert.Single(result.MemoryOperations);
        Assert.Equal(1, result.Summary.RejectionReasons["invalid-operation"]);
    }

    [Fact]
    public void MemoryProposalGate_reports_unknown_memory_class_rejection()
    {
        const string badJson = """
            {
              "operation": "upsert_document",
              "memoryClass": "preference",
              "subjectKind": "user",
              "subjectValue": "self",
              "anchor": { "canonicalName": "preferred-editor", "anchorType": "user" },
              "title": "Preferred editor",
              "content": "The user prefers Vim keybindings.",
              "aliases": ["editor preference"],
              "facets": ["development_tools"],
              "recallMode": "auto",
              "sensitivity": "normal",
              "confidence": 0.9
            }
            """;

        var proposal = JsonSerializer.Deserialize<MemoryProposal>(badJson, ParseOptions)!;
        var result = new MemoryProposalGate().Evaluate([proposal], nowMs: 123L);

        Assert.Equal(MemoryClass.Unknown, proposal.MemoryClass);
        Assert.Empty(result.MemoryOperations);
        Assert.Equal(1, result.Summary.RejectionReasons["invalid-memory-class"]);
    }

    [Fact]
    public void ObservedMemoryCheckpointPayload_round_trips_byte_identically()
    {
        var original = new ObservedMemoryCheckpointPayload(
            "channel/thread",
            CheckpointTriggerType.ObservedMemoryProposals,
            MemorySensitivity.Secret,
            []);

        var firstPass = JsonSerializer.Serialize(original);
        using (var doc = JsonDocument.Parse(firstPass))
        {
            // On-disk discriminators stay kebab/snake-case, not PascalCase enum names.
            Assert.Equal("observed-memory-proposals", doc.RootElement.GetProperty("TriggerType").GetString());
            Assert.Equal("secret", doc.RootElement.GetProperty("Sensitivity").GetString());
        }

        var rehydrated = JsonSerializer.Deserialize<ObservedMemoryCheckpointPayload>(firstPass)!;
        var secondPass = JsonSerializer.Serialize(rehydrated);

        Assert.Equal(firstPass, secondPass);
        Assert.Equal(CheckpointTriggerType.ObservedMemoryProposals, rehydrated.TriggerType);
        Assert.Equal(MemorySensitivity.Secret, rehydrated.Sensitivity);
    }

    [Fact]
    public void ObservedMemoryCheckpointPayload_deserializes_legacy_wire_document()
    {
        // A document written before Pass 7e — string discriminators — must still load.
        const string legacyJson = """
            {
              "SessionId": "channel/thread",
              "TriggerType": "observed-memory-proposals",
              "Sensitivity": "normal",
              "Operations": []
            }
            """;

        var payload = JsonSerializer.Deserialize<ObservedMemoryCheckpointPayload>(legacyJson)!;

        Assert.Equal(CheckpointTriggerType.ObservedMemoryProposals, payload.TriggerType);
        Assert.Equal(MemorySensitivity.Normal, payload.Sensitivity);
    }

    private sealed record DistillationResponseFixture(IReadOnlyList<MemoryProposal>? Proposals);
}
