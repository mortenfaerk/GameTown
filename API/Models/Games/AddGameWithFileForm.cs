using Microsoft.AspNetCore.Mvc;

public class AddGameWithFileForm
{
    [FromForm(Name = "title")]
    public string Title { get; set; } = default!;

    [FromForm(Name = "howTo")]
    public string HowTo { get; set; } = default!;

    [FromForm(Name = "rawgGameId")]
    public string? RAWGGameId { get; set; }

    [FromForm(Name = "file")]
    public IFormFile File { get; set; } = default!;
}
