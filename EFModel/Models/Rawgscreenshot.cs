using System;
using System.Collections.Generic;

namespace EFModel.Models;

public partial class Rawgscreenshot
{
    public int Id { get; set; }

    public string Image { get; set; } = null!;

    public int Width { get; set; }

    public int Height { get; set; }

    public bool IsDeleted { get; set; }

    public virtual ICollection<Rawggame> Games { get; set; } = new List<Rawggame>();
}
