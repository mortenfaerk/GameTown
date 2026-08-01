using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace API.Services;

/// <summary>
/// Reads the add-game multipart body, streaming the archive straight into the configured archive
/// directory as the bytes arrive.
///
/// This replaces <c>[FromForm] AddGameWithFileForm</c> binding, which wrote every archive TWICE: the
/// form reader spools anything over 64 KB to <c>Path.GetTempPath()</c> while it reads the request,
/// and the handler then copied that spool file into GameFilesPath. Two problems came out of that:
///
///  - The second copy could only start once the last byte had arrived, so the connection went silent
///    for as long as the copy took — minutes for a large archive on a CIFS mount. A reverse proxy
///    reads that as a dead upstream and times out (nginx defaults to 60s), so the user saw a failure
///    for an upload the server went on to complete.
///  - Under the installer's systemd unit, <c>PrivateTmp=true</c> can put that spool on a tmpfs, which
///    is RAM. A large enough archive is then an OOM rather than a disk write.
///
/// Streaming removes both: bytes land in their final home as they arrive, there is no temp copy, and
/// the transfer is genuinely finished when the progress bar says it is.
///
/// Field order does NOT matter. The archive is written under a server-generated name that depends on
/// nothing but its extension, and the text fields are only needed after the loop, so a client may
/// send them in either order. (upload.js happens to send fields first.)
/// </summary>
public static class ArchiveUpload
{
    /// <summary>
    /// A title or a RAWG id longer than this is a bug or an attack, not a form field. Bounded because
    /// unlike the archive, these ARE read into memory.
    /// </summary>
    private const int MaxFieldLength = 64 * 1024;

    /// <summary>
    /// Large on purpose. A multi-GB archive copied in 4 KB chunks over SMB is dominated by round
    /// trips rather than throughput.
    /// </summary>
    private const int CopyBufferSize = 1024 * 1024;

    /// <summary>
    /// Suffix an archive carries while it is still arriving. Shared with
    /// <see cref="SweepAbandonedPartsAsync"/>, which is the only thing that ever sees one that
    /// outlived its request.
    /// </summary>
    private const string PartSuffix = ".part";

    public static async Task<ArchiveUploadResult> ReadAsync(
        HttpRequest request,
        FileService files,
        SettingsService settings,
        CancellationToken cancellationToken)
    {
        var boundary = TryGetBoundary(request.ContentType);
        if (boundary is null)
            return ArchiveUploadResult.Rejected(StatusCodes.Status400BadRequest,
                "Expected a multipart/form-data upload.");

        var limitBytes = await settings.GetMaxUploadSizeBytesAsync();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? partPath = null;
        string? finalPath = null;
        long bytes = 0;
        var sha256 = string.Empty;
        var committed = false;

        try
        {
            var reader = new MultipartReader(boundary, request.Body);

            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                    continue;

                if (IsFileDisposition(disposition))
                {
                    if (finalPath is not null)
                        return ArchiveUploadResult.Rejected(StatusCodes.Status400BadRequest,
                            "Only one archive can be uploaded at a time.");

                    var fileName = Unquote(disposition.FileName) ?? Unquote(disposition.FileNameStar) ?? string.Empty;

                    // The allowlist is checked BEFORE a byte is written, so a rejected type never
                    // touches the disk. It is a server-side control, not a hint to the file picker.
                    if (!await files.IsAllowedFileTypeAsync(fileName))
                    {
                        var allowed = string.Join(", ", await settings.GetAllowedFileTypesAsync());
                        return ArchiveUploadResult.Rejected(StatusCodes.Status400BadRequest,
                            $"Invalid file format. Allowed types: {allowed}.");
                    }

                    // Never build a path from the client-supplied name: identically named uploads
                    // would overwrite each other and "../" would escape GameFilesPath entirely.
                    var extension = Path.GetExtension(fileName).ToLowerInvariant();
                    finalPath = await files.GetGameFilePathAsync($"{Guid.NewGuid()}{extension}");

                    // Written under .part and renamed on success. A rename within one directory is
                    // atomic and instant, so the archive directory never contains a truncated file
                    // that looks like a complete one.
                    partPath = finalPath + PartSuffix;

                    var (written, exceeded, hash) = await CopyAsync(section.Body, partPath, limitBytes, cancellationToken);
                    bytes = written;
                    sha256 = hash;

                    if (exceeded)
                        return ArchiveUploadResult.Rejected(StatusCodes.Status413PayloadTooLarge,
                            TooLargeMessage(limitBytes!.Value));
                }
                else if (IsFormDisposition(disposition))
                {
                    var name = Unquote(disposition.Name);
                    if (string.IsNullOrEmpty(name))
                        continue;

                    var (value, tooLong) = await ReadFieldAsync(section.Body, cancellationToken);
                    if (tooLong)
                        return ArchiveUploadResult.Rejected(StatusCodes.Status400BadRequest,
                            $"The '{name}' field is too long.");

                    fields[name] = value;
                }
            }

            if (partPath is null || finalPath is null)
                return ArchiveUploadResult.Rejected(StatusCodes.Status400BadRequest, "No file uploaded.");

            File.Move(partPath, finalPath);
            committed = true;

            return ArchiveUploadResult.Stored(finalPath, bytes, sha256, fields);
        }
        finally
        {
            // Covers every non-success exit, including a client that disconnected mid-upload and an
            // exception from the filesystem. Without it a cancelled upload leaves its bytes behind
            // forever, and the whole point of streaming to the final directory is that those bytes
            // are already there.
            if (!committed) TryDelete(partPath);
        }
    }

    /// <summary>
    /// Copies one multipart section to disk, hashing it on the way past and stopping if it exceeds
    /// <paramref name="limitBytes"/>.
    ///
    /// The limit is checked against what has actually been received, never against Content-Length:
    /// the client writes that header and can understate it.
    ///
    /// The SHA-256 costs one extra pass over bytes that are already in cache — far cheaper than
    /// re-reading the finished archive off disk, which on a CIFS mount would mean pulling every byte
    /// back over the network.
    /// </summary>
    private static async Task<(long Written, bool Exceeded, string Sha256)> CopyAsync(
        Stream source, string destination, long? limitBytes, CancellationToken cancellationToken)
    {
        await using var target = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None,
            CopyBufferSize, FileOptions.Asynchronous);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long written = 0;

        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken)) > 0)
            {
                written += read;
                if (limitBytes is not null && written > limitBytes)
                    return (written, true, string.Empty);

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (written, false, Convert.ToHexString(hash.GetHashAndReset()));
    }

    /// <summary>Reads a text field, refusing anything past <see cref="MaxFieldLength"/>.</summary>
    private static async Task<(string Value, bool TooLong)> ReadFieldAsync(
        Stream source, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024, leaveOpen: true);

        // One char over the limit is enough to know it was exceeded, and stops an oversized field
        // being read into memory just to measure it.
        var buffer = new char[MaxFieldLength + 1];
        var read = await reader.ReadBlockAsync(buffer, cancellationToken);

        return read > MaxFieldLength
            ? (string.Empty, true)
            : (new string(buffer, 0, read), false);
    }

    public static string TooLargeMessage(long limitBytes)
        => $"That file is larger than this server's {limitBytes / (1024 * 1024)} MB upload limit.";

    /// <summary>
    /// Deletes ".part" files left in the archive directory by a previous process.
    ///
    /// <see cref="ReadAsync"/> cleans up after itself on every path it can reach — a rejection, an
    /// exception, a client that disconnected mid-upload. What it cannot clean up after is the process
    /// not being there any more: a systemd restart during an upload, an OOM kill, or the host losing
    /// power. Those leave a partial archive that nothing references and nothing will ever remove, and
    /// on a library of multi-gigabyte games that quietly eats the disk.
    ///
    /// Called at startup, BEFORE the first request is served, which is what makes deleting every
    /// ".part" safe rather than needing an age heuristic: none of them can belong to an upload this
    /// process is running, because it is not running any yet. That reasoning assumes one GameTown per
    /// archive directory — the appliance's only supported shape, since the directory is paired with a
    /// single SQLite database that records what is in it.
    ///
    /// Never throws. A temp-file sweep must not be able to stop the application from starting.
    /// </summary>
    public static async Task SweepAbandonedPartsAsync(FileService files, ILogger logger)
    {
        try
        {
            var directory = await files.GetGameDirectoryAsync();
            var abandoned = Directory.GetFiles(directory, "*" + PartSuffix);
            if (abandoned.Length == 0) return;

            long reclaimed = 0;
            var removed = 0;

            foreach (var path in abandoned)
            {
                try
                {
                    var size = new FileInfo(path).Length;
                    File.Delete(path);
                    reclaimed += size;
                    removed++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not remove the abandoned upload {Path}.", path);
                }
            }

            logger.LogInformation(
                "Removed {Count} unfinished upload(s) from a previous run, reclaiming {Megabytes} MB.",
                removed, reclaimed / (1024 * 1024));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not sweep unfinished uploads from the archive directory.");
        }
    }

    private static string? TryGetBoundary(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
            return null;

        var boundary = Unquote(mediaType.Boundary);
        return string.IsNullOrWhiteSpace(boundary) ? null : boundary;
    }

    private static string? Unquote(StringSegment value)
        => StringSegment.IsNullOrEmpty(value) ? null : HeaderUtilities.RemoveQuotes(value).Value;

    // Spelled out rather than using the framework's IsFileDisposition/IsFormDisposition extensions,
    // so this file depends on nothing beyond the header type itself.
    private static bool IsFileDisposition(ContentDispositionHeaderValue disposition)
        => disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
           && (!StringSegment.IsNullOrEmpty(disposition.FileName)
               || !StringSegment.IsNullOrEmpty(disposition.FileNameStar));

    private static bool IsFormDisposition(ContentDispositionHeaderValue disposition)
        => disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
           && StringSegment.IsNullOrEmpty(disposition.FileName)
           && StringSegment.IsNullOrEmpty(disposition.FileNameStar);

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}

/// <summary>Outcome of reading an add-game upload. Either an archive landed on disk, or it did not.</summary>
public sealed class ArchiveUploadResult
{
    private static readonly IReadOnlyDictionary<string, string> NoFields =
        new Dictionary<string, string>();

    public bool Success { get; private init; }
    public int StatusCode { get; private init; }
    public string? Error { get; private init; }

    /// <summary>Absolute path of the stored archive. Only meaningful when <see cref="Success"/>.</summary>
    public string StoredPath { get; private init; } = string.Empty;

    public long Bytes { get; private init; }

    /// <summary>Uppercase hex SHA-256 of the stored archive, used to reject a duplicate upload.</summary>
    public string Sha256 { get; private init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Fields { get; private init; } = NoFields;

    public static ArchiveUploadResult Stored(
        string path, long bytes, string sha256, IReadOnlyDictionary<string, string> fields)
        => new()
        {
            Success = true,
            StatusCode = StatusCodes.Status204NoContent,
            StoredPath = path,
            Bytes = bytes,
            Sha256 = sha256,
            Fields = fields,
        };

    public static ArchiveUploadResult Rejected(int statusCode, string error)
        => new() { Success = false, StatusCode = statusCode, Error = error };

    public string? Field(string name) => Fields.TryGetValue(name, out var value) ? value : null;
}
