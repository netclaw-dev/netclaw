// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using BenchmarkDotNet.Running;

// Usage:
//   dotnet run -c Release --project benchmarks/Netclaw.Benchmarks                # pick interactively
//   dotnet run -c Release --project benchmarks/Netclaw.Benchmarks -- --filter '*'  # run everything
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

internal sealed partial class Program;
