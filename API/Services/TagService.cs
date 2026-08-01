using System.Text;
using EFModel.Models;
using Microsoft.EntityFrameworkCore;

namespace API.Services;

/// <summary>
/// Manual tags: the ones describing how a game gets played rather than what it is.
///
/// The whole design turns on one idea — a tag's identity is its <b>slug</b>, not its name. Names are
/// typed by hand, by several people, over months; "Co-op", "co op" and " CO-OP " are one tag to a
/// human and would be three rows to a database that keyed on the text. Normalising to a slug and
/// putting the UNIQUE constraint there is what makes the tag list stay short enough to be useful.
/// </summary>
public class TagService(DatabaseContext context)
{
    /// <summary>
    /// Longer than any sensible tag and short enough to keep the filter bar readable. Enforced here
    /// because SQLite does not enforce varchar lengths — the DDL's "varchar(50)" is a comment.
    /// </summary>
    public const int MaxNameLength = 50;

    /// <summary>
    /// Every tag, with how many games carry it.
    ///
    /// Ordered so the quick-add tags come first and in their configured order, then everything else
    /// alphabetically — which is the order both the filter bar and the editor want, so neither has to
    /// re-sort.
    /// </summary>
    public async Task<List<TagContract>> GetAllAsync(bool quickAddOnly = false)
    {
        var query = context.Tags.AsNoTracking();
        if (quickAddOnly) query = query.Where(t => t.IsQuickAdd);

        return await query
            .OrderByDescending(t => t.IsQuickAdd)
            .ThenBy(t => t.SortOrder)
            .ThenBy(t => t.Name)
            .Select(t => new TagContract
            {
                Id = t.Id,
                Name = t.Name,
                Slug = t.Slug,
                IsQuickAdd = t.IsQuickAdd,
                GameCount = t.Games.Count,
            })
            .ToListAsync();
    }

    /// <summary>
    /// Replaces a game's entire tag set, creating any tag that does not exist yet.
    ///
    /// Whole-set rather than add/remove: it makes the operation idempotent, which matters because the
    /// editor saves the list it is holding rather than a diff, and a retried request must not be able
    /// to double-apply anything.
    /// </summary>
    public async Task<List<TagContract>> SetGameTagsAsync(Guid gameId, IEnumerable<string> names)
    {
        var game = await context.GameTownGames
            .Include(g => g.Tags)
            .FirstOrDefaultAsync(g => g.Id == gameId)
            ?? throw new KeyNotFoundException($"Game with ID {gameId} not found.");

        var requested = Normalise(names);

        // What the game is losing, captured before the collection is rewritten — the orphan sweep at
        // the end needs to know which tags to reconsider, and afterwards it cannot tell.
        var removed = game.Tags.Where(t => !requested.ContainsKey(t.Slug)).Select(t => t.Id).ToList();

        var resolved = await ResolveAsync(requested);

        game.Tags.Clear();
        foreach (var tag in resolved) game.Tags.Add(tag);

        await context.SaveChangesAsync();
        await RemoveOrphansAsync(removed);

        return [.. resolved.Select(ToContract)];
    }

    public async Task<List<TagContract>> GetGameTagsAsync(Guid gameId)
        => await context.GameTownGames
            .AsNoTracking()
            .Where(g => g.Id == gameId)
            .SelectMany(g => g.Tags)
            .OrderByDescending(t => t.IsQuickAdd)
            .ThenBy(t => t.SortOrder)
            .ThenBy(t => t.Name)
            .Select(t => new TagContract
            {
                Id = t.Id,
                Name = t.Name,
                Slug = t.Slug,
                IsQuickAdd = t.IsQuickAdd,
            })
            .ToListAsync();

    /// <summary>
    /// Matches each requested tag to an existing row, coining a new one where there is none.
    ///
    /// The lookup is by slug against what is already stored: two contributors tagging two games
    /// "split screen" in the same session must land on the one row, not collide on the UNIQUE index.
    /// The <c>Local</c> check covers the same tag appearing twice within one unit of work, which the
    /// database query cannot see because nothing has been saved yet.
    /// </summary>
    private async Task<List<Tag>> ResolveAsync(Dictionary<string, string> requested)
    {
        if (requested.Count == 0) return [];

        var slugs = requested.Keys.ToList();
        var stored = await context.Tags.Where(t => slugs.Contains(t.Slug)).ToListAsync();

        var resolved = new List<Tag>();
        foreach (var (slug, name) in requested)
        {
            var match = stored.FirstOrDefault(t => t.Slug == slug)
                        ?? context.Tags.Local.FirstOrDefault(t => t.Slug == slug);

            if (match is null)
            {
                // Id left to EF: DatabaseContextConfiguration restores ValueGeneratedOnAdd for this
                // key, which the scaffolder marks ValueGeneratedNever because SQLite declares no
                // default for it. Without that every new tag would insert Guid.Empty.
                match = new Tag { Name = name, Slug = slug };
                context.Tags.Add(match);
            }

            resolved.Add(match);
        }

        return resolved;
    }

    /// <summary>
    /// Deletes tags that no game carries any more.
    ///
    /// Without this the tag list only ever grows: a typo applied once and corrected stays in the
    /// filter bar forever. Quick-add tags are exempt — they are the fixed vocabulary the editor
    /// offers as buttons, and a library with no co-op games in it still needs the button.
    /// </summary>
    private async Task RemoveOrphansAsync(List<Guid> candidateIds)
    {
        if (candidateIds.Count == 0) return;

        var orphans = await context.Tags
            .Where(t => candidateIds.Contains(t.Id) && !t.IsQuickAdd && t.Games.Count == 0)
            .ToListAsync();

        if (orphans.Count == 0) return;

        context.Tags.RemoveRange(orphans);
        await context.SaveChangesAsync();
    }

    private static TagContract ToContract(Tag tag) => new()
    {
        Id = tag.Id,
        Name = tag.Name,
        Slug = tag.Slug,
        IsQuickAdd = tag.IsQuickAdd,
    };

    /// <summary>
    /// Turns raw input into slug -> display-name pairs, dropping blanks and duplicates.
    ///
    /// First spelling wins on a duplicate, so "LAN" followed by "lan" keeps "LAN". That only decides
    /// the name of a tag being *created*; an existing row keeps the name it already has, because a
    /// save from one game must not rename a tag under every other game carrying it.
    /// </summary>
    public static Dictionary<string, string> Normalise(IEnumerable<string> names)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var name = raw.Trim();
            if (name.Length > MaxNameLength) name = name[..MaxNameLength].TrimEnd();

            var slug = Slugify(name);
            if (slug.Length == 0) continue;

            result.TryAdd(slug, name);
        }

        return result;
    }

    /// <summary>
    /// A tag's identity: lowercase, with every run of non-alphanumerics collapsed to a single '-'.
    ///
    /// Letters and digits are kept by Unicode category rather than by an ASCII range, so "Fælles skærm"
    /// slugs to "fælles-skærm" instead of losing its characters to a row of dashes. SQLite's NOCASE
    /// collation folds ASCII only, which is exactly why the slug is lowercased here in C# and the
    /// UNIQUE index is left with the default binary collation — the normalisation has to be something
    /// the database is not being asked to reproduce.
    /// </summary>
    public static string Slugify(string name)
    {
        var builder = new StringBuilder(name.Length);
        var pendingSeparator = false;

        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                // Held back rather than appended immediately, so a trailing run of punctuation cannot
                // leave the slug ending in '-'.
                if (pendingSeparator && builder.Length > 0) builder.Append('-');
                pendingSeparator = false;
                builder.Append(character);
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.ToString();
    }
}
