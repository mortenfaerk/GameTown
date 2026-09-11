using Microsoft.EntityFrameworkCore;

namespace EFModel.Models;

/// <summary>
/// Hand-written companion to the scaffolded <see cref="DatabaseContext"/>.
///
/// This file lives OUTSIDE Models/ on purpose: `dotnet ef dbcontext scaffold -f`
/// overwrites everything in that folder, and this configuration must survive a
/// re-scaffold. It hooks the `OnModelCreatingPartial` extension point the
/// scaffolder already emits, so nothing in the generated code needs editing.
/// </summary>
public partial class DatabaseContext
{
    /// <summary>
    /// Restores client-side Guid key generation.
    ///
    /// Under PostgreSQL these keys defaulted to `gen_random_uuid()`, so the database
    /// filled them in. SQLite has no such function, so the DDL declares no default —
    /// and the scaffolder, seeing a key with no default, emits `ValueGeneratedNever()`.
    /// Left alone that is a data-corruption bug rather than an inconvenience: EF would
    /// send `Guid.Empty` for every insert, the first row would succeed, and the *second*
    /// would fail on a primary-key collision.
    ///
    /// `ValueGeneratedOnAdd()` on a Guid key makes EF generate the value in memory before
    /// insert, which is the behaviour the application already assumed.
    ///
    /// RAWG entities are deliberately NOT listed here — they reuse RAWG's own integer ids
    /// and must keep `ValueGeneratedNever()`.
    ///
    /// The Metadata* entities ARE listed, for the opposite reason — see below.
    /// </summary>
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameTownGame>().Property(e => e.Id).ValueGeneratedOnAdd();
        modelBuilder.Entity<GameTownRole>().Property(e => e.Id).ValueGeneratedOnAdd();
        modelBuilder.Entity<GameTownUser>().Property(e => e.Id).ValueGeneratedOnAdd();
        modelBuilder.Entity<Tag>().Property(e => e.Id).ValueGeneratedOnAdd();
        modelBuilder.Entity<LanSuggestion>().Property(e => e.Id).ValueGeneratedOnAdd();

        // The same scaffolding artefact as the Guid keys above, arrived at from the other direction.
        //
        // These four have INTEGER primary keys, which in SQLite is an alias for the rowid and DOES
        // autoincrement — but the scaffolder cannot tell an id the database assigns from one the
        // application always supplies, and emits ValueGeneratedNever() for both. That is right for the
        // RAWG tables (every insert supplies RAWG's own id) and wrong here: unlike those, these keys
        // are SURROGATES. The provider's id lives in ExternalId, and the database assigns the key.
        //
        // Left alone the failure is the delayed kind this file exists to prevent: EF sends 0 for the
        // first insert, which succeeds — migration 007 seeds ids from RAWG's, so on an upgraded
        // install 0 is free — and the SECOND game added from a provider collides on the primary key.
        // A fresh install would see it on the second game ever added; a large library, weeks later.
        modelBuilder.Entity<MetadataGame>().Property(e => e.Id).ValueGeneratedOnAdd();
        modelBuilder.Entity<MetadataDeveloper>().Property(e => e.Id).ValueGeneratedOnAdd();
        modelBuilder.Entity<MetadataGenre>().Property(e => e.Id).ValueGeneratedOnAdd();
        modelBuilder.Entity<MetadataScreenshot>().Property(e => e.Id).ValueGeneratedOnAdd();
    }
}
