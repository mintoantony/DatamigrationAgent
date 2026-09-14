namespace Dbm.Core.Crypto;

public static class SecretProtector
{
    /// <summary>Windows → DPAPI (CurrentUser); macOS/Linux → AES-GCM with UserHome/key.</summary>
    public static ISecretProtector ForCurrentUser() =>
        OperatingSystem.IsWindows()
            ? new DpapiProtector()
            : new AesGcmFileKeyProtector(Path.Combine(UserHome.Dir, "key"));
}
