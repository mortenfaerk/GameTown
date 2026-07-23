using API.Helpers;
using API.Models.Users;
using EFModel.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
namespace API.Services;

public class UserService(DatabaseContext dbContext)
{
    readonly DatabaseContext _dbContext = dbContext;

    public async Task<bool> CreateUser(UserCreationRequest userDTO, string creatingUser)
    {

        var (pwHash, salt) = ApiKeyHelper.HashPassword(userDTO.Password);

        var user = userDTO.ToEntity(pwHash, salt);
        var now = DateTime.UtcNow;
        user.CreatedAt = now;
        user.CreatedBy = creatingUser;
        user.LastModifiedAt = now;
        user.LastModifiedBy = creatingUser;

        _dbContext.GameTownUsers.Add(user);
        await _dbContext.SaveChangesAsync();
        return true;
    }
    public async Task<UserContract?> GetUserById(Guid id)
    {
        var user = await _dbContext.GameTownUsers
            .Include(u => u.Apiroles)
            .FirstOrDefaultAsync(u => u.Id == id);
        if (user == null)
            return null;
        return user.ToContract();
    }
    public async Task<UserUpdateResult> UpdateUser(UserUpdateRequest userDTO, string modifyingUser)
    {
        try
        {
            var user = await _dbContext.GameTownUsers
                .FirstOrDefaultAsync(u => u.Id == userDTO.Id);

            if (user == null)
                return UserUpdateResult.NotFound();

            if (!string.IsNullOrWhiteSpace(userDTO.Username))
                user.Username = userDTO.Username;

            if (!string.IsNullOrWhiteSpace(userDTO.DisplayName))
                user.DisplayName = userDTO.DisplayName;

            if (!string.IsNullOrWhiteSpace(userDTO.Notes))
                user.Notes = userDTO.Notes;

            if (userDTO.IsActive.HasValue && user.IsActive != userDTO.IsActive.Value)
                user.IsActive = userDTO.IsActive.Value;

            user.LastModifiedAt = DateTime.UtcNow;
            user.LastModifiedBy = modifyingUser;

            await _dbContext.SaveChangesAsync();

            return UserUpdateResult.Ok();
        }
        catch (Exception ex)
        {
            //log `ex` here
            return UserUpdateResult.Failed("A database error occurred while updating the user.");
        }
    }
    public async Task<UserDeleteResult> DeleteUser(Guid id)
    {
        try
        {
            var user = await _dbContext.GameTownUsers
                .Include(u => u.Apiroles)
                .FirstOrDefaultAsync(u => u.Id == id);
            if (user == null)
                return UserDeleteResult.NotFound();
            _dbContext.GameTownUsers.Remove(user);
            await _dbContext.SaveChangesAsync();
            return UserDeleteResult.Ok();
        }
        catch (Exception ex)
        {
            //log `ex` here
            return UserDeleteResult.Failed("A database error occurred while deleting the user.");
        }
    }
    public async Task<List<UserContract>> GetAllUsers()
    {
        var users = await _dbContext.GameTownUsers
            .Include(u => u.Apiroles)
            .ToListAsync();
        return users.Select(u => u.ToContract()).ToList();
    }
    public async Task<List<RoleContract>> GetAllRoles()
    {
        var roles = await _dbContext.GameTownRoles.ToListAsync();
        return roles.Select(r => r.ToContract()).ToList();
    }
    public async Task<UserRoleUpdateResult> AddUserToRole(Guid userId, Guid roleId)
    {
        try
        {
            var user = await _dbContext.GameTownUsers
                .Include(u => u.Apiroles)
                .FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
                return UserRoleUpdateResult.UserNotFoundResponse();
            if (user.Apiroles.Any(r => r.Id == roleId))
                return UserRoleUpdateResult.Failed("User already has this role.");

            var role = await _dbContext.GameTownRoles
                .FirstOrDefaultAsync(r => r.Id == roleId);
            if (role == null)
                return UserRoleUpdateResult.RoleNotFoundResponse();

            user.Apiroles.Add(role);
            await _dbContext.SaveChangesAsync();
            return UserRoleUpdateResult.Ok();
        }
        catch (Exception ex)
        {
            //log `ex` here
            return UserRoleUpdateResult.Failed("A database error occurred while adding the user to the role.");
        }
    }
    public async Task<UserRoleUpdateResult> RemoveUserFromRole(Guid userId, Guid roleId)
    {
        try
        {
            var user = await _dbContext.GameTownUsers
                .Include(u => u.Apiroles)
                .FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
                return UserRoleUpdateResult.UserNotFoundResponse();
            var role = await _dbContext.GameTownRoles
                .FirstOrDefaultAsync(r => r.Id == roleId);
            if (role == null)
                return UserRoleUpdateResult.RoleNotFoundResponse();
            if (!user.Apiroles.Any(r => r.Id == roleId))
                return UserRoleUpdateResult.Failed("User does not have this role.");
            user.Apiroles.Remove(role);
            await _dbContext.SaveChangesAsync();
            return UserRoleUpdateResult.Ok();
        }
        catch (Exception ex)
        {
            //log `ex` here
            return UserRoleUpdateResult.Failed("A database error occurred while removing the user from the role.");
        }
    }
    public async Task AddRole(RoleCreationRequest roleDTO, string modifyingUser)
    {
        var newRole = new GameTownRole
        {
            Id = Guid.NewGuid(),
            Role = roleDTO.Name,
            IsActive = roleDTO.IsActive,
            CreatedBy = modifyingUser,
            CreatedDate = DateTime.UtcNow,
            ModifiedBy = modifyingUser,
            ModifiedDate = DateTime.UtcNow
        };
        _dbContext.GameTownRoles.Add(newRole);
        await _dbContext.SaveChangesAsync();
    }
    public async Task<RoleUpdateResult> UpdateRole(RoleUpdateRequest roleDTo, string modifyingUser)
    {
        try
        {
            var role = await _dbContext.GameTownRoles
                .FirstOrDefaultAsync(r => r.Id == roleDTo.Id);
            if (role == null)
                return RoleUpdateResult.NotFound();
            if (!string.IsNullOrWhiteSpace(roleDTo.Role))
                role.Role = roleDTo.Role;
            if (roleDTo.IsActive.HasValue && role.IsActive != roleDTo.IsActive.Value)
                role.IsActive = roleDTo.IsActive.Value;
            role.ModifiedBy = modifyingUser;
            role.ModifiedDate = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            return RoleUpdateResult.Ok();
        }
        catch (Exception ex)
        {
            //log `ex` here
            return RoleUpdateResult.Failed("A database error occurred while updating the role.");
        }
    }
    public async Task<RoleDeleteResult> DeleteRole(Guid roleId)
    {
        try
        {
            var role = await _dbContext.GameTownRoles
                .Include(r => r.Apiusers)
                .FirstOrDefaultAsync(r => r.Id == roleId);
            if (role == null)
                return RoleDeleteResult.NotFound();
            if (role.Apiusers.Count != 0)
                return RoleDeleteResult.InUse();
            _dbContext.GameTownRoles.Remove(role);
            await _dbContext.SaveChangesAsync();
            return RoleDeleteResult.Ok();
        }
        catch (Exception ex)
        {
            //log `ex` here
            return RoleDeleteResult.Failed("A database error occurred while deleting the role.");
        }

    }

    #region Auth
    public async Task<GameTownUser?> AuthenticateUser(LoginRequest req)
    {
        var matchedUser = await _dbContext.GameTownUsers
                      .Include(u => u.Apiroles)
                      .Where(u => u.IsActive && u.Username == req.Username)
                      .ToListAsync()
                      .ContinueWith(t =>
                      t.Result.FirstOrDefault(u => ApiKeyHelper.ValidatePassword(req.Password, u.PasswordHash, u.Salt))
                      );

        return matchedUser;
    }
    /// <summary>
    /// Builds the ClaimsPrincipal that gets signed into the auth cookie.
    ///
    /// These are the same claims the JWT used to carry; only the envelope changed. The cookie
    /// middleware serialises them into the encrypted cookie, so no token is ever handed to the
    /// browser and nothing client-side needs to parse one.
    /// </summary>
    public static ClaimsPrincipal BuildPrincipal(GameTownUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        };
        foreach (var role in user.Apiroles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role.Role));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    #endregion
}
