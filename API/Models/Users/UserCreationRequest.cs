using EFModel.Models;

namespace API.Models.Users;


public class UserCreationRequest
{

    public required string Username { get; set; }
    public required string Password { get; set; }
    public required string DisplayName { get; set; }
    public required bool IsActive { get; set; }
    public string? Notes { get; set; }

    public UserCreationRequest()
    {
        
    }
    public GameTownUser ToApiuser(string? PasswordHash, string? Salt)
    {
        return new GameTownUser
        {
   
            Id = Guid.NewGuid(),
            Username = Username,
            DisplayName = DisplayName,
            IsActive = IsActive,
            Notes = Notes,
            PasswordHash = PasswordHash ?? null,
            Salt = Salt ?? null
        };
    }
}
