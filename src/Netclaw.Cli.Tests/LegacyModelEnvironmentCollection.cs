// -----------------------------------------------------------------------
// <copyright file="LegacyModelEnvironmentCollection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Cli.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LegacyModelEnvironmentCollection
{
    public const string Name = "Legacy model environment";
}
