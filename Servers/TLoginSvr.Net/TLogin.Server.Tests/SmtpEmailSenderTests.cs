using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit.Net.Smtp;
using MimeKit;
using TLogin.Server.Security;
using Xunit;

namespace TLogin.Server.Tests;

public sealed class SmtpEmailSenderTests
{
    [Theory]
    [InlineData(SmtpEncryption.StartTls)]
    [InlineData(SmtpEncryption.SslOnConnect)]
    public async Task RealTlsSmtpDeliversToConfiguredRecipientAndAuthenticatesAfterEncryption(SmtpEncryption mode)
    {
        await using var server = new SmtpServer(mode);
        using var sender = server.Sender();
        await sender.SendSecurityCodeAsync("owner@example.test", "123456");
        var message = await server.Message.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("owner@example.test", Assert.IsType<MailboxAddress>(Assert.Single(message.To)).Address);
        Assert.Equal("login@example.test", Assert.IsType<MailboxAddress>(Assert.Single(message.From)).Address);
        Assert.Contains("123456", message.TextBody);
        Assert.DoesNotContain("123456", message.Subject);
        Assert.True(server.SawEncryptedAuthentication);
        Assert.False(server.SawPlainAuthentication);
    }

    [Fact]
    public async Task MissingStartTlsNeverDowngradesOrSendsCredentials()
    {
        await using var server = new SmtpServer(SmtpEncryption.StartTls) { AdvertiseStartTls = false };
        using var sender = server.Sender();
        await Assert.ThrowsAsync<NotSupportedException>(() => sender.SendSecurityCodeAsync("owner@example.test", "123456"));
        Assert.False(server.SawEncryptedAuthentication); Assert.False(server.SawPlainAuthentication);
        Assert.False(server.Message.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task UntrustedCertificateIsRejectedWithProductionValidation()
    {
        await using var server = new SmtpServer(SmtpEncryption.StartTls);
        using var sender = new SmtpEmailSender(server.Options);
        Assert.NotNull(await Record.ExceptionAsync(() => sender.SendSecurityCodeAsync("owner@example.test", "123456")));
        Assert.False(server.SawEncryptedAuthentication); Assert.False(server.SawPlainAuthentication);
    }

    [Fact]
    public async Task AuthenticationAndMessageRejectionAreNotReportedAsDelivery()
    {
        await using var server = new SmtpServer(SmtpEncryption.StartTls) { RejectMessage = true };
        using var sender = server.Sender();
        await Assert.ThrowsAsync<SmtpCommandException>(() => sender.SendSecurityCodeAsync("owner@example.test", "123456"));
        Assert.True(server.Message.Task.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("")]
    [InlineData("owner@example.test\r\nBcc:other@example.test")]
    [InlineData("Owner <owner@example.test>")]
    public async Task InvalidOrInjectedRecipientsAreRefusedBeforeConnecting(string email)
    {
        using var sender = new SmtpEmailSender(new() { Host = "localhost", From = "login@example.test" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendSecurityCodeAsync(email, "123456"));
    }

    [Fact]
    public async Task MissingSmtpConfigurationFailsClosed()
    {
        using var sender = new SmtpEmailSender(new());
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendSecurityCodeAsync("owner@example.test", "123456"));
        Assert.Throws<InvalidOperationException>(() => new SmtpEmailSender(new() { Encryption = (SmtpEncryption)99 }));
    }

    private sealed class SmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(15));
        private readonly X509Certificate2 _certificate;
        private readonly Task _run;
        private readonly SmtpEncryption _mode;
        public bool AdvertiseStartTls = true, RejectMessage;
        public bool SawEncryptedAuthentication, SawPlainAuthentication;
        public readonly TaskCompletionSource<MimeMessage> Message = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly SmtpOptions Options;

        public SmtpServer(SmtpEncryption mode)
        {
            _mode = mode;
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
                OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
            _listener.Start();
            Options = new() { Host = "localhost", Port = ((IPEndPoint)_listener.LocalEndpoint).Port, Encryption = mode,
                From = "login@example.test", UserName = "test-user", Password = "test-password", TimeoutSeconds = 5 };
            _run = ServeAsync();
        }

        public SmtpEmailSender Sender() => new(Options, () => new SmtpClient
        {
            ServerCertificateValidationCallback = (_, remote, _, _) => remote is not null
                && remote.GetCertHashString(HashAlgorithmName.SHA256) == _certificate.GetCertHashString(HashAlgorithmName.SHA256),
        });

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                Stream stream = client.GetStream();
                bool encrypted = false;
                if (_mode == SmtpEncryption.SslOnConnect) { stream = await EncryptAsync(stream); encrypted = true; }
                StreamReader reader = Reader(stream); StreamWriter writer = Writer(stream);
                try
                {
                    await writer.WriteLineAsync("220 localhost ESMTP");
                    while (await reader.ReadLineAsync(_cts.Token) is { } line)
                    {
                        if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("250-localhost");
                            if (!encrypted && AdvertiseStartTls) await writer.WriteLineAsync("250-STARTTLS");
                            await writer.WriteLineAsync("250 AUTH PLAIN");
                        }
                        else if (line == "STARTTLS")
                        {
                            await writer.WriteLineAsync("220 Ready for TLS");
                            reader.Dispose(); writer.Dispose();
                            stream = await EncryptAsync(stream); encrypted = true;
                            reader = Reader(stream); writer = Writer(stream);
                        }
                        else if (line.StartsWith("AUTH ", StringComparison.OrdinalIgnoreCase))
                        {
                            if (encrypted) SawEncryptedAuthentication = true; else SawPlainAuthentication = true;
                            await writer.WriteLineAsync("235 Authentication successful");
                        }
                        else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase)
                            || line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase)) await writer.WriteLineAsync("250 OK");
                        else if (line == "DATA")
                        {
                            await writer.WriteLineAsync("354 End with dot");
                            var text = new StringBuilder();
                            while (await reader.ReadLineAsync(_cts.Token) is { } part && part != ".") text.Append(part).Append("\r\n");
                            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(text.ToString()));
                            Message.TrySetResult(await MimeMessage.LoadAsync(bytes, _cts.Token));
                            await writer.WriteLineAsync(RejectMessage ? "550 Message rejected" : "250 Accepted");
                        }
                        else if (line == "QUIT") { await writer.WriteLineAsync("221 Bye"); break; }
                        else await writer.WriteLineAsync("250 OK");
                    }
                }
                finally { reader.Dispose(); writer.Dispose(); await stream.DisposeAsync(); }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
            catch (IOException) { /* expected when a peer rejects the test certificate */ }
            catch (AuthenticationException) { }
        }

        private async Task<Stream> EncryptAsync(Stream stream)
        {
            var tls = new SslStream(stream, leaveInnerStreamOpen: false);
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, _cts.Token);
                return tls;
            }
            catch { await tls.DisposeAsync(); throw; }
        }

        private static StreamReader Reader(Stream stream) => new(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
        private static StreamWriter Writer(Stream stream) => new(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
            { AutoFlush = true, NewLine = "\r\n" };
        public async ValueTask DisposeAsync()
        { _cts.Cancel(); _listener.Stop(); try { await _run; } catch (SocketException) { } _certificate.Dispose(); _cts.Dispose(); }
    }
}
