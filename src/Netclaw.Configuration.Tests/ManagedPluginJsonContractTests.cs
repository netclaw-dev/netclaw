// -----------------------------------------------------------------------
// <copyright file="ManagedPluginJsonContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class ManagedPluginJsonContractTests
{
    [Fact]
    public void Managed_plugin_wire_enums_serialize_as_strings_without_global_options()
    {
        var sourceJson = JsonSerializer.Serialize(new ManagedPluginSource
        {
            Id = "dotnet-skills",
            Repository = "Aaronontheweb/dotnet-skills",
            Format = ManagedPluginSourceValidator.CodexFormat,
            ReferenceKind = ManagedPluginReferenceKind.Branch,
            Reference = "main",
        });
        var installJson = JsonSerializer.Serialize(new ManagedPluginApi.InstallRequest
        {
            Repository = "Aaronontheweb/dotnet-skills",
            ReferenceKind = ManagedPluginApi.InstallReferenceKind.Tag,
            Reference = "v1.2.0",
        });
        var rowJson = JsonSerializer.Serialize(new ManagedPluginApi.PluginRow
        {
            SourceId = "dotnet-skills",
            Repository = "Aaronontheweb/dotnet-skills",
            SourceFormat = ManagedPluginSourceValidator.CodexFormat,
            ReferenceKind = ManagedPluginReferenceKind.Branch,
            Reference = "main",
            Enabled = true,
            Status = ManagedPluginApi.PluginStatus.Installed,
        });

        Assert.Contains("\"ReferenceKind\":\"Branch\"", sourceJson);
        Assert.Contains("\"ReferenceKind\":\"Tag\"", installJson);
        Assert.Contains("\"ReferenceKind\":\"Branch\"", rowJson);
        Assert.Contains("\"Status\":\"Installed\"", rowJson);
    }
}
