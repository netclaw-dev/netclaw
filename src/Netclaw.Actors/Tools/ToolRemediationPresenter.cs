// -----------------------------------------------------------------------
// <copyright file="ToolRemediationPresenter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Tools;

internal static class ToolRemediationPresenter
{
    public static SerializableChatMessage Present(
        SerializableChatMessage message,
        ToolInvocationReceipt? receipt,
        bool setWorkingDirectoryAvailable)
    {
        if (receipt?.RemediationCode is not { } remediationCode)
            return message;

        var action = remediationCode switch
        {
            ToolRemediationCode.SetWorkingDirectory when setWorkingDirectoryAvailable =>
                "Next action: call set_working_directory with an allowed project directory for this task, then retry the failed tool call.",
            ToolRemediationCode.SetWorkingDirectory => null,
            ToolRemediationCode.UseManagedTemporaryDirectory =>
                "Next action: use the managed temporary directory from this result for disposable files, or retry unchanged for exact platform paths.",
            ToolRemediationCode.ProvideUniqueOldString =>
                "Next action: retry file_edit with a unique OldString, or set ReplaceAll=true when every match should change.",
            ToolRemediationCode.UseNativeTool =>
                "Next action: call the native Netclaw tool named in this result directly instead of shell_execute.",
            _ => throw new InvalidOperationException("Unsupported tool remediation code.")
        };

        if (action is null)
            return message;

        var content = string.IsNullOrWhiteSpace(message.Content)
            ? action
            : $"{message.Content}\n{action}";
        return message with { Content = content };
    }
}
