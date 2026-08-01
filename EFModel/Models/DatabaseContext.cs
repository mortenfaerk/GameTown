using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace EFModel.Models;

public partial class DatabaseContext : DbContext
{
    public DatabaseContext()
    {
    }

    public DatabaseContext(DbContextOptions<DatabaseContext> options)
        : base(options)
    {
    }

    public virtual DbSet<GameTownGame> GameTownGames { get; set; }

    public virtual DbSet<GameTownRole> GameTownRoles { get; set; }

    public virtual DbSet<GameTownUser> GameTownUsers { get; set; }

    public virtual DbSet<Rawgdeveloper> Rawgdevelopers { get; set; }

    public virtual DbSet<Rawggame> Rawggames { get; set; }

    public virtual DbSet<Rawggenre> Rawggenres { get; set; }

    public virtual DbSet<Rawgscreenshot> Rawgscreenshots { get; set; }

    public virtual DbSet<SchemaVersion> SchemaVersions { get; set; }

    public virtual DbSet<Setting> Settings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameTownGame>(entity =>
        {
            entity.ToTable("GameTownGame");

            entity.HasIndex(e => e.ArchiveSha256, "IX_GameTownGame_ArchiveSha256");

            entity.HasIndex(e => e.Title, "IX_GameTownGame_Title");

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnType("uniqueidentifier");
            entity.Property(e => e.RawggameId).HasColumnName("RAWGGameId");
            entity.Property(e => e.Title).UseCollation("NOCASE");
            entity.Property(e => e.Url).HasColumnName("URL");

            entity.HasOne(d => d.Rawggame).WithMany(p => p.GameTownGames).HasForeignKey(d => d.RawggameId);
        });

        modelBuilder.Entity<GameTownRole>(entity =>
        {
            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnType("uniqueidentifier");
            entity.Property(e => e.CreatedDate)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("datetime");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnType("boolean");
            entity.Property(e => e.ModifiedDate)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("datetime");
        });

        modelBuilder.Entity<GameTownUser>(entity =>
        {
            entity.HasIndex(e => e.Username, "IX_GameTownUsers_Username").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnType("uniqueidentifier");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("datetime");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnType("boolean");
            entity.Property(e => e.LastModifiedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("datetime");

            entity.HasMany(d => d.Apiroles).WithMany(p => p.Apiusers)
                .UsingEntity<Dictionary<string, object>>(
                    "GameTownUsersRole",
                    r => r.HasOne<GameTownRole>().WithMany()
                        .HasForeignKey("ApiroleId")
                        .OnDelete(DeleteBehavior.ClientSetNull),
                    l => l.HasOne<GameTownUser>().WithMany()
                        .HasForeignKey("ApiuserId")
                        .OnDelete(DeleteBehavior.ClientSetNull),
                    j =>
                    {
                        j.HasKey("ApiuserId", "ApiroleId");
                        j.ToTable("GameTownUsers_Roles");
                        j.IndexerProperty<Guid>("ApiuserId")
                            .HasColumnType("uniqueidentifier")
                            .HasColumnName("APIUserId");
                        j.IndexerProperty<Guid>("ApiroleId")
                            .HasColumnType("uniqueidentifier")
                            .HasColumnName("APIRoleId");
                    });
        });

        modelBuilder.Entity<Rawgdeveloper>(entity =>
        {
            entity.ToTable("RAWGDevelopers");

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.GamesCount).HasColumnName("games_count");
            entity.Property(e => e.ImageBackground).HasColumnName("image_background");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Slug).HasColumnName("slug");
        });

        modelBuilder.Entity<Rawggame>(entity =>
        {
            entity.ToTable("RAWGGames");

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.AchievementsCount).HasColumnName("achievements_count");
            entity.Property(e => e.AdditionsCount).HasColumnName("additions_count");
            entity.Property(e => e.BackgroundImage).HasColumnName("background_image");
            entity.Property(e => e.BackgroundImageAdditional).HasColumnName("background_image_additional");
            entity.Property(e => e.CreatorsCount).HasColumnName("creators_count");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.DominantColor).HasColumnName("dominant_color");
            entity.Property(e => e.GameSeriesCount).HasColumnName("game_series_count");
            entity.Property(e => e.Metacritic).HasColumnName("metacritic");
            entity.Property(e => e.MetacriticUrl).HasColumnName("metacritic_url");
            entity.Property(e => e.MoviesCount).HasColumnName("movies_count");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.NameOriginal).HasColumnName("name_original");
            entity.Property(e => e.ParentAchievementsCount).HasColumnName("parent_achievements_count");
            entity.Property(e => e.ParentsCount).HasColumnName("parents_count");
            entity.Property(e => e.Playtime).HasColumnName("playtime");
            entity.Property(e => e.Rating).HasColumnName("rating");
            entity.Property(e => e.RatingTop).HasColumnName("rating_top");
            entity.Property(e => e.RatingsCount).HasColumnName("ratings_count");
            entity.Property(e => e.RedditCount).HasColumnName("reddit_count");
            entity.Property(e => e.RedditUrl).HasColumnName("reddit_url");
            entity.Property(e => e.Released)
                .HasColumnType("date")
                .HasColumnName("released");
            entity.Property(e => e.ReviewsCount).HasColumnName("reviews_count");
            entity.Property(e => e.ReviewsTextCount).HasColumnName("reviews_text_count");
            entity.Property(e => e.SaturatedColor).HasColumnName("saturated_color");
            entity.Property(e => e.ScreenshotsCount).HasColumnName("screenshots_count");
            entity.Property(e => e.Slug).HasColumnName("slug");
            entity.Property(e => e.SuggestionsCount).HasColumnName("suggestions_count");
            entity.Property(e => e.Tba)
                .HasColumnType("boolean")
                .HasColumnName("tba");
            entity.Property(e => e.TwitchCount).HasColumnName("twitch_count");
            entity.Property(e => e.Updated)
                .HasColumnType("datetime")
                .HasColumnName("updated");
            entity.Property(e => e.Website).HasColumnName("website");
            entity.Property(e => e.YoutubeCount).HasColumnName("youtube_count");

            entity.HasMany(d => d.Developers).WithMany(p => p.Games)
                .UsingEntity<Dictionary<string, object>>(
                    "RawggamesDeveloper",
                    r => r.HasOne<Rawgdeveloper>().WithMany()
                        .HasForeignKey("DeveloperId")
                        .OnDelete(DeleteBehavior.ClientSetNull),
                    l => l.HasOne<Rawggame>().WithMany()
                        .HasForeignKey("GameId")
                        .OnDelete(DeleteBehavior.ClientSetNull),
                    j =>
                    {
                        j.HasKey("GameId", "DeveloperId");
                        j.ToTable("RAWGGames_Developers");
                        j.IndexerProperty<int>("GameId").HasColumnName("game_id");
                        j.IndexerProperty<int>("DeveloperId").HasColumnName("developer_id");
                    });

            entity.HasMany(d => d.Genres).WithMany(p => p.Games)
                .UsingEntity<Dictionary<string, object>>(
                    "RawggamesGenre",
                    r => r.HasOne<Rawggenre>().WithMany()
                        .HasForeignKey("GenreId")
                        .OnDelete(DeleteBehavior.ClientSetNull),
                    l => l.HasOne<Rawggame>().WithMany()
                        .HasForeignKey("GameId")
                        .OnDelete(DeleteBehavior.ClientSetNull),
                    j =>
                    {
                        j.HasKey("GameId", "GenreId");
                        j.ToTable("RAWGGames_Genres");
                        j.IndexerProperty<int>("GameId").HasColumnName("game_id");
                        j.IndexerProperty<int>("GenreId").HasColumnName("genre_id");
                    });

            entity.HasMany(d => d.Screenshots).WithMany(p => p.Games)
                .UsingEntity<Dictionary<string, object>>(
                    "RawggamesScreenshot",
                    r => r.HasOne<Rawgscreenshot>().WithMany()
                        .HasForeignKey("Screenshotid")
                        .OnDelete(DeleteBehavior.ClientSetNull),
                    l => l.HasOne<Rawggame>().WithMany()
                        .HasForeignKey("Gameid")
                        .OnDelete(DeleteBehavior.ClientSetNull),
                    j =>
                    {
                        j.HasKey("Gameid", "Screenshotid");
                        j.ToTable("RAWGGames_Screenshots");
                        j.IndexerProperty<int>("Gameid").HasColumnName("gameid");
                        j.IndexerProperty<int>("Screenshotid").HasColumnName("screenshotid");
                    });
        });

        modelBuilder.Entity<Rawggenre>(entity =>
        {
            entity.ToTable("RAWGGenres");

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ImageBackground).HasColumnName("image_background");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Slug).HasColumnName("slug");
        });

        modelBuilder.Entity<Rawgscreenshot>(entity =>
        {
            entity.ToTable("RAWGScreenshots");

            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Height).HasColumnName("height");
            entity.Property(e => e.Image).HasColumnName("image");
            entity.Property(e => e.IsDeleted)
                .HasColumnType("boolean")
                .HasColumnName("is_deleted");
            entity.Property(e => e.Width).HasColumnName("width");
        });

        modelBuilder.Entity<SchemaVersion>(entity =>
        {
            entity.HasKey(e => e.Version);

            entity.ToTable("SchemaVersion");

            entity.Property(e => e.Version).ValueGeneratedNever();
            entity.Property(e => e.AppliedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("datetime");
        });

        modelBuilder.Entity<Setting>(entity =>
        {
            entity.HasKey(e => e.Key);
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
