using System.Net.Http.Json;

namespace GameTownApp.Services;

/// <summary>
/// Client for the /users endpoints. Every one of these requires the Admin policy.
///
/// Note the split in how arguments travel: add/update/addRole/updateRole send a JSON body, while
/// get/delete/addUserToRole/removeUserFromRole/deleteRole take their ids as <b>query-string</b>
/// parameters (the API handlers bind plain `string userId` / `string roleId` on GET/DELETE).
/// Sending those as a body silently binds nothing and yields a 400.
/// </summary>
public class UserService(HttpClient http)
{
    private readonly HttpClient _http = http;

    // ---------------------------------------------------------------- users

    public async Task<List<UserContract>> GetAllUsers()
        => await _http.GetFromJsonAsync<List<UserContract>>("/users/getAll") ?? [];

    public async Task<UserContract?> GetUser(Guid userId)
    {
        var response = await _http.GetAsync($"/users/get?userId={userId}");
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<UserContract>();
    }

    public async Task<ApiResult> AddUser(UserCreationRequest request)
        => await ApiResult.FromResponse(await _http.PostAsJsonAsync("/users/add", request));

    public async Task<ApiResult> UpdateUser(UserUpdateRequest request)
        => await ApiResult.FromResponse(await _http.PutAsJsonAsync("/users/update", request));

    public async Task<ApiResult> DeleteUser(Guid userId)
        => await ApiResult.FromResponse(await _http.DeleteAsync($"/users/delete?userId={userId}"));

    // ---------------------------------------------------------------- user <-> role

    public async Task<ApiResult> AddUserToRole(Guid userId, Guid roleId)
        => await ApiResult.FromResponse(
            await _http.PostAsync($"/users/addUserToRole?userId={userId}&roleId={roleId}", null));

    public async Task<ApiResult> RemoveUserFromRole(Guid userId, Guid roleId)
        => await ApiResult.FromResponse(
            await _http.DeleteAsync($"/users/removeUserFromRole?userId={userId}&roleId={roleId}"));

    // ---------------------------------------------------------------- roles

    public async Task<List<RoleContract>> GetAllRoles()
        => await _http.GetFromJsonAsync<List<RoleContract>>("/users/getAllRoles") ?? [];

    public async Task<ApiResult> AddRole(RoleCreationRequest request)
        => await ApiResult.FromResponse(await _http.PostAsJsonAsync("/users/addRole", request));

    public async Task<ApiResult> UpdateRole(RoleUpdateRequest request)
        => await ApiResult.FromResponse(await _http.PutAsJsonAsync("/users/updateRole", request));

    /// <summary>Deleting a role that is still assigned to someone comes back as a 400.</summary>
    public async Task<ApiResult> DeleteRole(Guid roleId)
        => await ApiResult.FromResponse(await _http.DeleteAsync($"/users/deleteRole?roleId={roleId}"));
}
