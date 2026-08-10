// -----------------------------------------------------------------------
// <copyright file="IChannelAdapterViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Tui.Wizard.Steps;

internal interface IChannelAdapterViewModel
{
    bool AdapterEnabled { get; set; }
    bool AllowDirectMessages { get; }
    int ConfiguredChannelCount { get; }
    void ResetConfig();
}
