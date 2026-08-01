// -----------------------------------------------------------------------
// <copyright file="ImageProxyAnalysis.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

public sealed record ImageProxyAnalysis
{
    public string RelativePath { get; init; } = string.Empty;

    public string DefinitionName { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string PromptVersion { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public long AnalyzedAtMs { get; init; }
}

public interface IImageProxyAnalyzer
{
    bool IsEnabled { get; }

    Task<ImageProxyAnalysis> AnalyzeAsync(
        SessionId sessionId,
        SerializableMediaReference media,
        string sessionsBasePath,
        CancellationToken cancellationToken);
}

public sealed class DisabledImageProxyAnalyzer : IImageProxyAnalyzer
{
    public static DisabledImageProxyAnalyzer Instance { get; } = new();

    private DisabledImageProxyAnalyzer()
    {
    }

    public bool IsEnabled => false;

    public Task<ImageProxyAnalysis> AnalyzeAsync(
        SessionId sessionId,
        SerializableMediaReference media,
        string sessionsBasePath,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The image proxy is not configured.");
}
