using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Common;

namespace Vouch.Infrastructure.Services;

public sealed class SmtpEmailService : IEmailService, IVerificationEmailSender
{
    private readonly SmtpSettings? _settings;
    public SmtpEmailService(IConfiguration configuration, ILogger<SmtpEmailService> logger)
    {
        _settings = SmtpSettings.Read(configuration);
        if (_settings is null) logger.LogWarning("SMTP is unconfigured. Verification is unavailable and alert emails remain pending.");
    }
    public async Task SendVerificationAsync(string to, string token, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(to, "Verify your Vouch university email",
                $"Enter this verification token in Vouch: {token}\n\nIt expires in 30 minutes. If you did not request it, ignore this email.", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw new VerificationDeliveryException(); }
    }
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        if (_settings is null) throw new InvalidOperationException("SMTP is not configured.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var client = new SmtpClient(_settings.Host, _settings.Port)
        {
            Credentials = new NetworkCredential(_settings.User, _settings.Password), EnableSsl = true
        };
        using var message = new MailMessage(_settings.FromAddress, to, subject, body);
        // Successful return means SMTP accepted the message. Failures must reach the durable retry worker.
        await client.SendMailAsync(message, deadline.Token);
    }
}