using Netclaw.Configuration;

namespace Netclaw.Channels;

public readonly record struct AudienceResult(TrustAudience Audience, string? Error)
{
    public AudienceResult(TrustAudience audience) : this(audience, null) { }
    public AudienceResult(string error) : this(default, error) { }
}
