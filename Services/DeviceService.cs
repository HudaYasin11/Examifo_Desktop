using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;

namespace Examifo_Desktop.Services;

public sealed class DeviceService(
    DeviceApiClient deviceApiClient,
    InstallationIdentityService installationIdentityService,
    AuthSessionStore sessionStore)
{
    public async Task<DeviceResponse> RegisterOrUpdateCurrentAsync(CancellationToken cancellationToken = default)
    {
        Guid installationId = installationIdentityService.GetOrCreateInstallationId();
        DeviceResponse device = await deviceApiClient.RegisterOrUpdateAsync(new DeviceInput(
            installationId, DeviceInfo.Name, DeviceInfo.Platform.ToString(), AppInfo.Current.VersionString, null),
            cancellationToken);
        AuthSession session = await RequireSessionAsync(cancellationToken);
        if (device.Id != session.DeviceId)
            throw new InvalidDataException("The active session is bound to a different Examifo device.");
        return device;
    }

    public Task<IReadOnlyList<DeviceResponse>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        deviceApiClient.GetDevicesAsync(cancellationToken);

    public async Task RevokeAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        AuthSession session = await RequireSessionAsync(cancellationToken);
        await deviceApiClient.RevokeAsync(deviceId, cancellationToken);
        if (deviceId == session.DeviceId)
            await sessionStore.ClearAsync(cancellationToken);
    }

    private async Task<AuthSession> RequireSessionAsync(CancellationToken cancellationToken) =>
        await sessionStore.LoadAsync(cancellationToken)
        ?? throw new InvalidOperationException("Sign in before managing Examifo devices.");
}
