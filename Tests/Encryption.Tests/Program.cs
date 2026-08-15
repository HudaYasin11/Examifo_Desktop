using Examifo_Desktop.Infrastructure.Security;
using Examifo_Desktop.Services;

var store = new MemorySecureValueStore();
var encryption = new EncryptionService(store);
const string secret = "opaque-authorization-token and candidate answer";

string first = await encryption.EncryptAsync(secret);
string second = await encryption.EncryptAsync(secret);
Assert(first != secret && !first.Contains(secret, StringComparison.Ordinal), "ciphertext hides plaintext");
Assert(first != second, "encryption uses a fresh nonce");
Assert(await encryption.DecryptAsync(first) == secret, "ciphertext decrypts after write");

var restarted = new EncryptionService(store);
Assert(await restarted.DecryptAsync(first) == secret, "securely stored key survives service restart");
Assert(await restarted.DecryptAsync("legacy plaintext") == "legacy plaintext", "legacy plaintext remains readable");

Console.WriteLine("All local encryption tests passed.");

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
}

sealed class MemorySecureValueStore : ISecureValueStore
{
    private readonly Dictionary<string, string> _values = [];
    public Task<string?> GetAsync(string key) =>
        Task.FromResult(_values.TryGetValue(key, out string? value) ? value : null);
    public Task SetAsync(string key, string value)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }
    public void Remove(string key) => _values.Remove(key);
}
