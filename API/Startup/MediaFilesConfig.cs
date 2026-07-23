using API.Services;
using Microsoft.Extensions.FileProviders;

namespace API.Startup;

public static class MediaFilesConfig
{
    /// <summary>
    /// Serves /media/* from the data directory.
    ///
    /// The directory is resolved once here, at startup, from the connection string — the static-file
    /// middleware binds its file provider once and cannot follow a value that changes later. That is
    /// exactly why the media location is derived from the data directory rather than exposed as an
    /// editable setting: a setting that saves and then does nothing until a restart is worse than no
    /// setting at all. See SettingsService.MediaDirectory.
    /// </summary>
    public static void UseMediaFiles(this WebApplication app)
    {
        var connectionString = app.Configuration.GetConnectionString("DefaultConnection");
        var dataDirectory = SqliteConnectionString.GetDataDirectory(
            SqliteConnectionString.WithRequiredPragmas(connectionString));

        var mediaDirectory = SettingsService.MediaDirectoryFor(dataDirectory);

        // Created eagerly because PhysicalFileProvider throws on a missing root, and on a fresh
        // install nothing has written any media yet.
        Directory.CreateDirectory(mediaDirectory);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(mediaDirectory),
            RequestPath = "/media",
        });
    }
}
