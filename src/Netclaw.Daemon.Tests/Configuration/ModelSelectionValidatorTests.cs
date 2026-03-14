using Netclaw.Configuration;
using Xunit;
using Netclaw.Daemon.Configuration;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ModelSelectionValidatorTests
{
    private static readonly ModelSelectionValidator Validator = new();

    [Fact]
    public void NoOverride_Passes()
    {
        var selection = new ModelSelection { Main = new ModelReference() };
        var result = Validator.Validate(null, selection);
        Assert.False(result.Failed);
    }

    [Fact]
    public void OverrideAtMinimum_Passes()
    {
        var selection = new ModelSelection { Main = new ModelReference { ContextWindowOverride = 8192 } };
        var result = Validator.Validate(null, selection);
        Assert.False(result.Failed);
    }

    [Fact]
    public void OverrideAboveMinimum_Passes()
    {
        var selection = new ModelSelection { Main = new ModelReference { ContextWindowOverride = 65536 } };
        var result = Validator.Validate(null, selection);
        Assert.False(result.Failed);
    }

    [Fact]
    public void OverrideBelowMinimum_Fails()
    {
        var selection = new ModelSelection { Main = new ModelReference { ContextWindowOverride = 4096 } };
        var result = Validator.Validate(null, selection);
        Assert.True(result.Failed);
        Assert.Contains("4096", result.FailureMessage);
        Assert.Contains("8192", result.FailureMessage);
    }

    [Fact]
    public void MultipleRolesBelowMinimum_AllFailuresReported()
    {
        var selection = new ModelSelection
        {
            Main = new ModelReference { ContextWindowOverride = 1024 },
            Compaction = new ModelReference { ContextWindowOverride = 512 }
        };
        var result = Validator.Validate(null, selection);
        Assert.True(result.Failed);
        Assert.Contains("Main", result.FailureMessage);
        Assert.Contains("Compaction", result.FailureMessage);
    }

    [Fact]
    public void NullOptionalRoles_DoNotCauseErrors()
    {
        var selection = new ModelSelection
        {
            Main = new ModelReference { ContextWindowOverride = 32768 },
            Fallback = null,
            Compaction = null
        };
        var result = Validator.Validate(null, selection);
        Assert.False(result.Failed);
    }
}
