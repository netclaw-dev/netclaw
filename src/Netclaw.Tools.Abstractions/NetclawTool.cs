using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Netclaw.Tools;

/// <summary>
/// Base class for source-generated tools. The generator implements
/// <see cref="INetclawTool"/> members and a typed <c>ParseArguments</c> method.
/// Tool authors override <see cref="ExecuteAsync(TParams, CancellationToken)"/>.
/// </summary>
/// <typeparam name="TParams">
/// A record type whose constructor parameters define the tool's schema.
/// The source generator reads these parameters to emit JSON schema and parsing logic.
/// </typeparam>
public abstract partial class NetclawTool<TParams> : INetclawTool where TParams : class
{
    private AITool? _aiTool;

    // These are implemented by the source generator in the partial class:
    //   public string Name { get; }
    //   public string Description { get; }
    //   public string GrantCategory { get; }
    //   public JsonElement ParameterSchema { get; }
    //   public TParams ParseArguments(IDictionary<string, object?> arguments)

    /// <inheritdoc />
    public AITool ToAITool()
    {
        return _aiTool ??= AIFunctionFactory.CreateDeclaration(Name, Description, ParameterSchema);
    }

    /// <summary>
    /// Execute the tool with typed, deserialized arguments.
    /// </summary>
    protected abstract Task<string> ExecuteAsync(TParams args, CancellationToken ct);

    /// <summary>
    /// Execute the tool with typed arguments and execution context.
    /// Override this in tools that need session-scoped state (e.g. temp directories).
    /// Default delegates to the context-free overload.
    /// </summary>
    protected virtual Task<string> ExecuteAsync(TParams args, ToolExecutionContext context, CancellationToken ct)
        => ExecuteAsync(args, ct);

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
    {
        return await ExecuteAsync(arguments, ToolExecutionContext.Empty, ct);
    }

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolExecutionContext context, CancellationToken ct = default)
    {
        if (arguments is null)
            return $"Error: No arguments provided for tool '{Name}'.";

        TParams args;
        try
        {
            args = ParseArguments(arguments);
        }
        catch (Exception ex)
        {
            return $"Error parsing arguments for tool '{Name}': {ex.Message}";
        }

        return await ExecuteAsync(args, context, ct);
    }

    // Partial method — implemented by the source generator
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string GrantCategory { get; }
    public abstract JsonElement ParameterSchema { get; }

    /// <summary>
    /// Deserialize raw LLM arguments into the typed params record.
    /// Implemented by the source generator to handle JsonElement and native CLR types.
    /// </summary>
    public abstract TParams ParseArguments(IDictionary<string, object?> arguments);
}
