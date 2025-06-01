namespace API.Models.Users;

public class RoleUpdateResult
{
    public bool Success { get; set; }
    public bool RoleNotFound { get; set; }
    public string? ErrorMessage { get; set; }

    public static RoleUpdateResult NotFound() => new() { RoleNotFound = true };
    public static RoleUpdateResult Failed(string error) => new() { Success = false, ErrorMessage = error };
    public static RoleUpdateResult Ok() => new() { Success = true };
}

