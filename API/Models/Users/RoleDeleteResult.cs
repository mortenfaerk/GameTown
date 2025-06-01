namespace API.Models.Users;

public class RoleDeleteResult
{
    public bool Success { get; set; }
    public bool RoleNotFound { get; set; }
    public bool RoleInUse { get; set; }
    public string? ErrorMessage { get; set; }

    public static RoleDeleteResult NotFound() => new() { RoleNotFound = true };
    public static RoleDeleteResult InUse() => new() { RoleInUse = true };
    public static RoleDeleteResult Failed(string error) => new() { Success = false, ErrorMessage = error };
    public static RoleDeleteResult Ok() => new() { Success = true };
}
