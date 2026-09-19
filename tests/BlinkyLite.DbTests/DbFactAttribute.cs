namespace BlinkyLite.DbTests;

/// <summary>
/// A fact that needs a real PostgreSQL. Skipped, with the reason visible in the
/// test output, when <c>BLINKYLITE_TEST_DB</c> is not set - a silent pass would
/// read as "the database works" on a machine that never had one.
/// </summary>
public sealed class DbFactAttribute : FactAttribute
{
    public const string Variable = "BLINKYLITE_TEST_DB";

    public DbFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            Skip = $"{Variable} is not set; no PostgreSQL to test against.";
        }
    }

    public static string? ConnectionString => Environment.GetEnvironmentVariable(Variable);
}
