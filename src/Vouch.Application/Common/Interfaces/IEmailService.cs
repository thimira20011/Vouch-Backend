namespace Vouch.Application.Common.Interfaces;

/// <summary>
/// Step 18 — NFR-12: Sends transactional emails from the application.
/// Used currently for high-severity report alerts to the Architect.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Sends a plain-text email. Fire-and-forget safe — implementations
    /// must not throw on transient SMTP errors (log and swallow instead).
    /// </summary>
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}
