using Examifo_Desktop.Domain.Models;

namespace Examifo_Desktop.Services;

public enum ExamAcquisitionState
{
    Checking,
    Downloading,
    OfflineReady,
    Unavailable,
    Failed
}

public sealed record ExamAcquisitionUpdate(
    Exam Exam,
    ExamAcquisitionState State,
    bool NewlyAvailable = false,
    string? Detail = null);

public sealed class ExamAcquisitionCoordinator(
    ExamService examService,
    ExamPackageStore packageStore)
{
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _parallelDownloads = new(2, 2);
    private bool _initialized;

    public async Task AcquireAvailablePackagesAsync(IEnumerable<Exam> exams,
        IProgress<ExamAcquisitionUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        Exam[] candidates = exams.GroupBy(x => x.Id).Select(x => x.First()).ToArray();
        await Task.WhenAll(candidates.Select(exam => AcquireOneAsync(
            exam, progress, cancellationToken)));
    }

    private async Task AcquireOneAsync(Exam exam, IProgress<ExamAcquisitionUpdate>? progress,
        CancellationToken cancellationToken)
    {
        try { await AcquireOneCoreAsync(exam, progress, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Automatic package acquisition failed: {ex}");
            progress?.Report(new(exam, ExamAcquisitionState.Failed, Detail: ex.Message));
        }
    }

    private async Task AcquireOneCoreAsync(Exam exam, IProgress<ExamAcquisitionUpdate>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(exam, ExamAcquisitionState.Checking));
        Exam? installed = await examService.TryLoadInstalledExamAsync(exam, cancellationToken);
        if (installed is not null)
        {
            progress?.Report(new(exam, ExamAcquisitionState.OfflineReady));
            return;
        }
        if (!exam.CanDownload)
        {
            progress?.Report(new(exam, ExamAcquisitionState.Unavailable));
            return;
        }

        await _parallelDownloads.WaitAsync(cancellationToken);
        try
        {
            progress?.Report(new(exam, ExamAcquisitionState.Downloading));
            Exception? lastError = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await examService.PrepareExamAsync(exam, cancellationToken);
                    progress?.Report(new(exam, ExamAcquisitionState.OfflineReady,
                        NewlyAvailable: true));
                    return;
                }
                catch (Exception ex) when (IsTransient(ex, cancellationToken))
                {
                    lastError = ex;
                    if (attempt < 3)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)),
                            cancellationToken);
                }
                catch (Exception ex)
                {
                    progress?.Report(new(exam, ExamAcquisitionState.Failed,
                        Detail: ex.Message));
                    return;
                }
            }
            progress?.Report(new(exam, ExamAcquisitionState.Failed,
                Detail: lastError?.Message));
        }
        finally
        {
            _parallelDownloads.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            packageStore.CleanupAbandonedFiles();
            _initialized = true;
        }
        finally { _initializationGate.Release(); }
    }

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException
        || ex is IOException
        || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested;
}
