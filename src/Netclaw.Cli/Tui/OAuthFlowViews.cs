// -----------------------------------------------------------------------
// <copyright file="OAuthFlowViews.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using R3;
using Termina.Clipboard;
using Termina.Layout;
using Termina.Terminal;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Shared UI builders for OAuth flows used by both InitWizardPage and ProviderManagerPage.
/// </summary>
internal static class OAuthFlowViews
{
    private static readonly string[] SpinnerFrames = ["\u280b", "\u2819", "\u2838", "\u2834", "\u2826", "\u2807"];

    /// <summary>
    /// Map auth methods to user-friendly display labels for selection lists.
    /// Uses custom per-provider labels from <see cref="MultiAuth.AuthMethodLabels"/> when available.
    /// </summary>
    public static List<string> BuildAuthMethodLabels(IProviderAuth auth)
    {
        var customLabels = (auth as MultiAuth)?.AuthMethodLabels;
        return auth.SupportedAuthMethods
            .Where(m => m != AuthMethod.None)
            .Select(m => customLabels?.TryGetValue(m, out var label) == true
                ? label
                : m switch
                {
                    AuthMethod.ApiKey => "API Key",
                    AuthMethod.OAuthPkce => "OAuth Login (recommended)",
                    AuthMethod.OAuthDevice => "OAuth Device Flow",
                    _ => m.ToString()
                })
            .ToList();
    }

    /// <summary>
    /// Parse a selected label back to an AuthMethod.
    /// Checks custom labels from <see cref="MultiAuth"/> before falling back to generic labels.
    /// </summary>
    public static AuthMethod ParseAuthMethodLabel(string label, IProviderAuth? auth = null)
    {
        // Check custom labels first
        if (auth is MultiAuth { AuthMethodLabels: { } labels })
        {
            foreach (var (method, methodLabel) in labels)
            {
                if (methodLabel == label) return method;
            }
        }

        return label switch
        {
            "API Key" => AuthMethod.ApiKey,
            "OAuth Login (recommended)" => AuthMethod.OAuthPkce,
            "OAuth Device Flow" => AuthMethod.OAuthDevice,
            _ => AuthMethod.ApiKey
        };
    }

    /// <summary>
    /// Get a spinner frame for the given tick count.
    /// Use a fast tick counter (not elapsed seconds) for smooth animation.
    /// </summary>
    public static string GetSpinnerFrame(int tick) => SpinnerFrames[tick % SpinnerFrames.Length];

    /// <summary>
    /// Copy a URL to clipboard via the provided service. Returns true if copied.
    /// </summary>
    public static bool TryCopyToClipboard(IClipboardService? clipboardService, string? url)
    {
        if (clipboardService is null || string.IsNullOrEmpty(url))
            return false;

        clipboardService.Copy(url);
        return true;
    }

    /// <summary>
    /// Build the browser OAuth flow view with three fallback layers.
    /// </summary>
    public static ILayoutNode BuildBrowserOAuthFlow(
        string providerType,
        DeviceFlowState flowState,
        bool browserOpenFailed,
        string? verificationUri,
        int spinnerTick,
        int elapsedSeconds,
        string? errorMessage,
        IClipboardService? clipboardService,
        ref TextInputNode? redirectUrlInput,
        Action<string> onRedirectUrlSubmitted)
    {
        var children = Layouts.Vertical();

        children.WithChild(new TextNode($"  OAuth Login for {providerType}")
            .WithForeground(Color.White).Bold());
        children.WithChild(new TextNode("").Height(1));

        switch (flowState)
        {
            case DeviceFlowState.NotStarted:
                children.WithChild(new TextNode("  Starting authorization...")
                    .WithForeground(Color.Yellow));
                break;

            case DeviceFlowState.WaitingForUser:
            case DeviceFlowState.Polling:
            {
                var frame = GetSpinnerFrame(spinnerTick);

                if (!browserOpenFailed)
                {
                    children.WithChild(new TextNode($"  {frame} Opening browser for authorization...")
                        .WithForeground(Color.Yellow));
                    children.WithChild(new TextNode("").Height(1));
                    children.WithChild(new TextNode($"  Waiting for callback...  ({elapsedSeconds}s)")
                        .WithForeground(Color.BrightBlack));
                }
                else
                {
                    children.WithChild(new TextNode("  Could not open browser automatically.")
                        .WithForeground(Color.Red));
                    children.WithChild(new TextNode("").Height(1));
                    children.WithChild(new TextNode("  Open this URL to authorize:")
                        .WithForeground(Color.White));

                    if (verificationUri is not null)
                    {
                        children.WithChild(new TextNode($"  {verificationUri}")
                            .WithForeground(Color.Cyan));

                        if (clipboardService is not null)
                        {
                            children.WithChild(new TextNode("  Press [C] to copy URL to clipboard")
                                .WithForeground(Color.BrightBlack));
                        }
                    }

                    children.WithChild(new TextNode("").Height(1));
                    children.WithChild(new TextNode($"  {frame} Waiting for callback...  ({elapsedSeconds}s)")
                        .WithForeground(Color.Yellow));
                }

                children.WithChild(new TextNode("").Height(1));
                children.WithChild(new TextNode("  Can't receive the callback? Paste the redirect URL:")
                    .WithForeground(Color.BrightBlack));

                if (redirectUrlInput is null)
                {
                    redirectUrlInput = new TextInputNode()
                        .WithPlaceholder("Paste redirect URL here...");
                    redirectUrlInput.Submitted
                        .Subscribe(text => onRedirectUrlSubmitted(text));
                }

                children.WithChild(new PanelNode()
                    .WithBorderColor(Color.Gray)
                    .WithContent(redirectUrlInput)
                    .Height(3));

                break;
            }

            case DeviceFlowState.Succeeded:
                children.WithChild(new TextNode("  \u2714 Authorization successful!")
                    .WithForeground(Color.Green));
                break;

            case DeviceFlowState.Denied:
            case DeviceFlowState.Expired:
            case DeviceFlowState.Error:
                children.WithChild(new TextNode($"  \u2718 {errorMessage ?? "Authorization failed."}")
                    .WithForeground(Color.Red));
                children.WithChild(new TextNode("").Height(1));
                children.WithChild(new TextNode("  Press [Esc] to go back and try again.")
                    .WithForeground(Color.BrightBlack));
                break;

            case DeviceFlowState.Cancelled:
                children.WithChild(new TextNode("  Authorization cancelled.")
                    .WithForeground(Color.Yellow));
                break;
        }

        return children;
    }
}
