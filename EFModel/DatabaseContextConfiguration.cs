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
    /// </summary>
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameTownGame>().Property(e => e.Id).ValueGeneratedOnAdd();
        modelBuilder.Entity<GameTownRole>().Property(e => e.Id).ValueGeneratedOnAdd();
        modelBuilder.Entity<GameTownUser>().Property(e => e.Id).ValueGeneratedOnAdd();
        modelBuilder.Entity<Tag>().Property(e => e.Id).ValueGeneratedOnAdd();
    }
}
