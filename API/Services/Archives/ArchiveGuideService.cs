using System.Text;
using EFModel.Models;
using Microsoft.EntityFrameworkCore;

namespace API.Services.Archives;

/// <summary>What happened to a request to bake or remove a guide.</summary>
public record GuideResult(bool Success, string Reason)
{
    public static GuideResult Failed(string reason) => new(false, reason);
    public static readonly GuideResult Ok = new(true, "ok");
}

/// <summary>
/// Writes a game's instructions into its own archive as <c>GameTownGuide.txt</c>.
///
/// The instructions live on the game's page, which is precisely where they are no use at the moment
/// they are needed: the archive has been downloaded, the extractor is open, and the browser tab was
/// closed twenty minutes ago. Putting a copy inside the archive moves the text to where the problem
/// is.
///
/// It is a copy, and the flag on the game says the copy should exist — so editing the instructions
/// with the toggle on rewrites it, and turning the toggle off removes it. <see cref="ZipGuideWriter"/>
/// does the archive half; this decides when, and enforces where.
/// </summary>
public class ArchiveGuideService(
    DatabaseContext context,
    FileService files,
    ILogger<ArchiveGuideService> logger)
{
    public const string GuideFileName = "GameTownGuide.txt";

    /// <summary>
    /// Formats to be excluded from `.zip` are not an oversight, they are the whole shape of the
    /// feature. Only ZIP keeps its index at the end where it can be rewritten cheaply. TAR could be
    /// added the same way; 7z needs the external 7-Zip binary, which is the dependency class this
    /// appliance exists without; and RAR has no free writer at all, so it is not a matter of effort.
    /// </summary>
    public static bool IsSupported(string? storedPath)
        => ".zip".Equals(Path.GetExtension(storedPath ?? string.Empty), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Brings the archive into line with the game's <c>GuideBaked</c> flag.
    ///
    /// Takes the desired state rather than "add" and "remove" verbs, because every caller is really
    /// saying "make the archive match what the game now says" — after a title change, after an
    /// instructions edit, after the toggle moved. Sending the same state twice is a no-op on disk.
    /// </summary>
    public async Task<GuideResult> ApplyAsync(Guid gameId, bool baked, CancellationToken cancellationToken = default)
    {
        var game = await context.GameTownGames.FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken)
            ?? throw new KeyNotFoundException($"Game with ID {gameId} not found.");

        if (baked && !IsSupported(game.Url))
            return GuideResult.Failed("unsupported-format");

        // Never the stored path directly. It is proven to sit inside the configured archive directory
        // first — the same rule the download and delete paths follow, and more important here because
        // this one *writes*.
        var (resolved, path) = await files.TryResolveGameFileAsync(game.Url);
        if (!resolved || !File.Exists(path))
            return GuideResult.Failed("archive-missing");

        try
        {
            if (baked)
                ZipGuideWriter.AddOrReplace(path, GuideFileName, Compose(game));
            else
                ZipGuideWriter.Remove(path, GuideFileName);
        }
        catch (ZipGuideWriter.UnsupportedArchiveException ex)
        {
            // Named .zip and is not one, or is damaged. The archive is untouched — the writer refuses
            // before writing anything — so this is worth reporting rather than hiding.
            logger.LogWarning(ex, "Could not write a guide into the archive for game {GameId}.", gameId);
            return GuideResult.Failed("not-a-zip");
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not write a guide into the archive for game {GameId}.", gameId);
            return GuideResult.Failed("write-failed");
        }

        // Recorded only once the archive actually agrees. A flag set ahead of the write would leave
        // the UI claiming a guide that is not in the file.
        if (game.GuideBaked != baked)
        {
            game.GuideBaked = baked;
            await context.SaveChangesAsync(cancellationToken);
        }

        return GuideResult.Ok;
    }

    /// <summary>
    /// Re-writes the guide if — and only if — the game already has one.
    ///
    /// For the edit path: changing the instructions has to change the copy inside the archive, or the
    /// two silently disagree and the one the player reads is the stale one. A game with the toggle off
    /// is left alone, including its archive's modification time.
    /// </summary>
    public async Task RefreshIfBakedAsync(Guid gameId, CancellationToken cancellationToken = default)
    {
        var baked = await context.GameTownGames
            .Where(g => g.Id == gameId)
            .Select(g => g.GuideBaked)
            .FirstOrDefaultAsync(cancellationToken);

        if (!baked) return;

        try
        {
            await ApplyAsync(gameId, baked: true, cancellationToken);
        }
        catch (Exception ex)
        {
            // Best effort, deliberately. This runs after a save that has already succeeded, and
            // failing the edit because the copy inside the archive could not be refreshed would be a
            // worse outcome than the copy being briefly stale.
            logger.LogWarning(ex, "Could not refresh the guide for game {GameId} after an edit.", gameId);
        }
    }

    /// <summary>
    /// The text itself.
    ///
    /// CRLF throughout and a UTF-8 byte-order mark, neither of which is fussiness: this file is opened
    /// by double-clicking it on Windows, quite possibly in an old build of Notepad, and without both
    /// the Danish text this library actually contains renders as one long line of mojibake.
    /// </summary>
    private static byte[] Compose(GameTownGame game)
    {
        var body = new StringBuilder();

        body.Append(game.Title).Append("\r\n");
        body.Append(new string('=', Math.Min(game.Title.Length, 60))).Append("\r\n\r\n");

        var instructions = string.IsNullOrWhiteSpace(game.HowTo)
            ? "(No instructions were written for this game.)"
            : game.HowTo.ReplaceLineEndings("\r\n").Trim();

        body.Append(instructions).Append("\r\n\r\n");
        body.Append("---\r\n");
        body.Append("Written into this archive by GameTown. Edits made in GameTown replace it.\r\n");

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(body.ToString())];
    }
}
