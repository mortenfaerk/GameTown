using EFModel.Models;
using RestSharp;
using Newtonsoft.Json;
using API.Models.Games;
namespace API.Services;

public class RAWGService(string apikey, DatabaseContext _context)
{
    private string APIKey { get; set; } = apikey;
    private DatabaseContext context = _context;

    public async Task<Rawggame?> GetGameById(int id)
    {
        var client = new RestClient();
        var request = new RestRequest($"https://api.rawg.io/api/games/{id}", Method.Get).AddParameter("key",APIKey);
        RestResponse response = await client.ExecuteAsync(request);

        if (response.Content == null)
            return null;
        Rawggame? result = JsonConvert.DeserializeObject<Rawggame?>(response.Content);
        return result;
    }
    public async Task<List<Rawgscreenshot>> GetGameScreenshots(int id, int page = 1, int pageSize = 20)
    {
        var client = new RestClient();
        var request = new RestRequest($"https://api.rawg.io/api/games/{id}/screenshots", Method.Get)
            .AddParameter("key", APIKey)
            .AddParameter("page", page)
            .AddParameter("page_size", pageSize);
        
        RestResponse response = await client.ExecuteAsync(request);
        List<Rawgscreenshot> screenshots = new List<Rawgscreenshot>();
        if (response.Content == null)
            return screenshots;
        else
        {
            var result = JsonConvert.DeserializeObject<RawgScreenshotsResponse>(response.Content);
            if(result == null || result.Results == null)
                return screenshots;
            int foundCount = result.Count;
            screenshots = result.Results?.ToList() ?? new List<Rawgscreenshot>();
            while(result.Next != null && screenshots.Count < foundCount)
            {
                request = new RestRequest(result.Next, Method.Get).AddParameter("key", APIKey);
                response = await client.ExecuteAsync(request);
                if (response.Content == null)
                    break;
                result = JsonConvert.DeserializeObject<RawgScreenshotsResponse>(response.Content);
                if(result != null && result.Results != null)
                    screenshots.AddRange(result.Results);
            }
            return screenshots;
        }
    }
    public async Task<List<ResponseRAWGGameDTO>> SearchGames(string query, int page = 1, int pageSize = 20)
    {
        var client = new RestClient();
        var request = new RestRequest("https://api.rawg.io/api/games", Method.Get)
            .AddParameter("key", APIKey)
            .AddParameter("page", page)
            .AddParameter("page_size", pageSize)
            .AddParameter("search", query);
        
        RestResponse response = await client.ExecuteAsync(request);
        if (response.Content == null)
            return new List<ResponseRAWGGameDTO>();
        
        var result = JsonConvert.DeserializeObject<RAWGSearchResponse>(response.Content);
        List<ResponseRAWGGameDTO> games = new();
        if (result?.results != null)
        {
            foreach (var game in result.results)
            {
                games.Add(new ResponseRAWGGameDTO(game));
            }
        }
        return games;
    }
    public async Task AddGameToDb(int id)
    {
        var game = await GetGameById(id) ?? throw new KeyNotFoundException($"Game with ID {id} not found in RAWG API.");
        var screenshots = await GetGameScreenshots(id);
        var existingGame = context.Rawggames.FirstOrDefault(g => g.Id == game.Id);
        if(existingGame != null)
        {
            context.Entry(existingGame).CurrentValues.SetValues(game);
        }
        else
        {
            context.Rawggames.Add(game);
        }
        if (screenshots != null && screenshots.Count > 0)
        {
            foreach (var screenshot in screenshots)
            {
                if (!context.Rawgscreenshots.Any(s => s.Id == screenshot.Id))
                {
                    context.Rawgscreenshots.Add(screenshot);
                }
            }
            game.Screenshots = screenshots.ToArray();
        }
        await context.SaveChangesAsync();
    }

}
