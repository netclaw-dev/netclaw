// -----------------------------------------------------------------------
// <copyright file="FileToolErrors.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Tools;

internal static class FileToolErrors
{
    public static string ControlPlaneWriteDenied(string path)
        => $"Error: Access denied: '{path}' is part of Netclaw's control plane "
           + "(secrets, keys, database, or lifecycle files) and cannot be modified by agent tools, "
           + "even with approval. If the user wants this change, ask them to run a dedicated command "
           + "(e.g. `netclaw doctor --fix`, `netclaw secrets set`) or edit the file directly.";

    public static string CredentialReadDenied(string path)
        => $"Error: Access denied: '{path}' contains credentials or keys "
           + "and cannot be read by agent tools.";
}
