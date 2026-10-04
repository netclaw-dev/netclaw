// -----------------------------------------------------------------------
// <copyright file="ExactVerbChainMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// A shell grant covers exactly its verb chain. A mutant that turns the
/// equality check back into prefix matching lets a "gh" grant cover
/// "gh auth logout", so these tests must reject it.
/// </summary>
public sealed class ExactVerbChainMutationTests
{
    [Fact]
    public void Grant_covers_the_equal_verb_chain()
    {
        Assert.True(Matches(Grant("gh"), "gh"));
        Assert.True(Matches(Grant("gh", "pr", "view"), "gh", "pr", "view"));
    }

    [Fact]
    public void Bare_program_grant_does_not_cover_a_subcommand()
    {
        Assert.False(Matches(Grant("gh"), "gh", "auth", "logout"));
        Assert.False(Matches(Grant("git"), "git", "push"));
    }

    [Fact]
    public void Subcommand_grant_does_not_cover_a_longer_or_shorter_chain()
    {
        Assert.False(Matches(Grant("git", "push"), "git", "push", "origin"));
        Assert.False(Matches(Grant("gh", "pr", "view"), "gh", "pr"));
    }

    [Fact]
    public void Legacy_phrase_does_not_cover_a_longer_chain()
    {
        var legacy = ApprovalEntry.CreateLegacyExact(ApprovalShell.Bash, "git push origin");

        Assert.True(Matches(legacy, "git", "push", "origin"));
        Assert.False(Matches(legacy, "git", "push", "origin", "v1.5.1"));
    }

    // R1: a program path names its file. A mutant that skips the join with the
    // working directory lets a "./tool" grant run any file named tool.
    [Fact]
    public void Program_path_names_the_file_in_its_working_directory()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Equal("/opt/tools/ilspycmd", ProgramWord("cd /opt/tools && ./ilspycmd --version"));
        Assert.Equal("/opt/tools/ilspycmd", ProgramWord("cd /opt/other && ../tools/ilspycmd --version"));
        Assert.Equal("/opt/tools/ilspycmd", ProgramWord("/opt/./tools/ilspycmd --version"));
        Assert.Equal("dotnet", ProgramWord("dotnet --info"));
    }

    // An older "./tool" grant with no folder covers only files named "tool".
    [Fact]
    public void Legacy_relative_grant_covers_only_its_file_name()
    {
        Assert.True(ShellProgramPath.MatchesLegacyRelative("./ilspycmd", "/opt/tools/ilspycmd"));
        Assert.True(ShellProgramPath.MatchesLegacyRelative("../bin/tool", "/opt/bin/tool"));
        Assert.False(ShellProgramPath.MatchesLegacyRelative("./ilspycmd", "/opt/tools/my-ilspycmd"));
        Assert.False(ShellProgramPath.MatchesLegacyRelative("../bin/tool", "/opt/sbin/tool"));
    }

    // A word after the verb slot that names an existing file or directory is an
    // operand. A mutant that keeps it stores a file name in a grant. A mutant
    // that drops the verb slot, a link, or a word without a file widens a grant.
    [Fact]
    public void File_word_after_the_verb_slot_leaves_the_command_words()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = Directory.CreateTempSubdirectory("netclaw-file-word-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "Phobos.slnx"), string.Empty);
            File.WriteAllText(Path.Combine(directory, "push"), string.Empty);
            Directory.CreateDirectory(Path.Combine(directory, "src"));
            File.CreateSymbolicLink(Path.Combine(directory, "Linked.slnx"), Path.Combine(directory, "Phobos.slnx"));

            Assert.Equal(["dotnet", "build"], CommandWords("dotnet build Phobos.slnx -c Release", directory));
            Assert.Equal(["dotnet", "list", "package"], CommandWords("dotnet list Phobos.slnx package", directory));
            Assert.Equal(["dotnet", "build"], CommandWords("dotnet build src", directory));
            // The dropped word is a path operand, so the path checks see its scope.
            Assert.Contains(Path.Combine(directory, "src"), Directories("dotnet build src", directory));
            Assert.Equal(["git", "push", "origin", "main"], CommandWords("git push origin main", directory));
            Assert.Equal(["git", "push", "origin", "main"], CommandWords("git push origin main", workingDirectory: null));
            Assert.Equal(["dotnet", "build", "Linked.slnx"], CommandWords("dotnet build Linked.slnx", directory));
            Assert.Equal(["dotnet", "build", "Phobos.slnx"], CommandWords("dotnet build Phobos.slnx", workingDirectory: null));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IReadOnlyList<string> CommandWords(string command, string? workingDirectory)
        => Assert.Single(Candidates(command, workingDirectory)
                .Select(static candidate => string.Join(' ', candidate.VerbTokens!))
                .Distinct(StringComparer.Ordinal))
            .Split(' ');

    private static IReadOnlyList<string?> Directories(string command, string workingDirectory)
        => Candidates(command, workingDirectory).Select(static candidate => candidate.Directory).ToArray();

    private static IReadOnlyList<ApprovalCandidate> Candidates(string command, string? workingDirectory)
        => new ShellApprovalMatcher(ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux))
            .AnalyzeInvocation(
                new ToolName(ShellTool.ToolName),
                new Dictionary<string, object?>
                {
                    ["Command"] = command,
                    ["WorkingDirectory"] = workingDirectory,
                })
            .Candidates;

    private static string ProgramWord(string command)
    {
        var analysis = new ShellApprovalMatcher(ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux))
            .AnalyzeInvocation(
                new ToolName(ShellTool.ToolName),
                new Dictionary<string, object?>
                {
                    ["Command"] = command,
                    ["WorkingDirectory"] = "/opt",
                });
        return Assert.Single(analysis.Candidates, static candidate => candidate.Verb != "cd").VerbTokens![0];
    }

    private static ApprovalEntry Grant(params string[] tokens)
        => ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, tokens);

    private static bool Matches(ApprovalEntry grant, params string[] tokens)
        => ApprovalPatternMatching.MatchesShellApproval(
            new ApprovalCandidate(string.Join(' ', tokens), Directory: null)
            {
                VerbTokens = Array.AsReadOnly(tokens),
                Shell = ApprovalShell.Bash,
            },
            cwd: null,
            [grant]);
}
