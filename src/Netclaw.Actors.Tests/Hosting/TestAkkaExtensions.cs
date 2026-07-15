// -----------------------------------------------------------------------
// <copyright file="TestAkkaExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;

namespace Netclaw.Actors.Tests.Hosting;

internal static class TestAkkaExtensions
{
    /// <summary>
    /// Turns on Akka's local-message serialization verification (`serialize-messages = on`).
    /// In combination with <c>WithStrictSerialization</c> (set by
    /// <c>WithNetclawSerialization</c>) this fails loudly when a type that crosses
    /// the actor boundary has neither <c>INetclawSerializableMessage</c> nor
    /// <c>INoSerializationVerificationNeeded</c> — that loud failure is the
    /// regression net for missing serializer bindings on new persisted types.
    ///
    /// Third-party library types we don't own (Akka.Reminders internals, Akka system
    /// messages, Akka.Persistence permit/permit-return envelopes, Akka.Streams
    /// stage protocol) get an explicit JSON binding here so verification doesn't
    /// false-positive on them. Add new entries as integration tests surface them.
    /// </summary>
    public static AkkaConfigurationBuilder WithSerializationVerification(
        this AkkaConfigurationBuilder builder) =>
        builder.AddHocon(
            """
            akka.actor {
                serialize-messages = on
                serialization-bindings {
                    "Akka.Actor.ActorIdentity, Akka" = json
                    "Akka.Actor.Identify, Akka" = json
                    "Akka.Actor.ReceiveTimeout, Akka" = json
                    "Akka.Dispatch.SysMsg.StopChild, Akka" = json
                    "Akka.Hosting.TestKit.TestKit+StableTestProbeRef+UpdateTarget, Akka.Hosting.TestKit" = json
                    "Akka.Persistence.Journal.AsyncWriteJournal+Desequenced, Akka.Persistence" = json
                    "Akka.Persistence.RecoveryPermitGranted, Akka.Persistence" = json
                    "Akka.Persistence.RequestRecoveryPermit, Akka.Persistence" = json
                    "Akka.Persistence.ReturnRecoveryPermit, Akka.Persistence" = json
                    "Akka.Reminders.ReminderProtocol+CancelReminder, Akka.Reminders" = json
                    "Akka.Reminders.ReminderProtocol+GetReminders, Akka.Reminders" = json
                    "Akka.Reminders.ReminderScheduler+FlushBufferedAcks, Akka.Reminders" = json
                    "Akka.Reminders.ReminderScheduler+InitResult, Akka.Reminders" = json
                }
            }
            """,
            HoconAddMode.Append);
}
