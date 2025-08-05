using API.Models.Games;
using EFModel.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace API.Services
{
    public class GTGamesService
    {
        readonly DatabaseContext _context;
        readonly RAWGService _rawgService;

        public GTGamesService(DatabaseContext context, RAWGService rawgService)
        {
            _context = context;
            _rawgService = rawgService;
        }

        public async Task<ResponseGameTownGameDTO?> GetGameById(Guid id)
        {
            var game = await _context.GameTownGames
                                                .Include(g => g.Rawggame)
                                                .ThenInclude(r => r.Developers)
                                                .Include(g => g.Rawggame)
                                                .ThenInclude(r => r.Genres)
                                                .SingleOrDefaultAsync(g => g.Id == id);
            if (game == null)   
                    return null;  
            return new ResponseGameTownGameDTO(game);
        
        }
        public async Task<string?> GetGameUrlById(Guid id)
        {
            return  await _context.GameTownGames
                .Where(g => g.Id == id)
                .Select(g => g.Url)
                .FirstOrDefaultAsync();
        }
        public async Task RemoveGameById(Guid id)
        {
            var game = await _context.GameTownGames.Include(g=>g.Rawggame).ThenInclude(rg=>rg.Screenshots).FirstOrDefaultAsync(g=>g.Id == id) ?? throw new KeyNotFoundException($"Game with ID {id} not found.");
            if (game.Rawggame.Screenshots != null)
            {
                try
                {
                    foreach (var screenshot in game.Rawggame.Screenshots)
                    {
                        var fileName = Path.GetFileName(screenshot.Image);
                        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "media",fileName);
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
            //Delte gamefile at Url
            try
            {
                var fileName = Path.GetFileName(game.Url);
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot","games",fileName );
                if (File.Exists(filePath))
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
        public async Task AddGame(RequestGameTownGameDTO game, string fileUrl, double fileSize)
        {
            var newGame = new GameTownGame
            {
                Id = Guid.NewGuid(),
                Title = game.Title,
                HowTo = game.HowTo,
                Url = fileUrl,
                Size = fileSize
            };
            if (game.RAWGGameId != null) { 
                var rawgGame = await _rawgService.GetGameById(game.RAWGGameId) ?? throw new KeyNotFoundException($"RAWG Game with ID {game.RAWGGameId} not found.");


                var screenshots = await _rawgService.GetGameScreenshots(game.RAWGGameId);
                rawgGame.Screenshots = screenshots;

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
                var rawgGame = await _rawgService.GetGameById(game.RawgGameId) ?? throw new KeyNotFoundException($"RAWG Game with ID {game.RawgGameId} not found.");
                var screenshots = await _rawgService.GetGameScreenshots(game.RawgGameId);
                rawgGame.Screenshots = screenshots;

                existingGame.RawggameId = rawgGame.Id;
                existingGame.Rawggame = rawgGame;
            }
            if (game.Url != null)
                existingGame.Url = game.Url;
            await _context.SaveChangesAsync();
        }
        public async Task<List<ResponseGameTownGameDTO>> GetGamePaged(int page, int page_size)
        {
            if (page < 1 || page_size < 1)
                throw new ArgumentOutOfRangeException("Page and page size must be greater than zero.");
            var games = await _context.GameTownGames
                .Include(g => g.Rawggame)
                .ThenInclude(r => r.Developers)
                .Include(g => g.Rawggame)
                .ThenInclude(r => r.Genres)
                .Include(g => g.Rawggame)
                                    .ThenInclude(r => r.Screenshots)
                .Skip((page - 1) * page_size)
                .Take(page_size)
                .ToListAsync();
            var results = new List<ResponseGameTownGameDTO>();
            foreach (var game in games) {
                results.Add(new ResponseGameTownGameDTO(game));
                    };
            return results;
        }
        public async Task<List<ResponseGameTownGameDTO>> SearchGames(string query, int page, int pageSize)
        {
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("Search query cannot be empty.", nameof(query));
            if (page < 1 || pageSize < 1)
                throw new ArgumentOutOfRangeException("Page and page size must be greater than zero.");
            var lowerQuery = query.ToLower();

            var games = await _context.GameTownGames
                .Include(g => g.Rawggame)
                    .ThenInclude(r => r.Developers)
                .Include(g => g.Rawggame)
                    .ThenInclude(r => r.Genres)
                .Include(g=>g.Rawggame)
                                    .ThenInclude(r => r.Screenshots)
                .Where(g => g.Title.ToLower().Contains(lowerQuery))
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
            return games.Select(g => new ResponseGameTownGameDTO(g)).ToList();
        }
    }
}
