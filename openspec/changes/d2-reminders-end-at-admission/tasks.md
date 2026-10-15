## 1. Reminder execution

- [x] 1.1 Add `ReminderFireKey` and use it in `ReminderExecutionActor`; verify with `ReminderFireKeyTests`.
- [x] 1.2 End the gateway run at `CommandAck`; delete the delivery wait, backstop, and `DeliveryObservedTimeout`; verify with `CurrentSession_reminder_succeeds_at_admission_whether_or_not_delivery_is_required`.
- [x] 1.3 Delete `DeliveryObserver`, `ReminderDeliveryResult`, and the observer code in the engine, the three binding actors, and `SignalRSessionActor`; verify the solution builds.

## 2. Session admission

- [x] 2.1 Add `AdmissionDecision.Dropped` and the drop rule in `PrepareAdmission`; verify with `ReminderFireAdmissionTests` and `ReminderFireAdmissionAccordantTests`.
- [x] 2.2 Delete `IsReminderDuplicate`, the reminder in-flight set, and `ProcessedReminderIds`; verify the session suites pass.
- [x] 2.3 Decide admission before tool batch abandonment; verify with `A_redelivered_fire_leaves_an_interrupted_tool_batch_alone`.
- [x] 2.4 Acknowledge a restart-resume message as a no-op outside a resume; verify with `RestartResumeAccordantTests`.

## 3. Proof and docs

- [x] 3.1 Add the focused mutation target for the drop decision and `ReminderFireKey.TryParse`; verify that every mutant dies.
- [x] 3.2 Update the `netclaw-operations` skill text for `delivery_required` and bump its version.
- [x] 3.3 Run the full Actors, Daemon, and Cli suites, slopwatch, and the header check.
