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

        public GTGamesService(DatabaseContext context, RAWGService rawgService, FileService fileService)
        {
            _context = context;
            _rawgService = rawgService;
            _fileService = fileService;
        }

        public async Task<GameContract?> GetGameById(Guid id)
        {
            var game = await _context.GameTownGames
                                                .Include(g => g.Rawggame)
                                                .ThenInclude(r => r!.Developers)
                                                .Include(g => g.Rawggame)
                                                .ThenInclude(r => r!.Genres)
                                                // Screenshots were missing here while the paged and
                                                // search queries both loaded them, so the detail
                                                // page — the one screen that shows them — got none.
                                                .Include(g => g.Rawggame)
                                                .ThenInclude(r => r!.Screenshots)
                                                .AsSplitQuery()
                                                .SingleOrDefaultAsync(g => g.Id == id);
            if (game == null)   
                    return null;  
            return game.ToContract();
        
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
                    try
                    {
                        foreach (var screenshot in game.Rawggame.Screenshots)
                        {
                            var fileName = Path.GetFileName(screenshot.Image);
                            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "media", fileName);
                            if (File.Exists(filePath))
                            {
                                File.Delete(filePath);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Error deleting screenshots for game {game.Title}: {ex.Message}", ex);
                    }
                }
            }

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

        public async Task AddGame(RequestGameTownGameDTO game, string fileUrl, double fileSize, string? archiveSha256 = null)
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
        public async Task<List<GameContract>> GetGamePaged(int page, int page_size)
        {
            if (page < 1 || page_size < 1)
                throw new ArgumentOutOfRangeException("Page and page size must be greater than zero.");
            var games = await _context.GameTownGames
                .Include(g => g.Rawggame)
                .ThenInclude(r => r!.Developers)
                .Include(g => g.Rawggame)
                .ThenInclude(r => r!.Genres)
                .Include(g => g.Rawggame)
                                    .ThenInclude(r => r!.Screenshots)
                // Paging without an ORDER BY leaves the order up to Postgres, so a game could appear
                // on two pages or on none. Title is the order the shelf is presented in anyway.
                .OrderBy(g => g.Title)
                .Skip((page - 1) * page_size)
                .Take(page_size)
                // Three collection includes in one query multiplies the rows together
                // (developers x genres x screenshots); split into separate queries instead.
                .AsSplitQuery()
                .ToListAsync();
            var results = new List<GameContract>();
            foreach (var game in games) {
                results.Add(game.ToContract());
                    };
            return results;
        }
        public async Task<List<GameContract>> SearchGames(string query, int page, int pageSize)
        {
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("Search query cannot be empty.", nameof(query));
            if (page < 1 || pageSize < 1)
                throw new ArgumentOutOfRangeException("Page and page size must be greater than zero.");
            var lowerQuery = query.ToLower();

            var games = await _context.GameTownGames
                .Include(g => g.Rawggame)
                    .ThenInclude(r => r!.Developers)
                .Include(g => g.Rawggame)
                    .ThenInclude(r => r!.Genres)
                .Include(g=>g.Rawggame)
                                    .ThenInclude(r => r!.Screenshots)
                .Where(g => g.Title.ToLower().Contains(lowerQuery))
                .OrderBy(g => g.Title)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .AsSplitQuery()
                .ToListAsync();
            return games.Select(g => g.ToContract()).ToList();
        }
    }
}
