using System.Text.Json.Serialization;

namespace GameTownApp.Models.Games
{
    public class RAWGGameViewModel
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("slug")]
        public string Slug { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("nameOriginal")]
        public string? NameOriginal { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("metacritic")]
        public int? Metacritic { get; set; }

        [JsonPropertyName("released")]
        public DateTime? Released { get; set; }

        [JsonPropertyName("tba")]
        public bool Tba { get; set; }

        [JsonPropertyName("updated")]
        public DateTime Updated { get; set; }

        [JsonPropertyName("backgroundImage")]
        public string? BackgroundImage { get; set; }

        [JsonPropertyName("backgroundImageAdditional")]
        public string? BackgroundImageAdditional { get; set; }

        [JsonPropertyName("website")]
        public string? Website { get; set; }

        [JsonPropertyName("rating")]
        public double Rating { get; set; }

        [JsonPropertyName("ratingTop")]
        public string? RatingTop { get; set; }

        [JsonPropertyName("playtime")]
        public int Playtime { get; set; }

        [JsonPropertyName("screenshotsCount")]
        public string? ScreenshotsCount { get; set; }

        [JsonPropertyName("moviesCount")]
        public string? MoviesCount { get; set; }

        [JsonPropertyName("creatorsCount")]
        public string? CreatorsCount { get; set; }

        [JsonPropertyName("achievementsCount")]
        public string? AchievementsCount { get; set; }

        [JsonPropertyName("parentAchievementsCount")]
        public string? ParentAchievementsCount { get; set; }

        [JsonPropertyName("redditUrl")]
        public string? RedditUrl { get; set; }

        [JsonPropertyName("redditCount")]
        public string? RedditCount { get; set; }

        [JsonPropertyName("twitchCount")]
        public string? TwitchCount { get; set; }

        [JsonPropertyName("youtubeCount")]
        public string? YoutubeCount { get; set; }

        [JsonPropertyName("reviewsTextCount")]
        public string? ReviewsTextCount { get; set; }

        [JsonPropertyName("ratingsCount")]
        public string? RatingsCount { get; set; }

        [JsonPropertyName("suggestionsCount")]
        public string? SuggestionsCount { get; set; }

        [JsonPropertyName("metacriticUrl")]
        public string? MetacriticUrl { get; set; }

        [JsonPropertyName("parentsCount")]
        public string? ParentsCount { get; set; }

        [JsonPropertyName("additionsCount")]
        public string? AdditionsCount { get; set; }

        [JsonPropertyName("gameSeriesCount")]
        public string? GameSeriesCount { get; set; }

        [JsonPropertyName("reviewsCount")]
        public string? ReviewsCount { get; set; }

        [JsonPropertyName("saturatedColor")]
        public string? SaturatedColor { get; set; }

        [JsonPropertyName("dominantColor")]
        public string? DominantColor { get; set; }

        public virtual ICollection<RAWGDeveloperViewModel> Developers { get; set; } = [];

        public virtual ICollection<RAWGGenreViewModel> Genres { get; set; } = [];
        [JsonPropertyName("screenshots")]
        public virtual ICollection<RAWGScreenshotViewModel> Screenshots { get; set; } = [];
        }
    }
