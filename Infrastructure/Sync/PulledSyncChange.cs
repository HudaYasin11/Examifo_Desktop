using System.Text.Json;

namespace Examifo_Desktop.Infrastructure.Sync;

public sealed record PulledSyncChange(long Revision, string Type, Guid EntityId, JsonElement Payload);
