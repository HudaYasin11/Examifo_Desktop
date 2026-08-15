using System.Net.Http.Json;
using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api.DTOs;

namespace Examifo_Desktop.Infrastructure.Api.Clients;

public sealed class DeviceApiClient(AuthenticatedHttpClient authenticatedHttpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DeviceResponse> RegisterOrUpdateAsync(
        DeviceInput device,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(device);
        using HttpResponseMessage response = await authenticatedHttpClient.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "api/v1/devices")
            {
                Content = JsonContent.Create(new DeviceRequest(device), options: JsonOptions)
            }, cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
        DeviceResponse result = await response.Content.ReadFromJsonAsync<DeviceResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Examifo returned an empty device response.");
        ValidateResponse(result);
        if (result.InstallationId != device.InstallationId)
            throw new InvalidDataException("Examifo returned a device for a different installation.");
        return result;
    }

    public async Task<IReadOnlyList<DeviceResponse>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await authenticatedHttpClient.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "api/v1/devices"),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        List<DeviceResponse> devices = await response.Content.ReadFromJsonAsync<List<DeviceResponse>>(
            JsonOptions, cancellationToken) ?? throw new InvalidDataException("Examifo returned an empty device list.");
        foreach (DeviceResponse device in devices) ValidateResponse(device);
        if (devices.Select(x => x.Id).Distinct().Count() != devices.Count)
            throw new InvalidDataException("Examifo returned duplicate device identifiers.");
        return devices;
    }

    public async Task RevokeAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("A device ID is required.", nameof(deviceId));
        using HttpResponseMessage response = await authenticatedHttpClient.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"api/v1/devices/{deviceId:D}"),
            cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static void ValidateInput(DeviceInput device)
    {
        if (device.InstallationId == Guid.Empty || string.IsNullOrWhiteSpace(device.Name)
            || string.IsNullOrWhiteSpace(device.Platform) || string.IsNullOrWhiteSpace(device.AppVersion))
            throw new ArgumentException("Complete installation and device information is required.", nameof(device));
    }

    private static void ValidateResponse(DeviceResponse device)
    {
        if (device.Id == Guid.Empty || device.InstallationId == Guid.Empty
            || string.IsNullOrWhiteSpace(device.Name) || string.IsNullOrWhiteSpace(device.Platform)
            || string.IsNullOrWhiteSpace(device.AppVersion) || string.IsNullOrWhiteSpace(device.Status)
            || device.RegisteredAtUtc == default)
            throw new InvalidDataException("Examifo returned an invalid device response.");
    }
}
