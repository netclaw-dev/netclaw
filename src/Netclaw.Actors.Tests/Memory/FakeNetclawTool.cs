using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Tools;

namespace Netclaw.Actors.Tests.Memory;

internal sealed class FakeNetclawTool : INetclawTool
{
    private readonly string _result;

    public FakeNetclawTool(string name, string result, string grantCategory = "builtin")
    {
        Name = name;
        _result = result;
        GrantCategory = grantCategory;
    }

    public string Name { get; }
    public string Description => "Fake tool";
    public string GrantCategory { get; }
    public System.Text.Json.JsonElement ParameterSchema => default;

    public bool WasCalled { get; private set; }
    public IDictionary<string, object?>? LastArguments { get; private set; }

    public AITool ToAITool() => AIFunctionFactory.Create(() => _result, name: Name, description: Description);

    public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
    {
        WasCalled = true;
        LastArguments = arguments;
        return Task.FromResult(_result);
    }
}
