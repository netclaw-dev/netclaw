// -----------------------------------------------------------------------
// <copyright file="McpToolResultFormatterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class McpToolResultFormatterTests
{
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Error_result_is_surfaced_as_an_attributed_tool_error()
    {
        // What the MCP SDK hands back when a tool sets isError=true: the whole
        // CallToolResult serialized. Without this formatting the model would see
        // the raw JSON blob and could not tell it from a netclaw failure (#1495).
        var result = Json("""{"content":[{"type":"text","text":"old_string not found"}],"isError":true}""");

        var message = McpToolResultFormatter.Format(result, "memorizer/edit");

        Assert.StartsWith("Error: MCP tool 'memorizer/edit' reported a failure:", message);
        Assert.Contains("old_string not found", message);
        Assert.DoesNotContain("isError", message);
    }

    [Fact]
    public void Error_result_with_multiple_text_blocks_joins_them()
    {
        var result = Json("""{"content":[{"type":"text","text":"line one"},{"type":"text","text":"line two"}],"isError":true}""");

        var message = McpToolResultFormatter.Format(result, "srv/tool");

        Assert.Contains("line one", message);
        Assert.Contains("line two", message);
    }

    [Fact]
    public void Error_result_without_text_reports_no_detail()
    {
        var result = Json("""{"content":[],"isError":true}""");

        var message = McpToolResultFormatter.Format(result, "srv/tool");

        Assert.Contains("no detail provided", message);
    }

    [Fact]
    public void Non_error_json_result_is_passed_through_unchanged()
    {
        // A structured (non-error) result also arrives as a JsonElement — it must
        // NOT be reframed as a failure.
        var result = Json("""{"value":42,"isError":false}""");

        var message = McpToolResultFormatter.Format(result, "srv/tool");

        Assert.DoesNotContain("reported a failure", message);
        Assert.Contains("42", message);
    }

    [Fact]
    public void Plain_string_result_is_passed_through()
        => Assert.Equal("Message sent.", McpToolResultFormatter.Format("Message sent.", "srv/tool"));

    [Fact]
    public void Null_result_is_empty()
        => Assert.Equal(string.Empty, McpToolResultFormatter.Format(null, "srv/tool"));
}
