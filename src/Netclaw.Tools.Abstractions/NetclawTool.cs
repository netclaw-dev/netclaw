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
/// Tool implementations receive a required per-call execution context.
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
    /// Execute the tool with typed arguments and required execution context.
    /// </summary>
    protected abstract Task<string> ExecuteAsync(TParams args, ToolInvocationContext context, CancellationToken ct);

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct = default)
    {
        return TryParse(arguments, out var error, out var args)
            ? await ExecuteAsync(args, context, ct)
            : error;
    }

    /// <summary>
    /// Execute the tool as a stream of <see cref="ToolCallUpdate"/> items. The
    /// default yields the non-streaming result as a single terminal completion
    /// item. Long-running tools override this to emit live activity while they
    /// work; whether that activity affects parent liveness depends on
    /// <see cref="LivenessMode"/>.
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
        ToolInvocationContext context,
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
    /// Inline output budget; <c>0</c> = use the session content budget. First-party
    /// tools override this to opt into a smaller (verbose) budget.
    /// </summary>
    public virtual int InlineOutputBudgetChars => 0;

    /// <inheritdoc />
    public virtual ToolLivenessMode LivenessMode => ToolLivenessMode.Opaque;

    /// <inheritdoc />
    public virtual bool SuppressOutputRedaction => false;

    /// <summary>
    /// Deserialize raw LLM arguments into the typed params record.
    /// Implemented by the source generator to handle JsonElement and native CLR types.
    /// </summary>
    public abstract TParams ParseArguments(IDictionary<string, object?> arguments);
}
