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
        => Complete(context, result, ToolInvocationOutcomeCategory.Success);

    public static string SuccessFile(
        this ToolInvocationContext context,
        string result,
        string canonicalPath,
        ToolFileActivityKind kind)
        => Complete(context, result, ToolInvocationOutcomeCategory.Success,
            [new ToolFileActivity(canonicalPath, kind)]);

    public static string SuccessFiles(
        this ToolInvocationContext context,
        string result,
        IEnumerable<string> canonicalPaths,
        ToolFileActivityKind kind)
        => Complete(
            context,
            result,
            ToolInvocationOutcomeCategory.Success,
            [.. canonicalPaths.Select(path => new ToolFileActivity(path, kind))]);

    public static string SuccessProject(
        this ToolInvocationContext context,
        string result,
        string canonicalProjectDirectory)
        => Complete(
            context,
            result,
            ToolInvocationOutcomeCategory.Success,
            declaredProjectDirectory: canonicalProjectDirectory);

    public static string SuccessProjectChange(
        this ToolInvocationContext context,
        string result,
        string canonicalChangedPath,
        string canonicalProjectDirectory)
        => Complete(
            context,
            result,
            ToolInvocationOutcomeCategory.Success,
            [new ToolFileActivity(canonicalChangedPath, ToolFileActivityKind.Changed)],
            declaredProjectDirectory: canonicalProjectDirectory);

    public static string InvalidInput(this ToolInvocationContext context, string result)
        => Complete(context, result, ToolInvocationOutcomeCategory.InvalidInput);

    public static string AccessDenied(this ToolInvocationContext context, string result)
        => Complete(context, result, ToolInvocationOutcomeCategory.AccessDenied);

    public static string NotFound(this ToolInvocationContext context, string result)
        => Complete(context, result, ToolInvocationOutcomeCategory.NotFound);

    public static string TransientFailure(this ToolInvocationContext context, string result)
        => Complete(context, result, ToolInvocationOutcomeCategory.TransientFailure);

    public static string RecoverableCorrection(
        this ToolInvocationContext context,
        string result,
        ToolRemediationCode remediationCode)
        => Complete(
            context,
            result,
            ToolInvocationOutcomeCategory.RecoverableCorrection,
            remediationCode: remediationCode);

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
        ToolInvocationOutcomeCategory category,
        IReadOnlyList<ToolFileActivity>? fileActivity = null,
        ToolRemediationCode? remediationCode = null,
        string? declaredProjectDirectory = null)
    {
        context.TryComplete(new ToolInvocationReceipt(
            category,
            fileActivity,
            remediationCode,
            declaredProjectDirectory));
        return result;
    }
}
