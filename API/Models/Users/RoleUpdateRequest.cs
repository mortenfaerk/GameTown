namespace API.Models.Users;

public class RoleUpdateRequest
{
    public Guid Id { get; set; }
    public string? Role { get; set; }
    public bool? IsActive { get; set; }

    public RoleUpdateRequest()
    {
        
    }
    public RoleUpdateRequest(Guid id, string? role, bool? isActive)
    {
        Id = id;
        Role = role;
        IsActive = isActive;
    }
}

