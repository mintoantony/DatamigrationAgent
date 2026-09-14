using System.Security.Cryptography;
using System.Text;

namespace Dbm.Core.Crypto;

/// <summary>AES-256-GCM with a random per-user key file (created 0600). Used on macOS/Linux.</summary>
public sealed class AesGcmFileKeyProtector(string keyPath) : ISecretProtector
{
    public const string Prefix = "aesgcm:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    public string KeyPath { get; } = Path.GetFullPath(keyPath);

    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var blob = new byte[NonceSize + TagSize + plain.Length];
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(LoadOrCreateKey(), TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Prefix + Convert.ToBase64String(blob);
    }

    public string Unprotect(string protectedText)
    {
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("not an AES-GCM-protected value (expected prefix 'aesgcm:')");
        var blob = Convert.FromBase64String(protectedText[Prefix.Length..]);
        if (blob.Length < NonceSize + TagSize) throw new CryptographicException("protected value is truncated");
        var plain = new byte[blob.Length - NonceSize - TagSize];
        using var aes = new AesGcm(LoadOrCreateKey(), TagSize);
        aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(KeyPath)) return ReadKey();

        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var key = RandomNumberGenerator.GetBytes(KeySize);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(KeyPath, options)) stream.Write(key);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return key;
        }
        catch (IOException) when (File.Exists(KeyPath))
        {
            return ReadKey();   // another process created it first
        }
    }

    private byte[] ReadKey()
    {
        var key = File.ReadAllBytes(KeyPath);
        return key.Length == KeySize ? key : throw new CryptographicException($"key file {KeyPath} must be {KeySize} bytes");
    }
}
