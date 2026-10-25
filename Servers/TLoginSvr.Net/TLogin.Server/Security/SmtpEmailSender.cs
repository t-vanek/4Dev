using System.Security.Authentication;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace TLogin.Server.Security;

public sealed class SmtpEmailSender : IEmailSender, IDisposable
{
    private readonly SmtpOptions _options;
    private readonly Func<SmtpClient> _clientFactory;
    private readonly SemaphoreSlim _admission = new(4, 4);

    public SmtpEmailSender(SmtpOptions options, Func<SmtpClient>? clientFactory = null)
    {
        _options = options;
        _clientFactory = clientFactory ?? (() => new SmtpClient());
        if (options.Port is < 1 or > 65535 || options.TimeoutSeconds is < 1 or > 120
            || options.Encryption is not (SmtpEncryption.StartTls or SmtpEncryption.SslOnConnect))
            throw new InvalidOperationException("SMTP requires an explicit TLS mode and valid port/timeout.");
    }

    public async Task SendSecurityCodeAsync(string email, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Host) || !TryAddress(_options.From, out var from)
            || !TryAddress(email, out var recipient)
            || (string.IsNullOrEmpty(_options.UserName) != string.IsNullOrEmpty(_options.Password)))
            throw new InvalidOperationException("SMTP delivery is not configured correctly.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        await _admission.WaitAsync(deadline.Token);
        try
        {
            using var client = _clientFactory();
            client.Timeout = _options.TimeoutSeconds * 1000;
            client.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            using var message = new MimeMessage();
            message.From.Add(from!);
            message.To.Add(recipient!);
            message.Subject = "4Story sign-in verification";
            message.Body = new TextPart("plain")
            {
                Text = $"Your single-use sign-in code is: {code}\r\n\r\nThis code expires in a few minutes. If you did not request it, do not share it and change your account password.",
            };
            var encryption = _options.Encryption == SmtpEncryption.StartTls
                ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;
            await client.ConnectAsync(_options.Host, _options.Port, encryption, deadline.Token);
            if (!string.IsNullOrEmpty(_options.UserName))
                await client.AuthenticateAsync(_options.UserName, _options.Password, deadline.Token);
            await client.SendAsync(message, deadline.Token);
            // Delivery has been accepted. A failed QUIT must not invalidate an already-accepted OTP.
            try { await client.DisconnectAsync(true, deadline.Token); }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        finally { _admission.Release(); }
    }

    internal static bool TryAddress(string value, out MailboxAddress? address)
    {
        address = null;
        return !string.IsNullOrEmpty(value) && value.Length <= 254 && !value.Contains('\r') && !value.Contains('\n')
            && MailboxAddress.TryParse(value, out address)
            && string.Equals(value, address.Address, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _admission.Dispose();
}
