using Microsoft.Data.Sqlite;

namespace API.Startup;

/// <summary>
/// Connection-string and pragma handling for SQLite.
///
/// Both settings here compensate for a SQLite default that fails *silently* rather than loudly,
/// which is why they are centralised instead of being left to whoever writes the connection string.
/// </summary>
public static class SqliteConnectionString
{
    /// <summary>
    /// Forces `Foreign Keys=True` onto a connection string, preserving everything else.
    ///
    /// SQLite disables foreign-key enforcement per connection by default. Without this every
    /// FOREIGN KEY in Database/sqlite/01_schema.sql is decorative — orphan rows insert happily and
    /// the ON DELETE CASCADE on RefreshTokens never fires.
    /// </summary>
    public static string WithRequiredPragmas(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "No database connection string configured. Expected something like " +
                "\"Data Source=/var/lib/gametown/gametown.db\".");

        var b = new SqliteConnectionStringBuilder(connectionString) { ForeignKeys = true };
        return b.ToString();
    }

    /// <summary>
    /// Switches the database file into WAL journalling.
    ///
    /// Unlike foreign keys this is stored in the database file itself, so it survives restarts and
    /// only needs applying once — but applying it on every startup is harmless and means a database
    /// restored from a plain copy is not left in rollback-journal mode by accident.
    /// </summary>
    public static void EnableWriteAheadLogging(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();
    }
}
