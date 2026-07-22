using System.ComponentModel.DataAnnotations;

namespace GameTown.Contracts.Users;

public class UserCreationRequest
{
    [Required(ErrorMessage = "Username is required.")]
    [StringLength(256)]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Display name is required.")]
    [StringLength(256)]
    public string DisplayName { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    [StringLength(512)]
    public string? Notes { get; set; }
}

/// <summary>Partial update of a user. Null members are left unchanged.</summary>
public class UserUpdateRequest
{
    [Required]
    public Guid Id { get; set; }

    [StringLength(256)]
    public string? Username { get; set; }

    [StringLength(256)]
    public string? DisplayName { get; set; }

    public bool? IsActive { get; set; }

    [StringLength(512)]
    public string? Notes { get; set; }
}
