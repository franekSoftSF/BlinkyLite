using System.Text;
using BlinkyLite.Server.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlinkyLite.UnitTests;

public sealed class SecretStoreTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("blinkylite-secrets").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void A_secret_file_is_read_without_the_newline_an_editor_leaves_behind()
    {
        File.WriteAllText(Path.Combine(directory, "blinkylite-jwt-signing-key"), "﻿c2VjcmV0\r\n", Encoding.UTF8);

        Assert.Equal("c2VjcmV0", Store().Find(SecretNames.JwtSigningKey));
    }

    [Fact]
    public void A_file_named_in_the_configuration_wins_over_the_directory()
    {
        var elsewhere = Path.Combine(directory, "somewhere-else");
        File.WriteAllText(Path.Combine(directory, "blinkylite-kek-1"), "from-directory");
        File.WriteAllText(elsewhere, "from-file-list");

        var options = new SecretOptions { Directory = directory, Files = { [SecretNames.Kek(1)] = elsewhere } };

        Assert.Equal("from-file-list", new FileSecretStore(options, NullLogger<FileSecretStore>.Instance).Find(SecretNames.Kek(1)));
    }

    [Fact]
    public void A_file_beats_an_environment_variable_and_the_variable_is_still_a_way_in()
    {
        var store = new ChainedSecretStore(Store(), new EnvironmentSecretStore());
        File.WriteAllText(Path.Combine(directory, "blinkylite-db-app-password"), "from-file");
        Environment.SetEnvironmentVariable("BLINKYLITE_SECRET_DB_APP_PASSWORD", "from-environment");
        Environment.SetEnvironmentVariable("BLINKYLITE_SECRET_DB_OWNER_PASSWORD", "owner-from-environment");
        try
        {
            Assert.Equal("from-file", store.Find(SecretNames.DatabaseAppPassword));
            Assert.Equal("owner-from-environment", store.Find(SecretNames.DatabaseOwnerPassword));
            Assert.Null(store.Find(SecretNames.LdapServicePassword));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLINKYLITE_SECRET_DB_APP_PASSWORD", null);
            Environment.SetEnvironmentVariable("BLINKYLITE_SECRET_DB_OWNER_PASSWORD", null);
        }
    }

    [Fact]
    public void A_missing_secret_says_where_it_was_looked_for()
    {
        var store = new ChainedSecretStore(Store(), new EnvironmentSecretStore());

        var where = store.Describe(SecretNames.JwtSigningKey);

        Assert.Contains(directory, where, StringComparison.Ordinal);
        Assert.Contains("BLINKYLITE_SECRET_JWT_SIGNING_KEY", where, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite, false)]
    // The group is how the database container and the server share one
    // password file; "others" is the hole.
    [InlineData(UnixFileMode.UserRead | UnixFileMode.GroupRead, false)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.OtherRead, true)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.OtherWrite, true)]
    public void A_secret_file_anybody_can_read_is_worth_a_warning(UnixFileMode mode, bool tooOpen) =>
        Assert.Equal(tooOpen, SecretFiles.IsTooOpen(mode));

    [Fact]
    public void Secrets_from_files_are_laid_over_the_configuration()
    {
        File.WriteAllText(Path.Combine(directory, "blinkylite-jwt-signing-key"), "c2lnbmluZy1rZXk=");
        File.WriteAllText(Path.Combine(directory, "blinkylite-kek-2"), "a2Vr");
        File.WriteAllText(Path.Combine(directory, "blinkylite-ldap-service-password"), "ldap-secret");
        File.WriteAllText(Path.Combine(directory, "blinkylite-db-app-password"), "db-secret");

        var configuration = Configuration(
            ("Secrets:Directory", directory),
            ("Secrets:CurrentKekVersion", "2"),
            ("Ldap:Server", "dc01.corp.example"),
            ("ConnectionStrings:App", "Host=db;Database=blinkylite;Username=blinkylite_app"));

        var overrides = ServerSecrets.Resolve(configuration, Store(), relaxed: false);

        Assert.Equal("c2lnbmluZy1rZXk=", overrides["Jwt:SigningKey"]);
        Assert.Equal("a2Vr", overrides["Secrets:Keks:2"]);
        Assert.Equal("ldap-secret", overrides["Ldap:ServicePassword"]);
        Assert.Contains("Password=db-secret", overrides["ConnectionStrings:App"], StringComparison.Ordinal);
        Assert.Contains("Username=blinkylite_app", overrides["ConnectionStrings:App"], StringComparison.Ordinal);
    }

    [Fact]
    public void An_LDAP_password_is_not_fetched_when_there_is_no_directory_to_use_it_on()
    {
        File.WriteAllText(Path.Combine(directory, "blinkylite-ldap-service-password"), "ldap-secret");

        var overrides = ServerSecrets.Resolve(Configuration(("Secrets:Directory", directory)), Store(), relaxed: false);

        Assert.DoesNotContain("Ldap:ServicePassword", overrides.Keys);
    }

    public static TheoryData<string, string> SecretsWrittenDown() => new()
    {
        { "Jwt:SigningKey", "c2VjcmV0" },
        { "Ldap:ServicePassword", "hunter2" },
        { "Secrets:Keks:1", "a2Vr" },
        { "ConnectionStrings:App", "Host=db;Username=blinkylite_app;Password=hunter2" },
    };

    [Theory]
    [MemberData(nameof(SecretsWrittenDown))]
    public void A_secret_written_into_the_configuration_stops_the_server_and_names_the_key(string key, string value)
    {
        var configuration = Configuration((key, value));

        var error = Assert.Throws<SecretConfigurationException>(() => ServerSecrets.Resolve(configuration, Store(), relaxed: false));

        Assert.Contains(key, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SecretsWrittenDown))]
    public void In_development_the_same_configuration_is_allowed(string key, string value) =>
        ServerSecrets.Resolve(Configuration((key, value)), Store(), relaxed: true);

    [Fact]
    public void A_connection_string_without_a_password_is_not_a_secret_in_the_configuration()
    {
        var configuration = Configuration(("ConnectionStrings:App", "Host=db;Database=blinkylite;Username=blinkylite_app"));

        var overrides = ServerSecrets.Resolve(configuration, Store(), relaxed: false);

        Assert.Empty(overrides);
    }

    [WindowsFact]
    public void A_DPAPI_file_written_on_this_machine_reads_back()
    {
        Assert.Equal(0, ServerSecrets.Protect(SecretNames.Kek(1), "a2Vr", directory));

        var path = Path.Combine(directory, "blinkylite-kek-1.dpapi");
        Assert.True(File.Exists(path));
        Assert.DoesNotContain("a2Vr", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal("a2Vr", Store().Find(SecretNames.Kek(1)));
    }

    private FileSecretStore Store() =>
        new(new SecretOptions { Directory = directory }, NullLogger<FileSecretStore>.Instance);

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}

/// <summary>A fact that only means something on Windows (DPAPI).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "DPAPI exists only on Windows.";
        }
    }
}
