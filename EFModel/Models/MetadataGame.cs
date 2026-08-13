using System;
using System.Collections.Generic;

namespace EFModel.Models;

public partial class MetadataGame
{
    public int Id { get; set; }

    public string Provider { get; set; } = null!;

    public int ExternalId { get; set; }

    public string Slug { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string Description { get; set; } = null!;

    public DateTime? Released { get; set; }

    public double? CriticScore { get; set; }

    public double? Rating { get; set; }

    public string? Image { get; set; }

    public string Website { get; set; } = null!;

    public DateTime? Updated { get; set; }

    public virtual ICollection<GameTownGame> GameTownGames { get; set; } = new List<GameTownGame>();

    public virtual ICollection<MetadataDeveloper> Developers { get; set; } = new List<MetadataDeveloper>();

    public virtual ICollection<MetadataGenre> Genres { get; set; } = new List<MetadataGenre>();

    public virtual ICollection<MetadataScreenshot> Screenshots { get; set; } = new List<MetadataScreenshot>();
}
