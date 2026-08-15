using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api.DTOs;

namespace Examifo_Desktop.Services;

public sealed record StoredOfflineAuthorization(
    Guid AuthorizationId, Guid AttemptId, Guid ExamId, Guid CandidateId, Guid DeviceId,
    long PackageVersion, DateTimeOffset NotBeforeUtc, DateTimeOffset MustStartBeforeUtc,
    DateTimeOffset MustSubmitBeforeUtc, int? DurationSeconds, int AttemptNumber,
    string ShuffleSeed, DateTimeOffset ServerTimeUtc, string AuthorizationToken)
{
    public static StoredOfflineAuthorization FromResponse(OfflineAuthorizationResponse value) => new(
        value.AuthorizationId, value.AttemptId, value.ExamId, value.CandidateId, value.DeviceId,
        value.PackageVersion, value.NotBeforeUtc, value.MustStartBeforeUtc, value.MustSubmitBeforeUtc,
        value.DurationSeconds, value.AttemptNumber, value.ShuffleSeed, value.ServerTimeUtc,
        value.AuthorizationToken);
}

public sealed class OfflineAuthorizationStore(ISecureValueStore secureValueStore)
{
    public const string StorageKey = "examifo.offline_authorizations.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task SaveAsync(StoredOfflineAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        Validate(authorization);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<StoredOfflineAuthorization> values = await LoadCoreAsync();
            values.RemoveAll(x => x.AuthorizationId == authorization.AuthorizationId
                || x.AttemptId == authorization.AttemptId || x.ExamId == authorization.ExamId);
            values.Add(authorization);
            await secureValueStore.SetAsync(StorageKey, JsonSerializer.Serialize(values, JsonOptions));
        }
        finally { _gate.Release(); }
    }

    public async Task<StoredOfflineAuthorization?> FindForExamAsync(Guid examId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await LoadCoreAsync()).SingleOrDefault(x => x.ExamId == examId); }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid authorizationId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<StoredOfflineAuthorization> values = await LoadCoreAsync();
            values.RemoveAll(x => x.AuthorizationId == authorizationId);
            if (values.Count == 0) secureValueStore.Remove(StorageKey);
            else await secureValueStore.SetAsync(StorageKey, JsonSerializer.Serialize(values, JsonOptions));
        }
        finally { _gate.Release(); }
    }

    private async Task<List<StoredOfflineAuthorization>> LoadCoreAsync()
    {
        string? json = await secureValueStore.GetAsync(StorageKey);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            List<StoredOfflineAuthorization>? values = JsonSerializer.Deserialize<List<StoredOfflineAuthorization>>(json, JsonOptions);
            if (values is null || values.Any(x => !IsValid(x))
                || values.Select(x => x.AuthorizationId).Distinct().Count() != values.Count)
                throw new JsonException();
            return values;
        }
        catch (JsonException)
        {
            secureValueStore.Remove(StorageKey);
            return [];
        }
    }

    private static void Validate(StoredOfflineAuthorization value)
    {
        if (!IsValid(value)) throw new InvalidOperationException("Refusing to store an invalid offline authorization.");
    }

    private static bool IsValid(StoredOfflineAuthorization x) =>
        x.AuthorizationId != Guid.Empty && x.AttemptId != Guid.Empty && x.ExamId != Guid.Empty
        && x.CandidateId != Guid.Empty && x.DeviceId != Guid.Empty && x.PackageVersion > 0
        && x.NotBeforeUtc != default && x.MustStartBeforeUtc >= x.NotBeforeUtc
        && x.MustSubmitBeforeUtc >= x.NotBeforeUtc && x.DurationSeconds is null or > 0
        && x.AttemptNumber > 0 && !string.IsNullOrWhiteSpace(x.ShuffleSeed)
        && x.ServerTimeUtc != default && !string.IsNullOrWhiteSpace(x.AuthorizationToken);
}
