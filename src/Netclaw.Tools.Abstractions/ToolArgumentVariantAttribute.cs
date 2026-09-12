// -----------------------------------------------------------------------
// <copyright file="ToolArgumentVariantAttribute.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

/// <summary>
/// Declares one closed argument branch for a generated first-party tool.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
internal sealed class ToolArgumentVariantAttribute : Attribute
{
    public ToolArgumentVariantAttribute(string discriminatorParameter, string discriminatorValue)
    {
        DiscriminatorParameter = discriminatorParameter;
        DiscriminatorValue = discriminatorValue;
    }

    public string DiscriminatorParameter { get; }
    public string DiscriminatorValue { get; }
    public string[] Required { get; set; } = [];
    public string[] Forbidden { get; set; } = [];
}
