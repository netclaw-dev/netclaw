using Microsoft.Extensions.Options;

namespace Netclaw.Daemon.Configuration;

public sealed class DaemonPersistenceOptionsValidator : IValidateOptions<DaemonPersistenceOptions>
{
    public ValidateOptionsResult Validate(string? name, DaemonPersistenceOptions options)
    {
        if (options.Provider == PersistenceProvider.Sqlite
            && options.Sqlite.Path is not null
            && string.IsNullOrWhiteSpace(options.Sqlite.Path))
        {
            return ValidateOptionsResult.Fail(
                "Persistence:Sqlite:Path cannot be empty when provided.");
        }

        return ValidateOptionsResult.Success;
    }
}
