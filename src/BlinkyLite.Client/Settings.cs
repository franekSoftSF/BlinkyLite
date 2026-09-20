using System.IO;
using System.Text.Json;
using Serilog;

namespace BlinkyLite.Client;

/// <summary>
/// What the client remembers between runs.
/// </summary>
/// <remarks>
/// <para>
/// The address, the operator's own login, the language and the theme - the
/// four things somebody would otherwise retype every morning. The first
/// operator to use this client typed the address without its port and was told
/// the server had a problem; a remembered address cannot make that mistake
/// twice.
/// </para>
/// <para>
/// <b>Never a password and never a token.</b> The token lives in the process
/// for thirty minutes and dies with it; a password does not belong in a file
/// this client could write, on a workstation, under the operator's profile.
/// The username is kept because it is not a secret and is on the screen
/// anyway.
/// </para>
/// </remarks>
public sealed record ClientSettings(
    string? Server = null,
    string? Username = null,
    string? Language = null,
    string? Theme = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>Under the operator's roaming profile: no installer, no rights, follows them between machines.</summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BlinkyLite",
        "client.json");

    public static ClientSettings Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(Path), Json) ?? new ClientSettings()
                : new ClientSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A settings file nobody can read is a convenience that failed,
            // not a reason to refuse to start.
            Log.Warning(e, "Nie da sie odczytac {Path}; zaczynam od pustych ustawien", Path);

            return new ClientSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Nie da sie zapisac {Path}", Path);
        }
    }
}
