// -----------------------------------------------------------------------
// <copyright file="NetclawUiCommit.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Tui;

internal enum NetclawUiStatusTone
{
    Neutral,
    Success,
    Warning,
    Error,
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
    SaveAnyway,
}

internal enum NetclawUiDynamicFailurePolicy
{
    Block,
    AllowSaveAnyway,
}

internal enum NetclawUiCommitStage
{
    StaticValidation,
    DynamicValidation,
    Persistence,
    Completed,
}

internal sealed record NetclawUiValidationResult(bool Success, string Message, NetclawUiStatusTone Tone)
{
    public static NetclawUiValidationResult Passed(string message = "")
        => new(true, message, NetclawUiStatusTone.Success);

    public static NetclawUiValidationResult Failed(string message)
        => new(false, message, NetclawUiStatusTone.Error);

    public static NetclawUiValidationResult Warning(string message)
        => new(false, message, NetclawUiStatusTone.Warning);
}

internal sealed record NetclawUiCommitResult(
    bool Success,
    string Message,
    NetclawUiStatusTone Tone,
    NetclawUiCommitStage Stage,
    bool CanSaveAnyway = false)
{
    public static NetclawUiCommitResult Completed(string message = "Saved.")
        => new(true, message, NetclawUiStatusTone.Success, NetclawUiCommitStage.Completed);

    public static NetclawUiCommitResult Failed(
        NetclawUiValidationResult validation,
        NetclawUiCommitStage stage,
        bool canSaveAnyway = false)
        => new(false, validation.Message, validation.Tone, stage, canSaveAnyway);

    public static NetclawUiCommitResult PersistenceFailed(string message)
        => new(false, message, NetclawUiStatusTone.Error, NetclawUiCommitStage.Persistence);
}

internal abstract record NetclawUiDynamicCheck<TDraft>
{
    private NetclawUiDynamicCheck()
    {
    }

    public static NetclawUiDynamicCheck<TDraft> Required(
        Func<TDraft, CancellationToken, ValueTask<NetclawUiValidationResult>> validateAsync,
        NetclawUiDynamicFailurePolicy failurePolicy = NetclawUiDynamicFailurePolicy.Block)
        => new RequiredCheck(validateAsync, failurePolicy);

    public static NetclawUiDynamicCheck<TDraft> NotApplicable(string justification)
        => new NotApplicableCheck(justification);

    internal sealed record RequiredCheck : NetclawUiDynamicCheck<TDraft>
    {
        public RequiredCheck(
            Func<TDraft, CancellationToken, ValueTask<NetclawUiValidationResult>> validateAsync,
            NetclawUiDynamicFailurePolicy failurePolicy)
        {
            ValidateAsync = validateAsync ?? throw new ArgumentNullException(nameof(validateAsync));
            FailurePolicy = failurePolicy;
        }

        public Func<TDraft, CancellationToken, ValueTask<NetclawUiValidationResult>> ValidateAsync { get; }

        public NetclawUiDynamicFailurePolicy FailurePolicy { get; }
    }

    internal sealed record NotApplicableCheck : NetclawUiDynamicCheck<TDraft>
    {
        public NotApplicableCheck(string justification)
        {
            if (string.IsNullOrWhiteSpace(justification))
                throw new ArgumentException("Dynamic validation NotApplicable requires a non-empty justification.", nameof(justification));

            Justification = justification;
        }

        public string Justification { get; }
    }
}

internal sealed record NetclawUiCommit<TDraft>
{
    public NetclawUiCommit(
        string Id,
        string Label,
        Func<TDraft> ReadDraft,
        Action<TDraft> WriteDraft,
        Func<TDraft, NetclawUiValidationResult> Validate,
        NetclawUiDynamicCheck<TDraft> DynamicCheck,
        Func<TDraft, CancellationToken, ValueTask> PersistAsync,
        Action<NetclawUiCommitResult> AfterCommit)
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new ArgumentException("Commit id is required.", nameof(Id));
        if (string.IsNullOrWhiteSpace(Label))
            throw new ArgumentException("Commit label is required.", nameof(Label));

        this.Id = Id;
        this.Label = Label;
        this.ReadDraft = ReadDraft ?? throw new ArgumentNullException(nameof(ReadDraft));
        this.WriteDraft = WriteDraft ?? throw new ArgumentNullException(nameof(WriteDraft));
        this.Validate = Validate ?? throw new ArgumentNullException(nameof(Validate));
        this.DynamicCheck = DynamicCheck ?? throw new ArgumentNullException(nameof(DynamicCheck));
        this.PersistAsync = PersistAsync ?? throw new ArgumentNullException(nameof(PersistAsync));
        this.AfterCommit = AfterCommit ?? throw new ArgumentNullException(nameof(AfterCommit));
    }

    public string Id { get; }

    public string Label { get; }

    public Func<TDraft> ReadDraft { get; }

    public Action<TDraft> WriteDraft { get; }

    public Func<TDraft, NetclawUiValidationResult> Validate { get; }

    public NetclawUiDynamicCheck<TDraft> DynamicCheck { get; }

    public Func<TDraft, CancellationToken, ValueTask> PersistAsync { get; }

    public Action<NetclawUiCommitResult> AfterCommit { get; }
}

internal sealed class NetclawUiCommitPipeline
{
    public async ValueTask<NetclawUiCommitResult> CommitAsync<TDraft>(
        NetclawUiCommit<TDraft> commit,
        NetclawUiCommitTrigger trigger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commit);

        var draft = commit.ReadDraft();
        var staticValidation = commit.Validate(draft);
        if (!staticValidation.Success)
            return Complete(commit, NetclawUiCommitResult.Failed(staticValidation, NetclawUiCommitStage.StaticValidation));

        if (commit.DynamicCheck is NetclawUiDynamicCheck<TDraft>.RequiredCheck required)
        {
            var dynamicValidation = await required.ValidateAsync(draft, ct);
            if (!dynamicValidation.Success)
            {
                var canSaveAnyway = required.FailurePolicy == NetclawUiDynamicFailurePolicy.AllowSaveAnyway;
                if (!(trigger == NetclawUiCommitTrigger.SaveAnyway && canSaveAnyway))
                {
                    return Complete(commit, NetclawUiCommitResult.Failed(
                        dynamicValidation,
                        NetclawUiCommitStage.DynamicValidation,
                        canSaveAnyway));
                }
            }
        }

        try
        {
            await commit.PersistAsync(draft, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Complete(commit, NetclawUiCommitResult.PersistenceFailed($"{commit.Label} save failed: {ex.Message}"));
        }

        return Complete(commit, NetclawUiCommitResult.Completed($"{commit.Label} saved."));
    }

    private static NetclawUiCommitResult Complete<TDraft>(NetclawUiCommit<TDraft> commit, NetclawUiCommitResult result)
    {
        commit.AfterCommit(result);
        return result;
    }
}
