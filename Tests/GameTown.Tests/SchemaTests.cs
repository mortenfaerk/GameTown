using API.Services;
using System.Net.Http.Json;

namespace GameTown.Tests;

public class SchemaTests
{
    /// <summary>
    /// The migration runner brings a baseline database forward. Without this an operator who
    /// installed an older build could not take a new one.
    /// </summary>
    [Fact]
    public async Task Startup_migrates_a_baseline_database_forward()
    {
        using var app = new GameTownApp();
        var before = app.QueryScalar(@"SELECT MAX(""Version"") FROM ""SchemaVersion""");
        Assert.Equal("1", before);

        // Booting the app is what runs the migrator.
        using var client = app.CreateBrowser();
        await client.GetAsync("/");

        var after = int.Parse(app.QueryScalar(@"SELECT MAX(""Version"") FROM ""SchemaVersion"""));
        Assert.True(after >= 2, $"expected the schema to advance past the baseline, got {after}");
    }

    [Fact]
    public async Task Migrations_are_not_reapplied()
    {
        using var app = new GameTownApp();
        using (var client = app.CreateBrowser()) await client.GetAsync("/");

        var rows = app.QueryScalar(@"SELECT COUNT(*) FROM ""SchemaVersion""");
        var distinct = app.QueryScalar(@"SELECT COUNT(DISTINCT ""Version"") FROM ""SchemaVersion""");

        Assert.Equal(rows, distinct);
    }

    /// <summary>
    /// A database created before schema versioning existed has no SchemaVersion table at all. It must
    /// be adopted as the baseline rather than crashing on startup.
    /// </summary>
    [Fact]
    public async Task A_database_predating_versioning_is_adopted_and_upgraded()
    {
        using var app = new GameTownApp();
        app.QueryScalar(@"DROP TABLE ""SchemaVersion""");

        using var client = app.CreateBrowser();
        await client.GetAsync("/");

        var version = int.Parse(app.QueryScalar(@"SELECT MAX(""Version"") FROM ""SchemaVersion"""));
        Assert.True(version >= 2);
    }

    /// <summary>
    /// The failure this design exists to prevent: the frozen baseline and the numbered migrations
    /// drifting apart. It would only ever show on upgraded installs, never in development — so the
    /// two paths are compared directly.
    /// </summary>
    [Fact]
    public async Task A_fresh_install_and_an_upgraded_install_end_up_identical()
    {
        using var fresh = new GameTownApp();
        using (var client = fresh.CreateBrowser()) await client.GetAsync("/");

        using var upgraded = new GameTownApp();
        upgraded.QueryScalar(@"DROP TABLE ""SchemaVersion""");
        using (var client = upgraded.CreateBrowser()) await client.GetAsync("/");

        const string dump = @"SELECT type||' '||name||' '||COALESCE(sql,'') FROM sqlite_master
                              WHERE name NOT LIKE 'sqlite_%' ORDER BY name";

        Assert.Equal(fresh.QueryScalar(dump), upgraded.QueryScalar(dump));
    }

    /// <summary>
    /// The fresh-install path: an empty data directory, with the application building its own schema.
    ///
    /// Compared against a database created by running the SQL files directly and then migrating —
    /// deliberately not against a second app boot, which would agree with the first even if the
    /// embedded baseline had drifted from <c>Database/sqlite/01_schema.sql</c>. The files on disk are
    /// the reference precisely because they are what the embedded copy is supposed to be.
    /// </summary>
    [Fact]
    public async Task An_empty_data_directory_gets_the_same_schema_as_the_baseline_scripts()
    {
        using var appCreated = GameTownApp.WithEmptyDataDirectory();
        using (var client = appCreated.CreateBrowser()) await client.GetAsync("/");

        using var fromScripts = new GameTownApp();
        using (var client = fromScripts.CreateBrowser()) await client.GetAsync("/");

        const string dump = @"SELECT type||' '||name||' '||COALESCE(sql,'') FROM sqlite_master
                              WHERE name NOT LIKE 'sqlite_%' ORDER BY name";

        Assert.Equal(fromScripts.QueryScalar(dump), appCreated.QueryScalar(dump));
    }

    /// <summary>
    /// The seed is a separate script from the schema, so it is separately forgettable. Without roles
    /// the first-run wizard cannot assign Admin and the install is unusable — while the schema
    /// comparison above would still pass, since roles are rows and not schema.
    /// </summary>
    [Fact]
    public async Task A_fresh_install_seeds_the_roles()
    {
        using var app = GameTownApp.WithEmptyDataDirectory();
        using (var client = app.CreateBrowser()) await client.GetAsync("/");

        Assert.Equal("Admin\nContributor",
            app.QueryScalar(@"SELECT ""Role"" FROM ""GameTownRoles"" ORDER BY ""Role"""));
    }

    /// <summary>
    /// Restarting a freshly installed appliance must not rebuild anything. The baseline uses bare
    /// CREATE TABLE, so a re-run would throw rather than corrupt — but it would still take the
    /// service down on every restart.
    /// </summary>
    [Fact]
    public async Task A_restart_after_a_fresh_install_changes_nothing()
    {
        using var app = GameTownApp.WithEmptyDataDirectory();
        using (var client = app.CreateBrowser()) await client.GetAsync("/");

        const string state = @"SELECT (SELECT COUNT(*) FROM ""SchemaVersion"")
                               || '/' || (SELECT MAX(""Version"") FROM ""SchemaVersion"")
                               || '/' || (SELECT COUNT(*) FROM ""GameTownRoles"")";
        var before = app.QueryScalar(state);

        // A restart is exactly this: the migrator running again over the same file.
        API.Startup.SchemaMigrator.ApplyMigrations(
            API.Startup.SqliteConnectionString.WithRequiredPragmas($"Data Source={app.DatabasePath}"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Equal(before, app.QueryScalar(state));
    }

    /// <summary>
    /// Foreign keys are OFF per connection by default in SQLite, which would make every FOREIGN KEY
    /// in the schema decorative and let orphan rows insert happily.
    ///
    /// Asserted on a connection built the way the application builds its own. An earlier version of
    /// this test shelled out to the sqlite3 CLI and passed vacuously: that client does not enable the
    /// pragma either, so the orphan insert it attempted succeeded and proved nothing about the app.
    /// </summary>
    [Fact]
    public void Foreign_keys_are_enforced_on_the_applications_connection()
    {
        using var app = new GameTownApp();
        var connectionString = API.Startup.SqliteConnectionString.WithRequiredPragmas(
            $"Data Source={app.DatabasePath}");

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys";
        Assert.Equal(1L, Convert.ToInt64(pragma.ExecuteScalar()));

        using var orphan = connection.CreateCommand();
        orphan.CommandText = @"INSERT INTO ""GameTownUsers_Roles"" (""APIUserId"",""APIRoleId"")
                               VALUES ('00000000-0000-0000-0000-000000000001',
                                       '00000000-0000-0000-0000-000000000002')";
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => orphan.ExecuteNonQuery());
    }
}

public class SettingsServiceUnitTests
{
    [Theory]
    [InlineData("ZIP", ".zip")]
    [InlineData(".7z", ".7z")]
    [InlineData("  .RAR  ", ".rar")]
    public void File_types_are_normalised_to_lowercase_with_a_leading_dot(string input, string expected)
        => Assert.Equal([expected], SettingsService.ParseFileTypes(input));

    [Fact]
    public void Duplicates_and_blanks_are_dropped()
        => Assert.Equal([".zip", ".7z"], SettingsService.ParseFileTypes("zip, .ZIP , ,7z"));
}

/// <summary>
/// The containment check that stands between a stored path and the filesystem. It was deliberately
/// kept a pure static so it could be tested without a database or a request — this is the security
/// control that turned an arbitrary file read and an arbitrary file delete back into a 404.
/// </summary>
public class FileContainmentTests
{
    [Theory]
    [InlineData("game.zip", true)]
    [InlineData("../../../etc/passwd", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_paths_inside_the_archive_directory_resolve(string? stored, bool expected)
    {
        var (resolved, _) = FileService.TryResolveWithin("/srv/gametown/games", stored);
        Assert.Equal(expected, resolved);
    }

    /// <summary>A sibling directory sharing a prefix must not pass as a child.</summary>
    [Fact]
    public void A_prefix_sibling_is_not_inside_the_directory()
    {
        var (resolved, _) = FileService.TryResolveWithin("/srv/games", "/srv/games-secret/x.zip");
        Assert.False(resolved);
    }

    [Fact]
    public void A_trailing_separator_on_the_root_still_matches_children()
    {
        var (resolved, full) = FileService.TryResolveWithin("/srv/games/", "x.zip");
        Assert.True(resolved);
        Assert.Equal("/srv/games/x.zip", full);
    }
}
