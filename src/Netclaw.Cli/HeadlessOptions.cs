// -----------------------------------------------------------------------
// <copyright file="HeadlessOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli;

/// <summary>
/// Configuration for headless (<c>chat -p</c>) mode.
/// </summary>
public sealed record HeadlessOptions(string Prompt)
{
    public string? ResumeSessionId { get; init; }
    public bool JsonOutput { get; init; }
}
