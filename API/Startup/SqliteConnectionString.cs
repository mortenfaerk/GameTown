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

        try
        {
            var b = new SqliteConnectionStringBuilder(connectionString) { ForeignKeys = true };
            return b.ToString();
        }
        catch (ArgumentException ex)
        {
            // The overwhelmingly likely cause is a connection string left over from the PostgreSQL
            // era — GameTown was database-first on Npgsql before it moved to SQLite. SqliteConnection-
            // StringBuilder rejects `Host=`/`Port=`/`Username=` with a keyword error that gives no
            // hint of that, and it happens at startup, so the process just aborts (exit 134). Name the
            // real cause instead.
            var looksLikePostgres = connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase)
                                 || connectionString.Contains("Username=", StringComparison.OrdinalIgnoreCase);
            var hint = looksLikePostgres
                ? " This looks like a PostgreSQL connection string. GameTown uses SQLite now — set " +
                  "ConnectionStrings:DefaultConnection to \"Data Source=/path/to/gametown.db\" (in " +
                  "development: dotnet user-secrets set \"ConnectionStrings:DefaultConnection\" " +
                  "\"Data Source=$HOME/gametown/gametown.db\" --project API)."
                : string.Empty;

            throw new InvalidOperationException(
                $"The configured connection string is not a valid SQLite one.{hint}", ex);
        }
    }

    /// <summary>
    /// The directory holding the database file — the application's data directory by definition.
    ///
    /// Everything that must outlive an upgrade belongs here rather than in the application folder,
    /// which a fresh install overwrites: the database, the Data Protection keyring, uploaded
    /// archives and re-hosted media.
    /// </summary>
    public static string GetDataDirectory(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));

        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException(
                $"Could not determine a data directory from data source \"{dataSource}\".");

        return directory;
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
