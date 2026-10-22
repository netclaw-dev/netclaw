// -----------------------------------------------------------------------
// <copyright file="DaemonApiTestDependencies.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.DataProtection;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Cli.Tests;

internal static class DaemonApiTestDependencies
{
    internal static LocalControlPairingProofProtector CreateProofProtector()
        => new(new EphemeralDataProtectionProvider());
}
