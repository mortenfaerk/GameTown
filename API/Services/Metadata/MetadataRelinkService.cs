using EFModel.Models;
using Microsoft.EntityFrameworkCore;

namespace API.Services.Metadata;

/// <summary>One library entry still carrying metadata from a provider that has been retired.</summary>
public sealed record RelinkCandidate(
    Guid GameId, string Title, int MetadataId, string MetadataName, string Provider, DateTime? Released);

/// <summary>A proposed pairing, for an administrator to confirm or reject.</summary>
public sealed record RelinkProposal(
    Guid GameId, string Title, ProviderSearchResult? Match, bool IsConfident, string Reason);

/// <summary>
/// Moves a library entry from a retired provider's metadata to the current provider's.
///
/// <b>Nothing in here runs by itself.</b> No startup hook, no background sweep, no "heal the library
/// on first boot after the upgrade". It is driven entirely by an administrator working through a
/// screen, and a library that never opens that screen stays on its old metadata indefinitely — which
/// is a supported state, not a pending task. Migration 007 carried every row across with its images
/// already local, so those entries render correctly and completely forever.
///
/// That restraint is deliberate and was the last decision made before this was built. Automatic
/// matching would put the network back into an upgrade path that is otherwise offline and
/// deterministic; it would depend on credentials the operator has not entered at upgrade time, so its
/// normal case would be "skipped"; and a half-finished automatic pass leaves a shelf where some games
/// changed and some did not, with nothing recording why. An admin who opens the tool sees exactly what
/// is unmatched and decides.
/// </summary>
public class MetadataRelinkService(
    DatabaseContext context,
    IGameMetadataProvider provider,
    GameMetadataService metadataService,
    MediaStore media,
    ILogger<MetadataRelinkService> logger)
{
    /// <summary>
    /// IGDB allows 4 requests/second. Bulk proposals are one search per game, so they are paced
    /// rather than fired off together — a 200-game library is under a minute at this rate, and being
    /// rate-limited half way through would leave the admin reading a list with arbitrary holes in it.
    /// </summary>
    private static readonly TimeSpan SearchInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>Games whose metadata did not come from the provider currently configured.</summary>
    public async Task<List<RelinkCandidate>> GetCandidatesAsync(CancellationToken cancellationToken = default)
        => await context.GameTownGames
            .AsNoTracking()
            .Where(g => g.Metadata != null && g.Metadata.Provider != provider.Id)
            .OrderBy(g => g.Title)
            .Select(g => new RelinkCandidate(
                g.Id, g.Title, g.Metadata!.Id, g.Metadata.Name, g.Metadata.Provider, g.Metadata.Released))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// How many candidates there are, without materialising them.
    ///
    /// The same predicate as <see cref="GetCandidatesAsync"/>, deliberately: both entry points to this
    /// feature — the sidebar link and the settings banner — are shown or hidden on this number, and if
    /// it were derived independently the two could disagree with the screen they lead to. That is not
    /// hypothetical: the settings banner used to ask whether any metadata ROW carried a foreign
    /// provider, which is true on any library still holding the orphan RAWG records migration 007 left
    /// behind, even when every game had already moved.
    /// </summary>
    public Task<int> CountCandidatesAsync(CancellationToken cancellationToken = default)
        => context.GameTownGames
            .AsNoTracking()
            .CountAsync(g => g.Metadata != null && g.Metadata.Provider != provider.Id, cancellationToken);

    /// <summary>
    /// Searches the current provider for each candidate and returns proposed pairings.
    ///
    /// Proposals, not changes. Nothing is written here — the admin reviews the list and confirms what
    /// they want. <see cref="RelinkProposal.IsConfident"/> is what the UI pre-ticks; everything else
    /// arrives unticked and has to be chosen deliberately.
    /// </summary>
    public async Task<List<RelinkProposal>> ProposeAsync(
        IEnumerable<Guid> gameIds, CancellationToken cancellationToken = default)
    {
        var wanted = gameIds.ToHashSet();
        var candidates = (await GetCandidatesAsync(cancellationToken))
            .Where(c => wanted.Contains(c.GameId))
            .ToList();

        var proposals = new List<RelinkProposal>();
        var first = true;

        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Paced between calls, not before the first — an admin proposing one game should not wait
            // 300ms to find that out.
            if (!first) await Task.Delay(SearchInterval, cancellationToken);
            first = false;

            // Searched on the METADATA name rather than the library title. The library title is
            // whatever the contributor typed on the upload form ("GTA V (LAN build)"), while the
            // metadata name came from a catalogue and is far more likely to match another catalogue.
            var response = await provider.SearchAsync(
                candidate.MetadataName, page: 1, pageSize: 5, cancellationToken);

            if (response.Reason == "rate-limited")
            {
                // Stop rather than mark the rest unmatched. "We were throttled" and "this game is not
                // in IGDB" are different answers and must not be recorded as the same one.
                logger.LogWarning("Re-link proposals stopped early: the provider rate-limited us.");
                proposals.Add(new RelinkProposal(
                    candidate.GameId, candidate.Title, null, false, "rate-limited"));
                break;
            }

            if (response.Reason != "ok")
            {
                proposals.Add(new RelinkProposal(
                    candidate.GameId, candidate.Title, null, false, response.Reason));
                continue;
            }

            var best = PickBest(candidate, response.Results, out var confident);
            proposals.Add(new RelinkProposal(
                candidate.GameId, candidate.Title, best, confident,
                best is null ? "no-match" : "ok"));
        }

        return proposals;
    }

    /// <summary>
    /// Picks the best candidate, and says whether it is good enough to pre-tick.
    ///
    /// "Confident" means an exact, case-insensitive name match AND — where both sides know a release
    /// year — the same year. The year check is what separates a remake from its original, which is
    /// the single most likely wrong match in a library like this and the one an admin skimming a list
    /// of ticked boxes would be least likely to catch.
    /// </summary>
    private static ProviderSearchResult? PickBest(
        RelinkCandidate candidate, List<ProviderSearchResult> results, out bool confident)
    {
        confident = false;
        if (results.Count == 0) return null;

        var exact = results
            .Where(r => string.Equals(r.Name, candidate.MetadataName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exact.Count == 0) return results[0];

        // More than one exact name match means the name does not identify the game — several
        // re-releases share it. Offer the first, but never pre-tick it.
        if (exact.Count > 1) return exact[0];

        var match = exact[0];
        var yearsAgree = candidate.Released is null
                         || match.Released is null
                         || candidate.Released.Value.Year == match.Released.Value.Year;

        confident = yearsAgree;
        return match;
    }

    /// <summary>
    /// Applies one confirmed pairing.
    ///
    /// What this deliberately does NOT touch: BoxArtUrl, Tags, HowTo, GuideBaked. Those are the
    /// contributor's own work — a chosen cover, hand-typed tags, written instructions — and no
    /// metadata provider has an opinion about them. Re-linking changes which catalogue record a game
    /// points at, and nothing else.
    ///
    /// The old metadata row is left in place if any other game still uses it, and its images are
    /// deleted only after the replacement has been fetched and stored successfully — so a failure
    /// here leaves the game exactly as it was rather than stripped of the art it had.
    /// </summary>
    public async Task<bool> ApplyAsync(
        Guid gameId, int externalId, CancellationToken cancellationToken = default)
    {
        var game = await context.GameTownGames
            .Include(g => g.Metadata).ThenInclude(m => m!.Screenshots)
            .FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken)
            ?? throw new KeyNotFoundException($"Game with ID {gameId} not found.");

        var previous = game.Metadata;

        // Fetched and stored FIRST. If the provider is unreachable this throws before anything about
        // the game has changed, which is the difference between "try again later" and "the cover is
        // gone and there is nothing to put back".
        var replacement = await metadataService.EnsurePersistedAsync(externalId, cancellationToken);

        game.Metadata = replacement;
        await context.SaveChangesAsync(cancellationToken);

        if (previous is not null && previous.Id != replacement.Id)
            await DiscardIfUnusedAsync(previous, cancellationToken);

        return true;
    }

    /// <summary>
    /// Removes a metadata record nothing points at any more, and the media files that belonged to it.
    ///
    /// Shared records are the reason for the check: two uploads of the same game point at one row, so
    /// re-linking one of them must not delete the metadata the other is still rendering from.
    /// </summary>
    private async Task DiscardIfUnusedAsync(MetadataGame previous, CancellationToken cancellationToken)
    {
        var stillInUse = await context.GameTownGames
            .AnyAsync(g => g.MetadataId == previous.Id, cancellationToken);

        if (stillInUse) return;

        foreach (var screenshot in previous.Screenshots)
            media.Delete(screenshot.Image);

        media.Delete(previous.Image);

        // The row itself goes too. Its screenshots and joins follow through ON DELETE CASCADE — which
        // is inert unless foreign keys are enabled, and SqliteConnectionString forces them on.
        context.MetadataGames.Remove(previous);
        await context.SaveChangesAsync(cancellationToken);
    }
}
