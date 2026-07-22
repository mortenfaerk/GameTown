using System;
using System.Collections.Generic;

namespace EFModel.Models;

public partial class Rawgdeveloper
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public string? Slug { get; set; }

    public int? GamesCount { get; set; }

    public string? ImageBackground { get; set; }

    public virtual ICollection<Rawggame> Games { get; set; } = new List<Rawggame>();
}
