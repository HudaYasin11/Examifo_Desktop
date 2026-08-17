namespace Examifo_Desktop.Domain.Models;

public class Exam : System.ComponentModel.INotifyPropertyChanged
{
    private string _offlineStatus = "Checking offline availability…";
    private string _offlineStatusColor = "#64748B";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public int DurationMinutes { get; set; }

    public DateTime StartsAtUtc { get; set; }

    public DateTime EndsAtUtc { get; set; }

    public int MaxAttempts { get; set; }

    public bool ProctoringEnabled { get; set; }

    public string PackageVersion { get; set; } = string.Empty;

    public string PackageHash { get; set; } = string.Empty;

    public long PackageSizeBytes { get; set; }

    public bool CanDownload { get; set; }

    public bool CanStartOffline { get; set; }

    public string? ExistingAttemptStatus { get; set; }

    public List<Question> Questions { get; set; } = new();

    public bool ShuffleQuestions { get; set; }

    public bool ShuffleOptions { get; set; }

    public string OfflineStatus
    {
        get => _offlineStatus;
        set => SetField(ref _offlineStatus, value);
    }

    public string OfflineStatusColor
    {
        get => _offlineStatusColor;
        set => SetField(ref _offlineStatusColor, value);
    }

    private void SetField(ref string field, string value,
        [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
