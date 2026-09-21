using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Serilog;

namespace BlinkyLite.Client;

/// <summary>
/// Puts the PowerShell module where pwsh finds it, for the operator who runs
/// the client (0052, D-35).
/// </summary>
/// <remarks>
/// <para>
/// The MSIX carries the module in <c>Modules\BlinkyLite</c>, but an MSIX has no
/// install-time actions and cannot write to Program Files - it is isolated by
/// design. So the client copies it on start into the user's own
/// <c>Documents\PowerShell\Modules\BlinkyLite\&lt;version&gt;</c>, which pwsh 7
/// searches without anybody touching <c>PSModulePath</c>. Documents is not
/// redirected for a packaged app, unlike AppData: the copy lands where pwsh
/// looks.
/// </para>
/// <para>
/// A folder per version, because PowerShell insists on it and because a
/// running pwsh holds the old DLL open: the new version goes next to it
/// instead of failing to overwrite it.
/// </para>
/// </remarks>
public static class ModuleInstaller
{
    public const string ModuleName = "BlinkyLite";

    public static string Source => Path.Combine(AppContext.BaseDirectory, "Modules", ModuleName);

    public static string UserModules => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PowerShell", "Modules", ModuleName);

    /// <summary>Copies the module when it is missing or different. Never throws: the client starts either way.</summary>
    public static void EnsureInstalled()
    {
        try
        {
            var manifest = Path.Combine(Source, $"{ModuleName}.psd1");

            // Not an installed package (a developer build, a zip from the share):
            // there is nothing to install from.
            if (!File.Exists(manifest))
            {
                return;
            }

            var version = VersionOf(File.ReadAllText(manifest));
            var target = Path.Combine(UserModules, version);

            if (SameFiles(Source, target))
            {
                return;
            }

            Copy(Source, target);
            Log.Information("PowerShell module {Version} installed for this user in {Target}", version, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            // Most often: pwsh has this very version loaded right now.
            Log.Warning(e, "PowerShell module could not be installed for this user");
        }
    }

    public static string VersionOf(string psd1)
    {
        var match = Regex.Match(psd1, @"ModuleVersion\s*=\s*'(?<v>[0-9.]+)'");
        return match.Success ? match.Groups["v"].Value : throw new FormatException("BlinkyLite.psd1 has no ModuleVersion.");
    }

    // Compared by content, not by date: an MSIX sets its own timestamps.
    private static bool SameFiles(string source, string target)
    {
        if (!Directory.Exists(target))
        {
            return false;
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(source, file));
            if (!File.Exists(copy) || !Hash(file).AsSpan().SequenceEqual(Hash(copy)))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private static void Copy(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, overwrite: true);
        }
    }
}
