namespace GameTown.Contracts.Settings;

/// <summary>
/// Current settings, as shown in the admin UI.
///
/// Note what a secret looks like here: a masked preview plus a flag, never the value. See
/// <see cref="RawgApiKeyMasked"/>.
/// </summary>
public class SettingsContract
{
    /// <summary>Where uploaded archives are written. Editable.</summary>
    public string GameFilesPath { get; set; } = string.Empty;

    /// <summary>
    /// Derived from the data directory and shown read-only, because static file serving binds its
    /// root at startup and could not follow a change without a restart.
    /// </summary>
    public string MediaDirectory { get; set; } = string.Empty;

    /// <summary>Read-only. Everything that must survive an upgrade lives under here.</summary>
    public string DataDirectory { get; set; } = string.Empty;

    public bool RawgApiKeyIsSet { get; set; }

    /// <summary>
    /// Last four characters only, e.g. "••••3f9a". The real key is never sent to the browser: it has
    /// no reason to be there, and round-tripping a secret through a page just so the form can post it
    /// back unchanged is how secrets end up in logs, history and screenshots.
    /// </summary>
    public string? RawgApiKeyMasked { get; set; }

    /// <summary>
    /// Whether a SteamGridDB key is stored. Without one, box-art *search* is unavailable and says so;
    /// uploading a file or pasting an image URL still works, which is why this is optional in the same
    /// way the RAWG key is.
    /// </summary>
    public bool BoxArtApiKeyIsSet { get; set; }

    /// <summary>Last four characters only, on the same terms as <see cref="RawgApiKeyMasked"/>.</summary>
    public string? BoxArtApiKeyMasked { get; set; }

    public List<string> AllowedFileTypes { get; set; } = [];

    /// <summary>
    /// Largest archive a contributor may upload, in megabytes. <c>0</c> means no limit.
    ///
    /// Worth remembering when this looks like it is not working: a reverse proxy in front of GameTown
    /// has its own body-size limit and it wins, because it rejects the request before the application
    /// ever sees it. nginx defaults to 1 MB.
    /// </summary>
    public long MaxUploadSizeMb { get; set; }
}

/// <summary>
/// A settings change. Every field is optional — null means "leave this alone" — so the UI can patch
/// one tab without resending the others.
/// </summary>
public class SettingsUpdateRequest
{
    public string? GameFilesPath { get; set; }

    /// <summary>
    /// Null or blank means "unchanged", NOT "clear it". That asymmetry is deliberate: the browser is
    /// never given the current key, so it cannot echo it back, and a blank submission is what an
    /// untouched form looks like. Clearing is a separate explicit action.
    /// </summary>
    public string? RawgApiKey { get; set; }

    public bool ClearRawgApiKey { get; set; }

    /// <summary>Blank means "unchanged", not "clear" — same asymmetry as <see cref="RawgApiKey"/>.</summary>
    public string? BoxArtApiKey { get; set; }

    public bool ClearBoxArtApiKey { get; set; }

    public List<string>? AllowedFileTypes { get; set; }

    /// <summary>Null means "unchanged". <c>0</c> is a real value and means "no limit".</summary>
    public long? MaxUploadSizeMb { get; set; }
}

public class PathCheckRequest
{
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// Result of probing a server path.
///
/// <see cref="Reason"/> is a fixed code from a known set, never an exception message: this endpoint
/// reports on arbitrary server paths, and raw exception text would disclose directory layout.
/// </summary>
public class PathCheckResult
{
    public bool Exists { get; set; }
    public bool Writable { get; set; }
    public string Reason { get; set; } = string.Empty;
    public long? FreeBytes { get; set; }

    /// <summary>
    /// The filesystem the directory sits on ("cifs", "ext4", "btrfs"), when it could be read.
    ///
    /// Reported so an operator pointing GameTown at a network share can tell whether the share is
    /// actually mounted. An unmounted mountpoint passes every other check in this result: it exists,
    /// it is writable, it has free space — and it is the local disk, quietly filling up under a
    /// directory the share will hide the moment it mounts.
    /// </summary>
    public string? FileSystem { get; set; }
}

public class RawgKeyCheckResult
{
    public bool Ok { get; set; }
    public string Reason { get; set; } = string.Empty;
}
