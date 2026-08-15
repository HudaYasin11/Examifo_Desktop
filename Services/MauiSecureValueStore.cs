namespace Examifo_Desktop.Services;

public sealed class MauiSecureValueStore : ISecureValueStore
{
    public Task<string?> GetAsync(string key) => SecureStorage.Default.GetAsync(key);
    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);
    public void Remove(string key) => SecureStorage.Default.Remove(key);
}
