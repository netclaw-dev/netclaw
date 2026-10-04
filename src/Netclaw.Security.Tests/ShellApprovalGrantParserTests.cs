// -----------------------------------------------------------------------
// <copyright file="ShellApprovalGrantParserTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellApprovalGrantParserTests
{
    // A word with a digit ends the chain, as for a call. A program path names
    // its file by the lexical absolute path (R1). A phrase in another form is
    // not canonical, so the CLI names the canonical phrase instead.
    [Theory]
    [InlineData("git show b42bf5a", "git show")]
    [InlineData("git push origin v0.4.0", "git push origin")]
    [InlineData("/opt/./tools//bin/../ilspycmd", "/opt/tools/ilspycmd")]
    // A word with a space has one canonical form: single quotes.
    [InlineData("\"/opt/My App/bin/tool\"", "'/opt/My App/bin/tool'")]
    public void Phrase_that_is_not_canonical_names_the_canonical_phrase(string source, string canonical)
    {
        var parsed = ShellApprovalGrantParser.TryCreateTokenPrefix(
            ApprovalShell.Bash,
            source,
            out var entry,
            out var error);

        Assert.False(parsed);
        Assert.Null(entry);
        Assert.Contains(canonical, error);
    }

    [Theory]
    [InlineData(ApprovalShell.Bash, "git push", "git", "push")]
    [InlineData(ApprovalShell.Bash, "git push origin", "git", "push", "origin")]
    [InlineData(ApprovalShell.Bash, "status-report", "status-report")]
    [InlineData(ApprovalShell.Bash, "/opt/tools/ilspycmd", "/opt/tools/ilspycmd")]
    [InlineData(ApprovalShell.Bash, "'/opt/My App/bin/tool'", "/opt/My App/bin/tool")]
    [InlineData(ApprovalShell.PowerShell, "Get-Content", "Get-Content")]
    [InlineData(ApprovalShell.PowerShell, "curl", "curl")]
    [InlineData(ApprovalShell.PowerShell, "gerr", "gerr")]
    public void Exact_static_phrase_creates_token_prefix(
        ApprovalShell shell,
        string source,
        params string[] expectedTokens)
    {
        var parsed = ShellApprovalGrantParser.TryCreateTokenPrefix(
            shell,
            source,
            out var entry,
            out var error);

        Assert.True(parsed, error);
        Assert.NotNull(entry);
        Assert.Equal(shell, entry.Shell);
        Assert.Equal(ApprovalMatchKind.TokenPrefix, entry.Match);
        Assert.Equal(expectedTokens, entry.VerbTokens);
        Assert.Null(entry.Directory);
    }

    [Theory]
    [InlineData(ApprovalShell.Bash, "git push --force")]
    [InlineData(ApprovalShell.Bash, "MODE=safe git push")]
    [InlineData(ApprovalShell.Bash, "git push >out")]
    [InlineData(ApprovalShell.Bash, "git status; rm file")]
    [InlineData(ApprovalShell.Bash, "git  push")]
    [InlineData(ApprovalShell.Bash, "$command")]
    [InlineData(ApprovalShell.Bash, "bash -c echo")]
    // A relative program path names a different file in each directory.
    [InlineData(ApprovalShell.Bash, "./ilspycmd")]
    [InlineData(ApprovalShell.Bash, "../tools/ilspycmd")]
    [InlineData(ApprovalShell.PowerShell, "Get-Content file.txt")]
    [InlineData(ApprovalShell.PowerShell, "gci")]
    [InlineData(ApprovalShell.PowerShell, "Get-Content; Remove-Item")]
    [InlineData(ApprovalShell.PowerShell, "& $command")]
    public void Extra_or_dynamic_shell_source_fails(
        ApprovalShell shell,
        string source)
    {
        var parsed = ShellApprovalGrantParser.TryCreateTokenPrefix(
            shell,
            source,
            out var entry,
            out var error);

        Assert.False(parsed);
        Assert.Null(entry);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Unknown_shell_identity_fails_without_a_fallback()
    {
        var parsed = ShellApprovalGrantParser.TryCreateTokenPrefix(
            (ApprovalShell)99,
            "git push",
            out var entry,
            out var error);

        Assert.False(parsed);
        Assert.Null(entry);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Resolved_windows_powershell_environment_requires_its_legacy_canonical_form()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            ShellSyntaxTree.PwshDialect.WindowsPowerShell51);

        var parsed = ShellApprovalGrantParser.TryCreateTokenPrefix(
            environment,
            "curl",
            out var entry,
            out var error);

        Assert.False(parsed);
        Assert.Null(entry);
        Assert.Contains("Invoke-WebRequest", error, StringComparison.Ordinal);
    }
}
