using API.Helpers;
using API.Models.Auth;
using API.Models.Users;
using EFModel.Models;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
namespace API.Services;

public class UserService(DatabaseContext dbContext, IConfiguration jwtConfiguration)
{
    readonly DatabaseContext _dbContext = dbContext;
    readonly IConfiguration _JwtConfiguration = jwtConfiguration.GetSection("JwtSettings");
    readonly int RefreshExpiresInDays = int.Parse(jwtConfiguration["RefreshExpiresInDays"] ?? "7");

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
    public async Task<TokenResponse?> GetToken(GameTownUser user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_JwtConfiguration["Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>()
                {
                    new(ClaimTypes.Name, user.Username),
                    new(ClaimTypes.NameIdentifier, user.Id.ToString()),

                };
        foreach (var role in user.Apiroles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role.Role));
        }

        var expires = DateTime.UtcNow.AddMinutes(int.Parse(_JwtConfiguration["ExpiresInMinutes"]!));
        var token = new JwtSecurityToken(
            issuer: _JwtConfiguration["Issuer"],
            audience: _JwtConfiguration["Audience"],
            claims: claims,
            expires: expires,
            signingCredentials: creds);

        var tokenStr = new JwtSecurityTokenHandler().WriteToken(token);
        return new TokenResponse { Token = tokenStr, ExpiresUTC = expires};
    }
    public async Task<RefreshToken> GenerateAndStoreRefreshToken(GameTownUser user)
    {
        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(64)),
            ExpiresAt = DateTime.UtcNow.AddDays(RefreshExpiresInDays), // Set your desired expiry
            IsRevoked = false,
            CreatedAt = DateTime.UtcNow,
            UserId = user.Id
        };

        _dbContext.RefreshTokens.Add(refreshToken);
        await _dbContext.SaveChangesAsync();

        return refreshToken;
    }
    public async Task<RefreshTokenValidationResult> ValidateRefreshToken(string token)
    {
        var refreshToken = await _dbContext.RefreshTokens
            .Include(rt => rt.User)
            .FirstOrDefaultAsync(rt => rt.Token == token && !rt.IsRevoked && rt.ExpiresAt > DateTime.UtcNow);

        if (refreshToken == null)
            return RefreshTokenValidationResult.InvalidRefreshToken();
        _dbContext.RefreshTokens.Remove(refreshToken);
        RefreshToken newRefreshToken = await GenerateAndStoreRefreshToken(refreshToken.User);

        var jwtToken = await GetToken(refreshToken.User);
        if (jwtToken == null)
            return RefreshTokenValidationResult.ServerError();

        await _dbContext.SaveChangesAsync();
        return RefreshTokenValidationResult.Success(jwtToken, newRefreshToken);
    }
    #endregion
}
