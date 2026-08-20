// -----------------------------------------------------------------------
// <copyright file="ToolOutcomeResults.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal static class ToolOutcomeResults
{
    public const string SetWorkingDirectoryRemediation = "set_working_directory";
    public const string UseSessionScratchRemediation = "use_session_scratch";

    public static string Success(this ToolInvocationContext context, string result)
        => Complete(context, result, ToolInvocationOutcomeCategory.Success);

    public static string SuccessFile(
        this ToolInvocationContext context,
        string result,
        string canonicalPath,
        ToolFileActivityKind kind)
        => Complete(context, result, ToolInvocationOutcomeCategory.Success,
            [new ToolFileActivity(canonicalPath, kind)]);

    public static string SuccessProject(
        this ToolInvocationContext context,
        string result,
        string canonicalProjectDirectory)
        => Complete(
            context,
            result,
            ToolInvocationOutcomeCategory.Success,
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
        string remediationCode)
        => Complete(
            context,
            result,
            ToolInvocationOutcomeCategory.RecoverableCorrection,
            remediationCode: remediationCode);

    public static string PathResolutionFailure(
        this ToolInvocationContext context,
        string result,
        ScopedFileAccessPolicy.PathResolutionFailure failure)
        => failure switch
        {
            ScopedFileAccessPolicy.PathResolutionFailure.MissingBase =>
                context.RecoverableCorrection(result, SetWorkingDirectoryRemediation),
            ScopedFileAccessPolicy.PathResolutionFailure.InvalidInput => context.InvalidInput(result),
            _ => context.AccessDenied(result)
        };

    private static string Complete(
        ToolInvocationContext context,
        string result,
        ToolInvocationOutcomeCategory category,
        IReadOnlyList<ToolFileActivity>? fileActivity = null,
        string? remediationCode = null,
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
