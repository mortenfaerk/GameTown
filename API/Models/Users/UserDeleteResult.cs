namespace API.Models.Users;

public class UserDeleteResult
{
    public bool Success { get; set; }
    public bool UserNotFound { get; set; }
    public string? ErrorMessage { get; set; }

    public static UserDeleteResult NotFound() => new() { UserNotFound = true };
    public static UserDeleteResult Failed(string error) => new() { Success = false, ErrorMessage = error };
    public static UserDeleteResult Ok() => new() { Success = true };
}
