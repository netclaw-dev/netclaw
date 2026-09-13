// -----------------------------------------------------------------------
// <copyright file="ToolOutcomeResults.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal static class ToolOutcomeResults
{
    public static string Success(this ToolInvocationContext context, string result)
        => Complete(context, result, new ToolInvocationReceipt.Succeeded([], null));

    public static string SuccessFile(
        this ToolInvocationContext context,
        string result,
        string canonicalPath,
        ToolFileActivityKind kind)
        => Complete(context, result, new ToolInvocationReceipt.Succeeded(
            [new ToolFileActivity(canonicalPath, kind)], null));

    public static string SuccessFiles(
        this ToolInvocationContext context,
        string result,
        IEnumerable<string> canonicalPaths,
        ToolFileActivityKind kind)
        => Complete(
            context,
            result,
            new ToolInvocationReceipt.Succeeded(
                [.. canonicalPaths.Select(path => new ToolFileActivity(path, kind))], null));

    public static string SuccessProject(
        this ToolInvocationContext context,
        string result,
        string canonicalProjectDirectory)
        => Complete(
            context,
            result,
            new ToolInvocationReceipt.Succeeded([], canonicalProjectDirectory));

    public static string SuccessProjectChange(
        this ToolInvocationContext context,
        string result,
        string canonicalChangedPath,
        string canonicalProjectDirectory)
        => Complete(
            context,
            result,
            new ToolInvocationReceipt.Succeeded(
                [new ToolFileActivity(canonicalChangedPath, ToolFileActivityKind.Changed)], canonicalProjectDirectory));

    public static string InvalidInput(this ToolInvocationContext context, string result)
        => Complete(context, result, new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.InvalidInput));

    public static string AccessDenied(this ToolInvocationContext context, string result)
        => Complete(context, result, new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.AccessDenied));

    public static string NotFound(this ToolInvocationContext context, string result)
        => Complete(context, result, new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.NotFound));

    public static string TransientFailure(this ToolInvocationContext context, string result)
        => Complete(context, result, new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.TransientFailure));

    public static string RecoverableCorrection(
        this ToolInvocationContext context,
        string result,
        ToolRemediationCode remediationCode)
        => Complete(
            context,
            result,
            new ToolInvocationReceipt.Correction(remediationCode));

    public static string PathAccessFailure(
        this ToolInvocationContext context,
        string result,
        PathAccessPolicy.PathAccessFailure failure)
        => failure switch
        {
            PathAccessPolicy.PathAccessFailure.MissingBase =>
                context.RecoverableCorrection(
                    result,
                    ToolRemediationCode.SetWorkingDirectory),
            PathAccessPolicy.PathAccessFailure.InvalidInput => context.InvalidInput(result),
            _ => context.AccessDenied(result)
        };

    private static string Complete(
        ToolInvocationContext context,
        string result,
        ToolInvocationReceipt receipt)
    {
        context.TryComplete(receipt);
        return result;
    }
}
