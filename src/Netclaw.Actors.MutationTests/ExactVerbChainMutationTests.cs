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
/// A verb grant (two or more words) covers its words and any later words. A
/// program-only grant covers its word alone. A mutant that lets a one-word
/// grant cover a longer chain lets a "gh" grant cover "gh auth logout", so
/// these tests must reject it.
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
    public void Verb_grant_covers_later_words_but_not_a_shorter_or_other_chain()
    {
        Assert.True(Matches(Grant("git", "push"), "git", "push", "origin"));
        Assert.True(Matches(Grant("git", "push", "upstream"), "git", "push", "upstream", "feature-x"));
        Assert.False(Matches(Grant("gh", "pr", "view"), "gh", "pr"));
        Assert.False(Matches(Grant("git", "push", "upstream"), "git", "push", "origin", "main"));
        Assert.False(Matches(Grant("git", "push", "origin", "feature-x"), "git", "push", "origin", "main"));
    }

    [Fact]
    public void Legacy_phrase_gets_the_same_rule()
    {
        var legacy = ApprovalEntry.CreateLegacyExact(ApprovalShell.Bash, "git push origin");

        Assert.True(Matches(legacy, "git", "push", "origin"));
        Assert.True(Matches(legacy, "git", "push", "origin", "v1.5.1"));
        Assert.False(Matches(legacy, "git", "push"));
        Assert.False(Matches(ApprovalEntry.CreateLegacyExact(ApprovalShell.Bash, "gh"), "gh", "auth", "logout"));
    }

    // An empty word list is invalid data. It must cover nothing, not everything.
    [Fact]
    public void Empty_grant_words_cover_nothing()
    {
        Assert.False(ToolApprovalEntryComparer.CoversCommandWords([], ["git", "push"], ApprovalShell.Bash));
        Assert.False(ToolApprovalEntryComparer.CoversCommandWords([], ["git"], ApprovalShell.Bash));
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

    // The store and the doctor apply the same rule to any stored phrase, so the
    // rule itself refuses a word that is not one entry of the directory.
    [Fact]
    public void File_word_names_one_entry_of_its_directory()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = Directory.CreateTempSubdirectory("netclaw-file-entry-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "Phobos.slnx"), string.Empty);
            Directory.CreateDirectory(Path.Combine(directory, "sub"));
            File.WriteAllText(Path.Combine(directory, "sub", "x"), string.Empty);
            File.CreateSymbolicLink(Path.Combine(directory, "Linked.slnx"), Path.Combine(directory, "Phobos.slnx"));
            var relative = Path.GetRelativePath(Environment.CurrentDirectory, directory);

            Assert.True(ShellGrantFileWords.NamesEntry("Phobos.slnx", directory, out var path));
            Assert.Equal(Path.Combine(directory, "Phobos.slnx"), path);
            Assert.True(ShellGrantFileWords.NamesEntry("sub", directory, out _));
            Assert.False(ShellGrantFileWords.NamesEntry("sub/x", directory, out _));
            Assert.False(ShellGrantFileWords.NamesEntry("..", directory, out _));
            Assert.False(ShellGrantFileWords.NamesEntry(".", directory, out _));
            Assert.False(ShellGrantFileWords.NamesEntry("", directory, out _));
            Assert.False(ShellGrantFileWords.NamesEntry("Linked.slnx", directory, out _));
            Assert.False(ShellGrantFileWords.NamesEntry("missing", directory, out _));
            Assert.False(ShellGrantFileWords.NamesEntry("Phobos.slnx", null, out _));
            Assert.False(ShellGrantFileWords.NamesEntry("Phobos.slnx", relative, out _));
            Assert.True(ShellGrantFileWords.NamesLink("Linked.slnx", directory, out var linkPath));
            Assert.Equal(Path.Join(directory, "Linked.slnx"), linkPath);
            Assert.False(ShellGrantFileWords.NamesLink("Phobos.slnx", directory, out _));
            Assert.False(ShellGrantFileWords.NamesLink("missing", directory, out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // SECURITY: a plain word that names a link to a protected path is denied,
    // command word or argument. A link to an ordinary file is not. A mutant that
    // skips the screen lets a verb grant reach the link target.
    [Fact]
    public void Plain_word_link_to_a_protected_path_is_denied()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Directory.CreateTempSubdirectory("netclaw-link-word-").FullName;
        try
        {
            var protectedDirectory = Directory.CreateDirectory(Path.Join(root, "keys")).FullName;
            File.WriteAllText(Path.Join(protectedDirectory, "a.pem"), string.Empty);
            var work = Directory.CreateDirectory(Path.Join(root, "work")).FullName;
            File.WriteAllText(Path.Join(work, "README.md"), string.Empty);
            Directory.CreateSymbolicLink(Path.Join(work, "keylink"), protectedDirectory);
            Directory.CreateSymbolicLink(Path.Join(work, "keys2"), protectedDirectory);
            File.CreateSymbolicLink(Path.Join(work, "key1link"), Path.Join(protectedDirectory, "a.pem"));
            File.CreateSymbolicLink(Path.Join(work, "readmelink"), Path.Join(work, "README.md"));
            var policy = new ToolPathPolicy(ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux), [protectedDirectory]);

            Assert.True(policy.CommandReferencesDeniedPath("mytool write keylink", work));
            Assert.True(policy.CommandReferencesDeniedPath("mytool write keys2", work));
            Assert.True(policy.CommandReferencesDeniedPath("mytool write key1link", work));
            Assert.True(policy.CommandReferencesDeniedPath("mytool write README.md keylink", work));
            Assert.True(policy.CommandReferencesDeniedPath($"cd {work} && mytool keylink", root));
            Assert.False(policy.CommandReferencesDeniedPath("mytool write readmelink", work));
            Assert.False(policy.CommandReferencesDeniedPath("mytool write keylink", root));
            Assert.False(policy.CommandReferencesDeniedPath("keylink write", work));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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
