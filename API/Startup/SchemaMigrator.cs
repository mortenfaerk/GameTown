using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Text.RegularExpressions;

namespace API.Startup;

/// <summary>
/// Creates the database if it is empty, then applies numbered SQL migrations — at startup, before
/// anything serves a request.
///
/// This exists because GameTown is installed by other people now: someone who installed last month
/// must be able to take a new build without losing their library. Before this there was no path at
/// all from an installed schema to a newer one.
///
/// Deliberately not EF Core Migrations. The model is scaffolded database-first from hand-written DDL
/// — EF migrations would want to own the schema and fight that workflow rather than help it.
///
/// Both the baseline and the migrations are **embedded resources**, not files on disk, so they
/// cannot go missing from a self-contained publish or be edited in place on a running install. That
/// is what lets the installer ship a single tarball: no source checkout, and no sqlite3 CLI.
///
/// A fresh install runs baseline → 002 → …, which is the same sequence an existing install takes.
/// Keeping the baseline frozen and replaying migrations over it is what stops the two from drifting;
/// the alternative — maintaining a "current" baseline *and* migrations — fails only on upgraded
/// installs, never in development.
/// </summary>
public static partial class SchemaMigrator
{
    [GeneratedRegex(@"\.(\d+)_[^.]*\.sql$")]
    private static partial Regex MigrationName();

    /// <summary>
    /// The frozen baseline. Named without an <c>NN_</c> segment on purpose — see the LogicalName
    /// comment in API.csproj — because <see cref="MigrationName"/> would otherwise discover these
    /// two as migrations 1 and 2.
    /// </summary>
    private const string BaselineSchemaResource = "API.Baseline.schema.sql";
    private const string BaselineSeedResource = "API.Baseline.seed.sql";

    /// <summary>The version <c>01_schema.sql</c> stamps for itself.</summary>
    private const int BaselineVersion = 1;

    public static void ApplyMigrations(string connectionString, ILogger logger)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        CreateBaselineIfEmpty(connection, logger);

        var current = GetCurrentVersion(connection);
        var pending = DiscoverMigrations().Where(m => m.Version > current).OrderBy(m => m.Version).ToList();

        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is at version {Version}; nothing to apply.", current);
            return;
        }

        logger.LogInformation("Database schema is at version {Current}; applying {Count} migration(s).",
            current, pending.Count);

        foreach (var migration in pending)
        {
            // Each script and its version row commit together. A failure therefore leaves the
            // database at the previous version rather than half-migrated — which is the difference
            // between "run the upgrade again" and "restore from backup".
            using var transaction = connection.BeginTransaction();
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = migration.Sql;
                    command.ExecuteNonQuery();
                }

                using (var stamp = connection.CreateCommand())
                {
                    stamp.Transaction = transaction;
                    stamp.CommandText = @"INSERT INTO ""SchemaVersion"" (""Version"") VALUES ($v)";
                    stamp.Parameters.AddWithValue("$v", migration.Version);
                    stamp.ExecuteNonQuery();
                }

                transaction.Commit();
                logger.LogInformation("Applied schema migration {Version} ({Name}).",
                    migration.Version, migration.Name);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                // Fail loudly and stop. Serving requests against a schema the code does not expect
                // is worse than not starting: it corrupts data instead of reporting a problem.
                throw new InvalidOperationException(
                    $"Schema migration {migration.Version} ({migration.Name}) failed. The database is " +
                    $"unchanged, at version {migration.Version - 1}. Restore the backup taken by the " +
                    $"installer if needed.", ex);
            }
        }
    }

    /// <summary>
    /// Builds the schema when the database has no tables at all.
    ///
    /// Point the application at a path that does not exist yet and it creates its own database —
    /// which is what removes the installer's need for a source checkout and for sqlite3. Previously
    /// that case wedged: SQLite auto-creates an empty file, <see cref="GetCurrentVersion"/> read the
    /// missing SchemaVersion table as a pre-versioning install and stamped it version 1, and the
    /// first migration then failed on tables that had never been created.
    ///
    /// So "no SchemaVersion table" means two different things, and they are separated here rather
    /// than in GetCurrentVersion: an empty file is a *fresh install*, while a file that already has
    /// tables is a database from *before versioning existed*. Only the second is adopted as the
    /// baseline; the first has the baseline actually applied to it.
    /// </summary>
    private static void CreateBaselineIfEmpty(SqliteConnection connection, ILogger logger)
    {
        if (CountUserTables(connection) > 0) return;

        logger.LogInformation("Database is empty; creating the baseline schema.");

        // Deliberately NOT wrapped in a transaction the way the numbered migrations are: both scripts
        // open and close their own (01_schema.sql BEGIN;…COMMIT;), and SQLite has no nested
        // transactions — beginning one here would throw before a single table was created.
        Execute(connection, ReadResource(BaselineSchemaResource));
        Execute(connection, ReadResource(BaselineSeedResource));

        // The baseline stamps its own version row. Verified rather than written, so that if the DDL
        // and this file ever disagree about the baseline version it fails loudly here, instead of
        // silently skipping or replaying a migration later.
        var version = GetCurrentVersion(connection);
        if (version != BaselineVersion)
            throw new InvalidOperationException(
                $"The baseline schema should leave a new database at version {BaselineVersion}, but " +
                $"it reports {version}. Database/sqlite/01_schema.sql and SchemaMigrator disagree.");

        logger.LogInformation("Baseline schema created at version {Version}.", version);
    }

    private static int CountUserTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string ReadResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"The embedded resource '{name}' is missing. It is declared with an explicit " +
                "LogicalName in API.csproj; changing that without changing SchemaMigrator would only " +
                "surface on a fresh install.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static int GetCurrentVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            @"SELECT COALESCE(MAX(""Version""), 0) FROM ""SchemaVersion""";
        try
        {
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch (SqliteException)
        {
            // No SchemaVersion table on a database that *has* tables: it predates versioning. Adopt
            // it as the baseline so the numbered migrations bring it forward. An empty database never
            // reaches this point — CreateBaselineIfEmpty has already built and stamped it.
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS "SchemaVersion" (
                    "Version"   INTEGER  NOT NULL PRIMARY KEY,
                    "AppliedAt" datetime NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
                INSERT INTO "SchemaVersion" ("Version") VALUES (1);
                """;
            create.ExecuteNonQuery();
            return 1;
        }
    }

    private static List<(int Version, string Name, string Sql)> DiscoverMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var migrations = new List<(int, string, string)>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = MigrationName().Match(resource);
            if (!match.Success) continue;

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            migrations.Add((int.Parse(match.Groups[1].Value), resource, reader.ReadToEnd()));
        }

        return migrations;
    }
}
