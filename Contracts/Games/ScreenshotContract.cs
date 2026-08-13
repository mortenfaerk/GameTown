namespace GameTown.Contracts.Games;

/// <summary>A screenshot from the metadata provider, re-hosted locally under /media/.</summary>
public class ScreenshotContract
{
    public int Id { get; set; }
    public string Image { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsDeleted { get; set; }
}
