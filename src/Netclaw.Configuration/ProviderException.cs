namespace Netclaw.Configuration;

/// <summary>
/// Exception thrown by LLM provider transport layers when the provider returns
/// an error with actionable diagnostic information. The <see cref="UserMessage"/>
/// is safe to surface to end users; <see cref="Exception.Message"/> retains
/// full technical detail for logging.
/// </summary>
public sealed class ProviderException : Exception
{
    /// <summary>
    /// A concise, user-safe description of what went wrong.
    /// Suitable for displaying in Slack, TUI, or other user-facing channels.
    /// </summary>
    public string UserMessage { get; }

    /// <summary>
    /// HTTP status code from the provider, if applicable.
    /// </summary>
    public int? StatusCode { get; }

    public ProviderException(string userMessage, string technicalMessage, int? statusCode = null, Exception? innerException = null)
        : base(technicalMessage, innerException)
    {
        UserMessage = userMessage;
        StatusCode = statusCode;
    }
}
