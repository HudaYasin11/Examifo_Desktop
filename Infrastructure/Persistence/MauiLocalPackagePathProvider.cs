namespace Examifo_Desktop.Infrastructure.Persistence;

public sealed class MauiLocalPackagePathProvider : ILocalPackagePathProvider
{
    public string TemporaryPackageDirectory =>
        Path.Combine(FileSystem.AppDataDirectory, "Packages", "Temporary");

    public string InstalledPackageDirectory =>
        Path.Combine(FileSystem.AppDataDirectory, "Packages", "Installed");
}
