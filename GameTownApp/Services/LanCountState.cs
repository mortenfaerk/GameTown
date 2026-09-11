namespace GameTownApp.Services;

/// <summary>
/// The one number two components both show: how many suggestions are waiting on a human.
///
/// It exists because they disagreed. <c>LanNavLink</c> read the count once in
/// <c>OnInitializedAsync</c> and never again, so setting a suggestion aside moved the tab badge from
/// 79 to 78 while the sidebar went on saying 79 until the next full page load. Neither number was
/// wrong when it was fetched, which is what made it a nuisance to notice and easy to distrust.
///
/// Scoped, like every other client service here: one instance per app load, which is the lifetime of
/// the SPA. Components subscribe on initialise and must unsubscribe on dispose — a NavLink outlives
/// most pages, but a page that forgets is a leak plus a render on a disposed component.
/// </summary>
public class LanCountState
{
    private readonly LanSuggestionService _lan;

    public LanCountState(LanSuggestionService lan) => _lan = lan;

    /// <summary>Suggestions with no game and not set aside. -1 until the first read completes.</summary>
    public int Unmatched { get; private set; } = -1;

    /// <summary>
    /// Every suggestion in every state — the test for whether this install uses the LAN bot at all,
    /// since suggestions only exist once a sync has run.
    /// </summary>
    public int Total { get; private set; } = -1;

    public bool Loaded => Unmatched >= 0;

    /// <summary>Raised after any change. Subscribers re-render; nobody re-fetches.</summary>
    public event Action? Changed;

    /// <summary>
    /// Reads both counts from the server.
    ///
    /// One request for both, which is the point: the sidebar used to answer "does this install use
    /// the bot" by pulling the ENTIRE suggestion list and taking its Count, so an install with an
    /// empty wishlist — the healthy steady state — downloaded every suggestion on every page load.
    /// </summary>
    public async Task Refresh()
    {
        try
        {
            var counts = await _lan.GetCounts();
            Unmatched = counts.Unmatched;
            Total = counts.Total;
        }
        catch (Exception)
        {
            // Leave whatever was last known. This drives a badge and a sidebar link, neither of which
            // is worth an error state — and blanking a count that was right a moment ago is worse
            // than showing it a little stale.
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Applies a change the caller has already made, without a round trip.
    ///
    /// The LAN screen knows exactly how many rows it just took off the wishlist, and re-asking the
    /// server would be a second request for a number already in hand. Clamped at zero so a double
    /// call cannot show a negative badge.
    /// </summary>
    public void Adjust(int unmatchedDelta)
    {
        if (unmatchedDelta == 0 || !Loaded) return;

        Unmatched = Math.Max(0, Unmatched + unmatchedDelta);
        Changed?.Invoke();
    }
}
