using System.ComponentModel;
using System.Text;
using Netclaw.Tools;

namespace Netclaw.Actors.Skills;

/// <summary>
/// Meta-tool that searches the <see cref="SkillRegistry"/> for available skills.
/// Returns full file content of matching skills so the LLM can read and follow
/// the procedural instructions. Max 3 results to limit token cost.
/// </summary>
[NetclawTool("search_skills",
    "Search for available skills by keyword. Returns full skill text so you can follow the instructions. "
    + "Use this to find procedures, workflows, and guidance before starting unfamiliar tasks.",
    Grant = "builtin")]
public sealed partial class SearchSkillsTool : NetclawTool<SearchSkillsTool.Params>
{
    private readonly SkillRegistry _registry;
    private const int MaxResults = 3;

    public record Params(
        [property: Description("Search query to match against skill names and descriptions")]
        string Query);

    public SearchSkillsTool(SkillRegistry registry)
    {
        _registry = registry;
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        var query = args.Query.Trim();
        var results = _registry.Search(query, MaxResults);

        if (results.Count == 0)
            return Task.FromResult($"No skills found matching '{query}'.");

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} skill(s):");

        foreach (var skill in results)
        {
            sb.AppendLine();
            sb.AppendLine($"━━━ {skill.DisplayName} ({skill.Name}) ━━━");

            try
            {
                var content = File.ReadAllText(skill.FilePath);
                sb.AppendLine(content);
            }
            catch (IOException ex)
            {
                sb.AppendLine($"[Error reading skill file: {ex.Message}]");
            }
        }

        return Task.FromResult(sb.ToString());
    }
}
