using System;
using System.Collections.Generic;

namespace EFModel.Models;

public partial class MetadataDeveloper
{
    public int Id { get; set; }

    public string Provider { get; set; } = null!;

    public int ExternalId { get; set; }

    public string? Name { get; set; }

    public string? Slug { get; set; }

    public virtual ICollection<MetadataGame> Metadata { get; set; } = new List<MetadataGame>();
}
