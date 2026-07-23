using API.Helpers;
using API.Services;
using EFModel.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace API.Pages;

/// <summary>
/// First-run wizard: creates the first administrator.
///
/// Server-rendered rather than part of the SPA, and that is the one place in this app where server
/// rendering is the right answer. Everything else assumes a configured, running system; this page has
/// to work on an install that has never been configured, so booting the whole WASM bundle into a
/// half-configured state just to draw a form would be the wrong shape.
///
/// AllowAnonymous is required — the authorization FallbackPolicy demands an authenticated user for
/// anything that does not opt out, and on a fresh install there is nobody to authenticate as.
/// </summary>
[AllowAnonymous]
public class SetupModel(DatabaseContext dbContext, SettingsService settings) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "Choose a username.")]
    [StringLength(256)]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "Choose a password.")]
    [MinLength(8, ErrorMessage = "Use at least 8 characters.")]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "Repeat the password.")]
    [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    [BindProperty]
    public string? GameFilesPath { get; set; }

    [BindProperty]
    public string? RawgApiKey { get; set; }

    public string DataDirectory => settings.DataDirectory;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await AdminExistsAsync())
            return NotFound();

        GameFilesPath ??= await settings.GetGameFilesPathAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // Re-checked on POST, not just GET. The gate is evaluated per request rather than captured
        // at startup precisely so it closes the moment an admin exists — a boot-time flag would stay
        // open for the life of the process and leave this endpoint creating admins forever.
        if (await AdminExistsAsync())
            return NotFound();

        if (!ModelState.IsValid)
            return Page();

        var adminRole = await dbContext.GameTownRoles.FirstOrDefaultAsync(r => r.Role == "Admin");
        if (adminRole is null)
        {
            ModelState.AddModelError(string.Empty,
                "The database has no Admin role. It was not seeded correctly — reinstall rather than continuing.");
            return Page();
        }

        if (await dbContext.GameTownUsers.AnyAsync(u => u.Username == Username))
        {
            ModelState.AddModelError(nameof(Username), "That username is already taken.");
            return Page();
        }

        var (hash, salt) = ApiKeyHelper.HashPassword(Password);
        var now = DateTime.UtcNow;

        var user = new GameTownUser
        {
            Username = Username,
            DisplayName = Username,
            PasswordHash = hash,
            Salt = salt,
            IsActive = true,
            Notes = "Created by first-run setup",
            CreatedAt = now,
            CreatedBy = "Setup",
            LastModifiedAt = now,
            LastModifiedBy = "Setup",
        };
        user.Apiroles.Add(adminRole);
        dbContext.GameTownUsers.Add(user);

        // One transaction covering the uniqueness check and the insert. Two people submitting this
        // form at the same moment is a genuine race, but a low-stakes one on a LAN, and the unique
        // index on Username is the real backstop — no locking scheme is warranted here.
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            await dbContext.SaveChangesAsync();

            if (!string.IsNullOrWhiteSpace(GameFilesPath))
                await settings.SetAsync(SettingsService.GameFilesPathKey, GameFilesPath.Trim());
            if (!string.IsNullOrWhiteSpace(RawgApiKey))
                await settings.SetAsync(SettingsService.RawgApiKeyKey, RawgApiKey.Trim());

            await transaction.CommitAsync();
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync();
            ModelState.AddModelError(nameof(Username), "That username is already taken.");
            return Page();
        }

        // Straight to the SPA's login page — the account exists, so the ordinary sign-in path now
        // works and there is no reason to invent a second one here.
        return Redirect("/login");
    }

    private Task<bool> AdminExistsAsync()
        => dbContext.GameTownUsers.AnyAsync(u => u.Apiroles.Any(r => r.Role == "Admin"));
}
