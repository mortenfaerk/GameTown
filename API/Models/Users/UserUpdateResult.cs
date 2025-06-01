namespace API.Models.Users;

public class UserUpdateResult
{
    public bool Success { get; set; }
    public bool UserNotFound { get; set; }
    public string? ErrorMessage { get; set; }

    public static UserUpdateResult NotFound() => new() { UserNotFound = true };
    public static UserUpdateResult Failed(string error) => new() { Success = false, ErrorMessage = error };
    public static UserUpdateResult Ok() => new() { Success = true };
}
