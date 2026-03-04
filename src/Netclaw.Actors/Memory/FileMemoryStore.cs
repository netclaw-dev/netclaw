using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Netclaw.Actors.Memory;

/// <summary>
/// File-backed memory store using markdown files with YAML front matter.
/// Manages <c>~/.netclaw/memories/</c> with a progressive-discovery index.
/// Thread-safe via <see cref="SemaphoreSlim"/> for write operations.
/// </summary>
public sealed partial class FileMemoryStore
{
    private readonly string _memoriesDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Cached index entries, rebuilt on first use or after writes.</summary>
    private List<MemoryEntry>? _cachedEntries;

    public FileMemoryStore(string memoriesDirectory, TimeProvider timeProvider)
    {
        _memoriesDirectory = memoriesDirectory;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Store a new memory as a markdown file with YAML front matter.
    /// Rebuilds the index after writing.
    /// </summary>
    public async Task StoreAsync(string title, string content, string[]? tags = null, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_memoriesDirectory);

            var now = _timeProvider.GetUtcNow();
            var fileName = GenerateFileName(now, title);
            var filePath = Path.Combine(_memoriesDirectory, fileName);

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"title: \"{EscapeYamlString(title)}\"");
            if (tags is { Length: > 0 })
                sb.AppendLine($"tags: [{string.Join(", ", tags)}]");
            sb.AppendLine($"created: {now:yyyy-MM-ddTHH:mm:ssZ}");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append(content);

            await File.WriteAllTextAsync(filePath, sb.ToString(), ct);

            // Invalidate cache and rebuild index
            _cachedEntries = null;
            await RebuildIndexAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Search memories by substring match against title, tags, and content.
    /// Scores: title match > tag match > content match.
    /// </summary>
    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(string query, int maxResults = 5, string[]? filterTags = null, CancellationToken ct = default)
    {
        var entries = await GetEntriesAsync(ct);
        if (entries.Count == 0 || string.IsNullOrWhiteSpace(query))
            return [];

        // Pre-filter by tags if specified
        IEnumerable<MemoryEntry> candidates = entries;
        if (filterTags is { Length: > 0 })
        {
            var filterSet = new HashSet<string>(filterTags, StringComparer.OrdinalIgnoreCase);
            candidates = entries.Where(e => e.Tags.Any(t => filterSet.Contains(t)));
        }

        var queryLower = query.ToLowerInvariant();
        var queryTerms = queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var scored = new List<MemorySearchResult>();

        foreach (var entry in candidates)
        {
            var score = ScoreEntry(entry, queryTerms);
            if (score > 0)
            {
                scored.Add(new MemorySearchResult(entry.Id, entry.Title, entry.Tags, entry.Content, entry.FilePath, score));
            }
        }

        return scored
            .OrderByDescending(r => r.Score)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Load full memory entries by their IDs.
    /// </summary>
    public async Task<IReadOnlyList<MemoryEntry>> GetByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var entries = await GetEntriesAsync(ct);
        var idSet = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        return entries.Where(e => idSet.Contains(e.Id)).ToList();
    }

    /// <summary>
    /// Find-and-replace edit on a memory file's content.
    /// Returns true if the replacement was made; false if the old text was not found.
    /// </summary>
    public async Task<bool> EditAsync(string id, string oldText, string newText, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var entry = FindEntryById(id);
            if (entry is null)
                return false;

            var fileContent = await File.ReadAllTextAsync(entry.FilePath, ct);
            if (!fileContent.Contains(oldText, StringComparison.Ordinal))
                return false;

            var updated = fileContent.Replace(oldText, newText, StringComparison.Ordinal);
            await File.WriteAllTextAsync(entry.FilePath, updated, ct);

            _cachedEntries = null;
            await RebuildIndexAsync(ct);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Delete a memory file by its ID.
    /// Returns true if the file was found and deleted; false otherwise.
    /// </summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var entry = FindEntryById(id);
            if (entry is null)
                return false;

            File.Delete(entry.FilePath);

            _cachedEntries = null;
            await RebuildIndexAsync(ct);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Get all indexed entries, rebuilding from disk on first access.
    /// </summary>
    public async Task<IReadOnlyList<MemoryEntry>> GetEntriesAsync(CancellationToken ct = default)
    {
        if (_cachedEntries is not null)
            return _cachedEntries;

        await _writeLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedEntries is not null)
                return _cachedEntries;

            _cachedEntries = ScanMemoryFiles();
            await WriteIndexFileAsync(_cachedEntries, ct);
            return _cachedEntries;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private List<MemoryEntry> ScanMemoryFiles()
    {
        var entries = new List<MemoryEntry>();

        if (!Directory.Exists(_memoriesDirectory))
            return entries;

        var indexFileName = Path.GetFileName(GetIndexPath());

        foreach (var filePath in Directory.EnumerateFiles(_memoriesDirectory, "*.md"))
        {
            var fileName = Path.GetFileName(filePath);
            if (string.Equals(fileName, indexFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            var entry = ParseMemoryFile(filePath);
            if (entry is not null)
                entries.Add(entry);
        }

        return entries.OrderByDescending(e => e.Created).ToList();
    }

    private static MemoryEntry? ParseMemoryFile(string filePath)
    {
        string text;
        try
        {
            text = File.ReadAllText(filePath);
        }
        catch (IOException)
        {
            return null;
        }

        var title = Path.GetFileNameWithoutExtension(filePath);
        string[] tags = [];
        DateTimeOffset created = default;
        var content = text;

        // Parse YAML front matter
        if (text.StartsWith("---", StringComparison.Ordinal))
        {
            var endIndex = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (endIndex > 0)
            {
                var frontMatter = text[3..endIndex].Trim();
                content = text[(endIndex + 4)..].TrimStart('\r', '\n');

                foreach (var line in frontMatter.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                    {
                        title = trimmed[6..].Trim().Trim('"');
                    }
                    else if (trimmed.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
                    {
                        var tagsPart = trimmed[5..].Trim().Trim('[', ']');
                        tags = tagsPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    }
                    else if (trimmed.StartsWith("created:", StringComparison.OrdinalIgnoreCase))
                    {
                        var dateStr = trimmed[8..].Trim();
                        DateTimeOffset.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out created);
                    }
                }
            }
        }

        var id = Path.GetFileNameWithoutExtension(filePath);
        return new MemoryEntry(id, title, tags, content, filePath, created);
    }

    private static double ScoreEntry(MemoryEntry entry, string[] queryTerms)
    {
        double score = 0;
        var titleLower = entry.Title.ToLowerInvariant();
        var tagsLower = string.Join(" ", entry.Tags).ToLowerInvariant();
        var contentLower = entry.Content.ToLowerInvariant();

        foreach (var term in queryTerms)
        {
            if (titleLower.Contains(term, StringComparison.Ordinal))
                score += 3.0;
            if (tagsLower.Contains(term, StringComparison.Ordinal))
                score += 2.0;
            if (contentLower.Contains(term, StringComparison.Ordinal))
                score += 1.0;
        }

        return score;
    }

    private async Task RebuildIndexAsync(CancellationToken ct)
    {
        var entries = _cachedEntries ?? ScanMemoryFiles();
        _cachedEntries = entries;
        await WriteIndexFileAsync(entries, ct);
    }

    private async Task WriteIndexFileAsync(List<MemoryEntry> entries, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Memory Index");
        sb.AppendLine();

        if (entries.Count == 0)
        {
            sb.AppendLine("_No memories stored yet._");
        }
        else
        {
            sb.AppendLine("| Date | Title | Tags |");
            sb.AppendLine("|------|-------|------|");

            foreach (var entry in entries)
            {
                var date = entry.Created == default ? "unknown" : entry.Created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var tags = entry.Tags.Length > 0 ? string.Join(", ", entry.Tags) : "";
                var fileName = Path.GetFileName(entry.FilePath);
                sb.AppendLine($"| {date} | [{EscapeMarkdownPipe(entry.Title)}]({fileName}) | {EscapeMarkdownPipe(tags)} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"_Total: {entries.Count} memories_");

        await File.WriteAllTextAsync(GetIndexPath(), sb.ToString(), ct);
    }

    private string GetIndexPath() => Path.Combine(_memoriesDirectory, "memory.md");

    internal static string GenerateFileName(DateTimeOffset date, string title)
    {
        var kebab = KebabCaseRegex().Replace(title.Trim().ToLowerInvariant(), "-");
        kebab = MultiDashRegex().Replace(kebab, "-").Trim('-');
        if (kebab.Length > 60)
            kebab = kebab[..60].TrimEnd('-');
        return $"{date:yyyy-MM-dd}-{kebab}.md";
    }

    private static string EscapeYamlString(string value)
        => value.Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string EscapeMarkdownPipe(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal);

    private MemoryEntry? FindEntryById(string id)
    {
        // Must be called under _writeLock or after GetEntriesAsync
        var entries = _cachedEntries ?? ScanMemoryFiles();
        return entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex KebabCaseRegex();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex MultiDashRegex();
}

/// <summary>
/// Parsed memory file entry with front matter metadata.
/// </summary>
public sealed record MemoryEntry(
    string Id,
    string Title,
    string[] Tags,
    string Content,
    string FilePath,
    DateTimeOffset Created);

/// <summary>
/// Search result with relevance score.
/// </summary>
public sealed record MemorySearchResult(
    string Id,
    string Title,
    string[] Tags,
    string Content,
    string FilePath,
    double Score);
