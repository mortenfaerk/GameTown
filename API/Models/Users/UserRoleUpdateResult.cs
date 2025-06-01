namespace API.Models.Users;

public class UserRoleUpdateResult
{
    public bool Success { get; set; }
    public bool UserNotFound { get; set; }
    public bool RoleNotFound { get; set; }
    public string? ErrorMessage { get; set; }


    public static UserRoleUpdateResult UserNotFoundResponse() => new() { UserNotFound = true };
    public static UserRoleUpdateResult RoleNotFoundResponse() => new() { RoleNotFound = true };
    public static UserRoleUpdateResult Failed(string error) => new() { Success = false, ErrorMessage = error };
    public static UserRoleUpdateResult Ok() => new() { Success = true };
}