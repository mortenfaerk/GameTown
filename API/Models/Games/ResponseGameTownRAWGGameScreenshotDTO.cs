using EFModel.Models;

namespace API.Models.Games;

public class ResponseGameTownRAWGGameScreenshotDTO
{
    public int Id { get; set; }

    public string Image { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool IsDeleted { get; set; }
    public ResponseGameTownRAWGGameScreenshotDTO(Rawgscreenshot rawgscreenshot)
    {
        this.Id = rawgscreenshot.Id;
        this.Image = rawgscreenshot.Image;
        this.Width = rawgscreenshot.Width;
        this.Height = rawgscreenshot.Height;
        this.IsDeleted = rawgscreenshot.IsDeleted;
    }
}
