// -----------------------------------------------------------------------
// <copyright file="ShellConfigReadTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Decision D6: each file under the config directory is readable, by a file
/// tool and by a shell program that only reads its operands, except
/// secrets.json, the webhook route files, and the keys. Those stay denied in every form, and a write to
/// a config file stays denied.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class ShellConfigReadTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // {C} is the config directory, {K} the keys directory, {N} the Netclaw home.
    public static TheoryData<string> ConfigReads => new()
    {
        "cat {C}/netclaw.json",
        "head -5 {C}/netclaw.json",
        "grep -n port {C}/netclaw.json",
        "jq .Tools {C}/netclaw.json",
        "grep -n port {C}/netclaw.json 2>/dev/null",
        "cat {C}/netclaw.json > copy.json",
        "cat {C}/hard-deny-overrides.json",
        "cat {C}/devices.json",
        "head {C}/daemon.env",
    };

    public static TheoryData<string> SecretReads => new()
    {
        "cat {C}/secrets.json",
        "cat {C}/secr*.json",
        "cat {C}/*.json",
        "cat '{C}'/*.json",
        "d={C}; cat \"$d\"/*.json",
        "cd {C} && cat *.json",
        "x={C}/secrets.json; cat \"$x\"",
        "cat {K}/key.xml",
        "cat {N}/k*/key.xml",
        "grep -r token {C}",
        "cd {N} && grep -r token config",
        "cat {C}/netclaw.json \"$X\"",
        "cat {C}/webhooks/hook.json",
        "grep -r secret {C}/webhooks",
        "jq -n 'import \"secrets\" as $s {search: \"{C}\"}; $s'",
        "jq -n 'include \"devices\" {search: \"{C}\"}; .'",
        "python3 -c \"import os; print(os.listdir('{C}'))\"",
        "node -e \"console.log(require('fs').readdirSync('{C}'))\"",
    };

    public static TheoryData<string> ConfigWrites => new()
    {
        "echo x > {C}/netclaw.json",
        "cat {C}/devices-copy.json > {C}/netclaw.json",
        "cat {C}/netclaw.json >> {C}/netclaw.json",
        "cp other.json {C}/netclaw.json",
        "sed -i s/a/b/ {C}/netclaw.json",
        "echo x | tee {C}/netclaw.json",
        "sort -o {C}/netclaw.json {C}/netclaw.json",
        "uniq {C}/devices-copy.json {C}/netclaw.json",
        "rm {C}/netclaw.json",
        "echo '{}' > {C}/hard-deny-overrides.json",
        "cp other.json {C}/hard-deny-overrides.json",
        "cp other.json {C}/netclaw.json &",
        "echo x > {C}/netclaw.json &",
        "sed -i s/a/b/ {C}/netclaw.json &",
        "cp other.json \"$(echo {C})/netclaw.json\"",
        "cd {C} && cp ../other.json netclaw.json",
    };

    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [MemberData(nameof(ConfigReads))]
    public async Task Read_only_program_can_read_a_config_file(string command)
    {
        await using var harness = await CreateHarnessAsync();

        var decision = await harness.EvaluateShellDecisionAsync(Expand(harness, command), Ct);

        Assert.True(
            decision.Outcome is ToolAuthorizationOutcome.Allowed or ToolAuthorizationOutcome.RequiresApproval,
            $"'{command}' was {decision.Outcome} ({decision.DenyReason}).");
    }

    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [MemberData(nameof(SecretReads))]
    public async Task Secret_read_stays_denied(string command)
        => await AssertDeniedAsync(command);

    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [MemberData(nameof(ConfigWrites))]
    public async Task Config_write_stays_denied(string command)
        => await AssertDeniedAsync(command);

    // Owner decision D6 for the file tools: each config file is readable, except
    // secrets.json, the webhook route files, and the keys. No config file is writable.
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [InlineData("{C}/hard-deny-overrides.json", true)]
    [InlineData("{C}/devices.json", true)]
    [InlineData("{C}/webhooks/hook.json", false)]
    [InlineData("{C}/secrets.json", false)]
    [InlineData("{K}/key.xml", false)]
    public async Task File_tool_reads_each_config_file_but_the_secrets(string path, bool readable)
    {
        await using var harness = await CreateHarnessAsync();
        var target = Expand(harness, path);

        var read = await harness.EvaluateToolAsync(FileReadTool.ToolName, ToolInput.Create("Path", target), Ct);
        var write = await harness.EvaluateToolAsync(
            FileWriteTool.ToolName,
            ToolInput.Create("Path", target, "Content", "{}"),
            Ct);

        Assert.Equal(readable, read.Outcome != ApprovalOutcome.Denied);
        Assert.Equal(ApprovalOutcome.Denied, write.Outcome);
    }

    private async Task AssertDeniedAsync(string command)
    {
        await using var harness = await CreateHarnessAsync();

        var decision = await harness.EvaluateShellDecisionAsync(Expand(harness, command), Ct);

        Assert.True(
            decision.Outcome == ToolAuthorizationOutcome.Denied,
            $"'{command}' was {decision.Outcome}; it must be denied.");
    }

    private async Task<ShellApprovalHarness> CreateHarnessAsync()
    {
        var harness = await ShellApprovalHarness.CreateAsync(
            "shell-config-read",
            new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52),
            Approvals.None,
            fixture.ActorSystem,
            Ct);
        var paths = harness.Paths;
        Directory.CreateDirectory(paths.WebhooksDirectory);
        Directory.CreateDirectory(paths.KeysDirectory);
        await File.WriteAllTextAsync(Path.Combine(paths.ConfigDirectory, "netclaw.json"), "{}", Ct);
        await File.WriteAllTextAsync(Path.Combine(paths.ConfigDirectory, "devices-copy.json"), "{}", Ct);
        await File.WriteAllTextAsync(paths.SecretsPath, "{}", Ct);
        await File.WriteAllTextAsync(paths.DevicesPath, "{}", Ct);
        await File.WriteAllTextAsync(paths.HardDenyOverridesPath, "{}", Ct);
        await File.WriteAllTextAsync(paths.DaemonEnvironmentFilePath, "X=1", Ct);
        await File.WriteAllTextAsync(Path.Combine(paths.KeysDirectory, "key.xml"), "<key/>", Ct);
        await File.WriteAllTextAsync(Path.Combine(paths.WebhooksDirectory, "hook.json"), "{}", Ct);
        return harness;
    }

    private static string Expand(ShellApprovalHarness harness, string command)
        => command
            .Replace("{C}", harness.Paths.ConfigDirectory, StringComparison.Ordinal)
            .Replace("{K}", harness.Paths.KeysDirectory, StringComparison.Ordinal)
            .Replace("{N}", harness.Paths.BasePath, StringComparison.Ordinal);
}
