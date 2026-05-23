// -----------------------------------------------------------------------
// <copyright file="ModelSelectionCatalogOverlayTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Locks in the contract for #1127: operator overrides persisted in
/// <see cref="ModelSelection.Catalog"/> overlay onto matching role records
/// (Main / Fallback / Compaction) when <see cref="ModelSelection.ApplyCatalogOverlays"/>
/// runs, with inline values winning over the catalog entry.
/// </summary>
public sealed class ModelSelectionCatalogOverlayTests
{
    [Fact]
    public void CatalogOverlay_AppliesContextWindow_WhenRoleHasNoInlineValue()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Models:Main:Provider"] = "spark-362c",
                ["Models:Main:ModelId"] = "Qwen/Qwen3.6-35B-A3B-FP8",
                ["Models:Catalog:spark-362c/Qwen/Qwen3.6-35B-A3B-FP8:ContextWindow"] = "200000",
            })
            .Build();

        var selection = config.GetSection("Models").Get<ModelSelection>()!;
        selection.ApplyCatalogOverlays();

        Assert.Equal(200_000, selection.Main.ContextWindow);
    }

    [Fact]
    public void InlineRoleValue_WinsOver_CatalogOverlay()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Models:Main:Provider"] = "p",
                ["Models:Main:ModelId"] = "m",
                ["Models:Main:ContextWindow"] = "1000",
                ["Models:Catalog:p/m:ContextWindow"] = "9999",
            })
            .Build();

        var selection = config.GetSection("Models").Get<ModelSelection>()!;
        selection.ApplyCatalogOverlays();

        Assert.Equal(1000, selection.Main.ContextWindow);
    }

    [Fact]
    public void CatalogOverlay_AppliesIndependentlyTo_AllThreeRoles()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Models:Main:Provider"] = "p",
                ["Models:Main:ModelId"] = "main-model",
                ["Models:Fallback:Provider"] = "p",
                ["Models:Fallback:ModelId"] = "fallback-model",
                ["Models:Compaction:Provider"] = "p",
                ["Models:Compaction:ModelId"] = "compaction-model",
                ["Models:Catalog:p/main-model:InputModalities"] = "Text, Image",
                ["Models:Catalog:p/fallback-model:ContextWindow"] = "65536",
                ["Models:Catalog:p/compaction-model:OutputModalities"] = "Text",
            })
            .Build();

        var selection = config.GetSection("Models").Get<ModelSelection>()!;
        selection.ApplyCatalogOverlays();

        Assert.Equal(ModelModality.Text | ModelModality.Image, selection.Main.InputModalities);
        Assert.Equal(65_536, selection.Fallback!.ContextWindow);
        Assert.Equal(ModelModality.Text, selection.Compaction!.OutputModalities);
    }

    [Fact]
    public void CatalogEntry_WithoutMatchingRole_IsIgnored()
    {
        // Operator switched Main from "old-model" to "new-model"; the old
        // model's overrides remain in the catalog. They should NOT leak onto
        // the unrelated new selection.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Models:Main:Provider"] = "p",
                ["Models:Main:ModelId"] = "new-model",
                ["Models:Catalog:p/old-model:ContextWindow"] = "12345",
            })
            .Build();

        var selection = config.GetSection("Models").Get<ModelSelection>()!;
        selection.ApplyCatalogOverlays();

        Assert.Null(selection.Main.ContextWindow);
    }

    [Fact]
    public void ApplyCatalogOverlays_IsNoOp_WhenCatalogAbsent()
    {
        var selection = new ModelSelection { Main = new ModelReference { Provider = "p", ModelId = "m" } };
        selection.ApplyCatalogOverlays(); // does not throw, leaves fields null
        Assert.Null(selection.Main.ContextWindow);
        Assert.Null(selection.Main.InputModalities);
    }
}
