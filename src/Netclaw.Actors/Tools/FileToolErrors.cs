// -----------------------------------------------------------------------
// <copyright file="FileToolErrors.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Tools;

internal static class FileToolErrors
{
    public static string ControlPlaneWriteDenied(string path)
        => $"Error: Access denied: '{path}' is protected (Netclaw control plane, or a credential location such as ~/.ssh, ~/.aws or ~/.kube) "
           + "and cannot be modified by agent tools, even with approval. Do not retry through another tool or path. "
           + "Tell the user what change is needed; they can make it themselves "
           + "(Netclaw settings: `netclaw secrets set` or `netclaw doctor --fix`; SSH or AWS files: their own shell).";

    public static string CredentialReadDenied(string path)
        => $"Error: Access denied: '{path}' is protected (Netclaw secrets, keys or control-plane state, or a credential location such as ~/.ssh, ~/.aws or ~/.kube) "
           + "and cannot be read by agent tools, even with approval. Do not look for another way to read it. "
           + "ssh, git and aws still use these credentials when you run them; if you need a value from the file, ask the user.";
}
