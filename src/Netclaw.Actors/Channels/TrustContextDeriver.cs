// -----------------------------------------------------------------------
// <copyright file="TrustContextDeriver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Optional working-context downgrade applied while the bot is handling riskier content.
/// </summary>
public sealed record WorkingContextOverride(
    TrustAudience? Audience = null,
    string? Reason = null);

/// <summary>
/// Runtime trust-context view derived from deployment defaults, source metadata,
/// and active working-context downgrades.
/// </summary>
public sealed record EffectiveTrustContext(
    DeploymentPosture DeploymentPosture,
    TrustAudience DeploymentAudience,
    TrustAudience SourceAudience,
    TrustAudience EffectiveAudience,
    TrustBoundary Boundary,
    PrincipalClassification Principal,
    TransportAuthenticity TransportAuthenticity,
    PayloadTaint PayloadTaint,
    SourceScope? SourceScope,
    SourceKind? SourceKind,
    bool UsedStrictFallback,
    bool WasDowngraded,
    string? DowngradeReason);

public sealed class TrustContextDeriver
{
    private readonly EffectivePolicyDefaults _defaults;

    public TrustContextDeriver(EffectivePolicyDefaults defaults)
    {
        _defaults = defaults;
    }

    public EffectiveTrustContext Derive(MessageSource? source, WorkingContextOverride? workingContext = null)
    {
        var sourceAudience = source?.Audience ?? _defaults.Audience;
        var boundary = source?.Boundary ?? SecurityPolicyDefaults.ResolveBoundaryFromAudience(sourceAudience);
        var principal = source?.Principal ?? PrincipalClassification.UntrustedExternal;
        // Fail-closed conservative provenance when the turn has no source at all
        // (Unverified transport, Public taint) — the most restrictive markers.
        var provenance = source?.Provenance
            ?? new SourceProvenance(TransportAuthenticity.Unverified, PayloadTaint.Public);

        var effectiveAudience = Narrowest(_defaults.Audience, sourceAudience);
        var downgradeReason = (string?)null;
        var wasDowngraded = effectiveAudience != sourceAudience || effectiveAudience != _defaults.Audience;

        if (workingContext?.Audience is { } workingAudience)
        {
            var narrowed = Narrowest(effectiveAudience, workingAudience);
            if (narrowed != effectiveAudience)
            {
                effectiveAudience = narrowed;
                wasDowngraded = true;
                downgradeReason = workingContext.Reason;
            }
        }

        return new EffectiveTrustContext(
            _defaults.DeploymentPosture,
            _defaults.Audience,
            sourceAudience,
            effectiveAudience,
            boundary,
            principal,
            provenance.TransportAuthenticity,
            provenance.PayloadTaint,
            provenance.SourceScope,
            provenance.SourceKind,
            _defaults.UsedStrictFallback,
            wasDowngraded,
            downgradeReason);
    }

    public EffectiveTrustContext DeriveFromTurnContext(TurnContext context, WorkingContextOverride? workingContext = null)
    {
        var effectiveAudience = Narrowest(_defaults.Audience, context.Audience);
        var downgradeReason = (string?)null;
        var wasDowngraded = effectiveAudience != context.Audience || effectiveAudience != _defaults.Audience;

        if (workingContext?.Audience is { } workingAudience)
        {
            var narrowed = Narrowest(effectiveAudience, workingAudience);
            if (narrowed != effectiveAudience)
            {
                effectiveAudience = narrowed;
                wasDowngraded = true;
                downgradeReason = workingContext.Reason;
            }
        }

        return new EffectiveTrustContext(
            _defaults.DeploymentPosture,
            _defaults.Audience,
            context.Audience,
            effectiveAudience,
            context.Boundary,
            context.RequesterPrincipal,
            context.Provenance.TransportAuthenticity,
            context.Provenance.PayloadTaint,
            context.Provenance.SourceScope,
            context.Provenance.SourceKind,
            _defaults.UsedStrictFallback,
            wasDowngraded,
            downgradeReason);
    }

    private static TrustAudience Narrowest(TrustAudience left, TrustAudience right)
        => left < right ? left : right;
}
