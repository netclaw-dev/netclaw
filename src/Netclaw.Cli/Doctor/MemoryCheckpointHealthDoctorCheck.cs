// -----------------------------------------------------------------------
// <copyright file="MemoryCheckpointHealthDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class MemoryCheckpointHealthDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    private const string CheckName = "Memory Checkpoint Health";

    public async Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!UsesSqliteMemory(paths))
        {
            return DoctorCheckResult.Pass(
                CheckName,
                "Memory provider is not SQLite.");
        }

        try
        {
            var store = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
            await store.InitializeAsync(cancellationToken);
            var pending = await store.GetPendingCheckpointCountAsync(cancellationToken);

            if (pending <= 25)
            {
                return DoctorCheckResult.Pass(
                    CheckName,
                    $"SQLite memory healthy ({pending} pending checkpoints).");
            }

            return DoctorCheckResult.Warning(
                CheckName,
                $"SQLite memory has {pending} pending checkpoints.",
                "Inspect memory queue health with `netclaw status` and daemon logs under ~/.netclaw/logs.");
        }
        catch (Exception ex)
        {
            return DoctorCheckResult.Error(
                CheckName,
                $"Unable to inspect SQLite memory health: {ex.Message}",
                "Run `netclaw doctor`, verify ~/.netclaw/memory permissions, and restart the daemon.");
        }
    }

    // SQLite is the only memory backend — always run the check.
    private static bool UsesSqliteMemory(NetclawPaths paths) => true;
}
