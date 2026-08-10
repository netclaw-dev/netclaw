// -----------------------------------------------------------------------
// <copyright file="WizardOrchestratorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

/// <summary>
/// Tests for <see cref="WizardOrchestrator"/> step sequencing, conditional inclusion,
/// and navigation with <see cref="NavigationDirection"/>.
/// </summary>
public sealed class WizardOrchestratorTests : WizardStepTestBase
{

    [Fact]
    public void Constructor_EntersFirstStep_Forward()
    {
        var steps = CreateSteps("a", "b", "c");
        using var orchestrator = new WizardOrchestrator(steps, Context);

        Assert.Equal("a", orchestrator.CurrentStep!.StepId);
        Assert.Equal(NavigationDirection.Forward, ((FakeStep)steps[0]).LastEntryDirection);
    }

    [Fact]
    public void ActiveStepCount_ReflectsApplicableSteps()
    {
        var steps = CreateSteps("a", "b", "c");
        using var orchestrator = new WizardOrchestrator(steps, Context);

        Assert.Equal(3, orchestrator.ActiveStepCount);
    }

    [Fact]
    public void GoNext_AdvancesToNextStep_WhenCurrentStepComplete()
    {
        var steps = CreateSteps("a", "b", "c");
        // Steps with 0 sub-steps always return false from TryAdvance
        using var orchestrator = new WizardOrchestrator(steps, Context);

        Assert.True(orchestrator.GoNext());
        Assert.Equal("b", orchestrator.CurrentStep!.StepId);
        Assert.Equal(NavigationDirection.Forward, ((FakeStep)steps[1]).LastEntryDirection);
    }

    [Fact]
    public void GoNext_HandlesSubSteps()
    {
        var steps = CreateSteps("a", "b", "c");
        ((FakeStep)steps[0]).SetSubStepCount(3); // 3 sub-steps

        using var orchestrator = new WizardOrchestrator(steps, Context);

        // First advance: sub-step 0 → 1 (handled internally)
        Assert.True(orchestrator.GoNext());
        Assert.Equal("a", orchestrator.CurrentStep!.StepId);
        Assert.Equal(1, orchestrator.CurrentStep.CurrentSubStep);

        // Second advance: sub-step 1 → 2
        Assert.True(orchestrator.GoNext());
        Assert.Equal("a", orchestrator.CurrentStep!.StepId);
        Assert.Equal(2, orchestrator.CurrentStep.CurrentSubStep);

        // Third advance: step complete → move to b
        Assert.True(orchestrator.GoNext());
        Assert.Equal("b", orchestrator.CurrentStep!.StepId);
    }

    [Fact]
    public void GoNext_ReturnsFalse_AtEnd()
    {
        var steps = CreateSteps("a");
        using var orchestrator = new WizardOrchestrator(steps, Context);

        Assert.False(orchestrator.GoNext()); // only one step, already complete
    }

    [Fact]
    public void SingleStepMode_GoNext_ReturnsFalse_AfterCurrentStepCompletes()
    {
        var steps = CreateSteps("a", "b");
        using var orchestrator = new WizardOrchestrator(steps, Context, singleStepMode: true);

        Assert.False(orchestrator.GoNext());
        Assert.Equal("a", orchestrator.CurrentStep!.StepId);
    }

    [Fact]
    public void GoNext_SkipsNonApplicableSteps()
    {
        var steps = CreateSteps("a", "b", "c");
        ((FakeStep)steps[1]).Applicable = false; // b is not applicable

        using var orchestrator = new WizardOrchestrator(steps, Context);

        Assert.Equal(2, orchestrator.ActiveStepCount);
        Assert.True(orchestrator.GoNext());
        Assert.Equal("c", orchestrator.CurrentStep!.StepId); // skipped b
    }

    [Fact]
    public void GoBack_ReturnsToPreviousStep_WithBackDirection()
    {
        var steps = CreateSteps("a", "b", "c");
        using var orchestrator = new WizardOrchestrator(steps, Context);

        orchestrator.GoNext(); // → b
        Assert.Equal("b", orchestrator.CurrentStep!.StepId);

        Assert.True(orchestrator.GoBack());
        Assert.Equal("a", orchestrator.CurrentStep!.StepId);
        Assert.Equal(NavigationDirection.Back, ((FakeStep)steps[0]).LastEntryDirection);
    }

    [Fact]
    public void GoBack_ResumesAtLastSubStep()
    {
        var steps = CreateSteps("a", "b");
        ((FakeStep)steps[0]).SetSubStepCount(3);

        using var orchestrator = new WizardOrchestrator(steps, Context);

        // Advance through all sub-steps of a, then to b
        orchestrator.GoNext(); // sub-step 1
        orchestrator.GoNext(); // sub-step 2
        orchestrator.GoNext(); // → b
        Assert.Equal("b", orchestrator.CurrentStep!.StepId);

        // Go back to a — should resume at last sub-step (2)
        Assert.True(orchestrator.GoBack());
        Assert.Equal("a", orchestrator.CurrentStep!.StepId);
        Assert.Equal(2, orchestrator.CurrentStep.CurrentSubStep);
    }

    [Fact]
    public void GoBack_HandlesSubStepsWithinStep()
    {
        var steps = CreateSteps("a", "b");
        ((FakeStep)steps[1]).SetSubStepCount(3);

        using var orchestrator = new WizardOrchestrator(steps, Context);

        orchestrator.GoNext(); // → b (sub-step 0)
        orchestrator.GoNext(); // b sub-step 1
        orchestrator.GoNext(); // b sub-step 2
        Assert.Equal("b", orchestrator.CurrentStep!.StepId);
        Assert.Equal(2, orchestrator.CurrentStep.CurrentSubStep);

        // Go back within b: sub-step 2 → 1
        Assert.True(orchestrator.GoBack());
        Assert.Equal("b", orchestrator.CurrentStep!.StepId);
        Assert.Equal(1, orchestrator.CurrentStep.CurrentSubStep);

        // Go back within b: sub-step 1 → 0
        Assert.True(orchestrator.GoBack());
        Assert.Equal("b", orchestrator.CurrentStep!.StepId);
        Assert.Equal(0, orchestrator.CurrentStep.CurrentSubStep);

        // Go back from b sub-step 0 → previous step a
        Assert.True(orchestrator.GoBack());
        Assert.Equal("a", orchestrator.CurrentStep!.StepId);
    }

    [Fact]
    public void GoBack_ReturnsFalse_AtBeginning()
    {
        var steps = CreateSteps("a", "b");
        using var orchestrator = new WizardOrchestrator(steps, Context);

        Assert.False(orchestrator.GoBack()); // already at first step
    }

    [Fact]
    public void GoBack_SkipsNonApplicableSteps()
    {
        var steps = CreateSteps("a", "b", "c");
        ((FakeStep)steps[1]).Applicable = false;

        using var orchestrator = new WizardOrchestrator(steps, Context);

        orchestrator.GoNext(); // → c (skipping b)
        Assert.Equal("c", orchestrator.CurrentStep!.StepId);

        Assert.True(orchestrator.GoBack());
        Assert.Equal("a", orchestrator.CurrentStep!.StepId); // skipped b going back
    }

    [Fact]
    public void GetDisplayStepNumber_ReturnsOneBased()
    {
        var steps = CreateSteps("a", "b", "c");
        using var orchestrator = new WizardOrchestrator(steps, Context);

        Assert.Equal(1, orchestrator.GetDisplayStepNumber());

        orchestrator.GoNext();
        Assert.Equal(2, orchestrator.GetDisplayStepNumber());

        orchestrator.GoNext();
        Assert.Equal(3, orchestrator.GetDisplayStepNumber());
    }

    [Fact]
    public void GetDisplayStepNumber_ById_AccountsForSkippedSteps()
    {
        var steps = CreateSteps("a", "b", "c");
        ((FakeStep)steps[1]).Applicable = false;

        using var orchestrator = new WizardOrchestrator(steps, Context);

        Assert.Equal(1, orchestrator.GetDisplayStepNumber("a"));
        Assert.Equal(-1, orchestrator.GetDisplayStepNumber("b")); // not active
        Assert.Equal(2, orchestrator.GetDisplayStepNumber("c"));
    }

    [Fact]
    public void CurrentStepIndex_UpdatesOnNavigation()
    {
        var steps = CreateSteps("a", "b", "c");
        using var orchestrator = new WizardOrchestrator(steps, Context);

        Assert.Equal(0, orchestrator.CurrentStepIndex.Value);

        orchestrator.GoNext(); // → b
        Assert.Equal(1, orchestrator.CurrentStepIndex.Value);

        orchestrator.GoNext(); // → c
        Assert.Equal(2, orchestrator.CurrentStepIndex.Value);

        orchestrator.GoBack(); // → b
        Assert.Equal(1, orchestrator.CurrentStepIndex.Value);
    }

    [Fact]
    public void StepApplicability_ReevaluatedOnTransition()
    {
        var steps = CreateSteps("a", "channels", "c");
        var channelsStep = (FakeStep)steps[1];

        // Channels starts as applicable
        channelsStep.Applicable = true;
        using var orchestrator = new WizardOrchestrator(steps, Context);
        Assert.Equal(3, orchestrator.ActiveStepCount);

        // Step a completes and sets AnyChatServicesEnabled = false
        // Make channels not applicable
        channelsStep.Applicable = false;

        orchestrator.GoNext(); // → c (channels now skipped)
        Assert.Equal("c", orchestrator.CurrentStep!.StepId);
        Assert.Equal(2, orchestrator.ActiveStepCount);
    }

    [Fact]
    public void Dispose_DisposesAllSteps()
    {
        var steps = CreateSteps("a", "b");
        var orchestrator = new WizardOrchestrator(steps, Context);

        orchestrator.Dispose();

        Assert.True(((FakeStep)steps[0]).Disposed);
        Assert.True(((FakeStep)steps[1]).Disposed);
    }

    [Fact]
    public void OnLeave_CalledOnCurrentStep_WhenAdvancing()
    {
        var steps = CreateSteps("a", "b");
        using var orchestrator = new WizardOrchestrator(steps, Context);

        orchestrator.GoNext();

        Assert.True(((FakeStep)steps[0]).LeftCalled);
    }

    [Fact]
    public void OnLeave_CalledOnCurrentStep_WhenGoingBack()
    {
        var steps = CreateSteps("a", "b");
        using var orchestrator = new WizardOrchestrator(steps, Context);

        orchestrator.GoNext(); // → b
        orchestrator.GoBack(); // → a

        Assert.True(((FakeStep)steps[1]).LeftCalled);
    }

    [Fact]
    public void WriteConfig_OnlyCallsContributeConfig_OnActiveSteps()
    {
        var steps = CreateSteps("a", "skipped", "c");
        ((FakeStep)steps[1]).Applicable = false;

        using var orchestrator = new WizardOrchestrator(steps, Context);

        // Advance to end so all active steps are entered
        orchestrator.GoNext(); // a → c (skipping "skipped")

        orchestrator.WriteConfig();

        Assert.True(((FakeStep)steps[0]).ContributeConfigCalled, "Active step 'a' should have ContributeConfig called");
        Assert.False(((FakeStep)steps[1]).ContributeConfigCalled, "Non-applicable step 'skipped' should NOT have ContributeConfig called");
        Assert.True(((FakeStep)steps[2]).ContributeConfigCalled, "Active step 'c' should have ContributeConfig called");
    }

    [Fact]
    public void WriteConfig_OnlyCallsContributeSecrets_OnActiveSteps()
    {
        var steps = CreateSteps("a", "skipped", "c");
        ((FakeStep)steps[1]).Applicable = false;

        using var orchestrator = new WizardOrchestrator(steps, Context);
        orchestrator.GoNext();

        orchestrator.WriteConfig();

        Assert.True(((FakeStep)steps[0]).ContributeSecretsCalled);
        Assert.False(((FakeStep)steps[1]).ContributeSecretsCalled);
        Assert.True(((FakeStep)steps[2]).ContributeSecretsCalled);
    }

    [Fact]
    public void StatusMessage_ClearedOnNavigation()
    {
        var steps = CreateSteps("a", "b");
        using var orchestrator = new WizardOrchestrator(steps, Context);

        Context.StatusMessage.Value = "some error";
        orchestrator.GoNext();
        Assert.Equal("", Context.StatusMessage.Value);
    }

    // ── Helpers ──

    private static List<IWizardStepViewModel> CreateSteps(params string[] ids)
        => [.. ids.Select(id => (IWizardStepViewModel)new FakeStep(id))];

    /// <summary>
    /// Minimal fake step for testing the orchestrator.
    /// </summary>
    private sealed class FakeStep : IWizardStepViewModel
    {
        private int _currentSubStep;
        private int _subStepCount = 1;

        public FakeStep(string stepId)
        {
            StepId = stepId;
            DisplayTitle = stepId;
        }

        public string StepId { get; }
        public string DisplayTitle { get; }
        public bool Applicable { get; set; } = true;
        public bool Disposed { get; private set; }
        public bool LeftCalled { get; private set; }
        public NavigationDirection? LastEntryDirection { get; private set; }

        public bool IsApplicable(WizardContext context) => Applicable;

        public int CurrentSubStep => _currentSubStep;
        public int SubStepCount => _subStepCount;

        public void SetSubStepCount(int count) => _subStepCount = count;

        public string GetHelpText() => $"Help for {StepId} sub-step {_currentSubStep}";

        public bool TryAdvance()
        {
            if (_currentSubStep < _subStepCount - 1)
            {
                _currentSubStep++;
                return true;
            }
            return false; // step complete
        }

        public bool TryGoBack()
        {
            if (_currentSubStep > 0)
            {
                _currentSubStep--;
                return true;
            }
            return false; // at first sub-step
        }

        public void OnEnter(WizardContext context, NavigationDirection direction)
        {
            LastEntryDirection = direction;
            LeftCalled = false;
            _currentSubStep = direction == NavigationDirection.Back
                ? _subStepCount - 1
                : 0;
        }

        public void OnLeave()
        {
            LeftCalled = true;
        }

        public bool ContributeConfigCalled { get; private set; }
        public bool ContributeSecretsCalled { get; private set; }

        public void ContributeConfig(WizardConfigBuilder builder) { ContributeConfigCalled = true; }
        public void ContributeSecrets(WizardSecretsBuilder builder) { ContributeSecretsCalled = true; }
        public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct) => Task.CompletedTask;

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
