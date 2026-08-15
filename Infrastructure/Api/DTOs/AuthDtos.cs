namespace Examifo_Desktop.Infrastructure.Api.DTOs;

public sealed record LoginRequest(string Email, string Password, DeviceInput Device);
public sealed record RefreshRequest(string RefreshToken, Guid DeviceId);
public sealed record DeviceInput(Guid InstallationId, string Name, string Platform, string AppVersion, string? PublicKey);
public sealed record DeviceRequest(DeviceInput Device);
public sealed record DeviceResponse(
    Guid Id,
    Guid InstallationId,
    string Name,
    string Platform,
    string AppVersion,
    string Status,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? RevokedAtUtc);
public sealed record LoginResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc, string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc, Guid DeviceId, DateTimeOffset ServerTimeUtc, AuthUserResponse User);
public sealed record AuthUserResponse(Guid Id, string Name, string? Email);
public sealed record CurrentIdentityResponse(AuthUserResponse User, Guid DeviceId, DateTimeOffset ServerTimeUtc);
public sealed record ProblemDetailsResponse(string? Type, string? Title, int? Status, string? Code, string? TraceId);
