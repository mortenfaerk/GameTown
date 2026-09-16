using System.Net;
using System.Net.Http.Json;

namespace GameTown.Tests;

/// <summary>
/// Deleting a user through <c>DELETE /users/delete</c>.
///
/// Regression coverage for a bug where deleting a user with any role assigned failed with "A
/// database error occurred while deleting the user." The join table ("GameTownUsers_Roles") never
/// declared ON DELETE on its user-side foreign key, and the scaffolded model set
/// DeleteBehavior.ClientSetNull on both sides of the relationship — but the join's key columns are
/// non-nullable, so EF Core threw inside SaveChangesAsync before SQLite was ever consulted. A user
/// with no roles never hit this, which is why it went unnoticed: migration 009 adds ON DELETE
/// CASCADE on the user side only, leaving role deletion's own "in use" guard untouched.
/// </summary>
public class UserManagementTests
{
    // Seeded by Database/sqlite/02_seed.sql.
    private const string ContributorRoleId = "37A3C94F-B2E0-46AC-A60B-2B9EB09C3A14";

    [Fact]
    public async Task Deleting_a_user_with_a_role_assigned_succeeds()
    {
        using var app = new GameTownApp();
        using var admin = await app.SignInAsAdminAsync();

        const string username = "role-holder";
        var created = await admin.PostAsJsonAsync("/users/add",
            new { username, password = "throwaway-password", displayName = username });
        created.EnsureSuccessStatusCode();

        var userId = app.QueryScalar($@"SELECT ""Id"" FROM ""GameTownUsers"" WHERE ""Username""='{username}'");

        var assigned = await admin.PostAsync($"/users/addUserToRole?userId={userId}&roleId={ContributorRoleId}", null);
        assigned.EnsureSuccessStatusCode();
        Assert.Equal("1", app.QueryScalar(
            $@"SELECT COUNT(*) FROM ""GameTownUsers_Roles"" WHERE ""APIUserId""='{userId}'"));

        var response = await admin.DeleteAsync($"/users/delete?userId={userId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("0", app.QueryScalar(
            $@"SELECT COUNT(*) FROM ""GameTownUsers"" WHERE ""Id""='{userId}'"));
        Assert.Equal("0", app.QueryScalar(
            $@"SELECT COUNT(*) FROM ""GameTownUsers_Roles"" WHERE ""APIUserId""='{userId}'"));
    }

    [Fact]
    public async Task Deleting_a_user_with_no_roles_still_succeeds()
    {
        using var app = new GameTownApp();
        using var admin = await app.SignInAsAdminAsync();

        const string username = "no-roles";
        var created = await admin.PostAsJsonAsync("/users/add",
            new { username, password = "throwaway-password", displayName = username });
        created.EnsureSuccessStatusCode();

        var userId = app.QueryScalar($@"SELECT ""Id"" FROM ""GameTownUsers"" WHERE ""Username""='{username}'");

        var response = await admin.DeleteAsync($"/users/delete?userId={userId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("0", app.QueryScalar(
            $@"SELECT COUNT(*) FROM ""GameTownUsers"" WHERE ""Id""='{userId}'"));
    }
}
