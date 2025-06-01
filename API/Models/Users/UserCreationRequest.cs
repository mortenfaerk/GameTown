using EFModel.Models;

namespace API.Models.Users;


public class UserCreationRequest
{
    public Guid? Id { get; set; }
    public required string Username { get; set; }
    public required string Password { get; set; }
    public required string DisplayName { get; set; }
    public required bool IsActive { get; set; }
    public string? Notes { get; set; }

    public UserCreationRequest()
    {
        
    }
    public UserCreationRequest(GameTownUser user)
    {
        Id = user.Id;
        Username = user.Username ?? string.Empty;
        DisplayName = user.DisplayName ?? string.Empty;
        IsActive = user.IsActive;
        Notes = user.Notes ?? string.Empty;
    }
    public GameTownUser ToApiuser(string? PasswordHash, string? Salt)
    {
        if (Id == null)
        {
            Id = Guid.NewGuid();
        }
        return new GameTownUser
        {
   
            Id = (Guid)Id,
            Username = Username,
            DisplayName = DisplayName,
            IsActive = IsActive,
            Notes = Notes,
            PasswordHash = PasswordHash ?? null,
            Salt = Salt ?? null
        };
    }
}
