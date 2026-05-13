// -----------------------------------------------------------------------
// <copyright file="LlmSessionTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Configuration;

namespace Netclaw.Actors.Tests.Sessions;

public abstract class LlmSessionTestBase : TestKit
{
    protected LlmSessionTestBase(ITestOutputHelper output) : base(output: output) { }

    protected sealed override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawSerialization()
            .WithNetclawActors()
            .WithStrictSerialization();
    }

    protected sealed override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());
        services.AddTestNetclawPaths();
        services.AddSingleton(SecurityPolicyDefaults.Resolve(null));
        services.AddSingleton<BackgroundJobDefinitionStore>();
        ConfigureSessionServices(services);
        services.AddLlmSessionCompositeRecords();
    }

    protected virtual void ConfigureSessionServices(IServiceCollection services) { }
}
