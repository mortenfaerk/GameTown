using System.ComponentModel.DataAnnotations;

namespace GameTown.Contracts.Users;

public class RoleCreationRequest
{
    [Required(ErrorMessage = "Role name is required.")]
    [StringLength(50)]
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}

/// <summary>Partial update of a role. Null members are left unchanged.</summary>
public class RoleUpdateRequest
{
    [Required]
    public Guid Id { get; set; }

    [StringLength(50)]
    public string? Role { get; set; }

    public bool? IsActive { get; set; }
}
