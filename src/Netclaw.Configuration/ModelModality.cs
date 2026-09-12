// -----------------------------------------------------------------------
// <copyright file="ModelModality.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Content modalities that a model may accept as input or produce as output.
/// Combine with bitwise OR for models supporting multiple modalities.
/// </summary>
[Flags]
public enum ModelModality
{
    None = 0,
    Text = 1 << 0,
    Image = 1 << 1,
    Audio = 1 << 2,
    Video = 1 << 3,
}
