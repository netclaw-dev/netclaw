// -----------------------------------------------------------------------
// <copyright file="FakeNetclawTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Tools;

namespace Netclaw.Actors.Tests.Memory;

internal sealed class FakeNetclawTool : INetclawTool
{
    private readonly string _result;
    private readonly Action<ToolInvocationContext>? _onExecute;

    public FakeNetclawTool(
        string name,
        string result,
        string grantCategory = "builtin",
        Action<ToolInvocationContext>? onExecute = null)
    {
        Name = name;
        LlmFacingName = LlmFacingToolName.FromCanonical(name);
        _result = result;
        GrantCategory = grantCategory;
        _onExecute = onExecute;
    }

    public string Name { get; }
    public LlmFacingToolName LlmFacingName { get; }
    public string Description => "Fake tool";
    public string GrantCategory { get; }
    public System.Text.Json.JsonElement ParameterSchema => default;

    public bool WasCalled { get; private set; }
    public IDictionary<string, object?>? LastArguments { get; private set; }
    public ToolInvocationContext? LastContext { get; private set; }

    public AITool ToAITool() => AIFunctionFactory.Create(() => _result, name: Name, description: Description);

    public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
    {
        WasCalled = true;
        LastArguments = arguments;
        return Task.FromResult(_result);
    }

    public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct = default)
    {
        LastContext = context;
        _onExecute?.Invoke(context);
        return ExecuteAsync(arguments, ct);
    }
}
