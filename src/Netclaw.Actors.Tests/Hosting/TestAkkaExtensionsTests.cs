// -----------------------------------------------------------------------
// <copyright file="TestAkkaExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Serialization;
using Netclaw.Actors.Hosting;
using Xunit;

namespace Netclaw.Actors.Tests.Hosting;

public sealed class TestAkkaExtensionsTests : TestKit
{
    public TestAkkaExtensionsTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithNetclawSerialization()
            .WithSerializationVerification();
    }

    [Fact]
    public void Test_probe_retarget_message_round_trips_with_strict_serialization()
    {
        var messageType = Type.GetType(
            "Akka.Hosting.TestKit.TestKit+StableTestProbeRef+UpdateTarget, Akka.Hosting.TestKit",
            throwOnError: true)!;
        var original = Activator.CreateInstance(messageType, TestActor)!;
        var serializer = Sys.Serialization.FindSerializerFor(original);
        var bytes = serializer.ToBinary(original);
        var manifest = serializer is SerializerWithStringManifest serializerWithManifest
            ? serializerWithManifest.Manifest(original)
            : messageType.AssemblyQualifiedName!;

        var roundTripped = Sys.Serialization.Deserialize(bytes, serializer.Identifier, manifest);
        var target = (IActorRef)messageType.GetProperty("Target")!.GetValue(roundTripped)!;

        Assert.Equal(TestActor, target);
    }
}
