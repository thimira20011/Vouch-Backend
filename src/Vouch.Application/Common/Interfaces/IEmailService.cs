namespace Vouch.Application.Common.Interfaces;

/// <summary>
/// Step 18 — NFR-12: Sends transactional emails from the application.
/// Used currently for high-severity report alerts to the Architect.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Sends a plain-text email. Return only after SMTP acceptance; propagate failures
    /// and cancellation so the durable delivery worker can retry without claiming success.
    /// </summary>
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}
