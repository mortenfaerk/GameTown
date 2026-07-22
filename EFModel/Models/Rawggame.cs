using System;
using System.Collections.Generic;

namespace EFModel.Models;

public partial class Rawggame
{
    public int Id { get; set; }

    public string Slug { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? NameOriginal { get; set; }

    public string Description { get; set; } = null!;

    public int? Metacritic { get; set; }

    public DateOnly? Released { get; set; }

    public bool? Tba { get; set; }

    public DateTime? Updated { get; set; }

    public string? BackgroundImage { get; set; }

    public string? BackgroundImageAdditional { get; set; }

    public string Website { get; set; } = null!;

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

    public virtual ICollection<GameTownGame> GameTownGames { get; set; } = new List<GameTownGame>();

    public virtual ICollection<Rawgdeveloper> Developers { get; set; } = new List<Rawgdeveloper>();

    public virtual ICollection<Rawggenre> Genres { get; set; } = new List<Rawggenre>();

    public virtual ICollection<Rawgscreenshot> Screenshots { get; set; } = new List<Rawgscreenshot>();
}
