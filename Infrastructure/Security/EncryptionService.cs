using System;
using System.Text;

namespace Examifo_Desktop.Infrastructure.Security;

public sealed class EncryptionService(Examifo_Desktop.Services.ISecureValueStore secureValueStore)
{
    private const string KeyName = "examifo.local_data_key.v1";
    private const string Prefix = "enc:v1:";
    private readonly SemaphoreSlim _keyGate = new(1, 1);
    private byte[]? _cachedKey;

    public async Task<string> EncryptAsync(string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(value)) return value;
        byte[] key = await GetOrCreateKeyAsync(cancellationToken);
        byte[] nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        byte[] plaintext = Encoding.UTF8.GetBytes(value);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        using var aes = new System.Security.Cryptography.AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return Prefix + Convert.ToBase64String(nonce) + ":"
            + Convert.ToBase64String(tag) + ":" + Convert.ToBase64String(ciphertext);
    }

    public async Task<string> DecryptAsync(string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        string[] parts = value[Prefix.Length..].Split(':');
        if (parts.Length != 3)
            throw new System.Security.Cryptography.CryptographicException("Invalid encrypted local value.");
        byte[] key = await GetOrCreateKeyAsync(cancellationToken);
        byte[] nonce = Convert.FromBase64String(parts[0]);
        byte[] tag = Convert.FromBase64String(parts[1]);
        byte[] ciphertext = Convert.FromBase64String(parts[2]);
        byte[] plaintext = new byte[ciphertext.Length];
        using var aes = new System.Security.Cryptography.AesGcm(key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private async Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken)
    {
        if (_cachedKey is not null) return _cachedKey;
        await _keyGate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedKey is not null) return _cachedKey;
            string? stored = await secureValueStore.GetAsync(KeyName);
            if (stored is not null)
            {
                try
                {
                    byte[] existing = Convert.FromBase64String(stored);
                    if (existing.Length != 32)
                        throw new System.Security.Cryptography.CryptographicException("Invalid local encryption key.");
                    return _cachedKey = existing;
                }
                catch (FormatException ex)
                {
                    throw new System.Security.Cryptography.CryptographicException(
                        "The protected local encryption key is corrupt.", ex);
                }
            }
            byte[] created = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            await secureValueStore.SetAsync(KeyName, Convert.ToBase64String(created));
            return _cachedKey = created;
        }
        finally { _keyGate.Release(); }
    }
}
