using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace BlinkyLite.Server.Secrets;

/// <summary>A secret was configured where it must never be written down.</summary>
public sealed class SecretConfigurationException(string message) : Exception(message);

/// <summary>Where the server's secrets come from; never from appsettings.json (D-18).</summary>
public interface ISecretStore
{
    /// <summary>The secret's value, or null if this store does not have it.</summary>
    string? Find(string name);

    /// <summary>Where this store looked, for an error message a person can act on.</summary>
    string Describe(string name);
}

public sealed class SecretOptions
{
    public const string Section = "Secrets";

    /// <summary>
    /// Directory with one file per secret, named <c>blinkylite-&lt;name&gt;</c>
    /// (optionally <c>.dpapi</c>). Docker mounts its secrets at /run/secrets;
    /// on Windows the installer writes them into ProgramData.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>Individual overrides: secret name to file path.</summary>
    public Dictionary<string, string> Files { get; set; } = [];

    /// <summary>KEK versions to look for; the current one is always included.</summary>
    public short[] KekVersions { get; set; } = [];
}

/// <summary>The names the server asks for.</summary>
public static class SecretNames
{
    public const string JwtSigningKey = "jwt-signing-key";
    public const string LdapServicePassword = "ldap-service-password";
    public const string DatabaseAppPassword = "db-app-password";
    public const string DatabaseOwnerPassword = "db-owner-password";

    public static string Kek(short version) => $"kek-{version}";
}

/// <summary>
/// One file per secret. A file whose name ends in <c>.dpapi</c> is unprotected
/// with the machine key - the scope the Windows service runs under, and the
/// one that survives a change of service account (the user scope does not).
/// </summary>
public sealed class FileSecretStore(SecretOptions options, ILogger<FileSecretStore> logger) : ISecretStore
{
    public const string ProtectedSuffix = ".dpapi";

    public static string DefaultDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BlinkyLite", "secrets")
        : "/run/secrets";

    public string? Find(string name)
    {
        foreach (var path in Candidates(name))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            WarnIfReadableByOthers(path);
            return path.EndsWith(ProtectedSuffix, StringComparison.OrdinalIgnoreCase)
                ? Unprotect(path)
                : Clean(File.ReadAllText(path, Encoding.UTF8));
        }

        return null;
    }

    public string Describe(string name) => string.Join(", ", Candidates(name));

    private IEnumerable<string> Candidates(string name)
    {
        if (options.Files.TryGetValue(name, out var configured))
        {
            yield return configured;
            yield break;
        }

        var directory = options.Directory ?? DefaultDirectory;
        yield return Path.Combine(directory, $"blinkylite-{name}{ProtectedSuffix}");
        yield return Path.Combine(directory, $"blinkylite-{name}");
    }

    private void WarnIfReadableByOthers(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // The ACL is set and checked by the installer (0051); .NET has no
            // ACL API in a cross-platform target to check it here.
            return;
        }

        var mode = File.GetUnixFileMode(path);
        if (SecretFiles.IsTooOpen(mode))
        {
            logger.LogWarning("Secret file {Path} is readable by others ({Mode}); 0600 is what it should be.", path, mode);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string UnprotectOnWindows(string path)
    {
        var protectedBytes = Convert.FromBase64String(File.ReadAllText(path, Encoding.UTF8).Trim());
        var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Unprotect(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new SecretConfigurationException($"{path} is a DPAPI file, which only Windows can read.");
        }

        return UnprotectOnWindows(path);
    }

    /// <summary>A file written by an editor ends with a newline; a secret does not contain one.</summary>
    private static string Clean(string value) => value.Trim('﻿', '\r', '\n', ' ', '\t');
}

/// <summary>Environment variables, for people who prefer them to files.</summary>
public sealed class EnvironmentSecretStore : ISecretStore
{
    public string? Find(string name) =>
        Environment.GetEnvironmentVariable(Variable(name)) is { Length: > 0 } value ? value.Trim() : null;

    public string Describe(string name) => $"environment variable {Variable(name)}";

    private static string Variable(string name) => $"BLINKYLITE_SECRET_{name.Replace('-', '_').ToUpperInvariant()}";
}

/// <summary>Files first, then environment variables.</summary>
public sealed class ChainedSecretStore(params ISecretStore[] stores) : ISecretStore
{
    public string? Find(string name) => stores.Select(store => store.Find(name)).FirstOrDefault(value => value is not null);

    public string Describe(string name) => string.Join(", ", stores.Select(store => store.Describe(name)));
}

public static class SecretFiles
{
    /// <summary>True if anybody but the owner can read the file.</summary>
    public static bool IsTooOpen(UnixFileMode mode) =>
        (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0;
}
