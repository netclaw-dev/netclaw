// -----------------------------------------------------------------------
// <copyright file="PipelinePolicyTestHarness.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

/// <summary>
/// Shared test utilities for exercising <see cref="PipelinePolicy"/>
/// implementations that mutate the outbound JSON request body.
/// Builds a <see cref="PipelineMessage"/> with the supplied body, runs the
/// policy synchronously against a single capture policy that records it was
/// reached, and deserializes the post-mutation body back to <see cref="JsonObject"/>.
/// </summary>
internal static class PipelinePolicyTestHarness
{
    /// <summary>
    /// Runs <paramref name="policy"/> against <paramref name="body"/> synchronously
    /// and returns the modified body, or null if the policy cleared the content.
    /// Asserts the policy invoked the downstream pipeline (i.e. did not swallow
    /// the request silently).
    /// </summary>
    public static JsonObject? RunSync(PipelinePolicy policy, JsonObject body)
    {
        var capture = new CapturePolicy();
        var message = CreateMessage(body);

        policy.Process(message, [policy, capture], 0);

        Assert.True(capture.WasCalled, "Policy must call ProcessNext");

        if (message.Request.Content is null)
            return null;

        using var stream = new MemoryStream();
        message.Request.Content.WriteTo(stream, default);
        return JsonSerializer.Deserialize<JsonObject>(stream.ToArray());
    }

    /// <summary>
    /// Creates a <see cref="PipelineMessage"/> with the supplied JSON body as
    /// the request content. Pass <c>null</c> to exercise the no-content path
    /// every body-editing policy should short-circuit on.
    /// </summary>
    public static PipelineMessage CreateMessage(JsonObject? body)
    {
        var pipeline = ClientPipeline.Create();
        var message = pipeline.CreateMessage();

        if (body is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
            message.Request.Content = BinaryContent.Create(BinaryData.FromBytes(bytes));
        }

        return message;
    }

    /// <summary>
    /// Terminal pipeline policy that records whether the upstream policy
    /// invoked the next step. Use it as the tail of a two-element pipeline.
    /// </summary>
    public sealed class CapturePolicy : PipelinePolicy
    {
        public bool WasCalled { get; private set; }

        public override void Process(
            PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            WasCalled = true;
        }

        public override ValueTask ProcessAsync(
            PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            WasCalled = true;
            return default;
        }
    }
}
