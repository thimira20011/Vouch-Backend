using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Vouch.Application.Common.Interfaces;

namespace Vouch.Infrastructure.Services;

/// <summary>
/// Step 18 — NFR-12: SMTP email implementation using System.Net.Mail.
/// No extra NuGet packages required — uses the BCL SmtpClient.
///
/// Configuration (appsettings.json → Smtp section):
///   Smtp:Host          — SMTP server hostname (e.g. smtp.gmail.com)
///   Smtp:Port          — SMTP port (587 for TLS, 465 for SSL)
///   Smtp:User          — Sender account username
///   Smtp:Password      — Sender account password / app-password
///   Smtp:FromAddress   — From address (defaults to Smtp:User if omitted)
///   Smtp:ArchitectEmail — Where high-severity alerts are sent
///
/// If Smtp:Host is empty/missing the service is a no-op (development safe).
/// </summary>
public sealed class SmtpEmailService : IEmailService
{
    private readonly ILogger<SmtpEmailService> _logger;
    private readonly string? _host;
    private readonly int _port;
    private readonly string? _user;
    private readonly string? _password;
    private readonly string _fromAddress;
    private readonly bool _isConfigured;

    public SmtpEmailService(IConfiguration configuration, ILogger<SmtpEmailService> logger)
    {
        _logger = logger;

        _host = configuration["Smtp:Host"];
        _port = int.TryParse(configuration["Smtp:Port"], out var port) ? port : 587;
        _user = configuration["Smtp:User"];
        _password = configuration["Smtp:Password"];
        _fromAddress = configuration["Smtp:FromAddress"] ?? _user ?? "noreply@vouch.app";

        // Only active if a host is configured — safe no-op in local dev without SMTP
        _isConfigured = !string.IsNullOrWhiteSpace(_host)
                     && !string.IsNullOrWhiteSpace(_user)
                     && !string.IsNullOrWhiteSpace(_password);

        if (!_isConfigured)
        {
            _logger.LogWarning(
                "SmtpEmailService: Smtp:Host / Smtp:User / Smtp:Password not configured. " +
                "Email sending is disabled. Set these values for production.");
        }
    }

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        if (!_isConfigured)
        {
            _logger.LogInformation(
                "SmtpEmailService (no-op): would send '{Subject}' to {To}.", subject, to);
            return;
        }

        try
        {
            using var client = new SmtpClient(_host, _port)
            {
                Credentials = new NetworkCredential(_user, _password),
                EnableSsl = true,
                Timeout = 15_000  // 15s — don't block caller threads indefinitely
            };

            using var message = new MailMessage(
                from: _fromAddress,
                to: to,
                subject: subject,
                body: body
            );

            await client.SendMailAsync(message, ct);

            _logger.LogInformation(
                "SmtpEmailService: email '{Subject}' sent to {To}.", subject, to);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("SmtpEmailService: send cancelled for '{Subject}' to {To}.", subject, to);
        }
        catch (Exception ex)
        {
            // NFR-12: Email failure must not disrupt the main report submission flow
            _logger.LogError(ex,
                "SmtpEmailService: failed to send '{Subject}' to {To}. Report was still saved.", subject, to);
        }
    }
}
