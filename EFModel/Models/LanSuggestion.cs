using System;
using System.Collections.Generic;

namespace EFModel.Models;

public partial class LanSuggestion
{
    public Guid Id { get; set; }

    public long RemoteId { get; set; }

    public string Name { get; set; } = null!;

    public string LanEventName { get; set; } = null!;

    public bool Played { get; set; }

    public Guid? GameId { get; set; }

    public string? LinkSource { get; set; }

    public Guid? RemoteMatchId { get; set; }

    public DateTime? PushedAtUtc { get; set; }

    public bool Dismissed { get; set; }

    public bool AutoMatchBlocked { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }

    public virtual GameTownGame? Game { get; set; }
}
