namespace GameTown.Contracts.Games;

/// <summary>A game in the GameTown library, optionally enriched with RAWG metadata.</summary>
public class GameContract
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string HowTo { get; set; } = string.Empty;

    /// <summary>Size of the uploaded archive, in megabytes.</summary>
    public double Size { get; set; }

    public RawgGameContract? RawgGame { get; set; }
}
