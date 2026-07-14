// -----------------------------------------------------------------------
// <copyright file="GeneratedToolStrictBindingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Generated ParseArguments must bind through the strict helpers: a
/// present-but-invalid value surfaces as a model-facing parse error (via
/// NetclawTool.TryParse) and the tool never executes — instead of the old
/// silent coercion to 0/0.0/false (tool-arg-validation spec).
/// </summary>
public class GeneratedToolStrictBindingTests
{
    private static FileReadTool NewFileReadTool() => new(new ToolConfig(), new NetclawPaths(), new Netclaw.Security.ToolPathPolicy([]));

    private static ToolExecutionContext PersonalContext()
        => TestToolExecutionContext.CreateBound("signalr/thread-1", Path.GetTempPath(), new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "signalr"
        });

    [Fact]
    public async Task Invalid_int_value_surfaces_parse_error_and_does_not_execute()
    {
        var tool = NewFileReadTool();
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Path"] = "/tmp/does-not-matter.txt",
            ["Limit"] = "abc"
        }, TestToolExecutionContext.CreateUnbound(), TestContext.Current.CancellationToken);

        Assert.Contains("Error parsing arguments for tool 'file_read'", result);
        Assert.Contains("'Limit'", result);
        Assert.Contains("'abc'", result);
        Assert.Contains("integer", result);
    }

    [Fact]
    public async Task Non_integral_json_number_surfaces_parse_error_not_truncation()
    {
        var tool = NewFileReadTool();
        var limit = JsonDocument.Parse("12.5").RootElement.Clone();
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Path"] = "/tmp/does-not-matter.txt",
            ["Limit"] = limit
        }, TestToolExecutionContext.CreateUnbound(), TestContext.Current.CancellationToken);

        Assert.Contains("Error parsing arguments", result);
        Assert.Contains("'Limit'", result);
    }

    [Fact]
    public async Task Absent_optional_int_keeps_default_and_executes()
    {
        var tool = NewFileReadTool();
        var path = Path.Combine(Path.GetTempPath(), $"strict-binding-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "line1\nline2\n", TestContext.Current.CancellationToken);
        try
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Path"] = path
            }, PersonalContext(), TestContext.Current.CancellationToken);

            Assert.DoesNotContain("Error parsing arguments", result);
            Assert.Contains("line1", result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Valid_string_number_still_binds()
    {
        var tool = NewFileReadTool();
        var path = Path.Combine(Path.GetTempPath(), $"strict-binding-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "line1\nline2\nline3\n", TestContext.Current.CancellationToken);
        try
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["Path"] = path,
                ["StartLine"] = "2",
                ["Limit"] = 1
            }, PersonalContext(), TestContext.Current.CancellationToken);

            Assert.DoesNotContain("Error parsing arguments", result);
            Assert.Contains("line2", result);
            Assert.DoesNotContain("line3", result);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
