using System.Security.Cryptography;
using System.Text;

namespace VspcAutotaskPlugin.Infrastructure;

/// <summary>
/// Encrypts secrets at rest. On Windows uses DPAPI (LocalMachine scope so the value
/// survives running under a service account); elsewhere falls back to AES-GCM with a
/// key file stored next to the database.
/// </summary>
public sealed class SecretProtector
{
    private const string DpapiPrefix = "dpapi:";
    private const string AesPrefix = "aes:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VspcAutotaskPlugin.v1");

    private readonly AppPaths _paths;
    private readonly object _keyLock = new();
    private byte[]? _aesKey;

    public SecretProtector(AppPaths paths) => _paths = paths;

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        var data = Encoding.UTF8.GetBytes(plaintext);

        if (OperatingSystem.IsWindows())
        {
            var blob = ProtectedData.Protect(data, Entropy, DataProtectionScope.LocalMachine);
            return DpapiPrefix + Convert.ToBase64String(blob);
        }

        var key = GetAesKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, data, cipher, tag);
        return AesPrefix + Convert.ToBase64String(nonce) + ":" +
               Convert.ToBase64String(tag) + ":" + Convert.ToBase64String(cipher);
    }

    public string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;

        if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "This secret was protected with Windows DPAPI and can only be read on the Windows host that stored it.");
            var blob = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
            var data = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(data);
        }

        if (stored.StartsWith(AesPrefix, StringComparison.Ordinal))
        {
            var parts = stored[AesPrefix.Length..].Split(':');
            var nonce = Convert.FromBase64String(parts[0]);
            var tag = Convert.FromBase64String(parts[1]);
            var cipher = Convert.FromBase64String(parts[2]);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(GetAesKey(), 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }

        // Unprefixed value: treat as plaintext (allows hand-seeded settings).
        return stored;
    }

    private byte[] GetAesKey()
    {
        if (_aesKey != null) return _aesKey;
        lock (_keyLock)
        {
            if (_aesKey != null) return _aesKey;
            if (File.Exists(_paths.AesKeyPath))
            {
                _aesKey = File.ReadAllBytes(_paths.AesKeyPath);
            }
            else
            {
                _aesKey = RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(_paths.AesKeyPath, _aesKey);
            }
            return _aesKey;
        }
    }
}
