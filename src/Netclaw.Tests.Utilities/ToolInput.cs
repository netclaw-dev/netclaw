// -----------------------------------------------------------------------
// <copyright file="ToolInput.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Tests.Utilities;

/// <summary>
/// Factory for tool execution argument dictionaries.
/// Replaces verbose <c>new Dictionary&lt;string, object?&gt; { ["Key"] = value }</c>
/// boilerplate in tool tests with a compact, readable call.
/// </summary>
internal static class ToolInput
{
    /// <summary>Creates an empty argument dictionary.</summary>
    public static Dictionary<string, object?> Empty() => new();

    /// <summary>Creates an argument dictionary with a single entry.</summary>
    public static Dictionary<string, object?> Create(string key, object? value) =>
        new() { [key] = value };

    /// <summary>Creates an argument dictionary with two entries.</summary>
    public static Dictionary<string, object?> Create(
        string key1, object? value1,
        string key2, object? value2) =>
        new() { [key1] = value1, [key2] = value2 };

    /// <summary>Creates an argument dictionary with three entries.</summary>
    public static Dictionary<string, object?> Create(
        string key1, object? value1,
        string key2, object? value2,
        string key3, object? value3) =>
        new() { [key1] = value1, [key2] = value2, [key3] = value3 };

    /// <summary>Creates an argument dictionary with four entries.</summary>
    public static Dictionary<string, object?> Create(
        string key1, object? value1,
        string key2, object? value2,
        string key3, object? value3,
        string key4, object? value4) =>
        new() { [key1] = value1, [key2] = value2, [key3] = value3, [key4] = value4 };

    /// <summary>Creates an argument dictionary with five entries.</summary>
    public static Dictionary<string, object?> Create(
        string key1, object? value1,
        string key2, object? value2,
        string key3, object? value3,
        string key4, object? value4,
        string key5, object? value5) =>
        new() { [key1] = value1, [key2] = value2, [key3] = value3, [key4] = value4, [key5] = value5 };
}
