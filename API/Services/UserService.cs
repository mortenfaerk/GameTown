using API.Models.Users;
using EFModel.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using API.Helpers;
namespace API.Services;

public class UserService
{
    readonly DatabaseContext _dbContext;
    public UserService(DatabaseContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> CreateUser(UserCreationRequest userDTO, string creatingUser)
    {

        var saltBytes = GenerateSalt();
        var saltbase64 = Convert.ToBase64String(saltBytes);
        var passwordHash = HashApiKey(userDTO.Password, saltBytes);

        var user = userDTO.ToApiuser(passwordHash, saltbase64);
        var now = DateTime.UtcNow;
        user.CreatedAt = now;
        user.CreatedBy = creatingUser;
        user.LastModifiedAt = now;
        user.LastModifiedBy = creatingUser;

        _dbContext.GameTownUsers.Add(user);
        await _dbContext.SaveChangesAsync();
        return true;
    }
    public async Task<UserDTO?> GetUserById(Guid id)
    {
        var user = await _dbContext.GameTownUsers
            .Include(u => u.Apiroles)
            .FirstOrDefaultAsync(u => u.Id == id);
        if (user == null)
            return null;
        return new UserDTO(user);
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
    public async Task<List<UserDTO>> GetAllUsers()
    {
        var users = await _dbContext.GameTownUsers
            .Include(u => u.Apiroles)
            .ToListAsync();
        return users.Select(u => new UserDTO(u)).ToList();
    }
    public async Task<List<RoleDTO>> GetAllRoles()
    {
        var roles = await _dbContext.GameTownRoles.ToListAsync();
        return roles.Select(r => new RoleDTO(r)).ToList();
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
            if (role.Apiusers.Any())
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
    private static byte[] GenerateSalt(int size = 16)
    {
        var salt = new byte[size];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(salt);
        return salt;
    }
    private static string HashApiKey(string apiKey, byte[] salt, int iterations = 100_000)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(apiKey, salt, iterations, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(pbkdf2.GetBytes(32));
    }

}
