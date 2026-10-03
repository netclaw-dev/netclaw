// -----------------------------------------------------------------------
// <copyright file="TerminalRuntimeProfiles.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;

namespace Netclaw.Cli.Tui;

internal static class TerminalRuntimeProfiles
{
    public static void ConfigureFullScreenSelection(TerminaRuntimeOptions options)
    {
        options.PresentationMode = TerminalPresentationMode.FullScreen;
        options.PreferRawInput = true;
        options.ScrollInputMode = ScrollInputMode.AlternateScroll;
        options.CtrlCHandlingMode = CtrlCHandlingMode.DoublePressWhenRawInput;
    }

    public static void ConfigureInlineChat(TerminaRuntimeOptions options)
    {
        options.PresentationMode = TerminalPresentationMode.Inline;
        options.PreferRawInput = true;
        options.ScrollInputMode = ScrollInputMode.NativeTerminal;
        options.CtrlCHandlingMode = CtrlCHandlingMode.DoublePressWhenRawInput;
    }
}
