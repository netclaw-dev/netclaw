// -----------------------------------------------------------------------
// <copyright file="ShellConfigReadTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization;
using Netclaw.Actors.Tools;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// The decision D6 gate. Each file under the config directory is readable, by a
/// file tool and by a shell program that only reads its operands, except
/// secrets.json, the webhook route files, and the keys. Those stay denied in
/// every form: glob, brace, dot segment, link, and program text. A write to a
/// config file stays denied. Each row runs attended and unattended through the
/// production authorization path, with real files and links.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class ShellConfigReadTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private const string Shell = "shell";
    private const string Read = "file_read";
    private const string Write = "file_write";
    private const string Edit = "file_edit";

    // Issue #2341 owns these gaps. A brace or a recursive read that names only a
    // parent of the config directory reaches a protected path that no check sees.
    private const string RecursionGap = "#2341: a recursive or brace read from a parent of the config directory.";

    // Issue #2343 (OS-level protection) owns these old gaps, which no policy rule
    // can see: a value that is known only at run time, or an escape in program
    // text. They also fail on the base without decision D6.
    private const string HiddenValueGap = "#2343: a run-time value or a program-text escape hides the protected path.";

    // Issue #2343 also owns this old gap: the parser reads $'\x6beys' as the
    // literal "$\x6beys", so a path outside the config directory is not seen.
    private const string AnsiQuoteGap = "#2343: the parser misreads ANSI-C quotes in a path outside the config directory.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // {C} is the config directory, {K} the keys directory, {N} the Netclaw home,
    // and {P} the project directory.
    private static readonly (string Tool, string Target, bool Readable, string? Gap)[] Rows =
    [
        // A read-only program or a file tool reads a config file.
        (Shell, "cat {C}/netclaw.json", true, null),
        (Shell, "cat \"{C}/netclaw.json\"", true, null),
        (Shell, "cat {C}/netclaw.json 2>/dev/null", true, null),
        (Shell, "cat {C}/netclaw.json > /dev/null", true, null),
        (Shell, "cat {C}/netclaw.json > copy.json", true, null),
        (Shell, "cat {C}/netclaw.json {C}/tool-approvals.json", true, null),
        (Shell, "head -n 5 {C}/netclaw.json", true, null),
        (Shell, "tail {C}/tool-approvals.json", true, null),
        (Shell, "wc -l {C}/netclaw.json", true, null),
        (Shell, "grep x {C}/netclaw.json | head", true, null),
        (Shell, "grep -n port {C}/netclaw.json", true, null),
        (Shell, "diff {C}/netclaw.json {C}/tool-approvals.json", true, null),
        (Shell, "jq . {C}/tool-approvals.json", true, null),
        (Shell, "jq -r .Tools {C}/netclaw.json", true, null),
        (Shell, "cat {C}/netclaw.json | jq '{a: .Tools}'", true, null),
        (Shell, "cat {C}/hard-deny-overrides.json", true, null),
        (Shell, "cat {C}/devices.json", true, null),
        (Shell, "head {C}/daemon.env", true, null),
        (Read, "{C}/netclaw.json", true, null),
        (Read, "{C}/hard-deny-overrides.json", true, null),
        (Read, "{C}/devices.json", true, null),

        // The protected set stays denied in each path form.
        (Shell, "cat {C}/secrets.json", false, null),
        (Shell, "cat {C}/./secrets.json", false, null),
        (Shell, "cat {C}//secrets.json", false, null),
        (Shell, "cat {C}/x/../secrets.json", false, null),
        (Shell, "cat {C}/../keys/key-1.xml", false, null),
        (Shell, "cat {K}/../config/secrets.json", false, null),
        (Shell, "cat -- {C}/secrets.json", false, null),
        (Shell, "cat < {C}/secrets.json", false, null),
        (Shell, "cat {C}/secrets.json 2>/dev/null", false, null),
        (Shell, "wc -c {C}/secrets.json", false, null),
        (Shell, "cat {C}/*", false, null),
        (Shell, "cat {C}/*.json", false, null),
        (Shell, "cat '{C}'/*.json", false, null),
        (Shell, "cat {C}/secr*.json", false, null),
        (Shell, "cat {C}/secrets.jso?", false, null),
        (Shell, "cat {C}/[s]ecrets.json", false, null),
        (Shell, "cat {N}/*/secrets.json", false, null),
        (Shell, "cat {N}/k*/key-1.xml", false, null),
        (Shell, "head -n 1000 {C}/*", false, null),
        (Shell, "cd {C} && cat secrets.json", false, null),
        (Shell, "cd {C} && cat *.json", false, null),
        (Shell, "cd {C} && cat netclaw.json", false, null),
        (Shell, "cd {N} && cat config/secrets.json", false, null),
        (Shell, "cd {N} && cat */secrets.json", false, null),
        (Shell, "d={C}; cat \"$d\"/*.json", false, null),
        (Shell, "f={C}/secrets.json; cat \"$f\"", false, null),
        (Shell, "cat {C}/netclaw.json \"$X\"", false, null),
        (Shell, "cat {C}/netclaw.json $(echo x)", false, null),
        (Shell, "cat {C}/netclaw.json {C}/secrets.json", false, null),
        (Shell, "cat {C}/netclaw.json | cat - {C}/secrets.json", false, null),
        (Shell, "diff {C}/netclaw.json {C}/secrets.json", false, null),
        (Shell, "jq . {C}/netclaw.json {C}/secrets.json", false, null),
        (Shell, "grep -f {C}/secrets.json {C}/netclaw.json", false, null),
        (Shell, "grep --file=\"{C}/secrets.json\" x {C}/netclaw.json", false, null),
        (Shell, "jq --rawfile x {C}/secrets.json -n '$x'", false, null),
        (Shell, "jq --slurpfile x {C}/secrets.json -n '$x'", false, null),
        (Shell, "grep -r token {C}", false, null),
        (Shell, "cd {N} && grep -r token config", false, null),
        (Shell, "diff -r {C} /tmp", false, null),
        (Shell, "cat {K}/key-1.xml", false, null),
        (Shell, "cat {C}/webhooks/route.json", false, null),
        (Shell, "cat {C}/webhooks/*", false, null),
        (Shell, "grep -r secret {C}/webhooks", false, null),
        (Shell, "cd {C}/webhooks && cat route.json", false, null),
        (Shell, "jq -n --rawfile x {C}/webhooks/route.json '$x'", false, null),
        (Read, "{C}/secrets.json", false, null),
        (Read, "{C}/x/../secrets.json", false, null),
        (Read, "{C}/../keys/key-1.xml", false, null),
        (Read, "{K}/key-1.xml", false, null),
        (Read, "{C}/webhooks/route.json", false, null),

        // The parser reports a brace word as one exact path, but Bash expands it.
        (Shell, "cat {C}/{netclaw,secrets}.json", false, null),
        (Shell, "cat {C}/{secrets,netclaw}.json", false, null),
        (Shell, "cat {C}/secrets.js{on,}", false, null),
        (Shell, "cat {N}/{config/secrets.json,config/netclaw.json}", false, null),
        (Shell, "cat {C}/{webhooks/route.json,netclaw.json}", false, null),
        (Shell, "head -c 99 {C}/{netclaw,secrets}.json", false, null),
        (Shell, "jq '{a: .Tools}' {C}/netclaw.json", false, null),
        (Shell, "cat {N}/{keys,config}/key-1.xml", false, RecursionGap),
        (Shell, "grep -r x {N}/{config,keys}", false, RecursionGap),

        // A recursive read from a parent of the config directory.
        (Shell, "grep -r token {N}", false, RecursionGap),
        (Shell, "grep -r token {N}/", false, RecursionGap),
        (Shell, "grep -rl token {N}", false, RecursionGap),
        (Shell, "cd {N} && grep -r token .", false, RecursionGap),
        (Shell, "diff -r {N} /tmp", false, RecursionGap),

        // A link to a protected path stays denied.
        (Shell, "cat {C}/alias.json", false, null),
        (Shell, "cd {C} && cat alias.json", false, null),
        (Shell, "cat {C}/hooklink/route.json", false, null),
        (Shell, "cat {C}/keyslink/key-1.xml", false, null),
        (Shell, "cat {P}/keylink.xml", false, null),
        (Shell, "cat {P}/secretlink.json", false, null),
        (Shell, "cat {P}/keysdir/key-1.xml", false, null),
        (Shell, "grep -r token {P}/keysdir", false, null),
        (Read, "{C}/alias.json", false, null),
        (Read, "{C}/hooklink/route.json", false, null),
        (Read, "{P}/keylink.xml", false, null),

        // Program text that names the config directory stays denied, in each spelling.
        (Shell, "jq -n 'import \"secrets\" as $s {search: \"{C}\"}; $s'", false, null),
        (Shell, "jq -n 'import \"secrets\" as $s {search: \"{N}/./config\"}; $s'", false, null),
        (Shell, "jq -n 'import \"secrets\" as $s {search: \"{N}//config\"}; $s'", false, null),
        (Shell, "jq -n 'import \"secrets\" as $s {search: \"{C}/.\"}; $s'", false, null),
        (Shell, "jq -n 'import \"secrets\" as $s {search: \"{N}/x/../config\"}; $s'", false, null),
        (Shell, "jq -n 'include \"devices\" {search: \"{C}\"}; .'", false, null),
        (Shell, "jq -n 'import \"secrets\" as $s {search: \"{N}/con'fig'\"}; $s'", false, null),
        (Shell, "jq -n 'import \"secrets\" as $s {search: \"'{N}/config'\"}; $s'", false, null),
        (Shell, "d={N}/con'fig'; jq -n \"import \\\"secrets\\\" as \\$s {search: \\\"$d\\\"}; \\$s\"", false, null),
        (Shell, "jq -L {C} -n 'import \"secrets\" as $s; $s'", false, null),
        (Shell, "jq -L {N}/./config -n 'import \"secrets\" as $s; $s'", false, null),
        (Shell, "python3 -c \"print(open('{C}/netclaw.json').read())\"", false, null),
        (Shell, "python3 -c \"import os; print(os.listdir('{C}'))\"", false, null),
        (Shell, "python3 -c \"import os; print(os.listdir('{N}//config'))\"", false, null),
        (Shell, "node -e \"console.log(require('fs').readdirSync('{C}'))\"", false, null),
        (Shell, "node -e \"console.log(require('fs').readdirSync('{N}/x/../config'))\"", false, null),
        (Shell, "awk '{print}' {C}/netclaw.json", false, null),
        (Shell, "sed -n 1p {C}/netclaw.json", false, null),
        (Shell, "bash -c 'cat {C}/secrets.json'", false, null),

        // An escaped or quoted name of a protected path stays denied.
        (Shell, "cat {C}/secret\\s.json", false, null),
        (Shell, "cat {C}/sec\"\"rets.json", false, null),
        (Shell, "cat {N}/con''fig/secrets.json", false, null),
        (Shell, "cat {C}/$'secrets.json'", false, null),
        (Shell, "cat {C}/$'\\x73ecrets.json'", false, null),
        (Shell, "cat {C}/$\"secrets.json\"", false, null),
        (Shell, "cat {C}/$\"webhooks\"/route.json", false, null),
        (Shell, "cat {K}/key\\-1.xml", false, null),
        (Shell, "cat {N}/ke\"\"ys/key-1.xml", false, null),
        (Shell, "cat {N}/$'\\x6beys'/key-1.xml", false, AnsiQuoteGap),
        (Shell, "cat {C}/web\"\"hooks/route.json", false, null),
        (Shell, "cat {C}/$'webhooks'/route.json", false, null),
        (Shell, "cat {C}/webhooks/rou\\te.json", false, null),

        // A name built from variable pieces stays denied.
        (Shell, "a=sec; cat {C}/${a}rets.json", false, null),
        (Shell, "cat {C}/\"$(echo secrets).json\"", false, null),
        (Shell, "a=ke; cat {N}/${a}ys/key-1.xml", false, null),
        (Shell, "cat {N}/\"$(echo keys)\"/key-1.xml", false, HiddenValueGap),
        (Shell, "a=web; cat {C}/${a}hooks/route.json", false, null),

        // Program text with an escape or a built name.
        (Shell, "jq -n 'import \"secrets\" as $s {search: \"{N}/con\\u0066ig\"}; $s'", false, HiddenValueGap),
        (Shell, "jq -n 'import \"secrets\" as $s {search: \"{N}/\\u002e/config\"}; $s'", false, HiddenValueGap),
        (Shell, "jq -n 'import \"secrets\" as $s {search: (\"{N}/con\" + \"fig\")}; $s'", false, HiddenValueGap),
        (Shell, "python3 -c \"print(open('{C}/sec'+'rets.json').read())\"", false, null),
        (Shell, "python3 -c \"print(open('{N}/con'+'fig/secrets.json').read())\"", false, null),
        (Shell, "python3 -c \"print(open('{N}/'+chr(99)+'onfig/secrets.json').read())\"", false, null),
        (Shell, "python3 -c \"print(open('{N}/'+chr(107)+'eys/key-1.xml').read())\"", false, HiddenValueGap),

        // A redirect read or a sourced file stays denied.
        (Shell, "while read l; do echo \"$l\"; done < {C}/secrets.json", false, null),
        (Shell, "exec 3< {C}/secrets.json; cat <&3", false, null),
        (Shell, "source {C}/secrets.json", false, null),
        (Shell, ". {C}/secrets.json", false, null),
        (Shell, "cat < {K}/key-1.xml", false, null),
        (Shell, "while read l; do echo \"$l\"; done < {K}/key-1.xml", false, null),
        (Shell, "exec 3< {K}/key-1.xml; cat <&3", false, null),
        (Shell, "source {K}/key-1.xml", false, null),
        (Shell, "cat < {C}/webhooks/route.json", false, null),
        (Shell, "while read l; do echo \"$l\"; done < {C}/webhooks/route.json", false, null),
        (Shell, "exec 3< {C}/webhooks/route.json; cat <&3", false, null),
        (Shell, ". {C}/webhooks/route.json", false, null),

        // A copy or a link out of the protected set stays denied.
        (Shell, "cp {C}/secrets.json /tmp/x", false, null),
        (Shell, "ln {C}/secrets.json /tmp/x", false, null),
        (Shell, "ln -s {C}/secrets.json /tmp/x", false, null),
        (Shell, "ln -s {N}/keys /tmp/k", false, null),
        (Shell, "cp {K}/key-1.xml /tmp/x", false, null),
        (Shell, "ln {K}/key-1.xml /tmp/x", false, null),
        (Shell, "cp -r {K} /tmp/k", false, null),
        (Shell, "cp {C}/webhooks/route.json /tmp/x", false, null),
        (Shell, "ln {C}/webhooks/route.json /tmp/x", false, null),
        (Shell, "ln -s {C}/webhooks /tmp/w", false, null),

        // A write to a config file stays denied.
        (Shell, "echo x > {C}/netclaw.json", false, null),
        (Shell, "echo x >> {C}/netclaw.json", false, null),
        (Shell, "echo x > {C}/new.json", false, null),
        (Shell, "echo x > \"{C}/netclaw.json\"", false, null),
        (Shell, "echo x > {C}/netclaw.json &", false, null),
        (Shell, "echo '{}' > {C}/hard-deny-overrides.json", false, null),
        (Shell, "cat /tmp/a > {C}/netclaw.json", false, null),
        (Shell, "cat {C}/devices-copy.json > {C}/netclaw.json", false, null),
        (Shell, "cat {C}/netclaw.json >> {C}/netclaw.json", false, null),
        (Shell, "grep x {C}/netclaw.json > {C}/out.json", false, null),
        (Shell, "jq . {C}/netclaw.json > {C}/netclaw.json", false, null),
        (Shell, "jq . {C}/netclaw.json | tee {C}/netclaw.json", false, null),
        (Shell, "echo x | tee {C}/netclaw.json", false, null),
        (Shell, "tee -a {C}/netclaw.json", false, null),
        (Shell, "cp other.json {C}/netclaw.json", false, null),
        (Shell, "cp other.json {C}/netclaw.json &", false, null),
        (Shell, "cp other.json {C}/hard-deny-overrides.json", false, null),
        (Shell, "cp other.json \"$(echo {C})/netclaw.json\"", false, null),
        (Shell, "cd {C} && cp ../other.json netclaw.json", false, null),
        (Shell, "cd {C} && echo x > netclaw.json", false, null),
        (Shell, "mv /tmp/a {C}/netclaw.json", false, null),
        (Shell, "install /tmp/a {C}/netclaw.json", false, null),
        (Shell, "ln -sf /tmp/a {C}/netclaw.json", false, null),
        (Shell, "dd of={C}/netclaw.json", false, null),
        (Shell, "sed -i s/a/b/ {C}/netclaw.json", false, null),
        (Shell, "sed -i s/a/b/ {C}/netclaw.json &", false, null),
        (Shell, "sort -o {C}/netclaw.json {C}/netclaw.json", false, null),
        (Shell, "uniq {C}/devices-copy.json {C}/netclaw.json", false, null),
        (Shell, "rm {C}/netclaw.json", false, null),
        (Shell, "chmod 600 {C}/netclaw.json", false, null),
        (Shell, "truncate -s 0 {C}/netclaw.json", false, null),
        (Shell, "touch {C}/new.json", false, null),
        (Shell, "mkdir {C}/x", false, null),
        (Write, "{C}/netclaw.json", false, null),
        (Write, "{C}/new.json", false, null),
        (Write, "{C}/hard-deny-overrides.json", false, null),
        (Edit, "{C}/netclaw.json", false, null),
    ];

    public static IEnumerable<TheoryDataRow<string, string, bool, bool>> Cases
        => Rows.SelectMany(row => new[] { true, false }.Select(attended =>
            new TheoryDataRow<string, string, bool, bool>(row.Tool, row.Target, attended, row.Readable)
            {
                Skip = row.Gap
            }));

    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host. Issues #2341 and #2343 own the rows that skip.")]
    [MemberData(nameof(Cases))]
    public async Task Config_read_follows_decision_D6(string tool, string target, bool attended, bool readable)
    {
        await using var harness = await CreateHarnessAsync(attended);
        var expanded = Expand(harness, target);
        var arguments = tool switch
        {
            Shell => ToolInput.Create("Command", expanded),
            Read => ToolInput.Create("Path", expanded),
            Write => ToolInput.Create("Path", expanded, "Content", "{}"),
            Edit => ToolInput.Create("Path", expanded, "OldString", "{", "NewString", "{ "),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown tool.")
        };
        var toolName = tool switch
        {
            Shell => ShellTool.ToolName,
            Read => FileReadTool.ToolName,
            Write => FileWriteTool.ToolName,
            _ => FileEditTool.ToolName
        };

        var observation = await harness.EvaluateToolAsync(toolName, arguments, Ct);

        // Unattended, a readable call that needs consent is denied for the missing
        // operator, not for the path.
        var reachable = observation.Outcome != ApprovalOutcome.Denied
            || observation.DenyReason == ToolAuthorizer.UnattendedApprovalRequired;
        Assert.True(
            reachable == readable,
            $"{tool} '{target}' ({(attended ? "attended" : "unattended")}) was {observation.Outcome} "
            + $"({observation.DenyReason}); it must be {(readable ? "readable" : "denied")}.");
    }

    private async Task<ShellApprovalHarness> CreateHarnessAsync(bool attended)
    {
        var harness = await ShellApprovalHarness.CreateAsync(
            "shell-config-read",
            new ShellApprovalInvocation("true", Interactive: attended, Host: ShellApprovalHost.Bash52),
            Approvals.None,
            fixture.ActorSystem,
            Ct);
        var paths = harness.Paths;
        Directory.CreateDirectory(paths.WebhooksDirectory);
        Directory.CreateDirectory(paths.KeysDirectory);
        await File.WriteAllTextAsync(Path.Combine(paths.ConfigDirectory, "netclaw.json"), "{}", Ct);
        await File.WriteAllTextAsync(Path.Combine(paths.ConfigDirectory, "tool-approvals.json"), "{}", Ct);
        await File.WriteAllTextAsync(Path.Combine(paths.ConfigDirectory, "devices-copy.json"), "{}", Ct);
        await File.WriteAllTextAsync(paths.SecretsPath, "{}", Ct);
        await File.WriteAllTextAsync(paths.DevicesPath, "{}", Ct);
        await File.WriteAllTextAsync(paths.HardDenyOverridesPath, "{}", Ct);
        await File.WriteAllTextAsync(paths.DaemonEnvironmentFilePath, "X=1", Ct);
        await File.WriteAllTextAsync(Path.Combine(paths.KeysDirectory, "key-1.xml"), "<key/>", Ct);
        await File.WriteAllTextAsync(Path.Combine(paths.WebhooksDirectory, "route.json"), "{}", Ct);
        File.CreateSymbolicLink(Path.Combine(paths.ConfigDirectory, "alias.json"), paths.SecretsPath);
        File.CreateSymbolicLink(Path.Combine(paths.ConfigDirectory, "hooklink"), paths.WebhooksDirectory);
        File.CreateSymbolicLink(Path.Combine(paths.ConfigDirectory, "keyslink"), paths.KeysDirectory);
        File.CreateSymbolicLink(
            Path.Combine(harness.ProjectDirectory, "keylink.xml"),
            Path.Combine(paths.KeysDirectory, "key-1.xml"));
        File.CreateSymbolicLink(Path.Combine(harness.ProjectDirectory, "keysdir"), paths.KeysDirectory);
        File.CreateSymbolicLink(Path.Combine(harness.ProjectDirectory, "secretlink.json"), paths.SecretsPath);
        return harness;
    }

    private static string Expand(ShellApprovalHarness harness, string target)
        => target
            .Replace("{C}", harness.Paths.ConfigDirectory, StringComparison.Ordinal)
            .Replace("{K}", harness.Paths.KeysDirectory, StringComparison.Ordinal)
            .Replace("{N}", harness.Paths.BasePath, StringComparison.Ordinal)
            .Replace("{P}", harness.ProjectDirectory, StringComparison.Ordinal);
}
