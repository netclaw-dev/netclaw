// -----------------------------------------------------------------------
// <copyright file="SensitiveStringConverterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration.Secrets;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class SensitiveStringConverterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DataProtectionSecretsProtector _protector;

    public SensitiveStringConverterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        var paths = new NetclawPaths(_tempDir);
        paths.EnsureDirectoriesExist();
        _protector = SecretsProtection.CreateProtector(paths);
    }

    public void Dispose()
    {
        // Restore to avoid test interference
        SensitiveStringTypeConverter.Protector = null;

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void TypeConverter_decrypts_ENC_values_when_protector_set()
    {
        SensitiveStringTypeConverter.Protector = _protector;

        var encrypted = _protector.Protect("my-secret");
        var converter = new SensitiveStringTypeConverter();
        var result = (SensitiveString)converter.ConvertFrom(null!, null, encrypted)!;

        Assert.Equal("my-secret", result.Value);
    }

    [Fact]
    public void TypeConverter_passes_through_plaintext_when_protector_set()
    {
        SensitiveStringTypeConverter.Protector = _protector;

        var converter = new SensitiveStringTypeConverter();
        var result = (SensitiveString)converter.ConvertFrom(null!, null, "plain-value")!;

        Assert.Equal("plain-value", result.Value);
    }

    [Fact]
    public void TypeConverter_passes_through_plaintext_when_no_protector()
    {
        SensitiveStringTypeConverter.Protector = null;

        var converter = new SensitiveStringTypeConverter();
        var result = (SensitiveString)converter.ConvertFrom(null!, null, "plain-value")!;

        Assert.Equal("plain-value", result.Value);
    }

    [Fact]
    public void JsonConverter_round_trips_via_STJ()
    {
        var original = new SensitiveString("secret-value");
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<SensitiveString>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("secret-value", deserialized.Value);
    }

    [Fact]
    public void JsonConverter_decrypts_ENC_values_when_protector_set()
    {
        SensitiveStringTypeConverter.Protector = _protector;

        var encrypted = _protector.Protect("json-secret");
        var json = $"\"{encrypted}\"";
        var result = JsonSerializer.Deserialize<SensitiveString>(json);

        Assert.NotNull(result);
        Assert.Equal("json-secret", result.Value);
    }

    [Fact]
    public void JsonConverter_writes_raw_value()
    {
        var sensitive = new SensitiveString("raw-value");
        var json = JsonSerializer.Serialize(sensitive);

        Assert.Equal("\"raw-value\"", json);
    }

    [Fact]
    public void ToString_returns_redacted()
    {
        var sensitive = new SensitiveString("should-not-appear");
        Assert.Equal("***REDACTED***", sensitive.ToString());
    }
}
