using System.Text.Json.Nodes;
using Json.Schema;
using Netclaw.Cli.Doctor;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class SchemaFixResolverTests
{
    private const string ServerLevelEnumSchema = """
        {
          "type": "object",
          "properties": {
            "Servers": {
              "type": "object",
              "additionalProperties": {
                "type": "object",
                "properties": {
                  "Level": {
                    "type": "string",
                    "enum": ["Low", "Medium", "High"],
                    "default": "Low"
                  }
                },
                "additionalProperties": false
              }
            }
          }
        }
        """;

    [Fact]
    public void FixesIntegerEnumToString()
    {
        var (schema, schemaJson) = ParseSchema(ServerLevelEnumSchema);

        var config = JsonNode.Parse("""
            {
              "Servers": {
                "alpha": { "Level": 2 }
              }
            }
            """)!.AsObject();

        var result = SchemaFixResolver.TryApplySchemaFixes(schema, schemaJson, config, out var fixes);

        Assert.True(result);
        Assert.Single(fixes);
        Assert.Contains("High", fixes[0]);
        Assert.Equal("High", config["Servers"]!["alpha"]!["Level"]!.GetValue<string>());
    }

    [Fact]
    public void SkipsAlreadyCorrectStringEnum()
    {
        var (schema, schemaJson) = ParseSchema(ServerLevelEnumSchema);

        var config = JsonNode.Parse("""
            {
              "Servers": {
                "alpha": { "Level": "Medium" }
              }
            }
            """)!.AsObject();

        var result = SchemaFixResolver.TryApplySchemaFixes(schema, schemaJson, config, out var fixes);

        Assert.False(result);
        Assert.Empty(fixes);
    }

    [Fact]
    public void SkipsOutOfRangeIntegerEnum()
    {
        var (schema, schemaJson) = ParseSchema(ServerLevelEnumSchema);

        var config = JsonNode.Parse("""
            {
              "Servers": {
                "alpha": { "Level": 99 }
              }
            }
            """)!.AsObject();

        var result = SchemaFixResolver.TryApplySchemaFixes(schema, schemaJson, config, out var fixes);

        // Should not fix — integer is out of enum range.
        // The type+enum error is still raised but we can't map 99 to a valid value.
        Assert.False(result);
        Assert.Empty(fixes);
    }

    [Fact]
    public void FixesMultipleServerEntries()
    {
        var (schema, schemaJson) = ParseSchema(ServerLevelEnumSchema);

        var config = JsonNode.Parse("""
            {
              "Servers": {
                "alpha": { "Level": 0 },
                "beta": { "Level": "High" },
                "gamma": { "Level": 1 }
              }
            }
            """)!.AsObject();

        var result = SchemaFixResolver.TryApplySchemaFixes(schema, schemaJson, config, out var fixes);

        Assert.True(result);
        Assert.Equal(2, fixes.Count);
        Assert.Equal("Low", config["Servers"]!["alpha"]!["Level"]!.GetValue<string>());
        Assert.Equal("High", config["Servers"]!["beta"]!["Level"]!.GetValue<string>());
        Assert.Equal("Medium", config["Servers"]!["gamma"]!["Level"]!.GetValue<string>());
    }

    [Fact]
    public void InsertsDefaultForMissingRequiredProperty()
    {
        var (schema, schemaJson) = ParseSchema("""
            {
              "type": "object",
              "required": ["configVersion"],
              "properties": {
                "configVersion": {
                  "type": "integer",
                  "default": 1
                },
                "Name": {
                  "type": "string"
                }
              }
            }
            """);

        var config = JsonNode.Parse("""
            {
              "Name": "test"
            }
            """)!.AsObject();

        var result = SchemaFixResolver.TryApplySchemaFixes(schema, schemaJson, config, out var fixes);

        Assert.True(result);
        Assert.Single(fixes);
        Assert.Contains("configVersion", fixes[0]);
        Assert.Equal(1, config["configVersion"]!.GetValue<int>());
    }

    [Fact]
    public void SkipsMissingRequiredWithoutDefault()
    {
        var (schema, schemaJson) = ParseSchema("""
            {
              "type": "object",
              "required": ["configVersion"],
              "properties": {
                "configVersion": {
                  "type": "integer"
                }
              }
            }
            """);

        var config = JsonNode.Parse("""
            {
              "Name": "test"
            }
            """)!.AsObject();

        var result = SchemaFixResolver.TryApplySchemaFixes(schema, schemaJson, config, out var fixes);

        Assert.False(result);
        Assert.Empty(fixes);
    }

    [Fact]
    public void RemovesDisallowedAdditionalProperty()
    {
        var (schema, schemaJson) = ParseSchema("""
            {
              "type": "object",
              "properties": {
                "Servers": {
                  "type": "object",
                  "additionalProperties": {
                    "type": "object",
                    "properties": {
                      "Transport": {
                        "type": "string"
                      }
                    },
                    "additionalProperties": false
                  }
                }
              }
            }
            """);

        var config = JsonNode.Parse("""
            {
              "Servers": {
                "alpha": {
                  "Transport": "stdio",
                  "CapabilityClass": "MemorySafe"
                }
              }
            }
            """)!.AsObject();

        var result = SchemaFixResolver.TryApplySchemaFixes(schema, schemaJson, config, out var fixes);

        Assert.True(result);
        Assert.Single(fixes);
        Assert.Contains("CapabilityClass", fixes[0]);
        Assert.Null(config["Servers"]!["alpha"]!["CapabilityClass"]);
        Assert.Equal("stdio", config["Servers"]!["alpha"]!["Transport"]!.GetValue<string>());
    }

    [Fact]
    public void LeavesAllowedPropertiesUntouched()
    {
        var (schema, schemaJson) = ParseSchema("""
            {
              "type": "object",
              "properties": {
                "Name": { "type": "string" },
                "Value": { "type": "integer" }
              },
              "additionalProperties": true
            }
            """);

        var config = JsonNode.Parse("""
            {
              "Name": "test",
              "Value": 42,
              "Extra": "custom"
            }
            """)!.AsObject();

        var result = SchemaFixResolver.TryApplySchemaFixes(schema, schemaJson, config, out var fixes);

        Assert.False(result);
        Assert.Empty(fixes);
    }

    [Fact]
    public void HandlesRefBasedEnumSchema()
    {
        var (schema, schemaJson) = ParseSchema("""
            {
              "type": "object",
              "properties": {
                "Mode": { "$ref": "#/$defs/AccessMode" }
              },
              "additionalProperties": false,
              "$defs": {
                "AccessMode": {
                  "type": "string",
                  "enum": ["ReadOnly", "ReadWrite", "Admin"]
                }
              }
            }
            """);

        var config = JsonNode.Parse("""
            {
              "Mode": 1
            }
            """)!.AsObject();

        var result = SchemaFixResolver.TryApplySchemaFixes(schema, schemaJson, config, out var fixes);

        Assert.True(result);
        Assert.Single(fixes);
        Assert.Equal("ReadWrite", config["Mode"]!.GetValue<string>());
    }

    private static (JsonSchema Schema, JsonObject Json) ParseSchema(string schemaText)
    {
        var schema = JsonSchema.FromText(schemaText);
        var json = JsonNode.Parse(schemaText)!.AsObject();
        return (schema, json);
    }
}
