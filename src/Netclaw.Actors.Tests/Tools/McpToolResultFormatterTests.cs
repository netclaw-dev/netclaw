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
    public void Error_detail_falls_back_to_structured_content_when_no_text_block()
    {
        // The error's actionable detail lives in structuredContent with no text
        // block — a bare content[].text scan would drop it and report "no detail".
        var result = Json("""{"content":[],"structuredContent":{"field":"name","reason":"required"},"isError":true}""");

        var message = McpToolResultFormatter.Format(result, "srv/tool");

        Assert.Contains("reported a failure", message);
        Assert.Contains("required", message);
        Assert.DoesNotContain("no detail provided", message);
    }

    [Fact]
    public void Error_result_without_any_detail_reports_no_detail()
    {
        var result = Json("""{"content":[],"isError":true}""");

        var message = McpToolResultFormatter.Format(result, "srv/tool");

        Assert.Contains("no detail provided", message);
    }

    [Fact]
    public void Structured_success_surfaces_clean_text_not_the_wrapper()
    {
        // Success WITH structuredContent is also serialized to a full
        // CallToolResult; surface the readable text, not the isError:false wrapper.
        var result = Json("""{"content":[{"type":"text","text":"42 results found"}],"structuredContent":{"count":42},"isError":false}""");

        var message = McpToolResultFormatter.Format(result, "srv/tool");

        Assert.Equal("42 results found", message);
        Assert.DoesNotContain("isError", message);
        Assert.DoesNotContain("reported a failure", message);
    }

    [Fact]
    public void Structured_success_without_text_surfaces_the_structured_content()
    {
        var result = Json("""{"content":[],"structuredContent":{"count":42},"isError":false}""");

        var message = McpToolResultFormatter.Format(result, "srv/tool");

        Assert.Contains("42", message);
        Assert.DoesNotContain("reported a failure", message);
    }

    [Fact]
    public void Plain_string_result_is_passed_through()
        => Assert.Equal("Message sent.", McpToolResultFormatter.Format("Message sent.", "srv/tool"));

    [Fact]
    public void Null_result_is_empty()
        => Assert.Equal(string.Empty, McpToolResultFormatter.Format(null, "srv/tool"));
}
