namespace API.Services
{
    /// <summary>
    /// Everything that touches the uploaded-archive directory.
    ///
    /// The directory is no longer captured at construction. It used to arrive as a constructor string
    /// fixed at startup, which meant changing it in the settings UI had no effect on the running
    /// process until a restart. It is now read per call from <see cref="SettingsService"/>.
    /// </summary>
    public class FileService(SettingsService settings)
    {
        /// <summary>
        /// The configured archive directory, created if absent.
        ///
        /// Creating it here rather than validating it at startup is what lets the app boot before it
        /// has been configured at all — a first run has no settings and must still reach the setup
        /// page instead of throwing.
        /// </summary>
        public async Task<string> GetGameDirectoryAsync()
        {
            var directory = await settings.GetGameFilesPathAsync();
            Directory.CreateDirectory(directory);
            return directory;
        }

        public async Task<string> GetGameFilePathAsync(string fileName)
            => Path.Combine(await GetGameDirectoryAsync(), fileName);

        /// <summary>
        /// Resolves a stored game path and confirms it really sits inside the configured archive
        /// directory before anyone opens or deletes it.
        ///
        /// Callers must never act on a path straight out of the database. The update endpoint used to
        /// let a client set that path, which turned the download route into an arbitrary file read and
        /// the delete route into an arbitrary file delete. The request field is gone now, but this
        /// keeps rows written before that fix — or by any future bug — from escaping the directory.
        /// </summary>
        public async Task<(bool Resolved, string FullPath)> TryResolveGameFileAsync(string? storedPath)
            => TryResolveWithin(await GetGameDirectoryAsync(), storedPath);

        /// <summary>
        /// The containment check itself, kept pure and separate from the settings lookup so the
        /// security-relevant logic stays synchronous, self-contained and directly testable.
        /// </summary>
        public static (bool Resolved, string FullPath) TryResolveWithin(string gameDirectory, string? storedPath)
        {
            if (string.IsNullOrWhiteSpace(storedPath))
                return (false, string.Empty);

            // A bare file name is the normal case; anything else is resolved and then bounds-checked.
            var candidate = Path.IsPathRooted(storedPath)
                ? storedPath
                : Path.Combine(gameDirectory, storedPath);

            string resolved;
            string root;
            try
            {
                resolved = Path.GetFullPath(candidate);
                root = Path.GetFullPath(gameDirectory);
            }
            catch (Exception)
            {
                // Malformed path (invalid characters, too long, …) — treat as not found.
                return (false, string.Empty);
            }

            // TrimEnd so a root of "/games/" and a resolved "/games/x.zip" still match, and the
            // separator check stops "/games-secret" passing as a child of "/games".
            root = root.TrimEnd(Path.DirectorySeparatorChar);
            if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return (false, string.Empty);

            return (true, resolved);
        }

        /// <summary>
        /// Whether an uploaded file's extension is on the configured allowlist.
        ///
        /// Enforced server-side on purpose: the SPA also filters the file picker, but that is a
        /// convenience for the user, not a control — anything can POST to the upload endpoint.
        /// </summary>
        public async Task<bool> IsAllowedFileTypeAsync(string fileName)
        {
            var allowed = await settings.GetAllowedFileTypesAsync();
            var extension = Path.GetExtension(fileName);
            return !string.IsNullOrEmpty(extension)
                   && allowed.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }
    }
}
