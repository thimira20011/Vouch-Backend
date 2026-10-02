namespace Vouch.Application.Common.Interfaces;

public interface IEncryptionService
{
    string Encrypt(string plainText, string purpose = "value");
    string Decrypt(string cipherText, string purpose = "value");
}
