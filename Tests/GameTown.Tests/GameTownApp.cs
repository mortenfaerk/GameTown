using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Net.Http.Json;

namespace GameTown.Tests;

/// <summary>
/// Boots the real application against a throwaway SQLite database.
///
/// Deliberately not a mocked or in-memory substitute. Almost every bug this suite exists to catch —
/// foreign keys off, a cookie the browser discards, routes that return HTML, services reading
/// configuration captured at startup — lives in the wiring between the app and a real SQLite file.
/// Anything that swaps that out would test a different program than the one that ships.
///
/// The database is created from <c>Database/sqlite/01_schema.sql</c> and <c>02_seed.sql</c>, the same
/// files the installer uses, so a drift between the DDL and the model fails here.
/// </summary>
public sealed class GameTownApp : WebApplicationFactory<Program>
{
    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "gametown.db");

    /// <summary>
    /// A stand-in for the LAN bot, when a test sets one.
    ///
    /// THE ONLY STUBBED DEPENDENCY IN THIS SUITE, and it is worth saying why it is not a precedent.
    /// Everything else here runs against the real thing — a real SQLite file, real cookies, the real
    /// /setup page — because the bugs this suite exists to catch live in that wiring. Outbound
    /// providers are covered by asserting their REFUSALS, which never reach the network (see
    /// BoxArtTests), and that works because nothing downstream of an IGDB search depends on what
    /// comes back.
    ///
    /// The LAN bot is different: its whole value is what GameTown does with the answer, and the rules
    /// that matter — one binding per game, reconciling to what the bot reports, not recording a link
    /// that failed to push — are only reachable on the success path. There is no way to observe them
    /// without deciding what the bot said.
    ///
    /// Kept to this one client on purpose. A general-purpose HTTP stub would make it easy to write a
    /// test that passes against a mock of code that never runs.
    /// </summary>
    public HttpMessageHandler? LanBotHandler { get; set; }

    public GameTownApp(bool seedRoles = true) : this(createDatabase: true, seedRoles: seedRoles) { }

    /// <summary>
    /// Boots against a data directory with no database in it at all — the state an appliance is in
    /// the moment it is installed. The application builds the schema itself at startup.
    ///
    /// This exists because every other test hands the app a database that the harness already
    /// created from <c>01_schema.sql</c>, so none of them execute the fresh-install path — the only
    /// path a real first run ever takes.
    /// </summary>
    public static GameTownApp WithEmptyDataDirectory() => new(createDatabase: false, seedRoles: false);

    private GameTownApp(bool createDatabase, bool seedRoles)
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "gametown-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);

        if (!createDatabase) return;

        RunSql(SchemaFile("01_schema.sql"));
        if (seedRoles) RunSql(SchemaFile("02_seed.sql"));
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // The connection string is the only thing the app requires before it starts; everything
            // else must come from the database, and a test that had to supply more would be evidence
            // the settings work had regressed.
            ["ConnectionStrings:DefaultConnection"] = $"Data Source={DatabasePath}",
        }));

        // Replaces the primary handler of the "lanbot" named client only, and only when a test asked
        // for it. Everything the sync does above the wire — the settings read, the base address, the
        // X-Api-Key header, the JSON binding — still runs exactly as it ships.
        builder.ConfigureServices(services =>
        {
            if (LanBotHandler is null) return;

            services.AddHttpClient(API.Services.Lan.LanBotClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => LanBotHandler);
        });

        return base.CreateHost(builder);
    }

    /// <summary>
    /// A client that keeps cookies, because authentication *is* a cookie now. The default
    /// WebApplicationFactory client does not, so every request after login would arrive anonymous.
    /// </summary>
    public HttpClient CreateBrowser() => CreateDefaultClient(new CookieHandler());

    public async Task<HttpClient> SignInAsAdminAsync(string username = "admin", string password = "adminpassword")
    {
        await CreateAdminAsync(username, password);
        var client = CreateBrowser();
        var response = await client.PostAsJsonAsync("/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        return client;
    }

    public async Task<HttpClient> SignInAsContributorAsync(string username = "contrib", string password = "contribpassword")
    {
        var admin = await SignInAsAdminAsync();
        var created = await admin.PostAsJsonAsync("/users/add",
            new { username, password, displayName = username });
        created.EnsureSuccessStatusCode();

        var roleId = QueryScalar(@"SELECT ""Id"" FROM ""GameTownRoles"" WHERE ""Role""='Contributor'");
        var userId = QueryScalar($@"SELECT ""Id"" FROM ""GameTownUsers"" WHERE ""Username""='{username}'");
        var assigned = await admin.PostAsync($"/users/addUserToRole?userId={userId}&roleId={roleId}", null);
        assigned.EnsureSuccessStatusCode();

        var client = CreateBrowser();
        var response = await client.PostAsJsonAsync("/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>
    /// Creates the first admin through the real /setup wizard rather than by inserting rows, so the
    /// wizard is exercised by every test that needs an account.
    /// </summary>
    public async Task CreateAdminAsync(string username, string password)
    {
        var client = CreateBrowser();
        var form = await client.GetStringAsync("/setup");
        var token = ExtractAntiforgeryToken(form);

        var response = await client.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Username"] = username,
            ["Password"] = password,
            ["ConfirmPassword"] = password,
        }));

        // 302 to /login is success; the wizard redirects rather than rendering a result page.
        if (response.StatusCode != System.Net.HttpStatusCode.Redirect)
            throw new InvalidOperationException($"Setup failed with {(int)response.StatusCode}.");
    }

    public static string ExtractAntiforgeryToken(string html)
    {
        const string marker = "name=\"__RequestVerificationToken\"";
        var at = html.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("No antiforgery token in the form.");

        var valueAt = html.IndexOf("value=\"", at, StringComparison.Ordinal) + 7;
        var end = html.IndexOf('"', valueAt);
        return html[valueAt..end];
    }

    public string QueryScalar(string sql)
    {
        var output = RunSqlite(DatabasePath, sql);
        return output.Trim();
    }

    /// <summary>
    /// The script's contents, rather than a <c>.read</c> pointing at it.
    ///
    /// A dot-command is parsed by the sqlite3 shell, which applies its own quoting rules to the
    /// argument: a Windows path arrives full of backslashes to be interpreted, and any path
    /// containing a space splits in two. The SQL goes down stdin instead and is never parsed as a
    /// path or an argument by anything.
    /// </summary>
    public void RunSql(string file) => RunSqlite(DatabasePath, File.ReadAllText(file));

    /// <summary>
    /// Shelling out to sqlite3 rather than opening a connection from the test process: it keeps the
    /// test's view of the database completely independent of the EF model under test, so a broken
    /// mapping cannot make a test pass by being broken consistently on both sides.
    ///
    /// The SQL goes in on stdin rather than as an argument, which is not merely tidier: sqlite3 reads
    /// any argv element beginning with "-" as an option, and 01_schema.sql opens with a comment — so
    /// passing a script that way makes the shell reject "--" as an unknown option and then sit waiting
    /// on an inherited stdin that never closes. <c>-bail</c> keeps the exit code meaningful, because a
    /// script fed through stdin otherwise runs past its own errors and still exits 0.
    /// </summary>
    private static string RunSqlite(string database, string sql)
    {
        var process = Process.Start(new ProcessStartInfo("sqlite3", ["-bail", "-batch", database])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start sqlite3.");

        process.StandardInput.Write(sql);
        // Closed rather than left open: EOF is what tells the shell the script has ended, and without
        // it an incomplete statement would hang the test run instead of failing it.
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"sqlite3 failed: {stderr}");

        // Carriage returns are dropped, and on Windows there are two unrelated sources of them.
        //
        // The shell translates LF to CRLF on its way out, which alone would only break comparisons
        // against literals in a test file. The subtler one is that sqlite_master stores the exact text
        // of each CREATE statement: the application applies its embedded copy of the DDL, carriage
        // returns and all, while the sqlite3 shell strips them as it reads the same file — so the two
        // databases the schema tests compare hold the same objects described in text that differs by
        // a byte per line. That is a line-ending convention, not schema drift, and a comparison that
        // failed on it would fail only on Windows and only for a reason nobody could act on.
        return stdout.Replace("\r", string.Empty);
    }

    public static string SchemaFile(string name) => Path.Combine(RepositoryRoot(), "Database", "sqlite", name);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GameTown.slnx")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { Directory.Delete(DataDirectory, recursive: true); } catch { /* best effort */ }
    }

    private sealed class CookieHandler : DelegatingHandler
    {
        private readonly System.Net.CookieContainer _cookies = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var header = _cookies.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(header))
                request.Headers.Add("Cookie", header);

            var response = await base.SendAsync(request, cancellationToken);

            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values)
                    _cookies.SetCookies(uri, value);

            return response;
        }
    }
}
