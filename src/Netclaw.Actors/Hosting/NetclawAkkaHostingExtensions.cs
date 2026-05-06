// -----------------------------------------------------------------------
// <copyright file="NetclawAkkaHostingExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Hosting;
using Akka.Reminders;
using Akka.Reminders.Sharding;
using Akka.Reminders.Sqlite;
using Akka.Reminders.Sqlite.Configuration;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Routing;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;

namespace Netclaw.Actors.Hosting;

public static class NetclawAkkaHostingExtensions
{
    public sealed record ReminderStorageOptions
    {
        public string? SqliteConnectionString { get; init; }
        public string TableName { get; init; } = "scheduled_reminders";
        public bool AutoInitialize { get; init; } = true;
    }

    /// <summary>
    /// Registers the session manager as a <see cref="GenericChildPerEntityParent"/>
    /// that routes <see cref="Protocol.IWithSessionId"/> messages to per-session
    /// <see cref="LlmSessionActor"/> children.
    /// </summary>
    public static AkkaConfigurationBuilder WithSessionManager(
        this AkkaConfigurationBuilder builder)
    {
        return builder.StartActors((system, registry, resolver) =>
        {
            var sessionManager = system.ActorOf(
                GenericChildPerEntityParent.CreateProps(
                    new SessionMessageExtractor(),
                    entityId => resolver.Props<LlmSessionActor>(entityId)),
                "session-manager");
            registry.Register<SessionManagerActorKey>(sessionManager);
        });
    }

    /// <summary>
    /// Registers the model capability cache as a singleton actor.
    /// Requires <see cref="Netclaw.Configuration.IModelCapabilityResolver"/>
    /// to be registered in DI.
    /// </summary>
    public static AkkaConfigurationBuilder WithModelCapabilityCache(
        this AkkaConfigurationBuilder builder)
    {
        return builder.StartActors((system, registry, resolver) =>
        {
            var capabilityActor = system.ActorOf(
                resolver.Props<ModelCapabilityActor>(),
                "model-capabilities");
            registry.Register<ModelCapabilityActorKey>(capabilityActor);
        });
    }

    /// <summary>
    /// Registers the reminder manager as a singleton actor and wires
    /// the local Akka.Reminders scheduler to deliver payloads to it.
    /// Uses Akka.Reminders' built-in default settings throughout — no
    /// configuration surface exposed. If operators ever need to tune
    /// <c>AckTimeout</c>, <c>MaxRetryBackoff</c>, or
    /// <c>MaxDeliveryAttempts</c>, a configuration knob can be added at
    /// that point. Right now: YAGNI.
    /// </summary>
    public static AkkaConfigurationBuilder WithReminderManager(
        this AkkaConfigurationBuilder builder,
        ReminderStorageOptions? storageOptions = null)
    {
        // Shared resolver: created at configuration time, populated at actor startup.
        // The scheduler starts first with an empty resolver; by the time any reminder
        // actually fires, the ReminderManagerActor is registered as the shard region.
        var sharedResolver = new TestShardRegionResolver();

        return builder
            .WithLocalReminders(reminders =>
            {
                if (!string.IsNullOrWhiteSpace(storageOptions?.SqliteConnectionString))
                {
                    reminders.WithStorage(system =>
                        new SqliteReminderStorage(
                            SqliteReminderStorageSettings.Create(
                                connectionString: storageOptions.SqliteConnectionString,
                                tableName: storageOptions.TableName,
                                autoInitialize: storageOptions.AutoInitialize),
                            system));
                }
                else
                {
                    reminders.WithInMemoryStorage();
                }

                reminders.WithResolver(_ => sharedResolver);
            })
            .StartActors((system, registry, resolver) =>
            {
                var reminderManager = system.ActorOf(
                    resolver.Props<ReminderManagerActor>(),
                    "reminder-manager");
                registry.Register<ReminderManagerActorKey>(reminderManager);

                // Register so akka-reminders delivers fired payloads to our manager
                sharedResolver.RegisterShardRegion(
                    ReminderManagerActor.ShardRegionName, reminderManager);
            });
    }

    public static AkkaConfigurationBuilder WithToolApprovalActor(
        this AkkaConfigurationBuilder builder)
    {
        return builder.StartActors((system, registry, resolver) =>
        {
            var actor = system.ActorOf(
                resolver.Props<ToolApprovalActor>(),
                "tool-approvals");
            registry.Register<ToolApprovalActorKey>(actor);
        });
    }

    public static AkkaConfigurationBuilder WithBackgroundJobManager(
        this AkkaConfigurationBuilder builder)
    {
        return builder.StartActors((system, registry, resolver) =>
        {
            var actor = system.ActorOf(
                resolver.Props<BackgroundJobManagerActor>(),
                "background-job-manager");
            registry.Register<BackgroundJobManagerActorKey>(actor);
        });
    }

    /// <summary>
    /// Convenience method that registers all Netclaw actor infrastructure.
    /// Requires <c>SessionConfig</c> and <see cref="Microsoft.Extensions.AI.IChatClient"/>
    /// to be registered in DI.
    /// </summary>
    public static AkkaConfigurationBuilder WithNetclawActors(
        this AkkaConfigurationBuilder builder,
        ReminderStorageOptions? reminderStorageOptions = null)
    {
        return builder
            .WithModelCapabilityCache()
            .WithSessionManager()
            .WithToolApprovalActor()
            .WithReminderManager(reminderStorageOptions)
            .WithBackgroundJobManager();
    }

    /// <summary>
    /// Configures Google Protobuf serialization for Netclaw protocol types.
    /// Wire format defined in <c>netclaw_messages.proto</c>; our serializer throws
    /// for unregistered manifests to fail loudly on schema drift.
    /// </summary>
    public static AkkaConfigurationBuilder WithNetclawSerialization(
        this AkkaConfigurationBuilder builder)
    {
        var boundTypes = new[]
        {
            typeof(SessionId),
            typeof(SendUserMessage),
            typeof(SerializableChatMessage),
            typeof(SerializableMediaReference),
            typeof(SerializableToolCall),
            typeof(TurnRecorded),
            typeof(SessionTitleSet),
            typeof(SessionCompacted),
            typeof(SessionSnapshot),
            typeof(TurnBroadcast),
            typeof(CompactionBroadcast),
            typeof(WorkingContext),
            typeof(ReminderId),
            typeof(ReminderDelivery),
            typeof(ReminderSchedule),
            typeof(ReminderPayload),
            typeof(AdoptedContextRecorded),
            typeof(Channels.CursorAdvanced),
        };

        return builder
            .WithCustomSerializer(
                serializerIdentifier: "netclaw-protobuf",
                boundTypes: boundTypes,
                serializerFactory: system => new NetclawProtobufSerializer(system))
            .WithStrictSerialization();
    }
}
