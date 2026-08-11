// -----------------------------------------------------------------------
// <copyright file="DoctorCommandOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Doctor;

public enum DoctorOutputFormat
{
    Text,
    Json
}

public sealed record DoctorCommandOptions(
    DoctorOutputFormat Format,
    bool Fix,
    bool DryRun,
    bool Yes)
{
    public static DoctorCommandOptions Parse(string[] args)
    {
        var format = DoctorOutputFormat.Text;
        var fix = false;
        var dryRun = false;
        var yes = false;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--fix":
                    fix = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--yes":
                case "-y":
                    yes = true;
                    break;
                case "--format":
                    if (i + 1 >= args.Length)
                        throw new InvalidOperationException("Missing value after --format. Expected text or json.");
                    i++;
                    format = ParseFormat(args[i]);
                    break;
                default:
                    if (arg.StartsWith("--format=", StringComparison.Ordinal))
                    {
                        var value = arg.Substring("--format=".Length);
                        format = ParseFormat(value);
                        break;
                    }

                    throw new InvalidOperationException($"Unknown doctor option: {arg}");
            }
        }

        if (dryRun)
            fix = true;

        return new DoctorCommandOptions(format, fix, dryRun, yes);
    }

    private static DoctorOutputFormat ParseFormat(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "text" => DoctorOutputFormat.Text,
            "json" => DoctorOutputFormat.Json,
            _ => throw new InvalidOperationException($"Unsupported format '{value}'. Expected text or json.")
        };
    }
}
