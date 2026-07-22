using System;
using System.Collections.Generic;

namespace EFModel.Models;

public partial class GameTownUser
{
    public Guid Id { get; set; }

    public string PasswordHash { get; set; } = null!;

    public string Salt { get; set; } = null!;

    public string Username { get; set; } = null!;

    public string? DisplayName { get; set; }

    public bool IsActive { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime? LastModifiedAt { get; set; }

    public string? LastModifiedBy { get; set; }

    public virtual ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();

    public virtual ICollection<GameTownRole> Apiroles { get; set; } = new List<GameTownRole>();
}
