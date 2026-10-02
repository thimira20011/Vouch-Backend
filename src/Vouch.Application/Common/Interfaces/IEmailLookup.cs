namespace Vouch.Application.Common.Interfaces;

public interface IEmailLookup
{
    string Normalize(string email);
    string Hash(string email);
}
