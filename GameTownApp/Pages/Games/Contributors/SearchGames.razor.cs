using GameTownApp.Models.Games;
using GameTownApp.Services;
using Microsoft.AspNetCore.Components;
namespace GameTownApp.Pages.Games.Contributors;
public partial class SearchGames
{
    private List<RAWGGameViewModel>? GamesResult;
    [Inject]
    private GamesService GamesService { get; set; } = default!;
    private string? SearchQuery;
    private int Page = 1;
    private int PageSize = 20;

    private bool IsLoading = false;
    private async Task SearchGamesMetadataAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            GamesResult = null;
            return;
        }
        IsLoading = true;
        try
        {
            GamesResult = await GamesService.SearchGamesMetadata(SearchQuery, Page, PageSize);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error searching games: {ex.Message}");
            GamesResult = null;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
