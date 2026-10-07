// -----------------------------------------------------------------------
// <copyright file="NoTerminaTraceRegistrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Termina trace listeners record every key event and every character that reaches a text
/// input. The TUIs take API keys, bot tokens and webhook secrets, so no shipped Netclaw
/// assembly may register a Termina trace listener. Program.cs is top-level statements, so
/// the check reads the compiled assemblies for a call to any Termina trace registration.
/// </summary>
public sealed class NoTerminaTraceRegistrationTests
{
    [Fact]
    public void No_shipped_assembly_registers_a_Termina_trace_listener()
    {
        var assemblies = new[] { "Netclaw.*.dll", "netclaw.dll", "netclawd.dll" }
            .SelectMany(pattern => Directory.GetFiles(AppContext.BaseDirectory, pattern))
            .Where(path => !Path.GetFileName(path).Contains("Tests", StringComparison.Ordinal))
            .ToArray();

        // Guard against a vacuous pass: the CLI and daemon assemblies must be among those scanned.
        Assert.Contains(assemblies, path => Path.GetFileName(path) == "netclaw.dll");
        Assert.Contains(assemblies, path => Path.GetFileName(path) == "netclawd.dll");

        var offenders = assemblies
            .SelectMany(path => FindTraceRegistrations(path).Select(name => $"{Path.GetFileName(path)}: {name}"))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static IEnumerable<string> FindTraceRegistrations(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            yield break;

        var reader = pe.GetMetadataReader();
        foreach (var handle in reader.MemberReferences)
        {
            var name = reader.GetString(reader.GetMemberReference(handle).Name);
            if (name.StartsWith("AddTermina", StringComparison.Ordinal)
                && name.Contains("Trac", StringComparison.Ordinal))
            {
                yield return name;
            }
        }
    }
}
