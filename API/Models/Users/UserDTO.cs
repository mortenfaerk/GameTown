using EFModel.Models;

namespace API.Models.Users;


public class UserDTO
{
    public Guid? Id { get; set; }
    public  string UserName { get; set; }
    public  string Displayname { get; set; }
    public  bool IsActive { get; set; }
    public string? Notes { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? LastModifiedBy { get; set; }
    public DateTime? LastModifiedAt { get; set; }
    public List<RoleDTO> Roles { get; set; } = new List<RoleDTO>();

    public UserDTO(string Id, string username, string displayName, bool isActive, string notes, string createdBy, DateTime createdAt, string lastModifiedBy, DateTime? lastModifiedAt)
    {
        this.Id = Guid.Parse(Id);
        UserName = username;
        Displayname = displayName;
        IsActive = isActive;
        Notes = notes;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        LastModifiedBy = lastModifiedBy;
        LastModifiedAt = lastModifiedAt;
    }
    
    public UserDTO(GameTownUser user)
    {
        Id = user.Id;
        UserName = user.Username ?? string.Empty;
        Displayname = user.DisplayName ?? string.Empty;
        IsActive = user.IsActive;
        Notes = user.Notes ?? string.Empty;
        CreatedBy = user.CreatedBy ?? string.Empty;
        CreatedAt = user.CreatedAt;
        LastModifiedBy = user.LastModifiedBy ?? string.Empty;
        LastModifiedAt = user.LastModifiedAt;
        foreach (var role in user.Apiroles)
        {
            Roles.Add(new RoleDTO(role));
        }
    }
}
