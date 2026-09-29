using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlinkyLite.Ui.Configuration;
using Serilog;

namespace BlinkyLite.Unlock;

/// <summary>
/// The one thing this window remembers: the server's address, for the
/// telephone mode (0057). The language and the theme follow Windows, as
/// before - there is nobody signed in here whose preference this could be.
/// </summary>
/// <remarks>
/// <para>
/// Three places, in this order: a Group Policy value
/// (<see cref="ManagedSettings"/>), then the person's own profile with
/// whatever they last typed, then a file under <c>%ProgramData%</c> that
/// whoever deploys the station writes once. The first two exist because a
/// person with a blocked PIN should not have to know the address of anything.
/// </para>
/// <para>
/// The policy wins, and then the window stops asking: an address an
/// administrator set is not a suggestion, and it decides where a request to
/// unblock a key is sent.
/// </para>
/// <para>
/// No token, no PUK, no PIN, ever. There is nothing else in here to be
/// tempted by.
/// </para>
/// </remarks>
/// <param name="FromPolicy">
/// The address came from Group Policy, so the window shows it and refuses to
/// let anybody change it.
/// </param>
public sealed record UnlockSettings(string? Server = null, [property: JsonIgnore] bool FromPolicy = false)
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
        if (ManagedSettings.Server is { } managed)
        {
            Log.Information("Adres serwera z polityki ({Scope})", managed.Scope);

            return new UnlockSettings(managed.Value, FromPolicy: true);
        }

        var machine = Read(MachinePath);
        var user = Read(UserPath);

        // The person's own address wins over the deployed file, because they
        // are the one standing here; neither wins over a policy.
        return new UnlockSettings(user.Server ?? machine.Server);
    }

    public void Save()
    {
        if (FromPolicy)
        {
            // Nothing to remember: the policy says it again at every start.
            return;
        }

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
