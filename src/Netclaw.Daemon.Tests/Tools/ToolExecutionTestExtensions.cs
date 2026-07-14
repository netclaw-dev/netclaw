// -----------------------------------------------------------------------
// <copyright file="ToolExecutionTestExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

internal static class ToolExecutionTestExtensions
{
    public static Task<string> ExecuteAsync(
        this INetclawTool tool,
        IDictionary<string, object?>? arguments,
        ToolExecutionContext context,
        CancellationToken ct = default)
        => tool.ExecuteAsync(arguments, context.Invocation, ct);
}
