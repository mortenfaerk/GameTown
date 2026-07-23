using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Text.RegularExpressions;

namespace API.Startup;

/// <summary>
/// Applies numbered SQL migrations at startup.
///
/// This exists because GameTown is installed by other people now: someone who installed last month
/// must be able to take a new build without losing their library. Before this there was no path at
/// all from an installed schema to a newer one.
///
/// Deliberately not EF Core Migrations. The model is scaffolded database-first from hand-written DDL
/// — EF migrations would want to own the schema and fight that workflow rather than help it.
///
/// The scripts are **embedded resources**, not files on disk, so they cannot go missing from a
/// self-contained publish or be edited in place on a running install.
/// </summary>
public static partial class SchemaMigrator
{
    [GeneratedRegex(@"\.(\d+)_[^.]*\.sql$")]
    private static partial Regex MigrationName();

    public static void ApplyMigrations(string connectionString, ILogger logger)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

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
            // No SchemaVersion table: a database created before versioning existed. Treat it as the
            // baseline so the numbered migrations bring it forward.
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
