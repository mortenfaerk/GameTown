using System;
using System.Collections.Generic;

namespace EFModel.Models;

public partial class RefreshToken
{
    public Guid Id { get; set; }

    public string Token { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }

    public bool IsRevoked { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid UserId { get; set; }

    public virtual GameTownUser User { get; set; } = null!;
}
