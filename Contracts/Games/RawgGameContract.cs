namespace GameTown.Contracts.Games;

/// <summary>RAWG metadata for a game, as re-served by the GameTown API.</summary>
public class RawgGameContract
{
    public int? Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? NameOriginal { get; set; }
    public string Description { get; set; } = string.Empty;
    public int? Metacritic { get; set; }
    public DateTime? Released { get; set; }
    public bool? Tba { get; set; }
    public DateTime? Updated { get; set; }
    public string? BackgroundImage { get; set; }
    public string? BackgroundImageAdditional { get; set; }
    public string Website { get; set; } = string.Empty;
    public double? Rating { get; set; }
    public int? RatingTop { get; set; }
    public int? Playtime { get; set; }
    public int? ScreenshotsCount { get; set; }
    public int? MoviesCount { get; set; }
    public int? CreatorsCount { get; set; }
    public int? AchievementsCount { get; set; }
    public int? ParentAchievementsCount { get; set; }
    public string? RedditUrl { get; set; }
    public int? RedditCount { get; set; }
    public int? TwitchCount { get; set; }
    public int? YoutubeCount { get; set; }
    public int? ReviewsTextCount { get; set; }
    public int? RatingsCount { get; set; }
    public int? SuggestionsCount { get; set; }
    public string? MetacriticUrl { get; set; }
    public int? ParentsCount { get; set; }
    public int? AdditionsCount { get; set; }
    public int? GameSeriesCount { get; set; }
    public int? ReviewsCount { get; set; }
    public string? SaturatedColor { get; set; }
    public string? DominantColor { get; set; }

    public List<ScreenshotContract> Screenshots { get; set; } = [];

    // Developers and Genres were eagerly loaded by GTGamesService but never exposed by the old
    // ResponseRAWGGameDTO, so the UI always rendered them empty. They are part of the contract now.
    public List<DeveloperContract> Developers { get; set; } = [];
    public List<GenreContract> Genres { get; set; } = [];
}
