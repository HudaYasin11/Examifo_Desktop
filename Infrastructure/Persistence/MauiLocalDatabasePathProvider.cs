namespace Examifo_Desktop.Infrastructure.Persistence;

public sealed class MauiLocalDatabasePathProvider : ILocalDatabasePathProvider
{
    public string DatabasePath { get; } = Path.Combine(FileSystem.AppDataDirectory, "examifo.db3");
}
