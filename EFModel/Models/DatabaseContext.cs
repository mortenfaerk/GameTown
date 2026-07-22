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

    public virtual DbSet<RefreshToken> RefreshTokens { get; set; }

    // NOTE: the scaffolder emits an OnConfiguring override with the full connection string
    // (password included) baked in. It is deliberately removed — the connection is supplied by
    // AddDbContext/UseNpgsql in API/Startup/DependenciesConfig.cs from user-secrets.
    // Delete it again after every re-scaffold.

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameTownGame>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("GameTownGame_pkey");

            entity.ToTable("GameTownGame");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.RawggameId).HasColumnName("RAWGGameId");
            entity.Property(e => e.Title).HasMaxLength(500);
            entity.Property(e => e.Url)
                .HasMaxLength(500)
                .HasColumnName("URL");

            entity.HasOne(d => d.Rawggame).WithMany(p => p.GameTownGames)
                .HasForeignKey(d => d.RawggameId)
                .HasConstraintName("FK_GameTownGame_Games");
        });

        modelBuilder.Entity<GameTownRole>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("GameTownRoles_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedBy).HasMaxLength(50);
            entity.Property(e => e.CreatedDate).HasDefaultValueSql("now()");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ModifiedBy).HasMaxLength(50);
            entity.Property(e => e.ModifiedDate).HasDefaultValueSql("now()");
            entity.Property(e => e.Role).HasMaxLength(50);
        });

        modelBuilder.Entity<GameTownUser>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("GameTownUsers_pkey");

            entity.HasIndex(e => e.Username, "GameTownUsers_Username_key").IsUnique();

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.CreatedBy).HasMaxLength(256);
            entity.Property(e => e.DisplayName).HasMaxLength(256);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.LastModifiedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.LastModifiedBy).HasMaxLength(256);
            entity.Property(e => e.Notes).HasMaxLength(512);
            entity.Property(e => e.PasswordHash).HasMaxLength(256);
            entity.Property(e => e.Salt).HasMaxLength(256);
            entity.Property(e => e.Username).HasMaxLength(256);

            entity.HasMany(d => d.Apiroles).WithMany(p => p.Apiusers)
                .UsingEntity<Dictionary<string, object>>(
                    "GameTownUsersRole",
                    r => r.HasOne<GameTownRole>().WithMany()
                        .HasForeignKey("ApiroleId")
                        .OnDelete(DeleteBehavior.ClientSetNull)
                        .HasConstraintName("FK_APIUsers_APIRoles_APIRoleId"),
                    l => l.HasOne<GameTownUser>().WithMany()
                        .HasForeignKey("ApiuserId")
                        .OnDelete(DeleteBehavior.ClientSetNull)
                        .HasConstraintName("FK_APIUsers_APIRoles_APIUserId"),
                    j =>
                    {
                        j.HasKey("ApiuserId", "ApiroleId").HasName("PK_APIUsers_APIRoles");
                        j.ToTable("GameTownUsers_Roles");
                        j.IndexerProperty<Guid>("ApiuserId").HasColumnName("APIUserId");
                        j.IndexerProperty<Guid>("ApiroleId").HasColumnName("APIRoleId");
                    });
        });

        modelBuilder.Entity<Rawgdeveloper>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("RAWGDevelopers_pkey");

            entity.ToTable("RAWGDevelopers");

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.GamesCount).HasColumnName("games_count");
            entity.Property(e => e.ImageBackground)
                .HasMaxLength(500)
                .HasColumnName("image_background");
            entity.Property(e => e.Name)
                .HasMaxLength(255)
                .HasColumnName("name");
            entity.Property(e => e.Slug)
                .HasMaxLength(255)
                .HasColumnName("slug");
        });

        modelBuilder.Entity<Rawggame>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("RAWGGames_pkey");

            entity.ToTable("RAWGGames");

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.AchievementsCount).HasColumnName("achievements_count");
            entity.Property(e => e.AdditionsCount).HasColumnName("additions_count");
            entity.Property(e => e.BackgroundImage)
                .HasMaxLength(500)
                .HasColumnName("background_image");
            entity.Property(e => e.BackgroundImageAdditional)
                .HasMaxLength(500)
                .HasColumnName("background_image_additional");
            entity.Property(e => e.CreatorsCount).HasColumnName("creators_count");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.DominantColor)
                .HasMaxLength(10)
                .HasColumnName("dominant_color");
            entity.Property(e => e.GameSeriesCount).HasColumnName("game_series_count");
            entity.Property(e => e.Metacritic).HasColumnName("metacritic");
            entity.Property(e => e.MetacriticUrl)
                .HasMaxLength(500)
                .HasColumnName("metacritic_url");
            entity.Property(e => e.MoviesCount).HasColumnName("movies_count");
            entity.Property(e => e.Name)
                .HasMaxLength(255)
                .HasColumnName("name");
            entity.Property(e => e.NameOriginal)
                .HasMaxLength(255)
                .HasColumnName("name_original");
            entity.Property(e => e.ParentAchievementsCount).HasColumnName("parent_achievements_count");
            entity.Property(e => e.ParentsCount).HasColumnName("parents_count");
            entity.Property(e => e.Playtime).HasColumnName("playtime");
            entity.Property(e => e.Rating).HasColumnName("rating");
            entity.Property(e => e.RatingTop).HasColumnName("rating_top");
            entity.Property(e => e.RatingsCount).HasColumnName("ratings_count");
            entity.Property(e => e.RedditCount).HasColumnName("reddit_count");
            entity.Property(e => e.RedditUrl)
                .HasMaxLength(500)
                .HasColumnName("reddit_url");
            entity.Property(e => e.Released).HasColumnName("released");
            entity.Property(e => e.ReviewsCount).HasColumnName("reviews_count");
            entity.Property(e => e.ReviewsTextCount).HasColumnName("reviews_text_count");
            entity.Property(e => e.SaturatedColor)
                .HasMaxLength(10)
                .HasColumnName("saturated_color");
            entity.Property(e => e.ScreenshotsCount).HasColumnName("screenshots_count");
            entity.Property(e => e.Slug)
                .HasMaxLength(255)
                .HasColumnName("slug");
            entity.Property(e => e.SuggestionsCount).HasColumnName("suggestions_count");
            entity.Property(e => e.Tba).HasColumnName("tba");
            entity.Property(e => e.TwitchCount).HasColumnName("twitch_count");
            entity.Property(e => e.Updated)
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updated");
            entity.Property(e => e.Website)
                .HasMaxLength(500)
                .HasColumnName("website");
            entity.Property(e => e.YoutubeCount).HasColumnName("youtube_count");

            entity.HasMany(d => d.Developers).WithMany(p => p.Games)
                .UsingEntity<Dictionary<string, object>>(
                    "RawggamesDeveloper",
                    r => r.HasOne<Rawgdeveloper>().WithMany()
                        .HasForeignKey("DeveloperId")
                        .OnDelete(DeleteBehavior.ClientSetNull)
                        .HasConstraintName("RAWGGames_Developers_developer_id_fkey"),
                    l => l.HasOne<Rawggame>().WithMany()
                        .HasForeignKey("GameId")
                        .OnDelete(DeleteBehavior.ClientSetNull)
                        .HasConstraintName("RAWGGames_Developers_game_id_fkey"),
                    j =>
                    {
                        j.HasKey("GameId", "DeveloperId").HasName("RAWGGames_Developers_pkey");
                        j.ToTable("RAWGGames_Developers");
                        j.IndexerProperty<int>("GameId").HasColumnName("game_id");
                        j.IndexerProperty<int>("DeveloperId").HasColumnName("developer_id");
                    });

            entity.HasMany(d => d.Genres).WithMany(p => p.Games)
                .UsingEntity<Dictionary<string, object>>(
                    "RawggamesGenre",
                    r => r.HasOne<Rawggenre>().WithMany()
                        .HasForeignKey("GenreId")
                        .OnDelete(DeleteBehavior.ClientSetNull)
                        .HasConstraintName("RAWGGames_Genres_genre_id_fkey"),
                    l => l.HasOne<Rawggame>().WithMany()
                        .HasForeignKey("GameId")
                        .OnDelete(DeleteBehavior.ClientSetNull)
                        .HasConstraintName("RAWGGames_Genres_game_id_fkey"),
                    j =>
                    {
                        j.HasKey("GameId", "GenreId").HasName("RAWGGames_Genres_pkey");
                        j.ToTable("RAWGGames_Genres");
                        j.IndexerProperty<int>("GameId").HasColumnName("game_id");
                        j.IndexerProperty<int>("GenreId").HasColumnName("genre_id");
                    });

            entity.HasMany(d => d.Screenshots).WithMany(p => p.Games)
                .UsingEntity<Dictionary<string, object>>(
                    "RawggamesScreenshot",
                    r => r.HasOne<Rawgscreenshot>().WithMany()
                        .HasForeignKey("Screenshotid")
                        .OnDelete(DeleteBehavior.ClientSetNull)
                        .HasConstraintName("RAWGGames_Screenshots_screenshotid_fkey"),
                    l => l.HasOne<Rawggame>().WithMany()
                        .HasForeignKey("Gameid")
                        .OnDelete(DeleteBehavior.ClientSetNull)
                        .HasConstraintName("RAWGGames_Screenshots_gameid_fkey"),
                    j =>
                    {
                        j.HasKey("Gameid", "Screenshotid").HasName("RAWGGames_Screenshots_pkey");
                        j.ToTable("RAWGGames_Screenshots");
                        j.IndexerProperty<int>("Gameid").HasColumnName("gameid");
                        j.IndexerProperty<int>("Screenshotid").HasColumnName("screenshotid");
                    });
        });

        modelBuilder.Entity<Rawggenre>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("RAWGGenres_pkey");

            entity.ToTable("RAWGGenres");

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ImageBackground)
                .HasMaxLength(500)
                .HasColumnName("image_background");
            entity.Property(e => e.Name)
                .HasMaxLength(255)
                .HasColumnName("name");
            entity.Property(e => e.Slug)
                .HasMaxLength(255)
                .HasColumnName("slug");
        });

        modelBuilder.Entity<Rawgscreenshot>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("RAWGScreenshots_pkey");

            entity.ToTable("RAWGScreenshots");

            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Height).HasColumnName("height");
            entity.Property(e => e.Image)
                .HasMaxLength(500)
                .HasColumnName("image");
            entity.Property(e => e.IsDeleted).HasColumnName("is_deleted");
            entity.Property(e => e.Width).HasColumnName("width");
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("RefreshTokens_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.Token).HasMaxLength(512);

            entity.HasOne(d => d.User).WithMany(p => p.RefreshTokens)
                .HasForeignKey(d => d.UserId)
                .HasConstraintName("FK_RefreshTokens_Users");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
