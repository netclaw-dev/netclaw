// -----------------------------------------------------------------------
// <copyright file="FileSearchTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

[NetclawTool(ToolName,
    "Recursively search authorized workspace files by literal file name or UTF-8 text without shell.",
    Grant = "file")]
public sealed partial class FileSearchTool : NetclawTool<FileSearchTool.Params>
{
    public const string ToolName = "file_search";
    internal const int MaximumResults = 200;
    internal const int MaximumEntries = 10_000;
    internal const int MaximumContentBytes = 4 * 1024 * 1024;
    private const int DefaultResults = 50;
    private const int DefaultEntries = 2_000;
    private const int DefaultContentBytes = 1024 * 1024;
    private const int MaximumExcerptChars = 400;

    private readonly ToolPathPolicy _pathPolicy;
    private readonly ScopedFileAccessPolicy _fileAccessPolicy;

    public record Params(
        [property: Description("Authorized directory to search. Relative paths use the current project, then session scratch.")] string Root,
        [property: Description("Literal text to find; regular expressions and executable query syntax are not accepted.")] string Query,
        [property: Description("Search mode: 'name' matches file names; 'content' matches UTF-8 text lines.")] string Mode,
        [property: Description("Maximum matches returned (default 50, maximum 200).")] int? MaxResults = null,
        [property: Description("Maximum filesystem entries inspected (default 2000, maximum 10000).")] int? MaxFiles = null,
        [property: Description("Maximum file-content bytes inspected (default 1048576, maximum 4194304).")] int? MaxContentBytes = null);

    public FileSearchTool(ToolConfig config, NetclawPaths paths, ToolPathPolicy pathPolicy)
    {
        _pathPolicy = pathPolicy;
        _fileAccessPolicy = new ScopedFileAccessPolicy(config, paths);
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Query))
            return context.InvalidInput("Error: 'Query' is required.");

        var mode = args.Mode?.Trim().ToLowerInvariant();
        if (mode is not ("name" or "content"))
            return context.InvalidInput("Error: 'Mode' must be either 'name' or 'content'.");

        if (!TryResolveLimits(args, out var limits, out var limitError))
            return context.InvalidInput(limitError);

        if (!_fileAccessPolicy.TryResolveReadPath(
                args.Root,
                context,
                out var root,
                out var accessError,
                out var resolutionFailure))
        {
            return context.PathResolutionFailure(accessError, resolutionFailure);
        }

        if (_pathPolicy.IsReadDenied(root))
            return context.AccessDenied(FileToolErrors.CredentialReadDenied(root));

        if (!Directory.Exists(root))
            return context.NotFound($"Error: Directory not found: {root}");

        if (IsReparsePoint(root))
            return context.AccessDenied($"Error: Search root may not be a symbolic link: {root}");

        try
        {
            var state = new SearchState(root, args.Query, mode, limits);
            await SearchAsync(state, ct);
            return context.Success(FormatResult(state));
        }
        catch (UnauthorizedAccessException ex)
        {
            return context.AccessDenied($"Error: Permission denied while searching: {ex.Message}");
        }
        catch (DirectoryNotFoundException ex)
        {
            return context.NotFound($"Error: Directory not found while searching: {ex.Message}");
        }
        catch (IOException ex)
        {
            return context.TransientFailure($"Error searching files: {ex.Message}");
        }
    }

    private static bool TryResolveLimits(Params args, out SearchLimits limits, out string error)
    {
        limits = default;
        if (!WorkspaceFileToolSupport.TryResolveBound(
                args.MaxResults,
                DefaultResults,
                MaximumResults,
                nameof(args.MaxResults),
                out var results,
                out error)
            || !WorkspaceFileToolSupport.TryResolveBound(
                args.MaxFiles,
                DefaultEntries,
                MaximumEntries,
                nameof(args.MaxFiles),
                out var entries,
                out error)
            || !WorkspaceFileToolSupport.TryResolveBound(
                args.MaxContentBytes,
                DefaultContentBytes,
                MaximumContentBytes,
                nameof(args.MaxContentBytes),
                out var contentBytes,
                out error))
        {
            return false;
        }

        limits = new SearchLimits(results, entries, contentBytes);
        return true;
    }

    private async Task SearchAsync(SearchState state, CancellationToken ct)
    {
        state.Pending.Add(state.Root);
        while (state.Pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var path = state.Pending.Min!;
            state.Pending.Remove(path);

            if (path.Any(char.IsControl))
            {
                state.SkippedEntries++;
                continue;
            }

            if (_pathPolicy.IsReadDenied(path))
            {
                state.SkippedEntries++;
                continue;
            }

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException
                                       or DirectoryNotFoundException
                                       or UnauthorizedAccessException
                                       or IOException)
            {
                state.SkippedEntries++;
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                state.SkippedEntries++;
                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                EnqueueChildren(state, path);
                continue;
            }

            if (state.Mode == "name")
            {
                if (Path.GetFileName(path).Contains(state.Query, StringComparison.OrdinalIgnoreCase))
                    state.Matches.Add(new SearchMatch(ToRelativePath(state.Root, path), null, null));
            }
            else
            {
                await SearchContentAsync(state, path, ct);
                if (state.ContentBytes >= state.Limits.ContentBytes)
                {
                    state.Truncated = state.Pending.Count > 0 || state.Truncated;
                    break;
                }
            }

            if (state.Matches.Count >= state.Limits.Results)
            {
                state.Truncated = true;
                break;
            }
        }

        if (state.Pending.Count > 0)
            state.Truncated = true;
    }

    private static void EnqueueChildren(SearchState state, string directory)
    {
        try
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                if (state.VisitedEntries >= state.Limits.Entries)
                {
                    state.Truncated = true;
                    break;
                }

                state.VisitedEntries++;
                state.Pending.Add(child);
            }
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException
                                   or UnauthorizedAccessException
                                   or IOException)
        {
            state.SkippedEntries++;
        }
    }

    private static async Task SearchContentAsync(SearchState state, string path, CancellationToken ct)
    {
        var remainingBytes = state.Limits.ContentBytes - state.ContentBytes;
        if (remainingBytes <= 0)
        {
            state.Truncated = true;
            return;
        }

        try
        {
            var fileLength = new FileInfo(path).Length;
            var byteLimit = (int)Math.Min(remainingBytes, Math.Min(fileLength, int.MaxValue));
            state.ContentBytes += byteLimit;
            var read = await WorkspaceFileToolSupport.ReadUtf8BytesAsync(path, byteLimit, ct);

            if (read.Truncated)
                state.Truncated = true;

            var lineNumber = 0;
            using var reader = new StringReader(read.Content);
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                var matchIndex = line.IndexOf(state.Query, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                    continue;

                state.Matches.Add(new SearchMatch(
                    ToRelativePath(state.Root, path),
                    lineNumber,
                    SanitizeExcerpt(line)));
                if (state.Matches.Count >= state.Limits.Results)
                    return;
            }
        }
        catch (DecoderFallbackException)
        {
            state.SkippedEntries++;
        }
        catch (Exception ex) when (ex is FileNotFoundException
                                   or DirectoryNotFoundException
                                   or UnauthorizedAccessException
                                   or IOException)
        {
            state.SkippedEntries++;
        }
    }

    private static string FormatResult(SearchState state)
    {
        var result = new StringBuilder();
        result.AppendLine($"mode={state.Mode}");
        result.AppendLine($"root={state.Root}");
        result.AppendLine($"matches={state.Matches.Count}");
        result.AppendLine($"visited={state.VisitedEntries}");
        result.AppendLine($"skipped={state.SkippedEntries}");
        result.AppendLine($"content_bytes={state.ContentBytes}");
        result.AppendLine($"truncated={state.Truncated.ToString().ToLowerInvariant()}");

        foreach (var match in state.Matches)
        {
            result.Append(match.Path);
            if (match.Line is { } line)
                result.Append(':').Append(line).Append(':').Append(' ').Append(match.Excerpt);
            result.AppendLine();
        }

        return result.ToString().TrimEnd();
    }

    private static string ToRelativePath(string root, string path)
        => Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static string SanitizeExcerpt(string line)
    {
        var buffer = line
            .Select(static character => char.IsControl(character) && character != '\t' ? ' ' : character)
            .Take(MaximumExcerptChars)
            .ToArray();
        return new string(buffer).Trim();
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private readonly record struct SearchLimits(int Results, int Entries, int ContentBytes);
    private sealed record SearchMatch(string Path, int? Line, string? Excerpt);

    private sealed class SearchState(
        string root,
        string query,
        string mode,
        SearchLimits limits)
    {
        public string Root { get; } = root;
        public string Query { get; } = query;
        public string Mode { get; } = mode;
        public SearchLimits Limits { get; } = limits;
        public SortedSet<string> Pending { get; } = new(StringComparer.Ordinal);
        public List<SearchMatch> Matches { get; } = [];
        public int VisitedEntries { get; set; }
        public int SkippedEntries { get; set; }
        public int ContentBytes { get; set; }
        public bool Truncated { get; set; }
    }
}
