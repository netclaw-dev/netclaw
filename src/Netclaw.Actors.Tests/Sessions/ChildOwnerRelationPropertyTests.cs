// -----------------------------------------------------------------------
// <copyright file="ChildOwnerRelationPropertyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using CsCheck;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Property specs for the check that <c>ControlChild</c> uses to decide whether a requester owns a child run.
/// <para>
/// Intent: <c>openspec/specs/background-subagent-runs/spec.md</c>, "Status enforces run ownership" says one requester
/// owns a run, the owner can see progress, and another requester is denied. The same requirement keeps the
/// "original requester, audience, and authority boundary". The comment on <c>TurnContext.HasSameAuthority</c> says
/// "child status can span several turns", so a new turn id must not change the owner.
/// </para>
/// <para>
/// The independent oracle is the requester definition: session, audience, boundary, channel, sender, and principal.
/// The oracle does not call <c>HasSameAuthority</c>.
/// </para>
/// </summary>
public sealed class ChildOwnerRelationPropertyTests(ITestOutputHelper output)
{
    private const string InitialSampleSeed = "0N0XIzNsQ0O2";

    // ControlChild calls one private check for status and cancel. The spec calls that method through
    // reflection so the exact production check is under test. See BindOwnerCheck for the contract.
    private static readonly Func<TurnContextRecord, TurnContextRecord, bool> ControlChildOwnerCheck = BindOwnerCheck();

    private enum Dimension { Session, Audience, Channel, Sender, Principal }

    private sealed record Identity(
        string Session, TrustAudience Audience, ChannelType Channel, string Sender, PrincipalClassification Principal);

    private sealed record TurnVariation(
        string TurnId, bool HasAdoptedContext, bool HasThirdPartyAdoptedContext, string[] AdoptedSpeakers);

    private sealed record OwnerPair(Identity Owner, TurnVariation First, TurnVariation Second, Dimension? Changed)
    {
        public override string ToString()
            => $"owner={Owner}; first=[{First.TurnId},adopted={First.HasAdoptedContext},3p={First.HasThirdPartyAdoptedContext},"
               + $"speakers={string.Join('|', First.AdoptedSpeakers)}]; second=[{Second.TurnId},adopted={Second.HasAdoptedContext},"
               + $"3p={Second.HasThirdPartyAdoptedContext},speakers={string.Join('|', Second.AdoptedSpeakers)}]; changed={Changed?.ToString() ?? "none"}";
    }

    [Fact]
    public void Same_requester_in_a_later_turn_owns_the_child_even_when_adoption_fields_differ()
    {
        // Arrange
        var sameRequester = GenerateIdentity.SelectMany(owner =>
            GenerateVariation.Select(GenerateVariation, (first, second) => new OwnerPair(owner, first, second, null)));

        // Act and assert: the requester is the same, so the owner check must say yes.
        sameRequester.Sample(pair =>
        {
            var first = BuildRecord(pair.Owner, pair.First);
            var second = BuildRecord(pair.Owner, pair.Second);

            Assert.True(ControlChildOwnerCheck(first, second),
                "The same session, audience, boundary, channel, sender, and principal must own the child. "
                + $"Only the turn id and adoption fields differ. Pair: {pair}");
        }, iter: 2_000, seed: InitialSampleSeed, threads: 1, print: pair => pair.ToString());
        output.WriteLine($"seed={InitialSampleSeed}; relation=same requester, different turn and adoption fields");
    }

    [Fact]
    public void A_different_requester_never_owns_the_child()
    {
        // Arrange
        var differentRequester = GenerateIdentity.SelectMany(owner =>
            Gen.OneOfConst(Enum.GetValues<Dimension>()).SelectMany(changed =>
                GenerateVariation.Select(GenerateVariation, (first, second) => new OwnerPair(owner, first, second, changed))));

        // Act and assert: a stranger who differs in any requester dimension must fail the owner check.
        differentRequester.Sample(pair =>
        {
            var first = BuildRecord(pair.Owner, pair.First);
            var second = BuildRecord(ChangeOneDimension(pair.Owner, pair.Changed!.Value), pair.Second);

            Assert.False(ControlChildOwnerCheck(first, second),
                $"A requester that differs in {pair.Changed} must not own the child. Pair: {pair}");
        }, iter: 2_000, seed: InitialSampleSeed, threads: 1, print: pair => pair.ToString());
        output.WriteLine($"seed={InitialSampleSeed}; relation=different requester");
    }

    // Named cases keep the business rule readable. The properties above explore the full input space.
    [Fact]
    public void Owner_keeps_the_child_in_a_later_turn_with_a_new_turn_id()
    {
        var owner = new Identity("session-a", TrustAudience.Personal, ChannelType.Slack, "alice", PrincipalClassification.Operator);
        var started = new TurnVariation("turn-1", false, false, []);
        var later = new TurnVariation("turn-2", false, false, []);

        Assert.True(ControlChildOwnerCheck(BuildRecord(owner, started), BuildRecord(owner, later)));
    }

    [Fact]
    public void Owner_keeps_the_child_when_a_later_thread_turn_adopts_quoted_context()
    {
        // A Slack thread user starts a child, then asks for status in a turn that quotes earlier thread messages.
        var owner = new Identity("session-a", TrustAudience.Personal, ChannelType.Slack, "alice", PrincipalClassification.Operator);
        var started = new TurnVariation("turn-1", false, false, []);
        var later = new TurnVariation("turn-2", true, false, ["u1"]);

        Assert.True(ControlChildOwnerCheck(BuildRecord(owner, started), BuildRecord(owner, later)),
            "The owner asked for status in a later turn that adopted thread context, and the check denied the owner.");
    }

    private static Gen<Identity> GenerateIdentity { get; } = Gen.Select(
        Gen.OneOfConst("session-a", "session-b"),
        Gen.OneOfConst(Enum.GetValues<TrustAudience>()),
        Gen.OneOfConst(Enum.GetValues<ChannelType>()),
        Gen.OneOfConst("alice", "bob"),
        Gen.OneOfConst(Enum.GetValues<PrincipalClassification>()),
        (session, audience, channel, sender, principal) => new Identity(session, audience, channel, sender, principal));

    private static Gen<TurnVariation> GenerateVariation { get; } = Gen.Select(
        Gen.OneOfConst("turn-1", "turn-2", "turn-3"),
        Gen.Bool,
        Gen.Bool,
        Gen.OneOfConst("u1", "u2", "u3").Array[0, 2],
        (turn, adopted, thirdParty, speakers) => new TurnVariation(turn, adopted, thirdParty, speakers));

    private static Identity ChangeOneDimension(Identity owner, Dimension changed) => changed switch
    {
        Dimension.Session => owner with { Session = owner.Session == "session-a" ? "session-b" : "session-a" },
        Dimension.Audience => owner with { Audience = owner.Audience == TrustAudience.Public ? TrustAudience.Team : TrustAudience.Public },
        Dimension.Channel => owner with { Channel = owner.Channel == ChannelType.Slack ? ChannelType.Tui : ChannelType.Slack },
        Dimension.Sender => owner with { Sender = owner.Sender == "alice" ? "bob" : "alice" },
        Dimension.Principal => owner with
        {
            Principal = owner.Principal == PrincipalClassification.Operator
                ? PrincipalClassification.UntrustedExternal
                : PrincipalClassification.Operator
        },
        _ => throw new ArgumentOutOfRangeException(nameof(changed), changed, null)
    };

    private static TurnContextRecord BuildRecord(Identity owner, TurnVariation turn)
    {
        var context = new TurnContext
        {
            SessionId = new SessionId(owner.Session),
            TurnId = new TurnId(turn.TurnId),
            Audience = owner.Audience,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(owner.Audience),
            ChannelType = owner.Channel,
            RequesterSenderId = new SenderId(owner.Sender),
            RequesterPrincipal = owner.Principal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
            HasAdoptedContext = turn.HasAdoptedContext,
            HasThirdPartyAdoptedContext = turn.HasThirdPartyAdoptedContext,
            AdoptedSpeakerIds = turn.AdoptedSpeakers,
            SupportsInteractiveApproval = owner.Channel.SupportsInteractiveApproval()
        };
        return context.ToRecord() with { AdoptedSpeakerIds = context.AdoptedSpeakerIds.ToArray() };
    }

    // Contract for the implementer: LlmSessionActor keeps a private static method
    // `HasSameChildRequester(TurnContextRecord, TurnContextRecord)`, and ControlChild uses it for status and cancel.
    // The start-idempotency check keeps its own method, `HasSameChildAuthority`, because it must compare the
    // full turn authority. The legacy name is bound only while HasSameChildRequester does not exist, so this spec
    // fails on the assertion (the real defect) at the base commit and not on a missing method. After the fix,
    // the legacy name is not used. If the implementer picks another name, update this one method and state why.
    private static Func<TurnContextRecord, TurnContextRecord, bool> BindOwnerCheck()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var method = typeof(LlmSessionActor).GetMethod("HasSameChildRequester", flags)
            ?? typeof(LlmSessionActor).GetMethod("HasSameChildAuthority", flags)
            ?? throw new InvalidOperationException(
                "LlmSessionActor.HasSameChildRequester moved or was renamed. Point this spec at the check that ControlChild uses.");
        return method.CreateDelegate<Func<TurnContextRecord, TurnContextRecord, bool>>();
    }
}
