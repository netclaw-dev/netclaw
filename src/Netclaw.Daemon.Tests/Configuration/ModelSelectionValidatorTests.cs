using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ModelSelectionValidatorTests
{
    private static readonly ModelSelectionValidator Validator = new();

    [Fact]
    public void NoContextWindow_Passes()
    {
        var selection = new ModelSelection { Main = new ModelReference() };

        var result = Validator.Validate(null, selection);

        Assert.False(result.Failed);
    }

    [Fact]
    public void ContextWindowAtMinimum_Passes()
    {
        var selection = new ModelSelection { Main = new ModelReference { ContextWindow = 4096 } };

        var result = Validator.Validate(null, selection);

        Assert.False(result.Failed);
    }

    [Fact]
    public void ContextWindowBelowMinimum_Fails()
    {
        var selection = new ModelSelection { Main = new ModelReference { ContextWindow = 2048 } };

        var result = Validator.Validate(null, selection);

        Assert.True(result.Failed);
        Assert.Contains("2048", result.FailureMessage);
        Assert.Contains("4096", result.FailureMessage);
    }

    [Fact]
    public void MultipleRolesBelowMinimum_AllFailuresReported()
    {
        var selection = new ModelSelection
        {
            Main = new ModelReference { ContextWindow = 1024 },
            Compaction = new ModelReference { ContextWindow = 512 }
        };

        var result = Validator.Validate(null, selection);

        Assert.True(result.Failed);
        Assert.Contains("Main", result.FailureMessage);
        Assert.Contains("Compaction", result.FailureMessage);
    }
}
