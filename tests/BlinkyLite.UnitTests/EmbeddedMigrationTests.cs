using BlinkyLite.Server.Data;

namespace BlinkyLite.UnitTests;

public sealed class EmbeddedMigrationTests
{
    [Fact]
    public void Every_script_in_db_migrations_is_embedded_in_order()
    {
        var onDisk = Directory.EnumerateFiles(Path.Combine(RepositoryConventionTests.Root.FullName, "db", "migrations"), "*.sql")
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal);

        Assert.Equal(onDisk, Migrations.Embedded.Select(m => m.Version));
        Assert.NotEmpty(Migrations.Embedded);
    }

    [Fact]
    public void Checksums_are_taken_over_LF_line_endings()
    {
        // A checkout that turned a script into CRLF has not changed the
        // migration and must not stop the server.
        Assert.All(Migrations.Embedded, m => Assert.DoesNotContain("\r\n", m.Sql, StringComparison.Ordinal));
    }
}
