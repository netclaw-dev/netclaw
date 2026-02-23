namespace Netclaw.Actors.Tools;

/// <summary>
/// Registers all first-party tool definitions with the <see cref="ToolRegistry"/>.
/// Tools are source-generated from <see cref="Netclaw.Tools.NetclawToolAttribute"/> — see ADR-001.
/// </summary>
public static class ToolRegistrationExtensions
{
    public static ToolRegistry WithFirstPartyTools(this ToolRegistry registry, ToolConfig config)
    {
        registry.Register(new ShellTool(config));
        registry.Register(new FileReadTool(config));
        registry.Register(new FileWriteTool());

        return registry;
    }
}
