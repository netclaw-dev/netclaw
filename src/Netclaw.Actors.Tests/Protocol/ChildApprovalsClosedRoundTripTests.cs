// -----------------------------------------------------------------------
// <copyright file="ChildApprovalsClosedRoundTripTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Serialization;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Protocol;

/// <summary>
/// <see cref="ChildApprovalsClosed"/> is a journal event (#2525). It uses the manifest <c>cac-v1</c> and its own proto
/// message, so it takes no field number of another message.
/// </summary>
public sealed class ChildApprovalsClosedRoundTripTests : TestKit
{
    private const string Manifest = "cac-v1";

    public ChildApprovalsClosedRoundTripTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.WithNetclawSerialization();
    }

    [Theory]
    [InlineData(ChildApprovalCloseReason.RunCancelled)]
    [InlineData(ChildApprovalCloseReason.RunTerminal)]
    [InlineData(ChildApprovalCloseReason.OwnerLost)]
    public void Each_reason_round_trips(ChildApprovalCloseReason reason)
    {
        var original = new ChildApprovalsClosed
        {
            SessionId = new SessionId("slack/C123/1700000000.000100"),
            RunId = new SubAgentRunId("run-7"),
            Reason = reason,
            ClosedAtMs = 1_760_000_000_123
        };

        var serializer = Sys.Serialization.FindSerializerFor(original);
        var swm = Assert.IsAssignableFrom<SerializerWithStringManifest>(serializer);
        Assert.Equal(Manifest, swm.Manifest(original));

        var restored = Assert.IsType<ChildApprovalsClosed>(
            Sys.Serialization.Deserialize(serializer.ToBinary(original), serializer.Identifier, Manifest));

        Assert.Equal(original, restored);
    }

    [Fact]
    public void A_message_with_no_reason_fails_loudly()
    {
        // An empty message has the proto zero value for the reason. The writer never writes it, so a reader that
        // maps it to a default would hide a defect. The reader must refuse it.
        var serializer = Assert.IsAssignableFrom<SerializerWithStringManifest>(
            Sys.Serialization.FindSerializerFor(new ChildApprovalsClosed
            {
                SessionId = new SessionId("s"),
                RunId = new SubAgentRunId("r"),
                Reason = ChildApprovalCloseReason.OwnerLost,
                ClosedAtMs = 1
            }));

        Assert.Throws<InvalidDataException>(() => serializer.FromBinary([], Manifest));
    }
}
