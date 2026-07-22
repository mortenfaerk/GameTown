namespace GameTown.Contracts.Users;

public class UserContract
{
    public Guid? Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Displayname { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string? Notes { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? LastModifiedBy { get; set; }
    public DateTime? LastModifiedAt { get; set; }
    public List<RoleContract> Roles { get; set; } = [];
}
