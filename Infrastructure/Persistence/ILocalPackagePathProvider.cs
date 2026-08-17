namespace Examifo_Desktop.Infrastructure.Persistence;

public interface ILocalPackagePathProvider
{
    string TemporaryPackageDirectory { get; }
    string InstalledPackageDirectory { get; }
}
