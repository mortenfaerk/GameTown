using EFModel.Models;
using GameTown.Contracts.Users;

namespace API.Mapping;

/// <summary>
/// Entity &lt;-&gt; wire-contract mapping for users and roles. Kept out of the Contracts project
/// so that Contracts stays EF-free (see GameMappings for the same rationale).
/// </summary>
public static class UserMappings
{
    public static UserContract ToContract(this GameTownUser user) => new()
    {
        Id = user.Id,
        UserName = user.Username,
        Displayname = user.DisplayName ?? string.Empty,
        IsActive = user.IsActive,
        Notes = user.Notes,
        CreatedBy = user.CreatedBy,
        CreatedAt = user.CreatedAt,
        LastModifiedBy = user.LastModifiedBy,
        LastModifiedAt = user.LastModifiedAt,
        Roles = user.Apiroles.Select(r => r.ToContract()).ToList()
    };

    public static RoleContract ToContract(this GameTownRole role) => new()
    {
        Id = role.Id,
        Name = role.Role,
        IsActive = role.IsActive,
        CreatedDate = role.CreatedDate,
        CreatedBy = role.CreatedBy,
        ModifiedDate = role.ModifiedDate,
        ModifiedBy = role.ModifiedBy
    };

    /// <summary>Builds a new user entity from a creation request plus an already-derived hash/salt pair.</summary>
    public static GameTownUser ToEntity(this UserCreationRequest request, string passwordHash, string salt) => new()
    {
        Id = Guid.NewGuid(),
        Username = request.Username,
        DisplayName = request.DisplayName,
        IsActive = request.IsActive,
        Notes = request.Notes,
        PasswordHash = passwordHash,
        Salt = salt
    };
}
