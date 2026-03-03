using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Cli.Secrets;

/// <summary>
/// Handles <c>netclaw secrets</c> CLI subcommands: encrypt, status.
/// No decrypt command exists by design — secrets are only decryptable
/// through internal authorized code paths (config binding), never
/// reversible to plaintext on disk.
/// </summary>
internal static class SecretsCommand
{
    public static int Run(string[] args, NetclawPaths paths, TextWriter? output = null)
    {
        var writer = output ?? Console.Out;
        var subcommand = args.Length > 1 ? args[1] : "help";

        return subcommand switch
        {
            "encrypt" => RunEncrypt(paths, writer),
            "status" => RunStatus(paths, writer),
            _ => RunHelp(writer),
        };
    }

    private static int RunEncrypt(NetclawPaths paths, TextWriter writer)
    {
        if (!File.Exists(paths.SecretsPath))
        {
            writer.WriteLine("No secrets.json found. Nothing to encrypt.");
            return 1;
        }

        var protector = SecretsProtection.CreateProtector(paths);
        var json = File.ReadAllText(paths.SecretsPath);

        var (alreadyEncrypted, plaintext) = SecretsFileWriter.CountEncryptionStatus(json);
        if (plaintext == 0)
        {
            writer.WriteLine($"All {alreadyEncrypted} secret value(s) are already encrypted. Nothing to do.");
            return 0;
        }

        SecretsFileWriter.Write(paths.SecretsPath, json, protector);

        writer.WriteLine($"Encrypted {plaintext} plaintext value(s) in-place ({alreadyEncrypted} were already encrypted).");
        return 0;
    }

    private static int RunStatus(NetclawPaths paths, TextWriter writer)
    {
        if (!File.Exists(paths.SecretsPath))
        {
            writer.WriteLine("No secrets.json found.");
            return 1;
        }

        var json = File.ReadAllText(paths.SecretsPath);
        var (encrypted, plaintext) = SecretsFileWriter.CountEncryptionStatus(json);

        writer.WriteLine($"Encrypted: {encrypted}");
        writer.WriteLine($"Plaintext: {plaintext}");

        if (plaintext > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Run `netclaw secrets encrypt` to encrypt plaintext values.");
        }

        return 0;
    }

    private static int RunHelp(TextWriter writer)
    {
        writer.WriteLine("Usage: netclaw secrets <subcommand>");
        writer.WriteLine();
        writer.WriteLine("Subcommands:");
        writer.WriteLine("  encrypt   Encrypt plaintext values in secrets.json in-place");
        writer.WriteLine("  status    Report encrypted vs plaintext value count");
        return 0;
    }
}
