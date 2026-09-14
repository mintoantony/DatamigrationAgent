namespace Dbm.Core.Crypto;

/// <summary>Encrypts secrets (connection strings) for the current OS user. Output is tagged "dpapi:" or "aesgcm:".</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}
