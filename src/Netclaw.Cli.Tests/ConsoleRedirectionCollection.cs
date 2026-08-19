// -----------------------------------------------------------------------
// <copyright file="ConsoleRedirectionCollection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Cli.Tests;

/// <summary>
/// Groups every test class that redirects <see cref="System.Console"/>. The CLI
/// writes failures to <c>Console.Error</c>, which is process-wide state, so two
/// classes that swap it at the same time can restore each other's writer. This
/// collection runs alone, so the swap is always safe.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleRedirectionCollection
{
    public const string Name = "Console redirection";
}
