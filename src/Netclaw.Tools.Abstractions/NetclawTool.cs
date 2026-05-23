// -----------------------------------------------------------------------
// <copyright file="NetclawTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
        return TryParse(arguments, out var error, out var args)
            ? await ExecuteAsync(args, context, ct)
            : error;
    }

    /// <summary>
    /// Execute the tool as a stream of <see cref="ToolCallUpdate"/> items. The
    /// default yields the non-streaming result as a single terminal completion
    /// item. Long-running tools override this to emit liveness/progress while
    /// they work, which keeps the caller's per-call watchdog alive.
    /// </summary>
    /// <remarks>
    /// Declared <c>virtual</c> rather than left to the
    /// <see cref="INetclawTool.ExecuteStreamAsync"/> default interface method:
    /// a DIM is bound at this interface-declaring base, so a derived tool's
    /// matching <c>public</c> method does not re-implement it and is unreachable
    /// through <c>INetclawTool</c> dispatch — only a derived <c>override</c> of
    /// this <c>virtual</c> is.
    /// </remarks>
    public virtual async IAsyncEnumerable<ToolCallUpdate> ExecuteStreamAsync(
        IDictionary<string, object?>? arguments,
        ToolExecutionContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ToolCompletedUpdate(await ExecuteAsync(arguments, context, ct));
    }

    /// <summary>
    /// Deserialize raw LLM arguments, returning a tool-result error string
    /// instead of throwing. Shared by the string-returning and streaming
    /// execution paths so their argument-error wording cannot drift.
    /// </summary>
    protected bool TryParse(
        IDictionary<string, object?>? arguments,
        [NotNullWhen(false)] out string? error,
        [NotNullWhen(true)] out TParams? args)
    {
        if (arguments is null)
        {
            error = $"Error: No arguments provided for tool '{Name}'.";
            args = null;
            return false;
        }

        try
        {
            error = null;
            args = ParseArguments(arguments);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Error parsing arguments for tool '{Name}': {ex.Message}";
            args = null;
            return false;
        }
    }

    // Partial method — implemented by the source generator
    public abstract string Name { get; }
    public abstract LlmFacingToolName LlmFacingName { get; }
    public abstract string Description { get; }
    public abstract string GrantCategory { get; }
    public abstract JsonElement ParameterSchema { get; }

    /// <summary>
    /// Deserialize raw LLM arguments into the typed params record.
    /// Implemented by the source generator to handle JsonElement and native CLR types.
    /// </summary>
    public abstract TParams ParseArguments(IDictionary<string, object?> arguments);
}
