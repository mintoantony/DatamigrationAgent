using System.Security.Cryptography;
using Dbm.Core.Crypto;

namespace Dbm.Tests.Unit.Crypto;

public class ProtectorTests
{
    private const string Secret = "Server=db;Database=Shop;User ID=migrator;Password=S3cr3t!pw;";

    private static string TempKeyPath() => Path.Combine(Path.GetTempPath(), "dbm-key-" + Guid.NewGuid().ToString("N"), "key");

    [Fact]
    public void AesGcm_round_trips_with_a_fresh_nonce_each_time()
    {
        var keyPath = TempKeyPath();
        var p = new AesGcmFileKeyProtector(keyPath);

        var a = p.Protect(Secret);
        var b = p.Protect(Secret);

        Assert.StartsWith("aesgcm:", a);
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("S3cr3t", a);
        Assert.Equal(Secret, p.Unprotect(a));
        Assert.Equal(Secret, new AesGcmFileKeyProtector(keyPath).Unprotect(b));
        Assert.Equal(32, File.ReadAllBytes(keyPath).Length);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath));
    }

    [Fact]
    public void AesGcm_rejects_tampering_foreign_prefixes_and_other_keys()
    {
        var p = new AesGcmFileKeyProtector(TempKeyPath());
        var protectedText = p.Protect(Secret);
        var bytes = Convert.FromBase64String(protectedText["aesgcm:".Length..]);
        bytes[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect("aesgcm:" + Convert.ToBase64String(bytes)));
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect("dpapi:AAAA"));
        Assert.ThrowsAny<CryptographicException>(() => new AesGcmFileKeyProtector(TempKeyPath()).Unprotect(protectedText));
    }

    [Fact]
    public void Dpapi_round_trips_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = new DpapiProtector();

        var protectedText = p.Protect(Secret);

        Assert.StartsWith("dpapi:", protectedText);
        Assert.DoesNotContain("S3cr3t", protectedText);
        Assert.Equal(Secret, p.Unprotect(protectedText));
        CryptographicException? rejected = null;
        try
        {
            p.Unprotect("aesgcm:AAAA");
        }
        catch (CryptographicException ex)
        {
            rejected = ex;
        }
        Assert.NotNull(rejected);
    }

    [Fact]
    public void ForCurrentUser_picks_the_platform_protector()
    {
        var p = SecretProtector.ForCurrentUser();

        if (OperatingSystem.IsWindows()) Assert.IsType<DpapiProtector>(p);
        else Assert.IsType<AesGcmFileKeyProtector>(p);
        Assert.Equal(Secret, p.Unprotect(p.Protect(Secret)));
    }
}
