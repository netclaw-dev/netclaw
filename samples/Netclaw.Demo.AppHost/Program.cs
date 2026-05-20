// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
var builder = DistributedApplication.CreateBuilder(args);

// .demo-home lives next to this AppHost project. We sandbox the daemon's
// state under it via NETCLAW_HOME so a host-installed NetClaw at
// ~/.netclaw/ keeps its own SQLite, keys, secrets, and identity files
// untouched. The 8 other SpecialFolder.UserProfile callsites in NetClaw
// intentionally read the real operator home and are not redirected by
// NETCLAW_HOME — that asymmetry is correct, not a bug.
var demoHome = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, ".demo-home", ".netclaw"));

builder.AddProject<Projects.Netclaw_Daemon>("daemon")
    .WithEnvironment("NETCLAW_HOME", demoHome)
    .WithEnvironment("NETCLAW_Daemon__Host", "127.0.0.1")
    .WithEnvironment("NETCLAW_Daemon__Port", "5299")
    .WithEnvironment("NETCLAW_Daemon__ExposureMode", "local");

builder.Build().Run();
