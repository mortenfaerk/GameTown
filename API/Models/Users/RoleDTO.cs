using EFModel.Models;

namespace API.Models.Users;

public class RoleDTO
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; }
    public DateTime ModifiedDate { get; set; }
    public string ModifiedBy { get; set; }
    public RoleDTO(string id, string name, bool isActive, string createdBy,string modifiedBy, DateTime createdDate, DateTime modifiedDate)
    {
        Id = Guid.Parse(id);
        Name = name;
        IsActive = isActive;
        CreatedDate = createdDate;
        CreatedBy = createdBy;
        ModifiedDate = modifiedDate;
        ModifiedBy = modifiedBy;
    }
    public RoleDTO(GameTownRole role)
    {
        Id = role.Id;
        Name = role.Role;
        IsActive = role.IsActive;
        CreatedDate = role.CreatedDate;
        CreatedBy = role.CreatedBy;
        ModifiedDate = role.ModifiedDate;
        ModifiedBy = role.ModifiedBy;

    }
}

