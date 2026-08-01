using API.Models.Games;
using EFModel.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace API.Services
{
    /// <summary>Where a game's archive lives on disk, and the title to present it under.</summary>
    public record GameFileInfo(string Url, string Title);

    public class GTGamesService
    {
        readonly DatabaseContext _context;
        readonly RAWGService _rawgService;
        readonly FileService _fileService;
        readonly MediaStore _media;

        public GTGamesService(
            DatabaseContext context, RAWGService rawgService, FileService fileService, MediaStore media)
        {
            _context = context;
            _rawgService = rawgService;
            _fileService = fileService;
            _media = media;
        }

        /// <summary>
        /// Everything a <see cref="GameContract"/> needs loaded, in one place.
        ///
        /// The four read paths kept drifting apart — screenshots were once missing from the detail
        /// query while the paged and search queries both had them, so the one screen that displays
        /// them got none. Tags would have been a fifth chance to make the same mistake.
        ///
        /// AsSplitQuery is not optional here: four collection includes in one statement multiply
        /// together (developers x genres x screenshots x tags), and the row count stops being
        /// something you can reason about.
        /// </summary>
        private IQueryable<GameTownGame> WithContractIncludes()
            => _context.GameTownGames
                .Include(g => g.Tags)
                .Include(g => g.Rawggame).ThenInclude(r => r!.Developers)
                .Include(g => g.Rawggame).ThenInclude(r => r!.Genres)
                .Include(g => g.Rawggame).ThenInclude(r => r!.Screenshots)
                .AsSplitQuery();

        public async Task<GameContract?> GetGameById(Guid id)
        {
            var game = await WithContractIncludes().SingleOrDefaultAsync(g => g.Id == id);
            return game?.ToContract();
        }
        /// <summary>
        /// Stored path plus title for a game. The title is what the browser should see as the
        /// download name: on disk the archive is a GUID, which is meaningless to the user.
        /// </summary>
        public async Task<GameFileInfo?> GetGameFileById(Guid id)
        {
            return await _context.GameTownGames
                .Where(g => g.Id == id)
                .Select(g => new GameFileInfo(g.Url, g.Title))
                .FirstOrDefaultAsync();
        }
        public async Task RemoveGameById(Guid id)
        {
            var game = await _context.GameTownGames.Include(g=>g.Rawggame).ThenInclude(rg => rg!.Screenshots).FirstOrDefaultAsync(g=>g.Id == id) ?? throw new KeyNotFoundException($"Game with ID {id} not found.");

            // Rawggame is null for games uploaded without RAWG metadata. The screenshots also hang
            // off the shared RAWG record, so only bin the image files once no other GameTown game
            // still points at it.
            if (game.Rawggame is not null)
            {
                var rawgStillInUse = await _context.GameTownGames
                    .AnyAsync(g => g.Id != game.Id && g.RawggameId == game.RawggameId);

                if (!rawgStillInUse)
                {
                    // Through MediaStore, which resolves against the DATA directory. This used to
                    // build its own path from Directory.GetCurrentDirectory() + "wwwroot/media" — the
                    // location re-hosted art was moved out of, precisely because an in-place upgrade
                    // deletes the application folder. Nothing has been written there for some time, so
                    // this quietly matched nothing and orphaned every screenshot it was meant to bin.
                    foreach (var screenshot in game.Rawggame.Screenshots)
                        _media.Delete(screenshot.Image);
                }
            }

            // The override, if this game had one. Unlike the RAWG screenshots above there is no
            // shared-use check to make: box art belongs to exactly one game by construction.
            _media.Delete(game.BoxArtUrl);

            // Uploads live under GameFilesPath, so resolve through FileService. The previous
            // wwwroot/games lookup never matched and silently orphaned every uploaded archive.
            // Resolution is bounds-checked: deleting a stored path verbatim would let any bad row
            // in the database unlink a file anywhere the service can write.
            try
            {
                var (resolved, filePath) = await _fileService.TryResolveGameFileAsync(game.Url);
                if (resolved && File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }catch (Exception ex)
            {
                throw new Exception($"Error deleting game file for game {game.Title}: {ex.Message}", ex);
            }
            _context.GameTownGames.Remove(game);
            await _context.SaveChangesAsync();

        }
        /// <summary>
        /// The title of the game already holding this archive hash, or null if it is new.
        ///
        /// Games uploaded before migration 003 have no hash recorded and are deliberately not
        /// matched — the null check keeps them from all colliding with each other.
        /// </summary>
        public async Task<string?> FindTitleByArchiveHash(string sha256)
        {
            if (string.IsNullOrEmpty(sha256)) return null;

            return await _context.GameTownGames
                .Where(g => g.ArchiveSha256 != null && g.ArchiveSha256 == sha256)
                .Select(g => g.Title)
                .FirstOrDefaultAsync();
        }

        /// <summary>
        /// Adds a game and returns its new id.
        ///
        /// The id is returned rather than discarded because the add-game screen has follow-up work to
        /// do with it — tags and box art are set by separate calls, and without the id there is
        /// nothing to address them to. This used to return void and the endpoint answered 204, which
        /// left the contributor having to find the game again to finish describing it.
        /// </summary>
        public async Task<Guid> AddGame(RequestGameTownGameDTO game, string fileUrl, double fileSize, string? archiveSha256 = null)
        {
            var newGame = new GameTownGame
            {
                Id = Guid.NewGuid(),
                Title = game.Title,
                HowTo = game.HowTo,
                Url = fileUrl,
                Size = fileSize,
                ArchiveSha256 = string.IsNullOrEmpty(archiveSha256) ? null : archiveSha256
            };
            if (game.RAWGGameId != null)
            {
                // Resolves against what is already stored, so a second game referencing the same
                // RAWG title (or a shared studio/genre) does not collide on the primary key.
                var rawgGame = await _rawgService.EnsureRawgGamePersisted(game.RAWGGameId);

                newGame.RawggameId = rawgGame.Id;
                newGame.Rawggame = rawgGame;
            }
            _context.GameTownGames.Add(newGame);
            await _context.SaveChangesAsync();
            return newGame.Id;
        }
        public async Task UpdateGame(GameTownGamePatchRequest game)
        {
            Guid gameGuid = Guid.Parse(game.Id);
            var existingGame = await _context.GameTownGames.FindAsync(gameGuid) ?? throw new KeyNotFoundException($"Game with ID {game.Id} not found.");
            if (game.Title != null)
                existingGame.Title = game.Title;
            if (game.HowTo != null)
                existingGame.HowTo = game.HowTo;
            if (game.RawgGameId != null)
            {
                var rawgGame = await _rawgService.EnsureRawgGamePersisted(game.RawgGameId);

                existingGame.RawggameId = rawgGame.Id;
                existingGame.Rawggame = rawgGame;
            }
            // Url is intentionally not patchable — it is the on-disk location, set once at upload.
            await _context.SaveChangesAsync();
        }
        public Task<List<GameContract>> GetGamePaged(int page, int page_size, IEnumerable<string>? tagSlugs = null)
        {
            if (page < 1 || page_size < 1)
                throw new ArgumentOutOfRangeException("Page and page size must be greater than zero.");

            return BrowseAsync(query: null, tagSlugs, page, page_size);
        }

        public Task<List<GameContract>> SearchGames(
            string query, int page, int pageSize, IEnumerable<string>? tagSlugs = null)
        {
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("Search query cannot be empty.", nameof(query));
            if (page < 1 || pageSize < 1)
                throw new ArgumentOutOfRangeException("Page and page size must be greater than zero.");

            return BrowseAsync(query, tagSlugs, page, pageSize);
        }

        /// <summary>
        /// The single query behind both the library listing and the search.
        ///
        /// They were two near-identical LINQ chains that had already drifted once (see
        /// <see cref="WithContractIncludes"/>), and tag filtering applies to both — an unfiltered shelf
        /// narrowed to "LAN" is the same operation as a search for "quake" narrowed to "LAN".
        ///
        /// Multiple tags are <b>AND</b>, not OR: each one narrows. "Split screen" plus "LAN" means a
        /// game that does both, because the question being asked is "what can the four of us play
        /// tonight" and an OR would answer a question nobody has.
        /// </summary>
        private async Task<List<GameContract>> BrowseAsync(
            string? query, IEnumerable<string>? tagSlugs, int page, int pageSize)
        {
            var games = WithContractIncludes();

            if (!string.IsNullOrWhiteSpace(query))
            {
                var lowerQuery = query.ToLower();

                // Tag names are matched as well as titles, so typing "co-op" into the search box does
                // what someone typing it expects rather than returning nothing. Slug as well as name,
                // because "co-op" and "Co-op" reach it by different routes.
                games = games.Where(g =>
                    g.Title.ToLower().Contains(lowerQuery)
                    || g.Tags.Any(t => t.Name.ToLower().Contains(lowerQuery)
                                       || t.Slug.Contains(lowerQuery)));
            }

            if (tagSlugs is not null)
            {
                // One Where per tag, deliberately. A single `.All(...)` over the requested list would
                // be OR-ish in translation; a separate EXISTS per tag is what actually means AND.
                foreach (var slug in tagSlugs.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())
                {
                    // Captured per iteration — without the local, every closure would see the last
                    // value and the filter would silently apply one tag N times.
                    var wanted = slug.Trim().ToLowerInvariant();
                    games = games.Where(g => g.Tags.Any(t => t.Slug == wanted));
                }
            }

            var page_of_games = await games
                // Paging without an ORDER BY leaves the order up to the database, so a game could
                // appear on two pages or on none. Title is the order the shelf is presented in anyway.
                .OrderBy(g => g.Title)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return [.. page_of_games.Select(g => g.ToContract())];
        }
    }
}
