// -----------------------------------------------------------------------
// <copyright file="ToolOutputSpillTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ToolOutputSpillTests : IDisposable
{
    private readonly string _sessionDir =
        Path.Combine(Path.GetTempPath(), "nc-spill-" + Guid.NewGuid().ToString("N"));

    public ToolOutputSpillTests() => Directory.CreateDirectory(_sessionDir);

    public void Dispose()
    {
        if (Directory.Exists(_sessionDir))
            Directory.Delete(_sessionDir, recursive: true);
    }

    private ToolInvocationContext Context() =>
        TestToolExecutionContext.CreateBound("session/thread", _sessionDir, new TestToolExecutionContextOptions
        { Audience = TrustAudience.Personal }).Invocation;

    private string ToolCallsDir => Path.Combine(_sessionDir, "tool-calls");

    // BoundAndSpillAsync receives an ALREADY-redacted result (the dispatcher redacts
    // first), so these tests pass content verbatim and assert the bound/spill shape.

    [Fact]
    public async Task Under_budget_returned_unchanged()
    {
        var input = new string('a', 50);
        var result = await ToolOutputSpill.BoundAndSpillAsync(
            input, "call_1", budget: 100, Context(), CancellationToken.None);

        Assert.Equal(input, result);
        Assert.False(Directory.Exists(ToolCallsDir)); // nothing spilled
    }

    [Fact]
    public async Task Over_budget_spills_full_output_and_steers()
    {
        var input = new string('H', 200) + new string('T', 200); // 400 > 100
        var result = await ToolOutputSpill.BoundAndSpillAsync(
            input, "call_2", budget: 100, Context(), CancellationToken.None);

        var spillPath = Path.Combine(ToolCallsDir, "call_2.log");
        Assert.True(File.Exists(spillPath));
        Assert.Equal(input, await File.ReadAllTextAsync(spillPath, CancellationToken.None)); // full output on disk
        Assert.StartsWith(new string('H', 50), result);                                      // inline head
        Assert.Contains("output saved to", result);
        Assert.Contains(spillPath, result);
        Assert.Contains("file_read", result);
        Assert.Contains("grep", result);
    }

    [Fact]
    public async Task Budget_zero_falls_back_to_content_default()
    {
        // budget 0 → DefaultContentBudget (12000); a 5000-char input fits, returned whole.
        var input = new string('x', 5000);
        var result = await ToolOutputSpill.BoundAndSpillAsync(
            input, "call_3", budget: 0, Context(), CancellationToken.None);

        Assert.Equal(input, result);
        Assert.False(Directory.Exists(ToolCallsDir));
    }

    [Fact]
    public async Task No_session_directory_degrades_to_inline_only()
    {
        var ctx = TestToolExecutionContext.CreateBound("session/thread", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
        }).Invocation;
        var input = new string('H', 200) + new string('T', 200);

        var result = await ToolOutputSpill.BoundAndSpillAsync(
            input, "call_5", budget: 100, ctx, CancellationToken.None);

        Assert.StartsWith(new string('H', 50), result);    // inline still produced
        Assert.DoesNotContain("saved to", result);          // but no spill path
    }

    [Fact]
    public async Task Unsafe_call_id_cannot_escape_tool_calls_directory()
    {
        var input = new string('H', 200) + new string('T', 200);
        await ToolOutputSpill.BoundAndSpillAsync(
            input, "../../evil", budget: 100, Context(), CancellationToken.None);

        var written = Directory.GetFiles(ToolCallsDir);
        Assert.Single(written);
        Assert.StartsWith(ToolCallsDir, Path.GetFullPath(written[0]));
    }
}
