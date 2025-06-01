namespace API.Models.Users;

public class RoleCreationRequest
{
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public RoleCreationRequest(string name, bool isActive)
    {
        Name = name;
        IsActive = isActive;
    }
    public RoleCreationRequest() { }
}
