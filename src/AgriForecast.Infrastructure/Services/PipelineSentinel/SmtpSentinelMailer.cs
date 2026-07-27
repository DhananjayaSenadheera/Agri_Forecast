using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using AgriForecast.Application.Services;
using Microsoft.Extensions.Configuration;

namespace AgriForecast.Infrastructure.Services.PipelineSentinel;

// The only thing in the codebase that talks SMTP. Deliberately thin: build a message, hand it to
// System.Net.Mail.SmtpClient over STARTTLS, let anything that goes wrong throw to the caller (which
// swallows and logs). No new package — the sentinel is a one-message-a-night job and does not justify a
// MailKit dependency.
//
// SECRET: the password comes from Smtp:Password, which ships EMPTY in appsettings.json and is supplied
// at runtime by the Smtp__Password environment variable (k8s secret agri-smtp). It is stored in a field,
// used once per send, and never logged, never echoed, never put into an exception message or an email
// body. IsConfigured reports only that it is non-empty.
//
// GMAIL: Host smtp.gmail.com, port 587, EnableSsl = true (which for port 587 means STARTTLS, not
// implicit TLS). The account needs 2-Step Verification and an APP PASSWORD; a normal account password is
// rejected by Google. See k8s/README.md "Email alerts".
public class SmtpSentinelMailer : ISentinelMailer
{
    private const string HostKey = "Smtp:Host";
    private const string PortKey = "Smtp:Port";
    private const string UserKey = "Smtp:User";
    private const string FromKey = "Smtp:From";
    private const string ToKey = "Smtp:To";
    private const string PasswordKey = "Smtp:Password";

    private const string SendTimeoutKey = "Smtp:SendTimeoutSeconds";

    private const string DefaultHost = "smtp.gmail.com";
    private const int DefaultPort = 587;
    private const int DefaultSendTimeoutSeconds = 30;

    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _password;
    private readonly string _from;
    private readonly string _to;
    private readonly TimeSpan _sendTimeout;

    public bool IsConfigured { get; }

    public SmtpSentinelMailer(IConfiguration configuration)
    {
        var host = configuration[HostKey];
        _host = string.IsNullOrWhiteSpace(host) ? DefaultHost : host.Trim();

        _port = int.TryParse(configuration[PortKey], NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var port) && port > 0
            ? port
            : DefaultPort;

        _sendTimeout = TimeSpan.FromSeconds(
            int.TryParse(configuration[SendTimeoutKey], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var timeoutSeconds) && timeoutSeconds > 0
                ? timeoutSeconds
                : DefaultSendTimeoutSeconds);

        _user = (configuration[UserKey] ?? string.Empty).Trim();
        _password = configuration[PasswordKey] ?? string.Empty;

        // From defaults to the authenticating account, which is what Gmail requires anyway — it rewrites
        // (or rejects) a From that is not the authenticated identity or one of its verified aliases.
        var from = configuration[FromKey];
        _from = string.IsNullOrWhiteSpace(from) ? _user : from.Trim();

        // A comma-separated list is accepted by MailMessage, so the owner can add a second address
        // without a code change.
        _to = (configuration[ToKey] ?? string.Empty).Trim();

        IsConfigured =
            !string.IsNullOrWhiteSpace(_host) &&
            !string.IsNullOrWhiteSpace(_user) &&
            !string.IsNullOrWhiteSpace(_from) &&
            !string.IsNullOrWhiteSpace(_to) &&
            !string.IsNullOrEmpty(_password);
    }

    public async Task SendAsync(SentinelEmail email, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            // Defensive: the sentinel checks IsConfigured before it ever gets here. Names the config
            // keys, never the values.
            throw new InvalidOperationException(
                "SMTP is not configured. Smtp:Host, Smtp:User, Smtp:To and Smtp:Password must all be set " +
                "(Smtp__Password comes from the agri-smtp secret in k8s).");
        }

        using var client = new SmtpClient(_host, _port)
        {
            EnableSsl = true, // STARTTLS on 587
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(_user, _password),
            // Bounds the SYNCHRONOUS Send only. Documented .NET behaviour: SmtpClient.Timeout has no
            // effect on SendMailAsync, which is why the linked token below exists. Set anyway so a
            // future synchronous call site is not left unbounded.
            Timeout = (int)_sendTimeout.TotalMilliseconds
        };

        using var message = new MailMessage(_from, _to)
        {
            Subject = email.Subject,
            Body = email.Body,
            IsBodyHtml = false,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8
        };

        // THE DEADLINE THAT ACTUALLY BINDS. Without it a black-holed SMTP endpoint — one that accepts
        // the TCP connection and then never speaks — parks this await forever. The caller is a
        // once-a-night background loop, so "forever" would not just lose tonight's alert: the loop would
        // never reach its next wait and EVERY future night would silently go unchecked. That is a worse
        // outage than the one being alerted on, so the send gets a hard wall-clock bound.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_sendTimeout);

        try
        {
            await client.SendMailAsync(message, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our deadline fired, not the caller's shutdown. Rethrown as a TimeoutException so the log
            // says what happened; host and port only, never a credential.
            throw new TimeoutException(
                $"SMTP send to {_host}:{_port} did not complete within {_sendTimeout.TotalSeconds:0}s.");
        }
    }
}
