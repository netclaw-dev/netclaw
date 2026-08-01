// -----------------------------------------------------------------------
// <copyright file="NamedModelRuntimeRegistry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

public sealed record NamedModelRuntime(
    string DefinitionName,
    ModelReference Model,
    IChatClient Client,
    ModelCapabilities Capabilities);

public interface INamedModelRuntimeRegistry
{
    NamedModelRuntime GetRequired(string definitionName);
}

/// <summary>
/// Creates one client pipeline for each named model definition.
/// </summary>
public sealed class NamedModelRuntimeRegistry : INamedModelRuntimeRegistry
{
    private readonly ModelRuntimeConfiguration _configuration;
    private readonly PipelineChatClientFactory _factory;
    private readonly ConcurrentDictionary<string, NamedModelRuntime> _runtimes =
        new(StringComparer.OrdinalIgnoreCase);

    public NamedModelRuntimeRegistry(
        ModelRuntimeConfiguration configuration,
        PipelineChatClientFactory factory)
    {
        _configuration = configuration;
        _factory = factory;
    }

    public NamedModelRuntime GetRequired(string definitionName)
    {
        if (!_configuration.Definitions.TryGetValue(definitionName, out var model))
        {
            throw new InvalidOperationException(
                $"Model definition '{definitionName}' is not available at runtime.");
        }

        return _runtimes.GetOrAdd(definitionName, name => new NamedModelRuntime(
            name,
            model,
            _factory.Create(model),
            ModelCapabilityResolution.ResolveModelCapabilities(model, detected: null)));
    }
}
