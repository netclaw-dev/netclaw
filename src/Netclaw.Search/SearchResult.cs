// -----------------------------------------------------------------------
// <copyright file="SearchResult.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Search;

/// <summary>
/// A single web search result returned by any search backend.
/// </summary>
public sealed record SearchResult(string Title, string Url, string Snippet);
