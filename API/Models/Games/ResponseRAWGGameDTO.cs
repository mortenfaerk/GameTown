using EFModel.Models;

namespace API.Models.Games;

public class ResponseRAWGGameDTO(int? Id_, string Slug_, string Name_, string? NameOriginal_, string Description_, int? Metacritic_, DateTime? Released_, bool? Tba_, DateTime? Updated_, string? BackgroundImage_, string? BackgroundImageAdditional_, string Website_, double? Rating_, int? RatingTop_, int? Playtime_, int? ScreenshotsCount_, int? MoviesCount_, int? CreatorsCount_, int? AchievementsCount_, int? ParentAchievementsCount_, string? RedditUrl_, int? RedditCount_, int? TwitchCount_, int? YoutubeCount_, int? ReviewsTextCount_, int? RatingsCount_, int? SuggestionsCount_, string? MetacriticUrl_, int? ParentsCount_, int? AdditionsCount_, int? GameSeriesCount_, int? ReviewsCount_, string? SaturatedColor_, string? DominantColor_, Rawgscreenshot[]? rawgscreenshots_)
{
    public int? Id { get; set; } = Id_;
    public string Slug { get; set; } = Slug_;
    public string Name { get; set; } = Name_;
    public string? NameOriginal { get; set; } = NameOriginal_;
    public string Description { get; set; } = Description_;
    public int? Metacritic { get; set; } = Metacritic_;
    public DateTime? Released { get; set; } = Released_;
    public bool? Tba { get; set; } = Tba_;
    public DateTime? Updated { get; set; } = Updated_;
    public string? BackgroundImage { get; set; } = BackgroundImage_;
    public string? BackgroundImageAdditional { get; set; } = BackgroundImageAdditional_;
    public string Website { get; set; } = Website_;
    public double? Rating { get; set; } = Rating_;
    public int? RatingTop { get; set; } = RatingTop_;
    public int? Playtime { get; set; } = Playtime_;
    public int? ScreenshotsCount { get; set; } = ScreenshotsCount_;
    public int? MoviesCount { get; set; } = MoviesCount_;
    public int? CreatorsCount { get; set; } = CreatorsCount_;
    public int? AchievementsCount { get; set; } = AchievementsCount_;
    public int? ParentAchievementsCount { get; set; } = ParentAchievementsCount_;
    public string? RedditUrl { get; set; } = RedditUrl_;
    public int? RedditCount { get; set; } = RedditCount_;
    public int? TwitchCount { get; set; } = TwitchCount_;
    public int? YoutubeCount { get; set; } = YoutubeCount_;
    public int? ReviewsTextCount { get; set; } = ReviewsTextCount_;
    public int? RatingsCount { get; set; } = RatingsCount_;
    public int? SuggestionsCount { get; set; } = SuggestionsCount_;
    public string? MetacriticUrl { get; set; } = MetacriticUrl_;
    public int? ParentsCount { get; set; } = ParentsCount_;
    public int? AdditionsCount { get; set; } = AdditionsCount_;
    public int? GameSeriesCount { get; set; } = GameSeriesCount_;
    public int? ReviewsCount { get; set; } = ReviewsCount_;
    public string? SaturatedColor { get; set; } = SaturatedColor_;
    public string? DominantColor { get; set; } = DominantColor_;
    public Rawgscreenshot[]? Screenshots { get; set; } = rawgscreenshots_;
    public ResponseRAWGGameDTO(Rawggame entity)
            : this(
                entity.Id,
                entity.Slug,
                entity.Name,
                entity.NameOriginal,
                entity.Description,
                entity.Metacritic,
                entity.Released,
                entity.Tba,
                entity.Updated,
                entity.BackgroundImage,
                entity.BackgroundImageAdditional,
                entity.Website,
                entity.Rating,
                entity.RatingTop,
                entity.Playtime,
                entity.ScreenshotsCount,
                entity.MoviesCount,
                entity.CreatorsCount,
                entity.AchievementsCount,
                entity.ParentAchievementsCount,
                entity.RedditUrl,
                entity.RedditCount,
                entity.TwitchCount,
                entity.YoutubeCount,
                entity.ReviewsTextCount,
                entity.RatingsCount,
                entity.SuggestionsCount,
                entity.MetacriticUrl,
                entity.ParentsCount,
                entity.AdditionsCount,
                entity.GameSeriesCount,
                entity.ReviewsCount,
                entity.SaturatedColor,
                entity.DominantColor,
                entity.Screenshots?.ToArray()
            )
    {
    }
}