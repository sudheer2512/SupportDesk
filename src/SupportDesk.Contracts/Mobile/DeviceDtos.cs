namespace SupportDesk.Contracts.Mobile;

// Push-device registration for the mobile app (agents: /api/v1/me/devices, portal contacts: /api/v1/portal/{brand}/devices).
//   platform: Android, iOS, Web

/// <summary>Registers (or refreshes) this device's push token for the signed-in agent or contact.</summary>
public sealed record RegisterDeviceRequest(string Platform, string Token, string? AppVersion = null);

/// <summary>Stops pushes to this token for the signed-in agent or contact (sign-out, notifications turned off).</summary>
public sealed record UnregisterDeviceRequest(string Token);

/// <summary>A registered device. The token itself is never echoed back — only its last characters.</summary>
public sealed record PushDeviceDto(
    Guid Id,
    string Platform,
    string TokenHint,
    string AppKind,
    string? AppVersion,
    bool IsActive,
    DateTime RegisteredAt,
    DateTime LastSeenAt);
