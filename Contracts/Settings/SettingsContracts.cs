namespace GameTown.Contracts.Settings;

/// <summary>
/// Current settings, as shown in the admin UI.
///
/// Note what a secret looks like here: a masked preview plus a flag, never the value. See
/// <see cref="IgdbClientSecretMasked"/>.
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

    /// <summary>
    /// Whether IGDB credentials are stored. Both halves must be present to count as configured —
    /// a client id without a secret cannot authenticate, so reporting it as "set" would be a lie the
    /// admin only discovers at the first search.
    /// </summary>
    public bool IgdbCredentialsAreSet { get; set; }

    /// <summary>
    /// The client id, in full and unmasked. It is not a secret — it is sent as a header on every IGDB
    /// request and is visible to anyone who can see the traffic. Showing it makes the settings page
    /// useful for confirming *which* Twitch application an install is pointed at.
    /// </summary>
    public string? IgdbClientId { get; set; }

    /// <summary>
    /// Last four characters only, e.g. "••••3f9a". The real secret is never sent to the browser: it
    /// has no reason to be there, and round-tripping a secret through a page just so the form can post
    /// it back unchanged is how secrets end up in logs, history and screenshots.
    /// </summary>
    public string? IgdbClientSecretMasked { get; set; }

    /// <summary>
    /// True when this install still has metadata carried over from RAWG and no IGDB credentials to
    /// replace it with.
    ///
    /// Drives the one-time banner on the settings page. Without it an operator upgrades, finds the
    /// metadata picker refusing to search, and has nothing on screen connecting that to a retired
    /// provider or telling them what to do about it. Their library is fine — that is the other half
    /// of the message.
    /// </summary>
    public bool HasRetiredProviderMetadata { get; set; }

    /// <summary>
    /// Whether a SteamGridDB key is stored. Without one, box-art *search* is unavailable and says so;
    /// uploading a file or pasting an image URL still works, which is why this is optional in the same
    /// way the IGDB credentials are.
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
    /// never given the current secret, so it cannot echo it back, and a blank submission is what an
    /// untouched form looks like. Clearing is a separate explicit action.
    /// </summary>
    public string? IgdbClientId { get; set; }

    /// <summary>Blank means "unchanged" — same asymmetry as <see cref="IgdbClientId"/>.</summary>
    public string? IgdbClientSecret { get; set; }

    /// <summary>Clears both halves. They are one credential and are never half-removed.</summary>
    public bool ClearIgdbCredentials { get; set; }

    /// <summary>Blank means "unchanged", not "clear" — same asymmetry as <see cref="IgdbClientId"/>.</summary>
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

/// <summary>
/// The result of one live call made with the stored credentials, to tell "saved" from "works".
///
/// Shared by the metadata and artwork providers: the shape is identical and the UI wording is the
/// caller's business.
/// </summary>
public class ProviderCredentialCheckResult
{
    public bool Ok { get; set; }

    /// <summary>
    /// A fixed code from a known set — "ok", "not-configured", "rejected", "rate-limited",
    /// "unreachable" — never an exception message. This reports on an outbound request, and raw
    /// exception text discloses proxy names and internal addresses.
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}
