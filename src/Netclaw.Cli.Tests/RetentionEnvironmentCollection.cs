// -----------------------------------------------------------------------
// <copyright file="RetentionEnvironmentCollection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Cli.Tests;

/// <summary>
/// Retention tests read <c>NETCLAW_Retention__*</c> variables, and one test sets one. They run
/// one at a time so that test cannot add a warning to another test's output.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RetentionEnvironmentCollection
{
    public const string Name = "Retention environment";
}
