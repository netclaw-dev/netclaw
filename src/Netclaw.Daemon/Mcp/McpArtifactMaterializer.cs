// -----------------------------------------------------------------------
// <copyright file="McpArtifactMaterializer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Admits MCP result artifacts into session storage and existing tool outputs.
/// </summary>
internal sealed class McpArtifactMaterializer(
    IContentScanner contentScanner,
    ILogger<McpArtifactMaterializer> logger)
{
    private readonly IContentScanner _contentScanner = contentScanner;
    private readonly ILogger<McpArtifactMaterializer> _logger = logger;

    internal async Task<IReadOnlyList<string>> MaterializeAsync(
        IReadOnlyList<McpResultArtifact> artifacts,
        string toolName,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (artifacts.Count == 0)
            return [];

        if (context.SessionStorage is not { } storage)
        {
            return ["[MCP artifacts rejected: this tool call has no session storage.]"];
        }

        var notes = new List<string>();
        var accepted = new List<AcceptedArtifact>();
        var createdFiles = new List<string>();

        try
        {
            for (var index = 0; index < artifacts.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = artifacts[index];
                var ordinal = index + 1;

                if (candidate.Data.IsEmpty)
                {
                    notes.Add($"[MCP artifact {ordinal} rejected: the artifact was empty.]");
                    continue;
                }

                var scanName = BuildCandidateName(candidate, ordinal);
                ContentScanResult scan;
                try
                {
                    scan = await _contentScanner.ScanAsync(
                        candidate.Data,
                        scanName,
                        candidate.DeclaredMimeType.Value,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "The content scanner failed for MCP artifact {Ordinal} from tool '{Tool}'.",
                        ordinal,
                        toolName);
                    notes.Add($"[MCP artifact {ordinal} rejected: the content scan failed.]");
                    continue;
                }

                if (!TryAdmit(scan, out var verifiedMimeType, out var definition))
                {
                    _logger.LogWarning(
                        "MCP artifact {Ordinal} from tool '{Tool}' failed content admission with {Error}.",
                        ordinal,
                        toolName,
                        scan.Error);
                    notes.Add($"[MCP artifact {ordinal} rejected: content validation failed.]");
                    continue;
                }

                var extension = MimeTypeCatalog.ExtensionFor(verifiedMimeType.MimeType);
                var displayName = BuildDisplayName(candidate, ordinal, extension);
                var filePath = Path.Combine(
                    storage.ArtifactDirectory.Value,
                    $"mcp-{Guid.NewGuid():N}{extension}");
                var ownsFile = false;

                try
                {
                    Directory.CreateDirectory(storage.ArtifactDirectory.Value);
                    await using var stream = new FileStream(
                        filePath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        FileOptions.Asynchronous);
                    ownsFile = true;
                    await stream.WriteAsync(candidate.Data, cancellationToken);
                    createdFiles.Add(filePath);
                }
                catch (OperationCanceledException)
                {
                    if (ownsFile)
                        DeleteIfPresent(filePath);
                    throw;
                }
                catch (Exception exception)
                {
                    if (ownsFile)
                        DeleteIfPresent(filePath);
                    _logger.LogWarning(
                        exception,
                        "Netclaw could not store MCP artifact {Ordinal} from tool '{Tool}'.",
                        ordinal,
                        toolName);
                    notes.Add($"[MCP artifact {ordinal} rejected: Netclaw could not store the artifact.]");
                    continue;
                }

                var inlineDecision = AttachmentInlineDecision.Resolve(
                    verifiedMimeType.MimeType,
                    definition.Category,
                    context.ModelInputModalities.HasFlag(ModelModality.Image));
                accepted.Add(new AcceptedArtifact(
                    filePath,
                    displayName,
                    verifiedMimeType.MimeType,
                    inlineDecision.Inlined,
                    inlineDecision.Note));
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var artifact in accepted)
            {
                context.Outputs.AddFileAttachment(artifact.Path, artifact.DisplayName, artifact.MimeType);
                if (artifact.InlineForModel)
                {
                    context.Outputs.AddModelInputFile(artifact.Path, artifact.DisplayName, artifact.MimeType);
                }
                else if (artifact.ModalityNote is { } modalityNote)
                {
                    notes.Add($"[MCP artifact delivered: {artifact.DisplayName}; {modalityNote}.]");
                }
            }

            return notes.AsReadOnly();
        }
        catch (OperationCanceledException)
        {
            foreach (var filePath in createdFiles)
                DeleteIfPresent(filePath);
            throw;
        }
    }

    private static string BuildCandidateName(McpResultArtifact artifact, int ordinal)
    {
        if (!string.IsNullOrWhiteSpace(artifact.Name))
        {
            var sanitized = FilenameSanitizer.Sanitize(artifact.Name);
            if (MimeTypeCatalog.FromPathExtension(sanitized) is not null)
                return sanitized;
        }

        var extension = MimeTypeCatalog.ExtensionFor(artifact.DeclaredMimeType.Value);
        return BuildDisplayName(artifact, ordinal, extension);
    }

    internal static bool TryAdmit(
        ContentScanResult scan,
        out VerifiedMimeType verifiedMimeType,
        out MediaTypeDefinition definition)
    {
        verifiedMimeType = default;
        definition = null!;
        if (!scan.IsAllowed)
            return false;
        if (!scan.VerifiedMimeType.HasValue)
            return false;

        var candidate = scan.VerifiedMimeType.Value;

        if (!MimeTypeCatalog.TryGet(candidate.MimeType, out definition)
            || !definition.SupportsNativeSignatureValidation)
        {
            return false;
        }

        verifiedMimeType = candidate;
        return true;
    }

    private static string BuildDisplayName(McpResultArtifact artifact, int ordinal, string extension)
    {
        var sanitized = FilenameSanitizer.Sanitize(artifact.Name ?? $"mcp-artifact-{ordinal}");
        var stem = Path.GetFileNameWithoutExtension(sanitized);
        if (string.IsNullOrWhiteSpace(stem))
            stem = $"mcp-artifact-{ordinal}";
        return FilenameSanitizer.Sanitize(stem + extension);
    }

    private void DeleteIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception)
        {
            // Cleanup is best effort because cancellation must remain the caller-visible result.
            _logger.LogWarning(exception, "Netclaw could not remove an incomplete MCP artifact file.");
        }
    }

    private sealed record AcceptedArtifact(
        string Path,
        string DisplayName,
        MimeType MimeType,
        bool InlineForModel,
        string? ModalityNote);
}
