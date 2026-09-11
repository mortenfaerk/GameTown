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

    public virtual DbSet<LanSuggestion> LanSuggestions { get; set; }

    public virtual DbSet<MetadataDeveloper> MetadataDevelopers { get; set; }

    public virtual DbSet<MetadataGame> MetadataGames { get; set; }

    public virtual DbSet<MetadataGenre> MetadataGenres { get; set; }

    public virtual DbSet<MetadataScreenshot> MetadataScreenshots { get; set; }

    public virtual DbSet<Rawgdeveloper> Rawgdevelopers { get; set; }

    public virtual DbSet<Rawggame> Rawggames { get; set; }

    public virtual DbSet<Rawggenre> Rawggenres { get; set; }

    public virtual DbSet<Rawgscreenshot> Rawgscreenshots { get; set; }

    public virtual DbSet<SchemaVersion> SchemaVersions { get; set; }

    public virtual DbSet<Setting> Settings { get; set; }

    public virtual DbSet<Tag> Tags { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameTownGame>(entity =>
        {
            entity.ToTable("GameTownGame");

            entity.HasIndex(e => e.ArchiveSha256, "IX_GameTownGame_ArchiveSha256");

            entity.HasIndex(e => e.MetadataId, "IX_GameTownGame_MetadataId");

            entity.HasIndex(e => e.Title, "IX_GameTownGame_Title");

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnType("uniqueidentifier");
            entity.Property(e => e.GuideBaked).HasColumnType("boolean");
            entity.Property(e => e.RawggameId).HasColumnName("RAWGGameId");
            entity.Property(e => e.Title).UseCollation("NOCASE");
            entity.Property(e => e.Url).HasColumnName("URL");

            entity.HasOne(d => d.Metadata).WithMany(p => p.GameTownGames).HasForeignKey(d => d.MetadataId);

            entity.HasOne(d => d.Rawggame).WithMany(p => p.GameTownGames).HasForeignKey(d => d.RawggameId);

            entity.HasMany(d => d.Tags).WithMany(p => p.Games)
                .UsingEntity<Dictionary<string, object>>(
                    "GameTownGameTag",
                    r => r.HasOne<Tag>().WithMany().HasForeignKey("TagId"),
                    l => l.HasOne<GameTownGame>().WithMany().HasForeignKey("GameId"),
                    j =>
                    {
                        j.HasKey("GameId", "TagId");
                        j.ToTable("GameTownGame_Tags");
                        j.HasIndex(new[] { "TagId" }, "IX_GameTownGame_Tags_TagId");
                        j.IndexerProperty<Guid>("GameId").HasColumnType("uniqueidentifier");
                        j.IndexerProperty<Guid>("TagId").HasColumnType("uniqueidentifier");
                    });
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

        modelBuilder.Entity<LanSuggestion>(entity =>
        {
            entity.ToTable("LanSuggestion");

            entity.HasIndex(e => e.RemoteMatchId, "IX_LanSuggestion_Bound").IsUnique();

            entity.HasIndex(e => e.GameId, "IX_LanSuggestion_GameId");

            entity.HasIndex(e => e.LanEventName, "IX_LanSuggestion_LanEvent");

            entity.HasIndex(e => e.RemoteId, "IX_LanSuggestion_RemoteId").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnType("uniqueidentifier");
            entity.Property(e => e.AutoMatchBlocked).HasColumnType("boolean");
            entity.Property(e => e.Dismissed).HasColumnType("boolean");
            entity.Property(e => e.FirstSeenUtc).HasColumnType("datetime");
            entity.Property(e => e.GameId).HasColumnType("uniqueidentifier");
            entity.Property(e => e.LanEventName).UseCollation("NOCASE");
            entity.Property(e => e.LastSeenUtc).HasColumnType("datetime");
            entity.Property(e => e.Name).UseCollation("NOCASE");
            entity.Property(e => e.Played).HasColumnType("boolean");
            entity.Property(e => e.PushedAtUtc).HasColumnType("datetime");
            entity.Property(e => e.RemoteId).HasColumnType("bigint");
            entity.Property(e => e.RemoteMatchId).HasColumnType("uniqueidentifier");

            entity.HasOne(d => d.Game).WithMany(p => p.LanSuggestions)
                .HasForeignKey(d => d.GameId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<MetadataDeveloper>(entity =>
        {
            entity.HasIndex(e => new { e.Provider, e.ExternalId }, "IX_MetadataDevelopers_provider_external_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ExternalId).HasColumnName("external_id");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Provider).HasColumnName("provider");
            entity.Property(e => e.Slug).HasColumnName("slug");
        });

        modelBuilder.Entity<MetadataGame>(entity =>
        {
            entity.HasIndex(e => new { e.Provider, e.ExternalId }, "IX_MetadataGames_provider_external_id").IsUnique();

            entity.HasIndex(e => e.Provider, "IX_MetadataGames_Provider");

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CriticScore).HasColumnName("critic_score");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.ExternalId).HasColumnName("external_id");
            entity.Property(e => e.Image).HasColumnName("image");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Provider).HasColumnName("provider");
            entity.Property(e => e.Rating).HasColumnName("rating");
            entity.Property(e => e.Released)
                .HasColumnType("date")
                .HasColumnName("released");
            entity.Property(e => e.Slug).HasColumnName("slug");
            entity.Property(e => e.Updated)
                .HasColumnType("datetime")
                .HasColumnName("updated");
            entity.Property(e => e.Website)
                .HasDefaultValue("")
                .HasColumnName("website");

            entity.HasMany(d => d.Developers).WithMany(p => p.Metadata)
                .UsingEntity<Dictionary<string, object>>(
                    "MetadataGamesDeveloper",
                    r => r.HasOne<MetadataDeveloper>().WithMany().HasForeignKey("DeveloperId"),
                    l => l.HasOne<MetadataGame>().WithMany().HasForeignKey("MetadataId"),
                    j =>
                    {
                        j.HasKey("MetadataId", "DeveloperId");
                        j.ToTable("MetadataGames_Developers");
                        j.IndexerProperty<int>("MetadataId").HasColumnName("metadata_id");
                        j.IndexerProperty<int>("DeveloperId").HasColumnName("developer_id");
                    });

            entity.HasMany(d => d.Genres).WithMany(p => p.Metadata)
                .UsingEntity<Dictionary<string, object>>(
                    "MetadataGamesGenre",
                    r => r.HasOne<MetadataGenre>().WithMany().HasForeignKey("GenreId"),
                    l => l.HasOne<MetadataGame>().WithMany().HasForeignKey("MetadataId"),
                    j =>
                    {
                        j.HasKey("MetadataId", "GenreId");
                        j.ToTable("MetadataGames_Genres");
                        j.IndexerProperty<int>("MetadataId").HasColumnName("metadata_id");
                        j.IndexerProperty<int>("GenreId").HasColumnName("genre_id");
                    });

            entity.HasMany(d => d.Screenshots).WithMany(p => p.Metadata)
                .UsingEntity<Dictionary<string, object>>(
                    "MetadataGamesScreenshot",
                    r => r.HasOne<MetadataScreenshot>().WithMany().HasForeignKey("ScreenshotId"),
                    l => l.HasOne<MetadataGame>().WithMany().HasForeignKey("MetadataId"),
                    j =>
                    {
                        j.HasKey("MetadataId", "ScreenshotId");
                        j.ToTable("MetadataGames_Screenshots");
                        j.IndexerProperty<int>("MetadataId").HasColumnName("metadata_id");
                        j.IndexerProperty<int>("ScreenshotId").HasColumnName("screenshot_id");
                    });
        });

        modelBuilder.Entity<MetadataGenre>(entity =>
        {
            entity.HasIndex(e => new { e.Provider, e.ExternalId }, "IX_MetadataGenres_provider_external_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ExternalId).HasColumnName("external_id");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Provider).HasColumnName("provider");
            entity.Property(e => e.Slug).HasColumnName("slug");
        });

        modelBuilder.Entity<MetadataScreenshot>(entity =>
        {
            entity.HasIndex(e => new { e.Provider, e.ExternalId }, "IX_MetadataScreenshots_provider_external_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ExternalId).HasColumnName("external_id");
            entity.Property(e => e.Height).HasColumnName("height");
            entity.Property(e => e.Image).HasColumnName("image");
            entity.Property(e => e.IsDeleted)
                .HasColumnType("boolean")
                .HasColumnName("is_deleted");
            entity.Property(e => e.Provider).HasColumnName("provider");
            entity.Property(e => e.Width).HasColumnName("width");
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

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.HasIndex(e => e.Slug, "IX_Tags_Slug").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnType("uniqueidentifier");
            entity.Property(e => e.IsQuickAdd).HasColumnType("boolean");
            entity.Property(e => e.Name).UseCollation("NOCASE");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
