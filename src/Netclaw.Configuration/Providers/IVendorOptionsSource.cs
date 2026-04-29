// -----------------------------------------------------------------------
// <copyright file="IVendorOptionsSource.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;

namespace Netclaw.Configuration.Providers;

/// <summary>
/// Applies vendor-specific options to a <see cref="ChatOptions"/> instance
/// before sending a request. Each provider plugin can optionally supply one.
/// </summary>
public interface IVendorOptionsSource
{
    void Apply(ChatOptions options);
}
