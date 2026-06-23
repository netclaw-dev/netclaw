// -----------------------------------------------------------------------
// <copyright file="McpToolPermissionsNavigationState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Mcp;

public sealed class McpToolPermissionsNavigationState
{
    private TrustAudience? _initialAudience;

    public void RequestInitialAudience(TrustAudience audience)
    {
        _initialAudience = audience;
    }

    public TrustAudience? ConsumeInitialAudience()
    {
        var audience = _initialAudience;
        _initialAudience = null;
        return audience;
    }
}
