using System.Text.Json.Serialization;

namespace BlinkyLite.Contracts;

/// <summary>A role carried in the JWT, granted by membership of an AD group.</summary>
/// <remarks>Serialised by name: a number on the wire would silently change meaning if the enum were reordered.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<Role>))]
public enum Role
{
    Admin,
    SecurityOfficer,
    Helpdesk,
}
