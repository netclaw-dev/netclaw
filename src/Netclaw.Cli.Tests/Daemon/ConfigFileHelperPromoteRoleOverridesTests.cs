// -----------------------------------------------------------------------
// <copyright file="ConfigFileHelperPromoteRoleOverridesTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Xunit;

namespace Netclaw.Cli.Tests.Daemon;

/// <summary>
/// Locks in the writer-side half of the #1127 contract: before overwriting a
/// role on a model swap, any operator overrides on the displaced role are
/// promoted to <c>Models.Catalog["{oldProvider}/{oldModelId}"]</c>. This is
/// what makes hand-edited <c>ContextWindow</c> / <c>InputModalities</c> /
/// <c>OutputModalities</c> survive a picker-driven model change.
/// </summary>
public sealed class ConfigFileHelperPromoteRoleOverridesTests
{
    [Fact]
    public void Promote_MovesContextWindow_FromRole_ToCatalog()
    {
        var modelsSection = new Dictionary<string, object>
        {
            ["Main"] = new Dictionary<string, object>
            {
                ["Provider"] = "p",
                ["ModelId"] = "m",
                ["ContextWindow"] = 200_000L,
            },
        };

        ConfigFileHelper.PromoteRoleOverridesToCatalog(modelsSection, "Main");

        var catalog = (Dictionary<string, object>)modelsSection["Catalog"];
        var entry = (Dictionary<string, object>)catalog["p/m"];
        Assert.Equal(200_000L, Assert.IsType<long>(entry["ContextWindow"]));
    }

    [Fact]
    public void Promote_IsNoOp_WhenRoleHasNoOverrideFields()
    {
        // The common case: most operators rely on auto-detection and never
        // hand-set ContextWindow / Modality fields. Promote must not seed an
        // empty Catalog entry in that case.
        var modelsSection = new Dictionary<string, object>
        {
            ["Main"] = new Dictionary<string, object>
            {
                ["Provider"] = "p",
                ["ModelId"] = "m",
                ["Provenance"] = "Live",
            },
        };

        ConfigFileHelper.PromoteRoleOverridesToCatalog(modelsSection, "Main");

        Assert.False(modelsSection.ContainsKey("Catalog"));
    }

    [Fact]
    public void Promote_InlineWins_WhenCatalogEntryAlreadyExists()
    {
        // Inline value represents the most recent operator intent — it should
        // overwrite a stale catalog entry from a previous switch.
        var modelsSection = new Dictionary<string, object>
        {
            ["Main"] = new Dictionary<string, object>
            {
                ["Provider"] = "p",
                ["ModelId"] = "m",
                ["ContextWindow"] = 100L,
            },
            ["Catalog"] = new Dictionary<string, object>
            {
                ["p/m"] = new Dictionary<string, object>
                {
                    ["ContextWindow"] = 50L,
                },
            },
        };

        ConfigFileHelper.PromoteRoleOverridesToCatalog(modelsSection, "Main");

        var catalog = (Dictionary<string, object>)modelsSection["Catalog"];
        var entry = (Dictionary<string, object>)catalog["p/m"];
        Assert.Equal(100L, Assert.IsType<long>(entry["ContextWindow"]));
    }

    [Fact]
    public void Promote_HandlesJsonElement_FromFreshlyLoadedConfig()
    {
        // Simulates the path where ConfigFileHelper.LoadConfigFiles returned a
        // dictionary whose nested values are JsonElement (un-materialized).
        var json = """
            {
              "Main": { "Provider": "p", "ModelId": "m", "InputModalities": "Text, Image" }
            }
            """;
        var modelsSection = JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;

        ConfigFileHelper.PromoteRoleOverridesToCatalog(modelsSection, "Main");

        var catalog = (Dictionary<string, object>)modelsSection["Catalog"];
        var entry = (Dictionary<string, object>)catalog["p/m"];
        Assert.Equal("Text, Image", Assert.IsType<string>(entry["InputModalities"]));
    }

    [Fact]
    public void Promote_LeavesIdentityFields_OnOldRole()
    {
        // The role record itself isn't deleted by Promote — it's about to be
        // overwritten by the caller. Verify Promote doesn't mutate the role's
        // Provider/ModelId/Provenance, only reads them.
        var modelsSection = new Dictionary<string, object>
        {
            ["Main"] = new Dictionary<string, object>
            {
                ["Provider"] = "p",
                ["ModelId"] = "m",
                ["Provenance"] = "Live",
                ["ContextWindow"] = 1234L,
            },
        };

        ConfigFileHelper.PromoteRoleOverridesToCatalog(modelsSection, "Main");

        var main = (Dictionary<string, object>)modelsSection["Main"];
        Assert.Equal("p", main["Provider"]);
        Assert.Equal("m", main["ModelId"]);
        Assert.Equal("Live", main["Provenance"]);
    }
}
