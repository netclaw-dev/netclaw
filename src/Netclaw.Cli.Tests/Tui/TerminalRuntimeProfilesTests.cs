// -----------------------------------------------------------------------
// <copyright file="TerminalRuntimeProfilesTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class TerminalRuntimeProfilesTests
{
    [Fact]
    public void InlineChat_GivesScrollAndWheelInputToThePrimaryTerminal()
    {
        var options = new TerminaRuntimeOptions();

        TerminalRuntimeProfiles.ConfigureInlineChat(options);

        Assert.Equal(TerminalPresentationMode.Inline, options.PresentationMode);
        Assert.Equal(ScrollInputMode.NativeTerminal, options.ScrollInputMode);
        Assert.True(options.PreferRawInput);
        Assert.Equal(CtrlCHandlingMode.DoublePressWhenRawInput, options.CtrlCHandlingMode);
    }

    [Fact]
    public void SelectionApps_ExplicitlyRestoreFullScreenMode()
    {
        var options = new TerminaRuntimeOptions
        {
            PresentationMode = TerminalPresentationMode.Inline,
            ScrollInputMode = ScrollInputMode.NativeTerminal
        };

        TerminalRuntimeProfiles.ConfigureFullScreenSelection(options);

        Assert.Equal(TerminalPresentationMode.FullScreen, options.PresentationMode);
        Assert.Equal(ScrollInputMode.AlternateScroll, options.ScrollInputMode);
        Assert.True(options.PreferRawInput);
        Assert.Equal(CtrlCHandlingMode.DoublePressWhenRawInput, options.CtrlCHandlingMode);
    }
}
