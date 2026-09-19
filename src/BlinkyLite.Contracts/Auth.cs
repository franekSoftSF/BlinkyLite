namespace BlinkyLite.Contracts;

/// <summary>Body of <c>POST /api/auth/login</c>. The username may be <c>jkowalski</c>, <c>CORP\jkowalski</c> or a UPN.</summary>
public sealed record LoginRequest(string Username, string Password);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, CurrentUser User);

/// <summary>Who the token belongs to; <c>GET /api/auth/me</c> returns the same.</summary>
public sealed record CurrentUser(string Upn, string Sid, string DisplayName, IReadOnlyList<Role> Roles);

/// <summary>A person found in AD who can receive a key.</summary>
/// <param name="SamAccount"><c>DOMAIN\sAMAccountName</c> - exactly what goes into the CMC RequesterName.</param>
public sealed record DirectoryUser(string SamAccount, string Upn, string Sid, string DisplayName, bool Enabled);
