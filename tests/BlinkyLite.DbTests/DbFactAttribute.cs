namespace BlinkyLite.DbTests;

/// <summary>
/// A fact that needs a real PostgreSQL. Skipped, with the reason visible in the
/// test output, when <c>BLINKYLITE_TEST_DB</c> is not set - a silent pass would
/// read as "the database works" on a machine that never had one.
/// </summary>
/// <remarks>
/// The connection string is a superuser's: the tests create a throw-away
/// database and run db/init/00_roles.sql in it, as an installer would.
/// </remarks>
public sealed class DbFactAttribute : FactAttribute
{
    public const string Variable = "BLINKYLITE_TEST_DB";

    public DbFactAttribute()
    {
        if (ConnectionString is null)
        {
            Skip = SkipReason;
        }
    }

    public static string SkipReason => $"{Variable} is not set; no PostgreSQL to test against.";

    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value ? value : null;
}

/// <summary>The theory counterpart of <see cref="DbFactAttribute"/>.</summary>
public sealed class DbTheoryAttribute : TheoryAttribute
{
    public DbTheoryAttribute()
    {
        if (DbFactAttribute.ConnectionString is null)
        {
            Skip = DbFactAttribute.SkipReason;
        }
    }
}
