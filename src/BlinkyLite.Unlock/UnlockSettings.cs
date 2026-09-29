using System.IO;
using System.Text.Json;
using Serilog;

namespace BlinkyLite.Unlock;

/// <summary>
/// The one thing this window remembers: the server's address, for the
/// telephone mode (0057). The language and the theme follow Windows, as
/// before - there is nobody signed in here whose preference this could be.
/// </summary>
/// <remarks>
/// <para>
/// Two files, in this order: one under <c>%ProgramData%</c> that whoever
/// deploys the station writes once, and one under the person's own profile
/// with whatever they last typed. The machine-wide one is the point - a person
/// with a blocked PIN should not have to know the address of anything.
/// </para>
/// <para>
/// No token, no PUK, no PIN, ever. There is nothing else in here to be
/// tempted by.
/// </para>
/// </remarks>
public sealed record UnlockSettings(string? Server = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>Written by whoever deploys the station; read-only as far as this window is concerned.</summary>
    public static string MachinePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BlinkyLite",
        "unlock.json");

    public static string UserPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BlinkyLite",
        "unlock.json");

    public static UnlockSettings Load()
    {
        var machine = Read(MachinePath);
        var user = Read(UserPath);

        // The person's own address wins, because they are the one standing
        // here; the machine's is what they see when they have never typed one.
        return new UnlockSettings(user.Server ?? machine.Server);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserPath)!);
            File.WriteAllText(UserPath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Nie da sie zapisac {Path}", UserPath);
        }
    }

    private static UnlockSettings Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<UnlockSettings>(File.ReadAllText(path), Json) ?? new UnlockSettings()
                : new UnlockSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Nie da sie odczytac {Path}", path);

            return new UnlockSettings();
        }
    }
}
