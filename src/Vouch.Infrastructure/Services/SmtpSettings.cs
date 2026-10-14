using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Vouch.Infrastructure.Security;

namespace Vouch.Infrastructure.Services;

public sealed record SmtpSettings(string Host, int Port, string User, string Password, string FromAddress)
{
    public static SmtpSettings? Read(IConfiguration config, bool production = false)
    {
        var host = config["Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            if (production) throw new InvalidOperationException("Production requires configured SMTP for verification and durable alerts.");
            return null;
        }
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) throw new InvalidOperationException("Smtp:Host must be a hostname or IP address.");
        if (!int.TryParse(config["Smtp:Port"] ?? "587", out var port) || port is < 1 or > 65535 || port == 465)
            throw new InvalidOperationException("Smtp:Port must support STARTTLS (normally 587); implicit TLS port 465 is unsupported.");
        var user = RequiredConfiguration.Read(config, "Smtp:User");
        var password = RequiredConfiguration.Secret(config, "Smtp:Password", 1);
        var from = string.IsNullOrWhiteSpace(config["Smtp:FromAddress"]) ? user : config["Smtp:FromAddress"]!;
        if (!MailAddress.TryCreate(from, out var address) || address.Address != from || from.IndexOfAny(['\r', '\n']) >= 0)
            throw new InvalidOperationException("Smtp:FromAddress must be a plain valid mailbox address.");
        return new(host, port, user, password, from);
    }
}
