// -----------------------------------------------------------------------
// <copyright file="OllamaDescriptorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OllamaDescriptorTests
{
    [Fact]
    public async Task Probe_FiltersEmbeddingOnlyModels()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            models = new object[]
            {
                new { name = "all-minilm:latest", capabilities = new[] { "embedding" } },
                new { name = "qwen2:0.5b", capabilities = new[] { "completion" } },
                new { name = "future-chat:latest", capabilities = new[] { "chat" } },
                new { name = "legacy-metadata:latest" },
            },
        }));
        var descriptor = new OllamaDescriptor(new HttpClient(handler));

        var result = await descriptor.ProbeAsync(new ProviderEntry
        {
            Type = "ollama",
            Endpoint = "http://ollama.test",
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var ids = result.Models.Select(model => model.ModelId.Value).ToList();
        Assert.Equal(["qwen2:0.5b", "future-chat:latest", "legacy-metadata:latest"], ids);
    }
}
