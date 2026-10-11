// -----------------------------------------------------------------------
// <copyright file="ToolTaskAdoptionMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.MutationTests;

public sealed class ToolTaskAdoptionMutationTests
{
    private static readonly SessionId Session = new("mutation/adoption");

    [Fact]
    public void A_well_formed_foreign_requester_cannot_adopt_an_admitted_input()
    {
        var input = Input("first", "operator-a");
        var state = SessionState.Empty.Apply(input);
        var evt = Adoption(input, [input.InputId]) with
        { TurnContext = input.TurnContext with { RequesterSenderId = new SenderId("operator-b") } };
        Assert.Throws<InvalidDataException>(() => state.Apply(evt));
        Assert.Null(state.AdoptedTaskContext);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("kind")]
    public void The_latest_admitted_source_fields_are_canonical(string field)
    {
        var input = Input("first", "operator-a");
        var state = SessionState.Empty.Apply(input);
        var evt = Adoption(input, [input.InputId]);
        evt = evt with { TurnContext = field == "scope"
            ? evt.TurnContext with { SourceScope = "foreign-scope" }
            : evt.TurnContext with { SourceKind = "foreign-kind" } };
        Assert.Throws<InvalidDataException>(() => state.Apply(evt));
        Assert.Null(state.AdoptedTaskContext);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_adoption_cannot_skip_or_reverse_the_admitted_prefix(bool reverse)
    {
        var first = Input("first", "operator-a");
        var second = Input("second", "operator-a");
        var state = SessionState.Empty.Apply(first).Apply(second);
        var evt = Adoption(second, reverse ? [second.InputId, first.InputId] : [second.InputId]);
        Assert.Throws<InvalidDataException>(() => state.Apply(evt));
        Assert.Equal([first.InputId, second.InputId], state.PendingInputs.Select(item => item.InputId));
    }

    [Fact]
    public void The_canonical_compatible_prefix_adopts_its_latest_context_once()
    {
        var first = Input("first", "operator-a");
        var second = Input("second", "operator-a");
        var evt = Adoption(second, [first.InputId, second.InputId]);
        var state = SessionState.Empty.Apply(first).Apply(second).Apply(evt);
        Assert.Equal(second.TurnContext, state.AdoptedTaskContext);
        Assert.Equal([first.InputId, second.InputId], state.AdoptedTaskInputIds);
        Assert.Same(state, state.Apply(evt));
    }

    private static InputAdmitted Input(string id, string requester) => new()
    {
        SessionId = Session, InputId = new InputId(id),
        UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = id },
        TurnContext = TurnContext.FromMessageSource(Session, new TurnId(id), new MessageSource
        {
            ChannelType = ChannelType.SignalR, SenderId = new SenderId(requester),
            Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
            Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        }).ToRecord()
    };

    private static ToolTaskAdopted Adoption(InputAdmitted input, IReadOnlyList<InputId> ids) => new(false)
    { SessionId = Session, TurnContext = input.TurnContext, InputIds = ids };
}
