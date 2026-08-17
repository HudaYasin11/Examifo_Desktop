using Examifo_Desktop.Infrastructure.Persistence;
using Examifo_Desktop.Infrastructure.Security;

namespace Examifo_Desktop.Services;

public sealed record PackageInstallation(string LocalPath, bool Created);

public sealed class ExamPackageStore(
    EncryptionService encryptionService,
    ILocalPackagePathProvider paths)
{
    public void CleanupAbandonedFiles()
    {
        Directory.CreateDirectory(paths.TemporaryPackageDirectory);
        Directory.CreateDirectory(paths.InstalledPackageDirectory);
        DeleteMatches(paths.TemporaryPackageDirectory, "*.download", SearchOption.TopDirectoryOnly);
        DeleteMatches(paths.InstalledPackageDirectory, "*.installing", SearchOption.AllDirectories);
    }

    public async Task<PackageInstallation> InstallAsync(Guid examId, long version, string contentHash,
        byte[] packageBytes, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(examId, version, contentHash);
        string root = Path.GetFullPath(paths.InstalledPackageDirectory);
        string examDirectory = Path.Combine(root, examId.ToString("N"));
        Directory.CreateDirectory(examDirectory);
        string finalPath = Path.Combine(examDirectory,
            $"{version}-{contentHash.ToLowerInvariant()}.examifo");
        if (File.Exists(finalPath)) return new PackageInstallation(finalPath, false);

        string stagingPath = Path.Combine(examDirectory, $".{Guid.NewGuid():N}.installing");
        try
        {
            string encrypted = await encryptionService.EncryptAsync(
                Convert.ToBase64String(packageBytes), cancellationToken);
            await File.WriteAllTextAsync(stagingPath, encrypted, cancellationToken);
            try
            {
                File.Move(stagingPath, finalPath, overwrite: false);
                return new PackageInstallation(finalPath, true);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                return new PackageInstallation(finalPath, false);
            }
        }
        finally
        {
            try { if (File.Exists(stagingPath)) File.Delete(stagingPath); }
            catch (IOException ex) { System.Diagnostics.Debug.WriteLine($"Package staging cleanup deferred: {ex}"); }
        }
    }

    public async Task<byte[]> ReadAsync(string localPath,
        CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(paths.InstalledPackageDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string resolved = Path.GetFullPath(localPath);
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved))
            throw new InvalidDataException("The installed exam package is missing or outside protected storage.");
        string encrypted = await File.ReadAllTextAsync(resolved, cancellationToken);
        if (!encrypted.StartsWith("enc:v1:", StringComparison.Ordinal))
            throw new InvalidDataException("The installed exam package is not encrypted.");
        string encoded = await encryptionService.DecryptAsync(encrypted, cancellationToken);
        try { return Convert.FromBase64String(encoded); }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The installed exam package payload is corrupt.", ex);
        }
    }

    public void DeleteIfManaged(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath)) return;
        string root = Path.GetFullPath(paths.InstalledPackageDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string resolved = Path.GetFullPath(localPath);
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        try { if (File.Exists(resolved)) File.Delete(resolved); }
        catch (IOException ex) { System.Diagnostics.Debug.WriteLine($"Old package cleanup deferred: {ex}"); }
    }

    private static void ValidateIdentity(Guid examId, long version, string contentHash)
    {
        if (examId == Guid.Empty || version <= 0 || contentHash.Length != 64
            || !contentHash.All(Uri.IsHexDigit))
            throw new ArgumentException("A valid package identity is required.");
    }

    private static void DeleteMatches(string root, string pattern, SearchOption option)
    {
        foreach (string path in Directory.EnumerateFiles(root, pattern, option))
        {
            try { File.Delete(path); }
            catch (IOException ex) { System.Diagnostics.Debug.WriteLine($"Abandoned package cleanup deferred: {ex}"); }
            catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine($"Abandoned package cleanup denied: {ex}"); }
        }
    }
}
