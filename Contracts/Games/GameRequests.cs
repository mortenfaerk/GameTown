using System.ComponentModel.DataAnnotations;

namespace GameTown.Contracts.Games;

/// <summary>
/// Non-file fields of the add-game form. The archive itself is sent alongside this as
/// multipart/form-data; see AddGameWithFileForm on the API side for the field names.
/// </summary>
public class AddGameRequest
{
    [Required(ErrorMessage = "Title is required.")]
    [StringLength(500, ErrorMessage = "Title cannot exceed 500 characters.")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Instructions are required.")]
    public string HowTo { get; set; } = string.Empty;

    /// <summary>RAWG game id chosen via the metadata picker. Optional — games can exist without metadata.</summary>
    public string? RawgGameId { get; set; }
}

/// <summary>
/// What the upload form is allowed to send, as the server currently has it configured.
///
/// Fetched before the file picker is used so an oversized or wrong-typed archive is refused in the
/// browser instead of after a multi-gigabyte upload. Both limits are still enforced server-side —
/// this is a courtesy to the user, not a control.
/// </summary>
public class UploadLimitsContract
{
    /// <summary>Megabytes. <c>0</c> means no limit.</summary>
    public long MaxUploadSizeMb { get; set; }

    public List<string> AllowedFileTypes { get; set; } = [];
}

/// <summary>
/// Partial update of a game. Null members are left unchanged.
///
/// There is deliberately no Url member: the stored path is server-generated at upload and must never
/// be settable by a caller. It previously was, which combined with the unauthenticated update route
/// to give an arbitrary file read (via download) and an arbitrary file delete.
/// </summary>
public class GameTownGamePatchRequest
{
    [Required]
    public string Id { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "Title cannot exceed 500 characters.")]
    public string? Title { get; set; }

    public string? HowTo { get; set; }
    public string? RawgGameId { get; set; }
}
