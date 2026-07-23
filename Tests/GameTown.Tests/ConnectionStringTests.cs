using API.Startup;

namespace GameTown.Tests;

public class ConnectionStringTests
{
    [Fact]
    public void A_valid_sqlite_string_gains_the_foreign_keys_pragma()
    {
        var result = SqliteConnectionString.WithRequiredPragmas("Data Source=/tmp/x.db");

        Assert.Contains("Foreign Keys=True", result);
        Assert.Contains("/tmp/x.db", result);
    }

    [Fact]
    public void A_blank_string_is_rejected_with_an_explanation()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SqliteConnectionString.WithRequiredPragmas("   "));

        Assert.Contains("Data Source", ex.Message);
    }

    /// <summary>
    /// A connection string left over from the PostgreSQL era is the predictable failure for anyone
    /// upgrading an existing checkout. It used to surface as a raw keyword ArgumentException at
    /// startup — the process just aborted with exit 134 and no hint of the cause.
    /// </summary>
    [Fact]
    public void A_postgres_string_is_rejected_by_naming_the_actual_problem()
    {
        var postgres = "Host=192.168.1.10;Port=5432;Database=gametown;Username=u;Password=p";

        var ex = Assert.Throws<InvalidOperationException>(
            () => SqliteConnectionString.WithRequiredPragmas(postgres));

        Assert.Contains("PostgreSQL", ex.Message);
        Assert.Contains("SQLite", ex.Message);
    }

    [Fact]
    public void The_data_directory_is_the_folder_holding_the_database()
    {
        var directory = SqliteConnectionString.GetDataDirectory("Data Source=/var/lib/gametown/gametown.db");

        Assert.Equal(Path.GetFullPath("/var/lib/gametown"), directory);
    }
}
