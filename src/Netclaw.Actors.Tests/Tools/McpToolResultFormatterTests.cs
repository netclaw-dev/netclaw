// -----------------------------------------------------------------------
// <copyright file="McpToolResultFormatterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
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
    public void Typed_error_completes_a_transient_failure_receipt()
    {
        var result = Json("""{"content":[{"type":"text","text":"declared failure"}],"isError":true}""");
        var context = TestToolExecutionContext.CreateBound(
            "test/thread",
            null,
            TrustAudience.Personal);

        var message = McpToolResultFormatter.FormatWithReceipt(
            result,
            "srv/tool",
            context.Invocation);

        Assert.Contains("declared failure", message);
        Assert.Equal(ToolInvocationOutcomeCategory.TransientFailure, context.Receipt?.Category);
        Assert.IsType<ToolInvocationReceipt.OtherOutcome>(context.Receipt);
    }

    [Fact]
    public void Error_prefix_with_typed_success_keeps_the_success_path()
    {
        var result = Json("""{"content":[{"type":"text","text":"Error: this is data"}],"isError":false}""");
        var context = TestToolExecutionContext.CreateBound(
            "test/thread",
            null,
            TrustAudience.Personal);

        var message = McpToolResultFormatter.FormatWithReceipt(
            result,
            "srv/tool",
            context.Invocation);

        Assert.Equal("Error: this is data", message);
        Assert.Null(context.Receipt);
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
    public void Structured_success_with_image_preserves_marker_and_structured_content()
    {
        var result = Json("""{"content":[{"type":"image","data":"AQID","mimeType":"image/png"}],"structuredContent":{"caption":"critical detail"},"isError":false}""");

        var message = McpToolResultFormatter.Format(result, "srv/tool");

        Assert.Equal("[image: image/png]\n{\"caption\":\"critical detail\"}", message);
        Assert.DoesNotContain("AQID", message);
    }

    [Fact]
    public void Metadata_success_projects_content_without_binary_data()
    {
        var result = Json("""
                          {
                            "content": [
                              { "type": "image", "data": "AQID", "mimeType": "image/png" }
                            ],
                            "isError": false,
                            "_meta": { "vendor/example": true }
                          }
                          """);

        var message = McpToolResultFormatter.Format(result, "srv/tool");

        Assert.Equal("[image: image/png]", message);
        Assert.DoesNotContain("AQID", message);
        Assert.DoesNotContain("_meta", message);
    }

    [Theory]
    [InlineData(
        """{"content":[{"type":"audio","data":"SECRET","mimeType":"audio/wav"}],"isError":false,"_meta":{}}""",
        "[attachment: audio/wav]")]
    [InlineData(
        """{"content":[{"type":"resource","resource":{"uri":"memory://notes","text":"resource-notes"}}],"isError":false,"_meta":{}}""",
        "resource-notes")]
    [InlineData(
        """{"content":[{"type":"resource","resource":{"uri":"memory://report","blob":"SECRET","mimeType":"application/pdf"}}],"isError":false,"_meta":{}}""",
        "[attachment: application/pdf]")]
    [InlineData(
        """{"content":[{"type":"resource","resource":{"uri":"memory://chart","blob":"SECRET","mimeType":"image/png"}}],"isError":false,"_meta":{}}""",
        "[image: image/png]")]
    [InlineData(
        """{"content":[{"type":"resource_link","uri":"memory://notes","name":"notes"}],"isError":false}""",
        "[unsupported MCP content: resource_link]")]
    [InlineData(
        """{"content":[{"type":"text"}],"isError":false}""",
        "[unsupported MCP content: text]")]
    [InlineData(
        """{"content":[42],"isError":false}""",
        "[unsupported MCP content: unknown]")]
    public void Json_content_projection_is_explicit_and_excludes_binary_data(
        string json,
        string expected)
    {
        var message = McpToolResultFormatter.Format(Json(json), "srv/tool");

        Assert.Equal(expected, message);
        Assert.DoesNotContain("SECRET", message);
    }

    [Fact]
    public void Success_without_model_readable_content_reports_that_state()
    {
        var result = Json("""{"content":[],"isError":false,"_meta":{"vendor/example":true}}""");

        var message = McpToolResultFormatter.Format(result, "srv/tool");

        Assert.Equal("MCP tool 'srv/tool' returned no model-readable content.", message);
        Assert.DoesNotContain("vendor/example", message);
    }

    [Fact]
    public void Plain_string_result_is_passed_through()
        => Assert.Equal("Message sent.", McpToolResultFormatter.Format("Message sent.", "srv/tool"));

    [Fact]
    public void Null_result_is_empty()
        => Assert.Equal(string.Empty, McpToolResultFormatter.Format(null, "srv/tool"));

    [Fact]
    public void Multi_content_AIContent_array_projects_text_and_image_marker()
    {
        var chartJson = """{"title":"Example title","series":[]}""";
        var result = new AIContent[]
        {
            new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
            new TextContent(chartJson),
        };

        var message = McpToolResultFormatter.Format(result, "srv/chart");

        Assert.Equal($"[image: image/png]\n{chartJson}", message);
        Assert.DoesNotContain("AIContent", message);
    }

    [Fact]
    public void Image_only_AIContent_array_projects_marker_only()
    {
        var result = new AIContent[] { new DataContent(Array.Empty<byte>(), "image/jpeg") };

        Assert.Equal("[image: image/jpeg]", McpToolResultFormatter.Format(result, "srv/tool"));
    }

    [Fact]
    public void Text_only_AIContent_array_projects_text()
    {
        var result = new AIContent[] { new TextContent("hello world") };

        Assert.Equal("hello world", McpToolResultFormatter.Format(result, "srv/tool"));
    }

    [Fact]
    public void Single_TextContent_is_passed_through()
        => Assert.Equal("done", McpToolResultFormatter.Format(new TextContent("done"), "srv/tool"));

    [Fact]
    public void Single_DataContent_projects_marker_without_binary_data()
    {
        var result = new DataContent(new byte[] { 1, 2, 3 }, "image/png");

        Assert.Equal("[image: image/png]", McpToolResultFormatter.Format(result, "srv/tool"));
    }

    [Fact]
    public void Non_image_DataContent_projects_attachment_marker()
    {
        var result = new AIContent[] { new DataContent(new byte[] { 4, 5 }, "application/pdf") };

        Assert.Equal("[attachment: application/pdf]", McpToolResultFormatter.Format(result, "srv/tool"));
    }

    [Fact]
    public void Unsupported_AIContent_is_reported_instead_of_dropped()
    {
        var result = new AIContent[]
        {
            new TextContent("before"),
            new FunctionCallContent("call-1", "nested-tool"),
            new FunctionResultContent("call-1", "nested-result"),
            new TextContent("after"),
        };

        var message = McpToolResultFormatter.Format(result, "srv/tool");

        Assert.Equal(
            "before\n[unsupported MCP content: FunctionCallContent]" +
            "\n[unsupported MCP content: FunctionResultContent]\nafter",
            message);
    }
}
