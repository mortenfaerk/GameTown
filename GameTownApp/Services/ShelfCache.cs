using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace GameTownApp.Services;

/// <summary>
/// The library shelf as it was when somebody opened a game from it, so coming back lands them where
/// they were.
///
/// An endless shelf has no page number to return to. Without this, scrolling to game 150, opening it
/// and pressing Back rebuilds the library from game 1 at the top, and the reader has to scroll the
/// whole way down again to find their place. Pagination at least left them on "page 7".
///
/// Restored only on the way back from a game: <see cref="Save"/> is called when a tile is clicked, and
/// the snapshot is dropped the moment the app goes anywhere other than a game page or the library.
/// Clicking "Library" from the admin screens therefore starts at the top, as anyone doing it expects.
///
/// Also dropped on any request that changes something (see <see cref="ShelfInvalidatingHandler"/>):
/// a game deleted or retitled from its own page must not come back on a shelf restored from before.
///
/// One snapshot, not one per filter: only the shelf just left is ever returned to.
/// </summary>
public sealed class ShelfCache : IDisposable
{
    public sealed record Snapshot(
        string FilterKey, List<GameContract> Games, string? Next, int? Total, double ScrollY);

    private readonly NavigationManager _navigation;
    private Snapshot? _saved;

    public ShelfCache(NavigationManager navigation)
    {
        _navigation = navigation;
        _navigation.LocationChanged += OnLocationChanged;
    }

    public void Save(Snapshot snapshot) => _saved = snapshot;

    /// <summary>
    /// The saved shelf if it was saved under these filters, and nothing otherwise. Consumed either way:
    /// a restore is a one-off, and a second visit to the library should not jump back down.
    /// </summary>
    public Snapshot? Take(string filterKey)
    {
        var saved = _saved;
        _saved = null;
        return saved is not null && saved.FilterKey == filterKey ? saved : null;
    }

    public void Invalidate() => _saved = null;

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (_saved is null) return;

        var path = _navigation.ToBaseRelativePath(e.Location);
        var end = path.IndexOfAny(['?', '#']);
        if (end >= 0) path = path[..end];

        // The library itself, or a game opened from it. Anywhere else is leaving the shelf.
        if (path.Length == 0 || path.StartsWith("game/", StringComparison.OrdinalIgnoreCase)) return;

        _saved = null;
    }

    public void Dispose() => _navigation.LocationChanged -= OnLocationChanged;
}

/// <summary>
/// Drops the saved shelf whenever the app sends anything but a GET.
///
/// One place rather than a call in every mutating method: edits, deletes, tags, box art, guides and LAN
/// links all change what a tile shows, and the next one added would be the one that forgot. A logout
/// clears it too, which is harmless. Uploads go through XHR and miss this, but an upload happens on
/// /addgame, and navigating there already drops the snapshot.
/// </summary>
public sealed class ShelfInvalidatingHandler(ShelfCache cache) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            cache.Invalidate();

        return base.SendAsync(request, cancellationToken);
    }
}
