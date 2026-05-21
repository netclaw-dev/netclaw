// -----------------------------------------------------------------------
// <copyright file="SensitiveStringStaticStateCollection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// xUnit collection used to serialize any test that mutates the static
/// <see cref="SensitiveStringTypeConverter.Protector"/> hook. The hook is
/// process-wide because TypeConverters can't be DI-injected; two tests
/// flipping it concurrently produces flaky cross-class failures
/// ("Expected: plaintext, Actual: ENC:…").
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SensitiveStringStaticStateCollection
{
    public const string Name = "SensitiveString static state";
}
