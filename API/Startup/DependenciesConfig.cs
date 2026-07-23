using API.Services;
using EFModel.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;

namespace API.Startup;

public static class DependenciesConfig
{
    public static void AddDependencies(this WebApplicationBuilder builder)
    {
        builder.AddServiceDefaults();
        builder.Services.AddOpenApiServices();

        // Razor Pages exists solely for the first-run wizard at /setup, which has to render on an
        // install that has never been configured — before the SPA has anything to talk to. It is not
        // a general shift away from minimal APIs.
        builder.Services.AddRazorPages();

        // Game archives are the whole point of this app and routinely exceed the framework defaults,
        // which are Kestrel's ~28.6 MB request body and a 128 MB multipart limit. Both have to be
        // lifted: Kestrel rejects first, then the form reader would. Until now anything larger than
        // ~28.6 MB failed with a 413 while the upload form advertised 2 GB.
        //
        // No ceiling is set deliberately. Uploading requires the Contributor policy, so this is not
        // an anonymous vector, but there is no backstop against a contributor filling the disk.
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);
        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = long.MaxValue;
            options.ValueLengthLimit = int.MaxValue;
            options.MultipartHeadersLengthLimit = int.MaxValue;
        });

        // The connection string is the ONLY thing that must be known before the app starts, because
        // everything else is now read out of the database it points at. Nothing else throws here:
        // an unconfigured install has to boot far enough to serve its own setup page.
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

        // SQLite needs two things Postgres gave us for free, and both are silent when missing:
        //
        //  - Foreign keys are OFF by default. Every FOREIGN KEY in the schema is inert without
        //    this, so orphan rows insert happily and no ON DELETE rule ever fires.
        //  - WAL lets readers proceed during a write. Game downloads hold long read transactions,
        //    so without it a large download can block metadata writes for its whole duration.
        //
        // WAL is a property of the database file and persists, so setting it once at startup is
        // enough; "Foreign Keys" is per-connection and has to live in the connection string.
        connectionString = SqliteConnectionString.WithRequiredPragmas(connectionString);
        SqliteConnectionString.EnableWriteAheadLogging(connectionString);

        builder.Services.AddDbContext<DatabaseContext>(options =>
            options.UseSqlite(connectionString)
            );
        // SettingsService is the only one that still needs a constructed value, and it is a path
        // derived from the connection string rather than a setting in its own right.
        //
        // RAWGService and FileService used to be registered with factory lambdas precisely because
        // they took primitive constructor arguments — a RAWG key and an archive directory, both read
        // once at startup. That is what made those values un-editable at runtime, so they are plain
        // scoped registrations now and read what they need per call.
        var dataDirectory = SqliteConnectionString.GetDataDirectory(connectionString);
        builder.Services.AddScoped(provider =>
            new SettingsService(provider.GetRequiredService<DatabaseContext>(), dataDirectory));
        builder.Services.AddScoped<RAWGService>();
        builder.Services.AddScoped<FileService>();
        builder.Services.AddScoped<GTGamesService>();
        builder.Services.AddScoped<UserService>();
        #region Authentication
        // Cookie authentication, not JWT bearer. Same-origin hosting (Phase 2a) means the browser
        // can hold an HttpOnly cookie the page cannot read, which is both simpler and better than
        // handing the SPA a token it had to store, parse and refresh itself.
        //
        // Four of the settings below each produce a build that runs and an app that is broken.
        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = "gametown_auth";
                options.Cookie.HttpOnly = true;

                // SameSite=Lax is what mitigates CSRF here. Moving from a bearer token (attached by
                // our code, so never sent cross-site) to a cookie (attached by the browser)
                // reintroduces CSRF as a threat class; Lax withholds the cookie on cross-site POST
                // and fetch, which kills the classic attack. It DOES still send it on top-level
                // cross-site GET navigation, so the mitigation holds only while no GET mutates
                // state. See SECURITY-NOTES.md — that is a standing invariant, not an accident.
                options.Cookie.SameSite = SameSiteMode.Lax;

                // SameAsRequest, deliberately not the Always default. The appliance ships serving
                // plain HTTP on a LAN, and Always marks the cookie Secure — which a browser then
                // silently refuses to send over HTTP. Login appears to succeed and every following
                // request arrives anonymous.
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

                options.ExpireTimeSpan = TimeSpan.FromDays(14);

                // Renews the cookie when it is more than half expired. This is the whole replacement
                // for the rotating refresh-token scheme: no /auth/refresh, no stored tokens, no
                // client-side expiry tracking.
                options.SlidingExpiration = true;

                // By default the cookie handler answers an unauthenticated request with a 302 to a
                // login page. For an API that is actively harmful: fetch follows the redirect and
                // receives 200 text/html, so the caller sees success and tries to parse HTML as
                // JSON. Return the status codes an API client can actually act on.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        // The auth cookie is encrypted with Data Protection keys. Left at the default the keyring
        // lives in memory, so every restart invalidates every cookie and signs everyone out. That
        // surfaces later as "saving a setting logged me out", which reads as a settings bug rather
        // than a crypto one. Keys go next to the database, which is the data directory by definition.
        var keyRingPath = Path.Combine(SqliteConnectionString.GetDataDirectory(connectionString), "keys");
        Directory.CreateDirectory(keyRingPath);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
            .SetApplicationName("GameTown");

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("Admin", policy =>
            {
                policy.RequireRole("Admin");
            });
            options.AddPolicy("Contributor", policy=>
            {
                policy.RequireRole("Contributor", "Admin");
            });

            // Secure by default. Endpoints used to be anonymous unless someone remembered to add a
            // policy, and three of them were missed — leaving game edit/delete and the RAWG proxy
            // open to anyone on the network. Now an endpoint must opt OUT with .AllowAnonymous().
            //
            // This only requires a signed-in user, not a role, so anything that needs a role still
            // declares .RequireAuthorization("Contributor"/"Admin") explicitly.
            //
            // Static files are unaffected: Program.cs runs UseStaticFiles() before UseAuthorization(),
            // so /media cover art stays public. Do not reorder that middleware.
            options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });
        #endregion
    }
}
