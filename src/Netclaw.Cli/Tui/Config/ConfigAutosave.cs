// -----------------------------------------------------------------------
// <copyright file="ConfigAutosave.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using R3;

namespace Netclaw.Cli.Tui.Config;

internal static class ConfigAutosave
{
    internal static bool Run(
        Func<bool> save,
        ReactiveProperty<ConfigStatusMessage> status,
        string failurePrefix,
        Action requestRedraw)
    {
        try
        {
            return save();
        }
        catch (Exception ex)
        {
            status.Value = new ConfigStatusMessage($"{failurePrefix}: {ex.Message}", ConfigStatusTone.Error);
            requestRedraw();
            return false;
        }
    }

    internal static async Task<bool> RunAsync(
        Func<CancellationToken, Task<bool>> saveAsync,
        ReactiveProperty<ConfigStatusMessage> status,
        string failurePrefix,
        Action requestRedraw,
        CancellationToken ct = default)
    {
        try
        {
            return await saveAsync(ct);
        }
        catch (Exception ex)
        {
            status.Value = new ConfigStatusMessage($"{failurePrefix}: {ex.Message}", ConfigStatusTone.Error);
            requestRedraw();
            return false;
        }
    }
}
