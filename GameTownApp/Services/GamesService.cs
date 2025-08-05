using GameTownApp.Models.Games;
using System.Net.Http.Json;
using System.Reflection;

namespace GameTownApp.Services;

public class GamesService
{
    private readonly HttpClient _http;

    public GamesService(HttpClient httpClient)
    {
        _http = httpClient;
    }
    public async Task<List<RAWGGameViewModel>> SearchGamesMetadata(string query, int? page = 1, int? pageSize = 20)
    {
        var url = $"/meta/searchMetadata?query={Uri.EscapeDataString(query)}&page={page}&pageSize={pageSize}";
        var response = await _http.GetAsync(url);

        // Set a breakpoint here to inspect the raw response
        var rawJson = await response.Content.ReadAsStringAsync();

        // You can inspect 'rawJson' in the debugger before deserialization
        var result = System.Text.Json.JsonSerializer.Deserialize<List<RAWGGameViewModel>>(rawJson);

        return result ?? new List<RAWGGameViewModel>();

    }
    public async Task<RAWGGameViewModel?> GetGameById(string gameId)
    {
        var url = $"/meta/getGame/{Uri.EscapeDataString(gameId)}";
        var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Failed to retrieve game with ID {gameId}. Status code: {response.StatusCode}");
        }
        var rawJson = await response.Content.ReadAsStringAsync();
        var result = System.Text.Json.JsonSerializer.Deserialize<RAWGGameViewModel>(rawJson);
        return result;
    }
}
