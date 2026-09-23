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
        // Arrange
        // What the MCP SDK hands back when a tool sets isError=true: the whole
        // CallToolResult serialized. Without this formatting the model would see
        // the raw JSON blob and could not tell it from a netclaw failure (#1495).
        var result = Json("""{"content":[{"type":"text","text":"old_string not found"}],"isError":true}""");

        // Act
        var message = McpToolResultFormatter.Format(result, "memorizer/edit");

        // Assert
        Assert.StartsWith("Error: MCP tool 'memorizer/edit' reported a failure:", message);
        Assert.Contains("old_string not found", message);
        Assert.DoesNotContain("isError", message);
    }

    [Fact]
    public void Error_result_with_multiple_text_blocks_joins_them()
    {
        // Arrange
        var result = Json("""{"content":[{"type":"text","text":"line one"},{"type":"text","text":"line two"}],"isError":true}""");

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Contains("line one", message);
        Assert.Contains("line two", message);
    }

    [Fact]
    public void Error_detail_falls_back_to_structured_content_when_no_text_block()
    {
        // Arrange
        // The error's actionable detail lives in structuredContent with no text
        // block — a bare content[].text scan would drop it and report "no detail".
        var result = Json("""{"content":[],"structuredContent":{"field":"name","reason":"required"},"isError":true}""");

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Contains("reported a failure", message);
        Assert.Contains("required", message);
        Assert.DoesNotContain("no detail provided", message);
    }

    [Fact]
    public void Error_result_without_any_detail_reports_no_detail()
    {
        // Arrange
        var result = Json("""{"content":[],"isError":true}""");

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Contains("no detail provided", message);
    }

    [Fact]
    public void Typed_error_completes_a_transient_failure_receipt()
    {
        // Arrange
        var result = Json("""{"content":[{"type":"text","text":"declared failure"}],"isError":true}""");
        var context = TestToolExecutionContext.CreateBound(
            "test/thread",
            null,
            TrustAudience.Personal);

        // Act
        var message = McpToolResultFormatter.FormatWithReceipt(
            result,
            "srv/tool",
            context.Invocation);

        // Assert
        Assert.Contains("declared failure", message);
        Assert.Equal(ToolInvocationOutcomeCategory.TransientFailure, context.Receipt?.Category);
        Assert.IsType<ToolInvocationReceipt.OtherOutcome>(context.Receipt);
    }

    [Fact]
    public void Error_prefix_with_typed_success_keeps_the_success_path()
    {
        // Arrange
        var result = Json("""{"content":[{"type":"text","text":"Error: this is data"}],"isError":false}""");
        var context = TestToolExecutionContext.CreateBound(
            "test/thread",
            null,
            TrustAudience.Personal);

        // Act
        var message = McpToolResultFormatter.FormatWithReceipt(
            result,
            "srv/tool",
            context.Invocation);

        // Assert
        Assert.Equal("Error: this is data", message);
        Assert.Null(context.Receipt);
    }

    [Fact]
    public void Structured_success_surfaces_clean_text_not_the_wrapper()
    {
        // Arrange
        // Success WITH structuredContent is also serialized to a full
        // CallToolResult; surface the readable text, not the isError:false wrapper.
        var result = Json("""{"content":[{"type":"text","text":"42 results found"}],"structuredContent":{"count":42},"isError":false}""");

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Equal("42 results found", message);
        Assert.DoesNotContain("isError", message);
        Assert.DoesNotContain("reported a failure", message);
    }

    [Fact]
    public void Structured_success_without_text_surfaces_the_structured_content()
    {
        // Arrange
        var result = Json("""{"content":[],"structuredContent":{"count":42},"isError":false}""");

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Contains("42", message);
        Assert.DoesNotContain("reported a failure", message);
    }

    [Fact]
    public void Structured_success_with_image_preserves_marker_and_structured_content()
    {
        // Arrange
        var result = Json("""{"content":[{"type":"image","data":"AQID","mimeType":"image/png"}],"structuredContent":{"caption":"critical detail"},"isError":false}""");

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Equal("[image: image/png]\n{\"caption\":\"critical detail\"}", message);
        Assert.DoesNotContain("AQID", message);
    }

    [Fact]
    public void Metadata_success_projects_content_without_binary_data()
    {
        // Arrange
        var result = Json("""
                          {
                            "content": [
                              { "type": "image", "data": "AQID", "mimeType": "image/png" }
                            ],
                            "isError": false,
                            "_meta": { "vendor/example": true }
                          }
                          """);

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
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
        // Arrange
        var result = Json(json);

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Equal(expected, message);
        Assert.DoesNotContain("SECRET", message);
    }

    [Fact]
    public void Success_without_model_readable_content_reports_that_state()
    {
        // Arrange
        var result = Json("""{"content":[],"isError":false,"_meta":{"vendor/example":true}}""");

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Equal("MCP tool 'srv/tool' returned no model-readable content.", message);
        Assert.DoesNotContain("vendor/example", message);
    }

    [Fact]
    public void Plain_string_result_is_passed_through()
    {
        // Arrange
        const string result = "Message sent.";

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Equal("Message sent.", message);
    }

    [Fact]
    public void Null_result_is_empty()
    {
        // Arrange
        object? result = null;

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public void Multi_content_AIContent_array_projects_text_and_image_marker()
    {
        // Arrange
        var chartJson = """{"title":"Example title","series":[]}""";
        var result = new AIContent[]
        {
            new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
            new TextContent(chartJson),
        };

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/chart");

        // Assert
        Assert.Equal($"[image: image/png]\n{chartJson}", message);
        Assert.DoesNotContain("AIContent", message);
    }

    [Fact]
    public void Image_only_AIContent_array_projects_marker_only()
    {
        // Arrange
        var result = new AIContent[] { new DataContent(Array.Empty<byte>(), "image/jpeg") };

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Equal("[image: image/jpeg]", message);
    }

    [Fact]
    public void Text_only_AIContent_array_projects_text()
    {
        // Arrange
        var result = new AIContent[] { new TextContent("hello world") };

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Equal("hello world", message);
    }

    [Fact]
    public void Single_TextContent_is_passed_through()
    {
        // Arrange
        var result = new TextContent("done");

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Equal("done", message);
    }

    [Fact]
    public void Single_DataContent_projects_marker_without_binary_data()
    {
        // Arrange
        var result = new DataContent(new byte[] { 1, 2, 3 }, "image/png");

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Equal("[image: image/png]", message);
    }

    [Fact]
    public void Non_image_DataContent_projects_attachment_marker()
    {
        // Arrange
        var result = new AIContent[] { new DataContent(new byte[] { 4, 5 }, "application/pdf") };

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Equal("[attachment: application/pdf]", message);
    }

    [Fact]
    public void Unsupported_AIContent_is_reported_instead_of_dropped()
    {
        // Arrange
        var result = new AIContent[]
        {
            new TextContent("before"),
            new FunctionCallContent("call-1", "nested-tool"),
            new FunctionResultContent("call-1", "nested-result"),
            new TextContent("after"),
        };

        // Act
        var message = McpToolResultFormatter.Format(result, "srv/tool");

        // Assert
        Assert.Equal(
            "before\n[unsupported MCP content: FunctionCallContent]" +
            "\n[unsupported MCP content: FunctionResultContent]\nafter",
            message);
    }
}
