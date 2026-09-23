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

/// <summary>
/// Verifies the model-text projection for each result shape that the MCP SDK can return.
/// </summary>
/// <remarks>
/// The SDK returns one <see cref="AIContent"/>, an <see cref="AIContent"/> array,
/// or a <see cref="JsonElement"/> envelope based on the protocol result.
/// </remarks>
public class McpToolResultFormatterTests
{
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Error_result_is_surfaced_as_an_attributed_tool_error()
    {
        // The SDK returns the complete CallToolResult when isError is true.
        // This test proves that the model sees an attributed error instead of protocol JSON.
        // Arrange
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
        // An MCP server can split one failure across several text blocks.
        // This test proves that no error detail disappears.
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
        // Some servers put the only useful failure detail in structuredContent.
        // This test proves that the formatter does not replace it with "no detail".
        // Arrange
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
        // A declared MCP failure can omit both text and structured detail.
        // This test proves that the model still gets an explicit attributed failure.
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
        // isError is a tool-level failure even when the transport call succeeds.
        // This test proves that the receipt records a transient failure.
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
        // Text that starts with "Error:" is not a failure when isError is false.
        // This test proves that protocol state, not text content, controls the receipt.
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
        // structuredContent forces the SDK to return a complete JsonElement envelope.
        // This test proves that readable text remains primary and envelope fields do not leak.
        // Arrange
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
        // Some successful tools return only structuredContent.
        // This test proves that machine-readable detail remains visible without text blocks.
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
        // An image marker must not hide structuredContent when no text block exists.
        // This test also proves that image bytes do not enter the model context.
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
        // Application metadata forces the SDK to return JsonElement for an otherwise simple image.
        // This test proves that metadata and base64 remain outside the model context.
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
        // JsonElement results can contain several protocol block types.
        // Each row proves a safe projection or an explicit unsupported marker.
        // Binary fields use SECRET so any leak fails every relevant row.
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
        // A metadata-only success has no content that the model can use.
        // This test proves that the formatter reports that state instead of raw JSON.
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
        // Non-MCP callers can already return plain strings.
        // This test protects that pass-through contract.
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
        // A tool can return no value.
        // This test protects the current empty-string contract.
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
        // The SDK returns AIContent[] for a multi-block result without extra protocol fields.
        // This test reproduces issue #2051 and proves ordered text plus image projection.
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
        // An image-only AIContent[] has no text fallback.
        // This test proves that the model still receives an artifact marker.
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
        // A text-only AIContent[] must not expose the collection type name.
        // This test proves that the text remains unchanged.
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
        // The SDK returns one AIContent object for one convertible block.
        // This test protects the single-text path that worked before issue #2051.
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
        // The SDK can return one DataContent object for one binary block.
        // This test proves that the formatter emits a marker without bytes.
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
        // Binary data can represent documents or audio, not only images.
        // This test proves that non-image data uses the attachment marker.
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
        // The SDK maps MCP tool-use and tool-result blocks to these AIContent subtypes.
        // This test proves that unsupported content stays explicit and preserves adjacent text.
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
