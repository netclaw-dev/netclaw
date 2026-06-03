// -----------------------------------------------------------------------
// <copyright file="ModelCatalogPersistenceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Configuration;
using Netclaw.Daemon.Providers;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class ModelCatalogPersistenceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ModelCatalogPersistenceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    [Fact]
    public void Write_SeedsConfigVersion_WhenConfigIsMissing()
    {
        var persistence = new ModelCatalogPersistence(_paths);

        var result = persistence.Write(new ModelCatalogWire.PutSelectionRequest
        {
            Role = "Main",
            Reference = new ModelCatalogWire.ModelReferenceWire
            {
                Provider = "local-ollama",
                ModelId = "qwen3:30b",
            },
        });

        Assert.True(result.Success);

        var root = JsonNode.Parse(File.ReadAllText(_paths.NetclawConfigPath))!.AsObject();
        Assert.Equal(EmbeddedSchemaLoader.CurrentSchemaVersion, root["configVersion"]!.GetValue<int>());
        Assert.Equal("local-ollama", root["Models"]!["Main"]!["Provider"]!.GetValue<string>());
        Assert.Equal("qwen3:30b", root["Models"]!["Main"]!["ModelId"]!.GetValue<string>());
    }

    [Fact]
    public void Write_RejectsInvalidProvenance()
    {
        var persistence = new ModelCatalogPersistence(_paths);

        var result = persistence.Write(new ModelCatalogWire.PutSelectionRequest
        {
            Role = "Main",
            Reference = new ModelCatalogWire.ModelReferenceWire
            {
                Provider = "local-ollama",
                ModelId = "qwen3:30b",
                Provenance = "Bogus",
            },
        });

        Assert.False(result.Success);
        Assert.Contains(result.ValidationErrors, error => error.Contains("Provenance", StringComparison.Ordinal));
        Assert.False(File.Exists(_paths.NetclawConfigPath));
    }

    public void Dispose() => _dir.Dispose();
}
