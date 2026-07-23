using API.Services;
using EFModel.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace API.Startup;

public static class DependenciesConfig
{
    public static void AddDependencies(this WebApplicationBuilder builder)
    {
        builder.AddServiceDefaults();
        builder.Services.AddOpenApiServices();
        builder.Services.AddCorsServices();

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

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        var RAWGKey = builder.Configuration.GetValue<string>("RAWGApiKey") ?? throw new Exception("The app requires an API key for RAWG to be set!");

        // SQLite needs two things Postgres gave us for free, and both are silent when missing:
        //
        //  - Foreign keys are OFF by default. Every FOREIGN KEY in the schema is inert without
        //    this, including the ON DELETE CASCADE from RefreshTokens to GameTownUsers — deleting
        //    a user would leave its tokens behind instead of failing loudly.
        //  - WAL lets readers proceed during a write. Game downloads hold long read transactions,
        //    so without it a large download can block metadata writes for its whole duration.
        //
        // WAL is a property of the database file and persists, so setting it once at startup is
        // enough; "Foreign Keys" is per-connection and has to live in the connection string.
        connectionString = SqliteConnectionString.WithRequiredPragmas(connectionString);

        builder.Services.AddDbContext<DatabaseContext>(options =>
            options.UseSqlite(connectionString)
            );
        builder.Services.AddScoped<RAWGService>(provider =>
        {
            var dbContext = provider.GetRequiredService<DatabaseContext>();
            var rawgKey = builder.Configuration.GetValue<string>("RAWGApiKey") ?? throw new Exception("The app requires an API key for RAWG to be set!");
            return new RAWGService(rawgKey, dbContext);
        });

        var GameFilesPath = builder.Configuration.GetValue<string>("GameFilesPath");
        if (string.IsNullOrWhiteSpace(GameFilesPath) || (!Path.Exists(GameFilesPath)))
        {
            throw new Exception("The app requires a GameFilesPath to be set and it must exist!");
        }
        builder.Services.AddScoped<FileService>(provider =>
        {
            return new FileService(GameFilesPath);
        });
        builder.Services.AddScoped<GTGamesService>();
        builder.Services.AddScoped<UserService>();
        #region Authentication
        var jwtSettings = builder.Configuration.GetSection("JwtSettings");
        if (string.IsNullOrEmpty(jwtSettings["Key"]))
        {
            throw new Exception("JWT Key is not configured in appsettings.json");
        }
        builder.Services
            .AddAuthentication(x =>
            {
                x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            }).AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.SaveToken = true;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwtSettings["Issuer"],
                    ValidAudience = jwtSettings["Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(
                                                    Encoding.UTF8.GetBytes(jwtSettings["Key"]!))
                };

            });

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
