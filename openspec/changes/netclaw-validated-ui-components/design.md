## Context

`netclaw config` currently has reusable presentation helpers, but mutable
behavior is still page-specific. Several pages manually subscribe to key
events, append text to view-model drafts, handle `Enter`, and call save or
autosave methods directly. That makes validation a convention: a page can look
correct, pass render tests, and still bypass static validation, dynamic
validation, or canonical runtime-consumer checks.

The active config change already states the desired behavior: completed
actions autosave after validation, incomplete drafts do not persist, and
runtime/probe failures are handled explicitly. This design makes that behavior
enforceable by moving mutable input and commit behavior into page-independent
Netclaw UI components.

This change does not affect actor/session boundaries. It is a CLI/TUI and
configuration-persistence boundary change. The important downstream consumers
are daemon startup/options binding, channel adapter config, skill scanning/feed
loading, search provider setup, webhook runtime, ACL/security policy, and other
runtime services that consume persisted config/secrets.

## Goals / Non-Goals

**Goals:**

- Make missing static validation impossible for mutable Netclaw TUI actions.
- Make missing dynamic validation explicit through either `Required` or
  `NotApplicable(justification)`.
- Route `Enter`, save/apply, autosave, toggles, picker selections, token
  rotation, reset, delete, and confirmed destructive actions through one
  pipeline.
- Move text input, paste, `Enter`, and autosave handling out of config pages
  and into standard page-independent components.
- Add build enforcement that fails when pages bypass the standard components
  or commit pipeline.
- Delete obsolete tests/components only when replacement coverage proves they
  are no longer needed.

**Non-Goals:**

- Redesigning the `netclaw config` information architecture.
- Implementing `simplify-netclaw-init`.
- Changing persisted config schema or runtime option types unless a migration
  task discovers an existing mismatch.
- Removing tests just because they are old.
- Converting non-mutable display pages to validated components.

## Decisions

### D1. Use a single mandatory commit object

The core abstraction is intentionally small: one required object describes a
mutable UI action.

```csharp
internal sealed record NetclawUiCommit<TDraft>(
    string Id,
    string Label,
    Func<TDraft> ReadDraft,
    Action<TDraft> WriteDraft,
    Func<TDraft, NetclawUiValidationResult> Validate,
    NetclawUiDynamicCheck<TDraft> DynamicCheck,
    Func<TDraft, CancellationToken, ValueTask> PersistAsync,
    Action<NetclawUiCommitResult> AfterCommit);
```

Rationale: this is simpler than a framework of validator/writer interfaces,
but it still forces every mutable field/action to declare all required hooks.
Interfaces can be introduced later only if delegate-based commits become hard
to read or reuse.

Alternative considered: separate `IConfigStaticValidator<T>`,
`IConfigDynamicValidator<T>`, `IConfigCommitWriter<T>`, and draft-binding
interfaces. Rejected for the first pass because it increases surface area
without adding enforcement beyond what one required commit object provides.

### D2. Dynamic validation is an explicit discriminated policy

Dynamic validation is never nullable and never absent by omission.

```csharp
internal abstract record NetclawUiDynamicCheck<TDraft>
{
    private NetclawUiDynamicCheck() { }

    internal sealed record Required(
        Func<TDraft, CancellationToken, ValueTask<NetclawUiValidationResult>> ValidateAsync,
        NetclawUiDynamicFailurePolicy FailurePolicy) : NetclawUiDynamicCheck<TDraft>;

    internal sealed record NotApplicable(string Justification) : NetclawUiDynamicCheck<TDraft>;
}
```

`NotApplicable` must reject empty or whitespace-only justification. The
justification is not busywork; it records why no runtime/probe check applies.

Alternative considered: make dynamic validation optional via nullable delegate.
Rejected because that recreates the current failure mode.

### D3. The commit pipeline is the only persistence path

All completed mutable actions flow through one pipeline.

```csharp
internal sealed class NetclawUiCommitPipeline
{
    public ValueTask<NetclawUiCommitResult> CommitAsync<TDraft>(
        NetclawUiCommit<TDraft> commit,
        NetclawUiCommitTrigger trigger,
        CancellationToken ct);
}

internal enum NetclawUiCommitTrigger
{
    Enter,
    Save,
    AutoSave,
    Toggle,
    PickerSelection,
    Delete,
    Reset,
    TokenRotation,
}
```

Pipeline order:

```text
ReadDraft
-> Validate
-> DynamicCheck.Required or DynamicCheck.NotApplicable
-> PersistAsync
-> AfterCommit
```

Persistence never runs after a static validation failure. Dynamic validation
never runs after a static validation failure. Persistence never runs after a
dynamic validation failure unless the failure policy explicitly allows a
save-anyway path and the operator chooses that path through the pipeline.

Alternative considered: keep `ConfigAutosave` and direct `Save` methods but
audit them harder. Rejected because that keeps multiple persistence paths.

### D4. Standard components own mutable input handling

Pages compose standard components. Components own the mutable interaction.

```csharp
internal interface INetclawUiComponent
{
    ILayoutNode Build();
    bool HandleInput(ConsoleKeyInfo keyInfo);
    void HandlePaste(PasteEvent paste);
}

internal sealed class NetclawValidatedTextField : INetclawUiComponent
{
    public NetclawValidatedTextField(
        NetclawUiCommit<string> commit,
        NetclawUiCommitPipeline pipeline,
        TextInputNode input);
}

internal sealed class NetclawValidatedAction<TDraft> : INetclawUiComponent
{
    public NetclawValidatedAction(
        NetclawUiCommit<TDraft> commit,
        NetclawUiCommitPipeline pipeline,
        Func<TDraft> nextDraft);
}

internal sealed class NetclawValidatedToggle : INetclawUiComponent
{
    public NetclawValidatedToggle(
        NetclawUiCommit<bool> commit,
        NetclawUiCommitPipeline pipeline);
}

internal sealed class NetclawValidatedPicker<TValue> : INetclawUiComponent
{
    public NetclawValidatedPicker(
        NetclawUiCommit<TValue> commit,
        NetclawUiCommitPipeline pipeline,
        IReadOnlyList<TValue> options);
}
```

The constructors require a commit object. There is no constructor that accepts
only a label, current value, and raw save callback.

Alternative considered: keep page-level `HandleKeyPress` methods and call the
pipeline from those handlers. Rejected because pages would still own the
dangerous control flow and future pages could bypass the pipeline again.

### D5. Use a validated page/input router where possible

Pages with mutable controls should derive from or compose a router that
delegates input to active validated components.

```csharp
internal abstract class NetclawValidatedPage<TViewModel> : ReactivePage<TViewModel>
{
    protected abstract IReadOnlyList<INetclawUiComponent> Components { get; }

    public sealed override bool HandlePageInput(ConsoleKeyInfo keyInfo);
}
```

If Termina constraints prevent a sealed override in every existing page, the
first pass may use an injected `NetclawUiInputRouter`. The enforcement rule
stays the same: mutable persistence is routed through validated components,
not page-specific save handlers.

Alternative considered: enforce only at view-model level. Rejected because the
bug class is specifically the mismatch between rendered TUI actions and the
actual user key path.

### D6. Enforcement is part of the feature, not a follow-up

The implementation must include build enforcement. Preferred shape:

```csharp
internal sealed class NetclawValidatedUiBypassAnalyzer : DiagnosticAnalyzer
```

Analyzer diagnostics should reject:

- raw `TextInputNode` construction for mutable persisted fields outside
  approved standard components
- page input handlers that call `Save`, `SaveAsync`, `ConfigAutosave`, or
  config writer methods directly
- page or view-model autosave paths that do not use `NetclawUiCommitPipeline`
- mutable config page `ConsoleKey.Enter` branches that persist directly
- `NetclawUiDynamicCheck.NotApplicable` with empty justification

Architecture tests may be added first as a backstop, but the task is not done
until build enforcement prevents the bypass class. If an analyzer is not
feasible in the current repo structure, the design must record why and the
architecture test must fail the build in CI for the same bypass cases.

Alternative considered: rely on code review and OpenSpec checklists. Rejected
because that is the current failure mode.

### D7. Config-specific logic lives behind adapters/factories

The reusable UI layer is page-independent. Config-specific adapters create
commits.

```text
SkillSourcesConfigPage
-> NetclawValidatedTextField
-> NetclawUiCommit<string>
-> SkillSourcesCommitFactory
-> static path/url/name/token validators
-> dynamic skill scanner/feed probe validators
-> config/secrets writers
-> runtime binding verifier tests
```

Suggested adapter names:

```csharp
internal static class SkillSourcesCommitFactory;
internal static class ChannelsCommitFactory;
internal static class TelemetryCommitFactory;
internal static class WorkspacesCommitFactory;
internal static class InboundWebhooksCommitFactory;
internal static class ExposureModeCommitFactory;
```

Alternative considered: name the reusable layer `ConfigCommit*`. Rejected
because these components should be usable outside config pages.

### D8. Deletion is proof-based

Old tests and components get deleted only when no longer needed.

Deletion checklist:

- the old artifact has no production callers, or all callers have migrated
- replacement tests cover the same behavior through the public user action
- no unique visual, accessibility, persistence, or edge-case assertion is lost
- `git grep` or architecture tests prove the old bypass pattern is gone

Render-only tests that assert labels may be removed when component interaction
tests already cover rendering plus typed input, paste, `Enter`, validation
failure, unchanged persistence, and successful persistence. Direct view-model
save tests may remain if they cover pure domain validation, but they do not
count as user-action validation proof.

Alternative considered: delete all legacy tests after migration starts.
Rejected because the operative rule is "we don't need," not "old."

## TUI to backend relationship

```text
TUI page
-> Netclaw validated component
-> NetclawUiCommit<TDraft>
-> NetclawUiCommitPipeline
-> static validator
-> dynamic validator or explicit NotApplicable policy
-> persistence writer
-> runtime binding verifier tests
-> status/reload/navigation
```

The TUI page owns layout and routing. The validated component owns user input.
The commit contract owns the mutation definition. The pipeline owns ordering
and failure handling. Backend validators and writers own domain behavior.
Runtime binding tests prove the persisted shape is consumed correctly.

## Failure modes and recovery behavior

- Static validation failure: show an error, do not call dynamic validation, do
  not write files, keep the draft available for correction unless the action is
  destructive and canceled.
- Dynamic validation failure: show a warning/error, do not write files, offer
  save-anyway only if the declared failure policy allows it.
- Persistence exception: catch in the pipeline, show an error, leave page
  active, and do not report success.
- Post-commit reload failure: show an error and do not hide the failure behind
  a success message.
- Analyzer false positive: add the narrowest exemption in the standard
  component layer only, never on a leaf page to silence a real bypass.

## Migration Plan

1. Add `NetclawUiCommit<TDraft>`, `NetclawUiDynamicCheck<TDraft>`,
   `NetclawUiCommitPipeline`, result types, and standard validated components.
2. Add focused pipeline/component tests that prove validation ordering,
   unchanged persistence on failure, typed input, paste, `Enter`, autosave, and
   explicit `NotApplicable` justification.
3. Add build enforcement in warning-as-error mode for the targeted bypasses.
4. Migrate Skill Sources first because it exposed the current regression.
5. Migrate Telemetry & Alerting, Workspaces Directory, Inbound Webhooks,
   Channels, Search, Browser Automation, and Exposure Mode.
6. Update config editor audit tests so user-action path coverage is required.
7. Remove obsolete helpers/tests only after replacement proof and caller
   migration are complete.
8. Run focused tests after each page migration and native smoke for migrated
   config paths before completion.

Rollback strategy: the change is internal to the CLI/TUI. If a migration slice
fails, keep the standard component layer and stop before removing old callers.
Do not reintroduce direct page-level save/autosave bypasses.

## Open Questions

- Whether the build enforcement should be implemented first as a Roslyn
  analyzer project or as architecture tests that run in the existing test
  suite, then promoted to analyzer once stable.
- Whether non-config onboarding pages should migrate in this change or only
  after config pages prove the component contract.
