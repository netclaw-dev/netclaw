// -----------------------------------------------------------------------
// <copyright file="WriteWiderThanReadDoctorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

/// <summary>
/// Issue #2554: <c>netclaw doctor</c> warns, with no auto-fix, when an audience
/// profile can write wider than it can read. The warning names the audience
/// and the two modes.
/// </summary>
public sealed class WriteWiderThanReadDoctorTests : IDisposable
{
    private const string WarningPhrase = "can write wider than it can read";

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public WriteWiderThanReadDoctorTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    // audience, read profile JSON, write profile JSON, write mode, read mode
    public static TheoryData<string, string, string, string, string> WiderCases => new()
    {
        // The live shape of issue #2554.
        {
            "Personal",
            """{ "Mode": "Roots", "Roots": ["~/repositories", "~/.netclaw"] }""",
            """{ "Mode": "All" }""",
            "All",
            "Roots"
        },
        { "Personal", """{ "Mode": "None" }""", """{ "Mode": "Roots", "Roots": ["/srv/drop"] }""", "Roots", "None" },
        { "Personal", """{ "Mode": "None" }""", """{ "Mode": "All" }""", "All", "None" },
        // A Team write-only folder: a write root that no read root contains.
        {
            "Team",
            """{ "Mode": "Roots", "Roots": ["{session_dir}"] }""",
            """{ "Mode": "Roots", "Roots": ["{session_dir}", "/srv/drop"] }""",
            "Roots",
            "Roots"
        },
        {
            "Public",
            """{ "Mode": "Roots", "Roots": ["{session_dir}"] }""",
            """{ "Mode": "Roots", "Roots": ["/srv/public-drop"] }""",
            "Roots",
            "Roots"
        },
    };

    [Theory]
    [MemberData(nameof(WiderCases))]
    public async Task A_profile_that_writes_wider_than_it_reads_gets_a_warning(
        string audience,
        string read,
        string write,
        string writeMode,
        string readMode)
    {
        WriteProfiles(audience, read, write);

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains(WarningPhrase, result.Message, StringComparison.Ordinal);
        Assert.Contains($"Tools.AudienceProfiles.{audience}", result.Message, StringComparison.Ordinal);
        Assert.Contains($"WriteFiles.Mode={writeMode}", result.Message, StringComparison.Ordinal);
        Assert.Contains($"ReadFiles.Mode={readMode}", result.Message, StringComparison.Ordinal);
    }

    // audience, read profile JSON, write profile JSON
    public static TheoryData<string, string, string> CoveredCases => new()
    {
        { "Personal", """{ "Mode": "All" }""", """{ "Mode": "All" }""" },
        { "Personal", """{ "Mode": "All" }""", """{ "Mode": "Roots", "Roots": ["/srv/drop"] }""" },
        { "Personal", """{ "Mode": "Roots", "Roots": ["/srv"] }""", """{ "Mode": "Roots", "Roots": ["/srv/drop"] }""" },
        { "Personal", """{ "Mode": "Roots", "Roots": ["/srv"] }""", """{ "Mode": "None" }""" },
        { "Team", """{ "Mode": "Roots", "Roots": ["{session_dir}"] }""", """{ "Mode": "Roots", "Roots": ["{session_dir}"] }""" },
        { "Team", """{ "Mode": "Roots", "Roots": ["{session_dir}", "/srv"] }""", """{ "Mode": "Roots", "Roots": ["/srv/drop"] }""" },
    };

    [Theory]
    [MemberData(nameof(CoveredCases))]
    public async Task A_profile_whose_reads_cover_its_writes_gets_no_warning(string audience, string read, string write)
    {
        WriteProfiles(audience, read, write);

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(WarningPhrase, result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Personal", "HostAllowed")]
    [InlineData("Team", "Off")]
    [InlineData("Public", "Off")]
    public async Task Posture_defaults_get_no_warning(string posture, string shellMode)
    {
        File.WriteAllText(
            _paths.NetclawConfigPath,
            $$"""
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "{{posture}}", "ShellExecutionMode": "{{shellMode}}", "StrictDefaults": true },
              "Tools": { "ShellMode": "{{shellMode}}" }
            }
            """);

        var result = await new ToolAudienceProfilesDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(WarningPhrase, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_fix_does_not_change_a_profile_that_writes_wider_than_it_reads()
    {
        // A narrow read profile can be intentional, so doctor offers no auto-fix.
        WriteProfiles("Personal", """{ "Mode": "Roots", "Roots": ["~/repositories"] }""", """{ "Mode": "All" }""");
        var before = await File.ReadAllTextAsync(_paths.NetclawConfigPath, TestContext.Current.CancellationToken);

        var plan = await new DoctorFixService(_paths, Path.Combine(_dir.Path, "unused.service"), systemdEnabled: false)
            .BuildPlanAsync(TestContext.Current.CancellationToken);

        Assert.False(plan.HasChanges);
        Assert.Equal(before, await File.ReadAllTextAsync(_paths.NetclawConfigPath, TestContext.Current.CancellationToken));
    }

    private void WriteProfiles(string audience, string read, string write)
        => File.WriteAllText(
            _paths.NetclawConfigPath,
            $$"""
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Personal", "ShellExecutionMode": "HostAllowed" },
              "Tools": {
                "ShellMode": "HostAllowed",
                "AudienceProfiles": {
                  "{{audience}}": {
                    "ReadFiles": {{read}},
                    "WriteFiles": {{write}}
                  }
                }
              }
            }
            """);
}
