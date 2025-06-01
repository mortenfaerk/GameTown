namespace API.Models.Users;

public class UserUpdateRequest
{
    public required Guid Id { get; set; }
    public  string? Username { get; set; }
    public  string? DisplayName { get; set; }
    public  bool? IsActive { get; set; }
    public string? Notes { get; set; }

    public UserUpdateRequest()
    {

    }
}