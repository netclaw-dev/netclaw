// -----------------------------------------------------------------------
// <copyright file="ToolRegistrationExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Tests for MCP tool registration with description truncation.
/// Schema warning tests require real McpClientTool instances (MCP SDK) and are
/// covered by integration tests in Netclaw.Daemon.Tests.
/// </summary>
public class ToolRegistrationExtensionsTests
{
    [Fact]
    public void McpToolAdapter_Truncated_SanitizedAIFunction_UsesClampedDescription()
    {
        var longDescription = new string('y', 10000);
        var fakeTool = AIFunctionFactory.Create(() => "result", "big_tool", longDescription);
        var adapter = new McpToolAdapter(fakeTool, "notion", "big_tool", maxDescriptionChars: 2048);

        // The AITool exposed to the LLM should have the truncated description
        var aiTool = adapter.ToAITool();
        var aiFunc = Assert.IsAssignableFrom<AIFunction>(aiTool);
        Assert.Equal(2048 + " [truncated]".Length, aiFunc.Description.Length);
        Assert.EndsWith(" [truncated]", aiFunc.Description);

        // Verify adapter.Description matches what the LLM sees
        Assert.Equal(adapter.Description, aiFunc.Description);
    }
}
