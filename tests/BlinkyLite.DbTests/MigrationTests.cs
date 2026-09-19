using BlinkyLite.Server.Data;
using Npgsql;

namespace BlinkyLite.DbTests;

[Collection(DatabaseCollection.Name)]
public sealed class MigrationTests(DatabaseFixture db)
{
    [DbFact]
    public async Task Migrations_apply_to_an_empty_database_and_a_second_run_does_nothing()
    {
        var (_, owner, app, _) = await db.CreateDatabaseAsync();
        await using var ownerSource = NpgsqlDataSource.Create(owner);
        await using var appSource = NpgsqlDataSource.Create(app);

        await Assert.ThrowsAsync<MigrationException>(() => Migrations.VerifyAsync(appSource));

        var first = await Migrations.ApplyAsync(ownerSource, _ => { });
        var second = await Migrations.ApplyAsync(ownerSource, _ => { });

        Assert.Equal(Migrations.Embedded.Count, first);
        Assert.Equal(0, second);
        await Migrations.VerifyAsync(appSource);
    }

    [DbFact]
    public async Task An_applied_migration_that_changed_stops_the_server_with_its_name()
    {
        var (_, owner, app, _) = await db.CreateDatabaseAsync();
        await using var ownerSource = NpgsqlDataSource.Create(owner);
        await using var appSource = NpgsqlDataSource.Create(app);
        await Migrations.ApplyAsync(ownerSource, _ => { });

        await using (var command = ownerSource.CreateCommand(
            "update blinkylite.schema_migrations set sha256 = 'edited' where version = '0002_functions_issuance'"))
        {
            await command.ExecuteNonQueryAsync();
        }

        var verify = await Assert.ThrowsAsync<MigrationException>(() => Migrations.VerifyAsync(appSource));
        var apply = await Assert.ThrowsAsync<MigrationException>(() => Migrations.ApplyAsync(ownerSource, _ => { }));

        Assert.Contains("0002_functions_issuance", verify.Message);
        Assert.Contains("0002_functions_issuance", apply.Message);
    }

    [DbFact]
    public async Task A_database_migrated_by_a_newer_server_is_refused()
    {
        var (_, owner, app, _) = await db.CreateDatabaseAsync();
        await using var ownerSource = NpgsqlDataSource.Create(owner);
        await using var appSource = NpgsqlDataSource.Create(app);
        await Migrations.ApplyAsync(ownerSource, _ => { });

        await using (var command = ownerSource.CreateCommand(
            "insert into blinkylite.schema_migrations (version, sha256) values ('9999_future', 'x')"))
        {
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<MigrationException>(() => Migrations.VerifyAsync(appSource));
        Assert.Contains("9999_future", error.Message);
    }

    [DbFact]
    public async Task Migrations_refuse_to_run_as_anyone_but_the_owner()
    {
        var (superuser, _, _, _) = await db.CreateDatabaseAsync();
        await using var source = NpgsqlDataSource.Create(superuser);

        var error = await Assert.ThrowsAsync<MigrationException>(() => Migrations.ApplyAsync(source, _ => { }));

        Assert.Contains(Migrations.OwnerRole, error.Message);
        await using var command = source.CreateCommand("select to_regclass('blinkylite.issuances') is null");
        Assert.Equal(true, await command.ExecuteScalarAsync());
    }
}
