using System;
using System.Collections.Generic;

namespace EFModel.Models;

public partial class MetadataScreenshot
{
    public int Id { get; set; }

    public string Provider { get; set; } = null!;

    public int ExternalId { get; set; }

    public string Image { get; set; } = null!;

    public int Width { get; set; }

    public int Height { get; set; }

    public bool IsDeleted { get; set; }

    public virtual ICollection<MetadataGame> Metadata { get; set; } = new List<MetadataGame>();
}
