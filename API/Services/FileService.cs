namespace API.Services
{
    public class FileService
    {
        readonly string _gameDirectory;

        public FileService(string gameDirectory)
        {
            _gameDirectory = gameDirectory;
        }

        public string GetGameFilePath(string fileName) {
            return Path.Combine(_gameDirectory, fileName);
        }

        /// <summary>
        /// Resolves a stored game path and confirms it really sits inside the configured
        /// GameFilesPath before anyone opens or deletes it.
        ///
        /// Callers must never act on a path straight out of the database. The update endpoint used to
        /// let a client set that path, which turned the download route into an arbitrary file read and
        /// the delete route into an arbitrary file delete. The request field is gone now, but this
        /// keeps rows written before that fix — or by any future bug — from escaping the directory.
        /// </summary>
        public bool TryResolveGameFile(string? storedPath, out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(storedPath))
                return false;

            // A bare file name is the normal case; anything else is resolved and then bounds-checked.
            var candidate = Path.IsPathRooted(storedPath)
                ? storedPath
                : Path.Combine(_gameDirectory, storedPath);

            string resolved;
            string root;
            try
            {
                resolved = Path.GetFullPath(candidate);
                root = Path.GetFullPath(_gameDirectory);
            }
            catch (Exception)
            {
                // Malformed path (invalid characters, too long, …) — treat as not found.
                return false;
            }

            // TrimEnd so a root of "/games/" and a resolved "/games/x.zip" still match, and the
            // separator check stops "/games-secret" passing as a child of "/games".
            root = root.TrimEnd(Path.DirectorySeparatorChar);
            if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return false;

            fullPath = resolved;
            return true;
        }
    }
}
