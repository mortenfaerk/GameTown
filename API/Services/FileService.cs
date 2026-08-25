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
        ///
        /// Deliberately does NOT check that the directory is writable — see
        /// <see cref="GetWritableGameDirectoryAsync"/>. Reading does not need write access, and a
        /// library whose archive directory has gone read-only must still serve every download in it.
        /// </summary>
        public async Task<string> GetGameDirectoryAsync()
        {
            var directory = await settings.GetGameFilesPathAsync();
            Directory.CreateDirectory(directory);
            return directory;
        }

        /// <summary>
        /// The archive directory, proven writable by actually writing to it.
        ///
        /// <see cref="Directory.CreateDirectory"/> is not a writability check. On a directory that
        /// already exists it is a no-op that inspects nothing, so an archive directory the service
        /// does not own — the normal state of a mountpoint added after installation, whose owner is
        /// root or, in an unprivileged container, a uid with no mapping at all — sails straight
        /// through it. The upload then failed several hundred lines later inside the copy, as an
        /// unhandled IO exception: a body-less 500 that the SPA could only render as
        /// "Request failed (500)." and that was legible nowhere but the server's journal.
        ///
        /// Probing moves that verdict to before the first byte is written and attaches a reason to
        /// it. The cost is one file created and deleted per upload, against a transfer that is
        /// routinely measured in gigabytes.
        /// </summary>
        /// <exception cref="ArchiveDirectoryException">The directory cannot be written to.</exception>
        public async Task<string> GetWritableGameDirectoryAsync()
        {
            var directory = await settings.GetGameFilesPathAsync();

            // Probe creates the directory itself when it is absent, so this covers the case
            // GetGameDirectoryAsync uses CreateDirectory for as well.
            var probe = DirectoryProbe.Probe(directory);
            if (!probe.Writable)
                throw new ArchiveDirectoryException(probe.Reason);

            return directory;
        }

        /// <summary>
        /// Where an uploaded archive is about to be written. Goes through the writability probe,
        /// which is what makes "the directory is usable" a structural property of the upload path
        /// rather than something a caller has to remember to check.
        /// </summary>
        public async Task<string> GetGameFilePathAsync(string fileName)
            => Path.Combine(await GetWritableGameDirectoryAsync(), fileName);

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

    /// <summary>
    /// The configured archive directory cannot be written to, so an upload cannot be stored.
    ///
    /// The message is written for the person who actually meets it: a contributor part-way through an
    /// upload, who cannot see the server, cannot reach the settings page, and has no way to tell a
    /// misconfigured appliance apart from a bad file. So it says what broke, that their file is not at
    /// fault, that retrying is pointless, and who can fix it.
    ///
    /// It deliberately does not name the path. Contributors are not administrators and are shown the
    /// archive directory nowhere else — the same reasoning that makes <see cref="DirectoryProbe"/>
    /// return fixed reason codes instead of raw exception text.
    /// </summary>
    public sealed class ArchiveDirectoryException(string reason) : IOException(Describe(reason))
    {
        /// <summary>The <see cref="DirectoryProbe"/> reason code behind this failure.</summary>
        public string Reason { get; } = reason;

        private static string Describe(string reason)
            => Cause(reason)
             + " Your upload was not saved. This is a problem with the server rather than with your"
             + " file, so trying again will not help — an administrator needs to check the archive"
             + " directory under Administer → Settings.";

        /// <summary>
        /// One sentence per reason code. The default arm is not padding: <see cref="DirectoryProbe"/>
        /// owns that vocabulary and may grow it, and a new code must degrade to a usable sentence
        /// rather than to an empty one.
        /// </summary>
        private static string Cause(string reason) => reason switch
        {
            "permission-denied" =>
                "GameTown does not have permission to write to its archive directory.",
            "not-found" =>
                "GameTown's archive directory does not exist and could not be created.",
            // Deliberately not "exists but could not be written to", the wording the setup wizard
            // uses. The same code also covers a path whose parent is a FILE, where the directory does
            // not exist and cannot be made — so the neutral phrasing is the one true in every case.
            "io-error" =>
                "GameTown could not write to its archive directory.",
            "unc-not-supported" =>
                "GameTown's archive directory is set to a network share address, which it cannot open"
                + " directly — the share has to be mounted on the server first.",
            "not-absolute" =>
                "GameTown's archive directory is not set to an absolute path.",
            _ => "GameTown's archive directory is not usable.",
        };
    }
}
