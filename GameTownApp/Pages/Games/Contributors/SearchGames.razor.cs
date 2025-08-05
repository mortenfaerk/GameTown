using BlazorBootstrap;
using GameTownApp.Models.Games;
using GameTownApp.Services;
using Microsoft.AspNetCore.Components;
namespace GameTownApp.Pages.Games.Contributors;
public partial class SearchGames
{
    private List<RAWGGameViewModel>? GamesResult;
    private RAWGGameViewModel? SelectedGame;
    [Inject]
    private GamesService GamesService { get; set; } = default!;
    private string? SearchQuery;
    private int Page = 1;
    private int PageSize = 20;

    private bool IsLoading = false;
    
    private async Task<AutoCompleteDataProviderResult<RAWGGameViewModel>> GamesDataProvider(AutoCompleteDataProviderRequest<RAWGGameViewModel> request)
    {
        var games = await GamesService.SearchGamesMetadata(request.Filter.Value, 1, 20);
        return new AutoCompleteDataProviderResult<RAWGGameViewModel>
        {
            Data = games,
            TotalCount = games.Count
        };
    }
    private async Task OnGameSelected(RAWGGameViewModel? selectedGame)
    {
        if(selectedGame != null)
        {
            SearchQuery = selectedGame?.Name;
            SelectedGame = await GamesService.GetGameById(selectedGame.Id.ToString());
            return;
        }
    }
}
