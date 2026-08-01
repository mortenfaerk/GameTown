using System;
using System.Collections.Generic;

namespace EFModel.Models;

public partial class Tag
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public string Slug { get; set; } = null!;

    public bool IsQuickAdd { get; set; }

    public int SortOrder { get; set; }

    public virtual ICollection<GameTownGame> Games { get; set; } = new List<GameTownGame>();
}
