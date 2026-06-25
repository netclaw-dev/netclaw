// -----------------------------------------------------------------------
// <copyright file="StreamingToolCallTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tests.Memory;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions.Pipelines;

/// <summary>
/// Unit test for the <c>INetclawTool</c> default streaming adapter: a tool that does
/// not override <c>ExecuteStreamAsync</c> yields exactly one terminal completion item
/// wrapping its non-streaming result. Opaque- and self-monitoring-tool liveness is
/// covered at the pipeline level in SessionToolExecutionPipelineTests.
/// </summary>
public sealed class StreamingToolCallTests
{
    [Fact]
    public async Task Non_streaming_tool_yields_one_terminal_completion_item()
    {
        // A tool that does not override ExecuteStreamAsync inherits the
        // INetclawTool default: exactly one terminal completion item.
        INetclawTool tool = new FakeNetclawTool("greet", "hello there");

        var updates = new List<ToolCallUpdate>();
        await foreach (var update in tool.ExecuteStreamAsync(
            new Dictionary<string, object?>(), ToolExecutionContext.Empty, TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        var completed = Assert.IsType<ToolCompletedUpdate>(Assert.Single(updates));
        Assert.Equal("hello there", completed.Result);
    }
}
