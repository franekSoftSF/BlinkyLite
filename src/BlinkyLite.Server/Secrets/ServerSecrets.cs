using BlinkyLite.Server.Auth;
using Npgsql;

namespace BlinkyLite.Server.Secrets;

/// <summary>
/// Reads the server's secrets from the secret store and hands them to the rest
/// of the configuration, so that nothing else has to know where they came from.
/// Outside development it also refuses to start when a secret was written into
/// the configuration itself (D-18): a value "temporarily" pasted into
/// appsettings.json stays there, and ends up in a backup, a ticket and git.
/// </summary>
public static class ServerSecrets
{
    /// <summary>Configuration keys that must never carry a value.</summary>
    private static readonly (string Key, string What)[] Forbidden =
    [
        ($"{JwtOptions.Section}:SigningKey", "the JWT signing key"),
        ($"{LdapOptions.Section}:ServicePassword", "the LDAP service account password"),
    ];

    public static ISecretStore Store(IConfiguration configuration, ILoggerFactory loggers)
    {
        var options = configuration.GetSection(SecretOptions.Section).Get<SecretOptions>() ?? new SecretOptions();
        return new ChainedSecretStore(
            new FileSecretStore(options, loggers.CreateLogger<FileSecretStore>()),
            new EnvironmentSecretStore());
    }

    /// <summary>
    /// The values to lay over the configuration. Empty where a secret is not
    /// configured at all - the component that needs it complains with its own
    /// message.
    /// </summary>
    public static Dictionary<string, string?> Resolve(IConfiguration configuration, ISecretStore store, bool relaxed)
    {
        Refuse(configuration, relaxed);

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        Copy(store, SecretNames.JwtSigningKey, $"{JwtOptions.Section}:SigningKey", overrides);

        if (!string.IsNullOrWhiteSpace(configuration[$"{LdapOptions.Section}:Server"]))
        {
            Copy(store, SecretNames.LdapServicePassword, $"{LdapOptions.Section}:ServicePassword", overrides);
        }

        foreach (var version in KekVersions(configuration))
        {
            Copy(store, SecretNames.Kek(version), $"{SecretOptions.Section}:Keks:{version}", overrides);
        }

        WithPassword(configuration, store, "App", SecretNames.DatabaseAppPassword, overrides);
        WithPassword(configuration, store, "Owner", SecretNames.DatabaseOwnerPassword, overrides);

        return overrides;
    }

    /// <summary>Protects a secret with the machine's DPAPI key, for the Windows installation.</summary>
    public static int Protect(string name, string value, string? directory = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--protect-secret only works on Windows; elsewhere use a Docker secret or a file.");
            return 2;
        }

        var target = directory ?? FileSecretStore.DefaultDirectory;
        Directory.CreateDirectory(target);
        var path = Path.Combine(target, $"blinkylite-{name}{FileSecretStore.ProtectedSuffix}");

        File.WriteAllText(path, Convert.ToBase64String(
            System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(value),
                optionalEntropy: null,
                System.Security.Cryptography.DataProtectionScope.LocalMachine)));

        Console.WriteLine($"wrote {path}");
        Console.WriteLine("Check that only the service account and administrators can read it.");
        return 0;
    }

    private static void Refuse(IConfiguration configuration, bool relaxed)
    {
        if (relaxed)
        {
            return;
        }

        var written = Forbidden
            .Where(f => !string.IsNullOrWhiteSpace(configuration[f.Key]))
            .Select(f => $"{f.Key} ({f.What})")
            .ToList();

        written.AddRange(configuration.GetSection($"{SecretOptions.Section}:Keks").GetChildren()
            .Where(kek => !string.IsNullOrWhiteSpace(kek.Value))
            .Select(kek => $"{SecretOptions.Section}:Keks:{kek.Key} (a KEK)"));

        written.AddRange(configuration.GetSection("ConnectionStrings").GetChildren()
            .Where(entry => HasPassword(entry.Value))
            .Select(entry => $"ConnectionStrings:{entry.Key} (a database password)"));

        if (written.Count > 0)
        {
            throw new SecretConfigurationException(
                "These secrets are written into the configuration: " + string.Join("; ", written) +
                ". Move them to a secret file or an environment variable (docs/04-security.md#sekrety-na-serwerze).");
        }
    }

    private static bool HasPassword(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        try
        {
            return !string.IsNullOrEmpty(new NpgsqlConnectionStringBuilder(connectionString).Password);
        }
        catch (ArgumentException)
        {
            // Not parseable: whoever wrote it will hear about it from Npgsql.
            return false;
        }
    }

    private static IEnumerable<short> KekVersions(IConfiguration configuration)
    {
        var options = configuration.GetSection(SecretOptions.Section).Get<SecretOptions>() ?? new SecretOptions();
        var keks = configuration.GetSection(KekOptions.Section).Get<KekOptions>() ?? new KekOptions();

        return options.KekVersions.Append(keks.CurrentKekVersion).Where(v => v > 0).Distinct();
    }

    private static void Copy(ISecretStore store, string name, string key, Dictionary<string, string?> overrides)
    {
        if (store.Find(name) is { } value)
        {
            overrides[key] = value;
        }
    }

    private static void WithPassword(
        IConfiguration configuration, ISecretStore store, string name, string secret, Dictionary<string, string?> overrides)
    {
        var connectionString = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(connectionString) || store.Find(secret) is not { } password)
        {
            return;
        }

        // The connection string in the configuration carries everything except
        // the password; it is put back together here and nowhere else.
        overrides[$"ConnectionStrings:{name}"] =
            new NpgsqlConnectionStringBuilder(connectionString) { Password = password }.ConnectionString;
    }
}
