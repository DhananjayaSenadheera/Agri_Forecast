namespace AgriForecast.Application.Services;

/// <summary>
/// One plain-text email from the pipeline sentinel. Deliberately not HTML: this message has to survive
/// any client, and the only thing that matters is that the owner can read what went wrong at a glance.
/// </summary>
public sealed record SentinelEmail(string Subject, string Body);

/// <summary>
/// The send seam for the nightly pipeline sentinel. Implemented in Infrastructure over
/// System.Net.Mail.SmtpClient; faked in tests so no suite can ever put a message on the wire.
/// </summary>
public interface ISentinelMailer
{
    /// <summary>
    /// True only when every field needed to send is present (host, port, user, from, to, password).
    /// The sentinel checks this ONCE at startup and idles when it is false: an API with no email
    /// configured must still boot and serve, it just cannot alert. Never throws, never touches the
    /// network, and never reveals which field was missing beyond its config key name.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends the message. Throws on any transport failure — the caller is expected to swallow and log,
    /// because an alerting bug must never take the API down. Implementations must never put the SMTP
    /// password into an exception message, a log line, or the email itself.
    /// </summary>
    Task SendAsync(SentinelEmail email, CancellationToken cancellationToken = default);
}
