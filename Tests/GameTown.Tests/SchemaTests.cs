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
    /// The question every schema change has to answer: does a real, populated, already-deployed
    /// install survive the upgrade?
    ///
    /// Set up as a v0.1.1 appliance actually is — schema at version 2, with a library in it — rather
    /// than as a fresh test database, because "the migration runs" and "the migration runs without
    /// destroying anything" are different claims and only the second one matters to an operator.
    /// Data is asserted intact field by field, not just counted.
    ///
    /// Deliberately left at version 2 as later migrations are added, rather than moved forward to
    /// the newest one. An install that skipped two releases replays the whole chain in one boot, and
    /// that is the run most likely to go wrong — the version this starts from is the oldest still in
    /// the field, not the most recent.
    /// </summary>
    [Fact]
    public async Task A_populated_install_at_version_2_upgrades_without_losing_its_library()
    {
        using var app = new GameTownApp();

        // Bring the database to exactly version 2: the state of an install running the last release.
        app.RunSql(GameTownApp.SchemaFile(Path.Combine("migrations", "002_game_title_index.sql")));
        app.QueryScalar(@"INSERT INTO ""SchemaVersion"" (""Version"") VALUES (2)");

        // A library that predates the new column. GUID literal uppercase — EF writes them that way
        // and SQLite compares TEXT binary.
        app.QueryScalar("""
            INSERT INTO "GameTownGame" ("Id","Title","HowTo","RAWGGameId","URL","Size")
            VALUES ('11111111-1111-1111-1111-111111111111','Existing Game','Unzip it',NULL,
                    '/var/lib/gametown/games/abc.zip', 1234.5)
            """);

        Assert.Equal("2", app.QueryScalar(@"SELECT MAX(""Version"") FROM ""SchemaVersion"""));

        // Booting the new build is the upgrade.
        using (var client = app.CreateBrowser()) await client.GetAsync("/");

        // Every migration ran, not just the next one. An install can be several releases behind.
        Assert.Equal("7", app.QueryScalar(@"SELECT MAX(""Version"") FROM ""SchemaVersion"""));

        // The row is untouched, and each new column is present with its "nobody asked for this"
        // value rather than being backfilled with a guess.
        Assert.Equal("Existing Game|Unzip it|/var/lib/gametown/games/abc.zip|1234.5|||0",
            app.QueryScalar("""
                SELECT "Title"||'|'||"HowTo"||'|'||"URL"||'|'||"Size"||'|'
                       ||COALESCE("ArchiveSha256",'')||'|'||COALESCE("BoxArtUrl",'')||'|'||"GuideBaked"
                FROM "GameTownGame" WHERE "Id" = '11111111-1111-1111-1111-111111111111'
                """));

        // The game gained no tags on the way past. A migration that seeds a vocabulary must not
        // apply any of it — what a game is tagged with is a decision, not a default.
        Assert.Equal("0", app.QueryScalar(@"SELECT COUNT(*) FROM ""GameTownGame_Tags"""));

        // ...but the vocabulary itself is there, exactly once each.
        Assert.Equal("4", app.QueryScalar(@"SELECT COUNT(*) FROM ""Tags"" WHERE ""IsQuickAdd"" = 1"));

        // And nothing else was rebuilt on the way past.
        Assert.Equal("1", app.QueryScalar(@"SELECT COUNT(*) FROM ""GameTownGame"""));
        Assert.Equal("2", app.QueryScalar(@"SELECT COUNT(*) FROM ""GameTownRoles"""));
    }

    /// <summary>
    /// The RAWG-to-neutral-tables move, against a library that actually has RAWG metadata in it.
    ///
    /// This is the test the whole migration rests on. An installed library is the ONLY copy of its own
    /// metadata now — RAWG cannot be asked for it again — so if migration 007 drops a field, that
    /// field is gone from that install permanently. "The migration runs" is not the claim being
    /// checked here; "the migration runs without losing anything" is, which is why every column is
    /// asserted by value rather than the rows being counted.
    ///
    /// Note the two games sharing one metadata record. That is the normal case (two uploads of the
    /// same title) and it is where a re-pointing bug would show as one game silently adopting
    /// another's metadata.
    /// </summary>
    [Fact]
    public async Task A_library_with_rawg_metadata_keeps_every_field_across_the_move_to_neutral_tables()
    {
        using var app = new GameTownApp();

        // Version 6 — an install on the release immediately before this one, which is the state every
        // upgrading appliance is actually in.
        foreach (var migration in new[]
                 {
                     "002_game_title_index", "003_game_archive_hash", "004_game_box_art",
                     "005_game_tags", "006_game_guide"
                 })
        {
            app.RunSql(GameTownApp.SchemaFile(Path.Combine("migrations", $"{migration}.sql")));
        }
        app.QueryScalar(@"INSERT INTO ""SchemaVersion"" (""Version"") VALUES (2),(3),(4),(5),(6)");

        // A RAWG game with the full graph hanging off it, and images ALREADY re-hosted as local
        // /media paths — which is what makes the migration a pure local move and is the property the
        // last assertion here pins.
        app.QueryScalar("""
            INSERT INTO "RAWGGames" ("id","slug","name","description","website","metacritic","rating",
                                     "background_image","released","updated")
            VALUES (3498,'gta-v','Grand Theft Auto V','<p>An <b>open world</b> game.</p>',
                    'https://rockstargames.com',92,4.47,'/media/aaaa-1111.jpg','2013-09-17',
                    '2024-01-01 00:00:00');
            INSERT INTO "RAWGDevelopers" ("id","name","slug") VALUES (10,'Rockstar North','rockstar-north');
            INSERT INTO "RAWGGenres" ("id","name","slug") VALUES (4,'Action','action');
            INSERT INTO "RAWGScreenshots" ("Id","image","width","height","is_deleted")
            VALUES (1234,'/media/shot-1.jpg',1920,1080,0);
            INSERT INTO "RAWGGames_Developers" VALUES (3498,10);
            INSERT INTO "RAWGGames_Genres" VALUES (3498,4);
            INSERT INTO "RAWGGames_Screenshots" VALUES (3498,1234);

            INSERT INTO "GameTownGame" ("Id","Title","HowTo","RAWGGameId","URL","Size","BoxArtUrl","GuideBaked")
            VALUES ('AAAAAAAA-1111-1111-1111-111111111111','GTA V','Run setup.exe',3498,
                    '/var/lib/gametown/games/gta.zip',60000.0,'/media/box.jpg',1);
            INSERT INTO "GameTownGame" ("Id","Title","HowTo","RAWGGameId","URL","Size")
            VALUES ('BBBBBBBB-2222-2222-2222-222222222222','GTA V (LAN)','Use hamachi',3498,
                    '/var/lib/gametown/games/gta2.zip',60000.0);
            INSERT INTO "GameTownGame" ("Id","Title","HowTo","URL","Size")
            VALUES ('CCCCCCCC-3333-3333-3333-333333333333','No Metadata','Just run it',
                    '/var/lib/gametown/games/x.zip',10.0);

            INSERT INTO "Settings" ("Key","Value") VALUES ('RAWGApiKey','deadbeef'),('GameFilesPath','/mnt/games');
            """);

        // Booting the new build is the upgrade.
        using (var client = app.CreateBrowser()) await client.GetAsync("/");

        Assert.Equal("7", app.QueryScalar(@"SELECT MAX(""Version"") FROM ""SchemaVersion"""));

        // Field by field. The image path especially: it is a local file this server is still serving,
        // and carrying the row across without it would blank the cover on a game nobody touched.
        Assert.Equal("rawg|3498|gta-v|Grand Theft Auto V|<p>An <b>open world</b> game.</p>|"
                     + "2013-09-17|92.0|4.47|/media/aaaa-1111.jpg|https://rockstargames.com",
            app.QueryScalar("""
                SELECT "provider"||'|'||"external_id"||'|'||"slug"||'|'||"name"||'|'||"description"||'|'
                       ||"released"||'|'||"critic_score"||'|'||"rating"||'|'||"image"||'|'||"website"
                FROM "MetadataGames" WHERE "external_id" = 3498
                """));

        // The related rows and their joins came too — a game with no genres or screenshots renders as
        // a stub, which is a data loss that looks like a UI bug.
        Assert.Equal("Rockstar North|Action|/media/shot-1.jpg|1920",
            app.QueryScalar("""
                SELECT d."name"||'|'||ge."name"||'|'||s."image"||'|'||s."width"
                FROM "MetadataGames" g
                JOIN "MetadataGames_Developers" md ON md."metadata_id" = g."id"
                JOIN "MetadataDevelopers" d ON d."id" = md."developer_id"
                JOIN "MetadataGames_Genres" mg ON mg."metadata_id" = g."id"
                JOIN "MetadataGenres" ge ON ge."id" = mg."genre_id"
                JOIN "MetadataGames_Screenshots" ms ON ms."metadata_id" = g."id"
                JOIN "MetadataScreenshots" s ON s."id" = ms."screenshot_id"
                """));

        // Both games re-pointed at the SAME record, and the third — which never had metadata — was
        // left null rather than being given someone else's.
        Assert.Equal("GTA V|3498", app.QueryScalar(
            @"SELECT ""Title""||'|'||""MetadataId"" FROM ""GameTownGame"" WHERE ""Id"" = 'AAAAAAAA-1111-1111-1111-111111111111'"));
        Assert.Equal("GTA V (LAN)|3498", app.QueryScalar(
            @"SELECT ""Title""||'|'||""MetadataId"" FROM ""GameTownGame"" WHERE ""Id"" = 'BBBBBBBB-2222-2222-2222-222222222222'"));
        Assert.Equal("0", app.QueryScalar(
            @"SELECT COUNT(*) FROM ""GameTownGame"" WHERE ""Id"" = 'CCCCCCCC-3333-3333-3333-333333333333' AND ""MetadataId"" IS NOT NULL"));

        // Curated work is untouched. Box art and the baked-guide flag belong to the contributor and no
        // migration has any business rewriting them.
        Assert.Equal("/media/box.jpg|1", app.QueryScalar(
            @"SELECT ""BoxArtUrl""||'|'||""GuideBaked"" FROM ""GameTownGame"" WHERE ""Id"" = 'AAAAAAAA-1111-1111-1111-111111111111'"));

        // The dead credential is gone and the unrelated setting is not.
        Assert.Equal("0", app.QueryScalar(@"SELECT COUNT(*) FROM ""Settings"" WHERE ""Key"" = 'RAWGApiKey'"));
        Assert.Equal("/mnt/games", app.QueryScalar(@"SELECT ""Value"" FROM ""Settings"" WHERE ""Key"" = 'GameFilesPath'"));

        // The old tables are still there, holding the same rows. 007 is additive on purpose: it keeps
        // the release reversible, and "RAWGGameId" cannot be dropped anyway because a table-level FK
        // constraint names it.
        Assert.Equal("1", app.QueryScalar(@"SELECT COUNT(*) FROM ""RAWGGames"""));
        Assert.Equal("3498", app.QueryScalar(
            @"SELECT ""RAWGGameId"" FROM ""GameTownGame"" WHERE ""Id"" = 'AAAAAAAA-1111-1111-1111-111111111111'"));
    }

    /// <summary>
    /// The other side of the same migration: a fresh install must reach exactly the state an upgraded
    /// one does. The INSERT ... SELECTs in 007 copy zero rows here, which is what makes the two paths
    /// the same sequence rather than two sequences that happen to agree today.
    /// </summary>
    [Fact]
    public async Task A_fresh_install_gets_the_metadata_tables_and_copies_nothing_into_them()
    {
        using var app = new GameTownApp();
        using (var client = app.CreateBrowser()) await client.GetAsync("/");

        Assert.Equal("7", app.QueryScalar(@"SELECT MAX(""Version"") FROM ""SchemaVersion"""));
        Assert.Equal("0", app.QueryScalar(@"SELECT COUNT(*) FROM ""MetadataGames"""));

        // Present and usable, not merely present: a surrogate the database assigns, which is the
        // behaviour DatabaseContextConfiguration has to restore because the scaffolder marks these
        // keys ValueGeneratedNever.
        app.QueryScalar("""
            INSERT INTO "MetadataGames" ("provider","external_id","slug","name","description","website")
            VALUES ('igdb',1009,'the-last-of-us','The Last of Us','x','');
            """);
        Assert.Equal("1", app.QueryScalar(@"SELECT ""id"" FROM ""MetadataGames"" WHERE ""external_id"" = 1009"));
    }

    /// <summary>
    /// Rolling back to the previous release after the upgrade has run. The old binary knows nothing
    /// about version 3 or the new column, and must simply leave both alone rather than trying to
    /// "fix" the database it does not recognise.
    /// </summary>
    [Fact]
    public async Task A_database_ahead_of_the_binary_is_left_alone()
    {
        using var app = new GameTownApp();
        using (var client = app.CreateBrowser()) await client.GetAsync("/");

        // Pretend a future release has been and gone.
        app.QueryScalar(@"INSERT INTO ""SchemaVersion"" (""Version"") VALUES (99)");

        var before = app.QueryScalar(
            @"SELECT COUNT(*)||'/'||MAX(""Version"") FROM ""SchemaVersion""");

        API.Startup.SchemaMigrator.ApplyMigrations(
            API.Startup.SqliteConnectionString.WithRequiredPragmas($"Data Source={app.DatabasePath}"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Equal(before, app.QueryScalar(
            @"SELECT COUNT(*)||'/'||MAX(""Version"") FROM ""SchemaVersion"""));
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
