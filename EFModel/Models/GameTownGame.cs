using System;
using System.Collections.Generic;

namespace EFModel.Models;

public partial class GameTownGame
{
    public Guid Id { get; set; }

    public string Title { get; set; } = null!;

    public string HowTo { get; set; } = null!;

    public int? RawggameId { get; set; }

    public string Url { get; set; } = null!;

    public double Size { get; set; }

    public string? ArchiveSha256 { get; set; }

    public string? BoxArtUrl { get; set; }

    public bool GuideBaked { get; set; }

    public virtual Rawggame? Rawggame { get; set; }

    public virtual ICollection<Tag> Tags { get; set; } = new List<Tag>();
}
