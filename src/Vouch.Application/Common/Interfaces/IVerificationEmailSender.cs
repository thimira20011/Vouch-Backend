namespace Vouch.Application.Common.Interfaces;

public interface IVerificationEmailSender
{
    // Unlike optional moderation alerts, verification must report delivery failures.
    Task SendVerificationAsync(string to, string token, CancellationToken ct = default);
}
