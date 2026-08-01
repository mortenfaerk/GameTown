namespace API.Services;

/// <summary>
/// The one place that knows where re-hosted images live and what their stored paths look like.
///
/// Three features write here — RAWG cover art, RAWG screenshots and box art — and before this each
/// carried its own copy of "combine the media directory with the file name, generate a GUID, write
/// the bytes, prefix the result with /media/". Two of those copies had already drifted: one wrote into
/// the application's own wwwroot (which an in-place upgrade deletes) and one deleted superseded files
/// while the other orphaned them.
///
/// The stored form is always "/media/{guid}.{ext}" — a path, never a remote URL. Program.cs maps that
/// request path onto <see cref="Directory"/>, which lives in the data directory rather than in the
/// application folder, because the data directory is the only location an upgrade does not overwrite.
/// </summary>
public class MediaStore(SettingsService settings, ILogger<MediaStore> logger)
{
    /// <summary>The prefix every stored media path carries. Also what marks a path as ours to delete.</summary>
    public const string UrlPrefix = "/media/";

    public string Directory => settings.MediaDirectory;

    /// <summary>
    /// Writes bytes under a generated name and returns the path to store.
    ///
    /// The extension is the caller's, and every caller derives it from the content itself rather than
    /// from a URL or an upload's filename — these files are served back as static content from the
    /// API's own origin, so the extension decides the Content-Type a browser will act on.
    /// </summary>
    public async Task<string> WriteAsync(byte[] bytes, string extension, CancellationToken cancellationToken = default)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var fileName = $"{Guid.NewGuid()}{extension}";
        await File.WriteAllBytesAsync(Path.Combine(Directory, fileName), bytes, cancellationToken);

        return UrlPrefix + fileName;
    }

    /// <summary>
    /// Removes a file this application stored. Anything else is left alone.
    ///
    /// Best effort by design: a leftover file costs disk, while throwing here would fail an operation
    /// that has otherwise already succeeded — a contributor unable to change a cover because the
    /// *previous* one could not be unlinked is a worse outcome than a stray file.
    /// </summary>
    public void Delete(string? storedPath)
    {
        // A remote URL — left in place when a re-host failed — is not ours to unlink, and neither is
        // anything a hand-edited row might contain.
        if (string.IsNullOrWhiteSpace(storedPath) || !storedPath.StartsWith(UrlPrefix, StringComparison.Ordinal))
            return;

        try
        {
            // GetFileName discards any directory portion, so "/media/../../etc/passwd" resolves to
            // "passwd" inside the media directory rather than escaping it.
            var fileName = Path.GetFileName(storedPath);
            if (string.IsNullOrEmpty(fileName)) return;

            var path = Path.Combine(Directory, fileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove stored media {Path}.", storedPath);
        }
    }

    /// <summary>Whether a stored value is a local copy rather than a remote URL left in place.</summary>
    public static bool IsLocal(string? storedPath)
        => storedPath?.StartsWith(UrlPrefix, StringComparison.Ordinal) == true;
}
