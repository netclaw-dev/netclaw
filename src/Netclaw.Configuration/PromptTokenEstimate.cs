// -----------------------------------------------------------------------
// <copyright file="PromptTokenEstimate.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// The one prompt-size rule that session compaction and the default output limit share: total
/// characters / 4, with media counted at its base64 size. It is an estimate, so each caller keeps a
/// margin.
/// </summary>
public static class PromptTokenEstimate
{
    public const int CharactersPerToken = 4;

    public static int FromCharacters(long characters) => (int)(characters / CharactersPerToken);

    /// <summary>The characters that a media payload of <paramref name="bytes"/> adds after base64 encoding.</summary>
    public static long MediaCharacters(long bytes) => bytes * 4 / 3;
}
