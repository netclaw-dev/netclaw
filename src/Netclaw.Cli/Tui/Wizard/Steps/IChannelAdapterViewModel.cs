namespace Netclaw.Cli.Tui.Wizard.Steps;

internal interface IChannelAdapterViewModel
{
    bool AdapterEnabled { get; set; }
    bool AllowDirectMessages { get; }
    int ConfiguredChannelCount { get; }
    void ResetConfig();
}
