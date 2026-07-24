// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Netclaw.SecretsLockProbe <secretsPath> <childValue>");
    return 2;
}

try
{
    var committed = SecretsFileWriter.Update<bool>(
        args[0],
        (root, _) =>
        {
            Console.Out.WriteLine("entered");
            Console.Out.Flush();

            var command = Console.In.ReadLine();
            if (!string.Equals(command, "release", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected command '{command}'.");

            root["Child"] = args[1];
            return (root, true);
        });

    Console.Out.WriteLine(committed ? "committed" : "not-committed");
    return committed ? 0 : 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
