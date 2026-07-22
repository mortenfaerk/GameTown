using System;
using System.Collections.Generic;

namespace EFModel.Models;

public partial class GameTownRole
{
    public Guid Id { get; set; }

    public string Role { get; set; } = null!;

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedDate { get; set; }

    public string ModifiedBy { get; set; } = null!;

    public DateTime ModifiedDate { get; set; }

    public bool IsActive { get; set; }

    public virtual ICollection<GameTownUser> Apiusers { get; set; } = new List<GameTownUser>();
}
