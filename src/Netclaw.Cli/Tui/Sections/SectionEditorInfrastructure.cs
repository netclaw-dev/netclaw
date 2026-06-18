// -----------------------------------------------------------------------
// <copyright file="SectionEditorInfrastructure.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Sections;

/// <summary>
/// Reusable leaf editor contract shared by init-owned flows and future config surfaces.
/// The registry is intentionally flat at the leaf level and does not define dashboard IA.
/// </summary>
public interface ISectionEditor
{
    string SectionId { get; }
    string DisplayName { get; }
    string? Category { get; }
    bool ShowInMenu { get; }
    SectionStatus GetStatus(WizardContext context);
    string Summary(WizardContext context);
    IReadOnlyList<string> RelevantDoctorChecks { get; }
    IWizardStepViewModel CreateEditor(IServiceProvider services);
    SectionContribution BuildContribution(IWizardStepViewModel editor);
}

public enum SectionStatus
{
    NotConfigured,
    Configured,
    NeedsAttention,
}

/// <summary>
/// Path-based merge instructions for one leaf editor.
/// Config and secret paths use dot-separated segments rooted at the top-level file object.
/// </summary>
public sealed record SectionContribution(
    IReadOnlyList<SectionFieldAction>? FieldActions = null,
    IReadOnlyList<SectionSecretAction>? SecretActions = null,
    IReadOnlyList<SectionEditorStateAction>? StateActions = null)
{
    public static readonly SectionContribution Empty = new([], [], []);

    public IReadOnlyList<SectionFieldAction> FieldActionsOrEmpty => FieldActions ?? [];
    public IReadOnlyList<SectionSecretAction> SecretActionsOrEmpty => SecretActions ?? [];
    public IReadOnlyList<SectionEditorStateAction> StateActionsOrEmpty => StateActions ?? [];
}

public sealed record SectionFieldAction(string Path, SectionFieldActionKind Action, object? Value = null);

public sealed record SectionSecretAction
{
    public SectionSecretAction(string path, SectionSecretActionKind action, SensitiveString? value = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (action == SectionSecretActionKind.Set && value is null)
            throw new ArgumentNullException(nameof(value), "Secret set actions require a SensitiveString value.");

        if (action != SectionSecretActionKind.Set && value is not null)
            throw new ArgumentException("Only secret set actions may carry a value.", nameof(value));

        Path = path;
        Action = action;
        Value = value;
    }

    public string Path { get; }
    public SectionSecretActionKind Action { get; }
    public SensitiveString? Value { get; }
}

public sealed record SectionEditorStateAction(
    string SectionId,
    string Key,
    SectionEditorStateActionKind Action,
    object? Value = null);

public enum SectionFieldActionKind
{
    Set,
    Delete,
}

public enum SectionSecretActionKind
{
    Preserve,
    Set,
    Delete,
}

public enum SectionEditorStateActionKind
{
    Set,
    Delete,
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NoDoctorChecksAttribute(string justification) : Attribute
{
    public string Justification { get; } = justification;
}

/// <summary>
/// Documents synthetic or init-owned surfaces that intentionally do not behave like config-menu entries.
/// Future routed handoff entries belong to the config command change and are audited separately.
/// </summary>
public static class SectionEditorExemptions
{
    public static readonly IReadOnlySet<string> ConfigSmokeExemptions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "provider",
            "identity"
        };
}

public sealed record SectionEditorRegistration(Type ImplementationType);

/// <summary>
/// Registry of reusable leaf editors. It validates duplicate IDs eagerly and does not imply any future menu hierarchy.
/// </summary>
public sealed class SectionEditorRegistry : IDisposable
{
    private readonly List<ISectionEditor> _editors;

    public SectionEditorRegistry(IServiceProvider services, IEnumerable<SectionEditorRegistration> registrations)
    {
        _editors = [];
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var registration in registrations)
        {
            var editor = (ISectionEditor)ActivatorUtilities.CreateInstance(services, registration.ImplementationType);
            if (!ids.Add(editor.SectionId))
            {
                throw new InvalidOperationException(
                    $"Duplicate section editor ID '{editor.SectionId}'. Leaf editor IDs must be unique.");
            }

            _editors.Add(editor);
        }
    }

    public IReadOnlyList<ISectionEditor> Editors => _editors;

    public ISectionEditor Get(string sectionId)
        => _editors.FirstOrDefault(e => string.Equals(e.SectionId, sectionId, StringComparison.Ordinal))
           ?? throw new InvalidOperationException($"Unknown section editor '{sectionId}'.");

    public void Dispose()
    {
        foreach (var editor in _editors.OfType<IDisposable>())
            editor.Dispose();
    }
}

public static class SectionEditorServiceCollectionExtensions
{
    public static IServiceCollection AddSectionEditor<TEditor>(this IServiceCollection services)
        where TEditor : class, ISectionEditor
    {
        services.AddTransient<TEditor>();
        services.AddSingleton(new SectionEditorRegistration(typeof(TEditor)));
        services.AddSingleton<SectionEditorRegistry>();
        return services;
    }
}

internal static class SectionEditorAudit
{
    public static string? GetDoctorCheckJustification(ISectionEditor editor)
        => editor.GetType().GetCustomAttributes(typeof(NoDoctorChecksAttribute), inherit: false)
            .OfType<NoDoctorChecksAttribute>()
            .FirstOrDefault()
            ?.Justification;

    public static bool HasExistingConfig(WizardContext context, string path)
        => context.ExistingConfig is not null && ConfigFileHelper.PathPresent(context.ExistingConfig, path);
}
