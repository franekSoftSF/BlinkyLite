namespace BlinkyLite.Contracts;

/// <summary>Body of <c>GET /health</c>; the only endpoint that needs no token.</summary>
public sealed record HealthResponse(string Status, string Version);
