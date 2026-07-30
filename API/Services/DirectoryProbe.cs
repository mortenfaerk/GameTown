using GameTown.Contracts.Settings;

namespace API.Services;

/// <summary>
/// Answers "can this process actually write there?" by writing — not by inspecting permission bits.
///
/// The distinction matters on the two storage layouts this appliance actually meets. A local
/// directory can be owned by root while the service runs as <c>gametown</c>, and a CIFS mount carries
/// the credentials' rights rather than the caller's, so neither mode bits nor ownership predict the
/// answer. Creating a file and deleting it again does.
///
/// Lives here rather than in the settings endpoint because the first-run wizard needs the same
/// verdict, and a second implementation would eventually disagree with this one — the wizard would
/// accept a path the settings page rejects, on the one screen where the operator has no way back.
///
/// Every <see cref="PathCheckResult.Reason"/> is a fixed code. Raw exception text would leak
/// directory structure out of a check whose entire job is reporting on arbitrary server paths.
/// </summary>
public static class DirectoryProbe
{
    /// <summary>The codes <see cref="Probe"/> can return. Both UIs translate these into prose.</summary>
    public static readonly string[] Reasons =
        ["ok", "not-absolute", "unc-not-supported", "permission-denied", "not-found", "io-error", "invalid"];

    public static PathCheckResult Probe(string? path)
    {
        path = path?.Trim();

        if (string.IsNullOrEmpty(path))
            return new PathCheckResult { Reason = "not-absolute" };

        // Checked before IsPathRooted, which would call these "not absolute" — true but useless
        // advice for someone who has just typed the address of a network share. GameTown runs
        // unprivileged and cannot mount anything, so a UNC path is never something it can be talked
        // into opening; it has to become a mountpoint first. See smb-mount.sh.
        if (LooksLikeNetworkShare(path))
            return new PathCheckResult { Reason = "unc-not-supported" };

        if (!Path.IsPathRooted(path))
            return new PathCheckResult { Reason = "not-absolute" };

        var result = new PathCheckResult();
        try
        {
            var info = new DirectoryInfo(path);
            result.Exists = info.Exists;

            if (!info.Exists)
            {
                // Pointing at a directory that does not exist yet is a normal thing to do while
                // setting this up; refusing would send the operator out to a shell mid-wizard.
                info.Create();
                result.Exists = true;
            }

            var probe = Path.Combine(path, $".gametown-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            result.Writable = true;
            result.Reason = "ok";

            Describe(path, result);
        }
        catch (UnauthorizedAccessException)
        {
            result.Reason = "permission-denied";
        }
        catch (DirectoryNotFoundException)
        {
            result.Reason = "not-found";
        }
        catch (IOException)
        {
            result.Reason = "io-error";
        }
        catch (Exception)
        {
            result.Reason = "invalid";
        }
        return result;
    }

    /// <summary>
    /// Free space and filesystem type of the mount the directory actually sits on.
    ///
    /// <c>new DriveInfo(path)</c> resolves to the closest mount point on Unix; the obvious-looking
    /// <c>DirectoryInfo.Root</c> is always "/" there, so it reported the root filesystem's free space
    /// no matter where the archive directory pointed — reassuring and wrong for exactly the operator
    /// who moved the library onto a second disk.
    ///
    /// The filesystem name is reported so the answer to "is my share mounted?" is visible: an
    /// unmounted mountpoint is writable, local, and silently the wrong disk. It shows as ext4 or
    /// btrfs where the operator expects cifs.
    /// </summary>
    private static void Describe(string path, PathCheckResult result)
    {
        try
        {
            var drive = new DriveInfo(path);
            result.FreeBytes = drive.AvailableFreeSpace;
            result.FileSystem = drive.DriveFormat;
        }
        catch (Exception)
        {
            // Decoration only. A filesystem that will not report on itself is still writable, and
            // the write test above is what actually answers the question.
        }
    }

    /// <summary>
    /// Windows UNC (<c>\\server\share</c>) and the URI form (<c>smb://server/share</c>).
    ///
    /// POSIX <c>//foo</c> is deliberately not treated as a share: it is a legal absolute path, and
    /// guessing wrong there would reject a directory that works.
    /// </summary>
    private static bool LooksLikeNetworkShare(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal)
        || path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("cifs://", StringComparison.OrdinalIgnoreCase);
}
