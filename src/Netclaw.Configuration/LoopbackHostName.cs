// -----------------------------------------------------------------------
// <copyright file="LoopbackHostName.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;

namespace Netclaw.Configuration;

/// <summary>
/// The strict loopback name test. It accepts <c>localhost</c> and the canonical text of a
/// loopback IP literal, and it never resolves DNS.
///
/// Request admission depends on it: the loopback Host check in <c>LoopbackHostOriginPolicy</c>
/// calls it directly, and <see cref="DaemonExposureValidator.IsLoopbackHost"/> builds on it.
/// A DNS name that resolves to loopback is not a loopback name, because a rebinding page
/// controls the name. A widening of this test is a security change.
/// </summary>
public static class LoopbackHostName
{
    /// <summary>
    /// True for <c>localhost</c> (case-insensitive, no trailing dot, no trimming) and for an
    /// IP literal in <c>127.0.0.0/8</c> or <c>::1</c> whose canonical text equals
    /// <paramref name="host"/>. An IPv6 literal has no brackets here.
    /// </summary>
    public static bool IsStrictLoopback(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var address)
               && IPAddress.IsLoopback(address)
               && !address.IsIPv4MappedToIPv6
               && address.ToString() == host;
    }
}
