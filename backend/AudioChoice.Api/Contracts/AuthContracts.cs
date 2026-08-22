namespace AudioChoice.Api.Contracts;

public sealed record RegisterRequest(string Email, string Password, string? DisplayName);
public sealed record LoginRequest(string Email, string Password);
public sealed record PasswordResetRequest(string Email);
public sealed record PasswordResetConfirmRequest(string Token, string NewPassword);
public sealed record ExternalLoginRequest(string Provider, string AuthorizationCode, string? IdentityToken, string? DisplayName);
public sealed record LinkedIdentitiesResponse(IReadOnlyList<string> Providers);
public sealed record AuthUser(Guid ID, string Email, string DisplayName, string Provider);
public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, AuthUser User);
public sealed record AuthActionResponse(string Status);
