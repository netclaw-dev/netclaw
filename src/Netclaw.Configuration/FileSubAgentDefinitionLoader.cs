using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Netclaw.Configuration;

/// <summary>
/// Loads subagent definitions from JSON files in the agents directory.
/// Each agent is a <c>.json</c> file with an optional companion <c>.md</c> prompt file.
/// Called during startup after MCP discovery so tool names are resolvable.
/// </summary>
public sealed class FileSubAgentDefinitionLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _agentsDirectory;
    private readonly ILogger<FileSubAgentDefinitionLoader> _logger;

    public FileSubAgentDefinitionLoader(NetclawPaths paths, ILogger<FileSubAgentDefinitionLoader> logger)
    {
        _agentsDirectory = paths.AgentsDirectory;
        _logger = logger;
    }

    /// <summary>
    /// Scan the agents directory for <c>*.json</c> files and return parsed profiles.
    /// Invalid or unreadable files are logged and skipped.
    /// </summary>
    public IReadOnlyList<SubAgentProfileFile> LoadAll()
    {
        if (!Directory.Exists(_agentsDirectory))
        {
            _logger.LogDebug("Agents directory does not exist: {Path}", _agentsDirectory);
            return [];
        }

        var files = Directory.GetFiles(_agentsDirectory, "*.json");
        if (files.Length == 0)
        {
            _logger.LogDebug("No agent definition files found in {Path}", _agentsDirectory);
            return [];
        }

        var results = new List<SubAgentProfileFile>();
        foreach (var filePath in files)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var file = JsonSerializer.Deserialize<SubAgentProfileFile>(json, JsonOptions);
                if (file is null)
                {
                    _logger.LogWarning("Agent definition file is empty or invalid: {Path}", filePath);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(file.Name))
                {
                    _logger.LogWarning("Agent definition missing 'name': {Path}", filePath);
                    continue;
                }

                // Resolve system prompt from companion file or inline
                if (!string.IsNullOrWhiteSpace(file.SystemPromptFile))
                {
                    var promptPath = Path.Combine(_agentsDirectory, file.SystemPromptFile);
                    if (File.Exists(promptPath))
                    {
                        file = file with { ResolvedSystemPrompt = File.ReadAllText(promptPath) };
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Agent '{Name}' references prompt file '{PromptFile}' which does not exist — skipping",
                            file.Name, file.SystemPromptFile);
                        continue;
                    }
                }
                else if (string.IsNullOrWhiteSpace(file.SystemPrompt))
                {
                    _logger.LogWarning(
                        "Agent '{Name}' has neither 'systemPromptFile' nor 'systemPrompt' — skipping",
                        file.Name);
                    continue;
                }

                if (file.Tools is null || file.Tools.Count == 0)
                {
                    _logger.LogWarning("Agent '{Name}' has no tools defined — skipping", file.Name);
                    continue;
                }

                results.Add(file);
                _logger.LogInformation("Loaded agent definition: {Name} ({ToolCount} tools, timeout={Timeout}s)",
                    file.Name, file.Tools.Count, file.TimeoutSeconds);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse agent definition: {Path}", filePath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to read agent definition: {Path}", filePath);
            }
        }

        return results;
    }
}

/// <summary>
/// JSON wire type for a subagent definition file.
/// </summary>
public sealed record SubAgentProfileFile
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; init; }

    [JsonPropertyName("systemPromptFile")]
    public string? SystemPromptFile { get; init; }

    [JsonPropertyName("tools")]
    public List<string> Tools { get; init; } = [];

    [JsonPropertyName("modelRole")]
    public string ModelRole { get; init; } = "Compaction";

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Resolved prompt content from the companion .md file. Not serialized.
    /// </summary>
    [JsonIgnore]
    public string? ResolvedSystemPrompt { get; init; }

    /// <summary>
    /// Returns the effective system prompt (resolved file content or inline).
    /// </summary>
    [JsonIgnore]
    public string EffectiveSystemPrompt => ResolvedSystemPrompt ?? SystemPrompt ?? string.Empty;

    /// <summary>
    /// Convert to the runtime <see cref="SubAgentProfile"/> type used by the registry and spawner.
    /// </summary>
    public SubAgentProfile ToProfile()
    {
        var modelRole = Enum.TryParse<Netclaw.Configuration.ModelRole>(ModelRole, ignoreCase: true, out var parsed)
            ? parsed
            : Netclaw.Configuration.ModelRole.Compaction;

        return new SubAgentProfile
        {
            Name = Name,
            Description = Description,
            SystemPrompt = EffectiveSystemPrompt,
            ToolNames = Tools,
            ModelRole = modelRole,
            TimeoutSeconds = TimeoutSeconds,
            Visibility = SubAgentVisibility.UserFacing
        };
    }
}
