# Changelog

All notable changes to this project will be documented in this file.
## [0.7.1] - 2026-10-09
- Bump release version to 0.7.1.
- **The library scrolls endlessly instead of paging.** Previous/Next and "Page N" are gone: the next
  batch of 48 loads about a screen before you reach the end of the shelf, the header reads "72 of
  340" rather than "24 on this page", and the shelf ends with "That's all N". A "Load more" button
  stays at the bottom for keyboard and screen-reader users, and a failed batch offers a Retry in
  place without throwing away what already loaded.
  - **Back returns you to where you were.** Opening a game and coming back restores the shelf and
    its scroll position without reloading it. Going anywhere else first, or changing anything about a
    game, starts the shelf fresh from the top.
  - New `GET /GTGames/browse` (anonymous): cursor-paged in (Title, Id) order, with `?q=`, `?tags=`
    and `?lan=` as before, `?limit=` clamped to 1–100, and a `total` on the first batch. A cursor
    names the last game seen rather than an offset, so a game uploaded or deleted mid-scroll can no
    longer repeat a tile or skip one. A malformed cursor is a 400.
  - `getPaged` and `search` are unchanged and still used by the game picker.
- Tests: 306, up from 293.

## [0.7.0] - 2026-10-04
- Bump release version to 0.7.0.
- **New setting: host the API documentation.** *Administer → Settings → API docs* now has a toggle
  for serving the interactive API reference (Scalar) and the OpenAPI document, with a link to them
  while it's on. These used to exist only in Development builds, so on an installed appliance there
  was no way to reach them at all.
  - **Off by default**, including after an upgrade — taking this build exposes nothing on its own.
    While it's on they're readable by anyone who can reach the server, which is the point on a LAN;
    what becomes visible is the API's *shape*, and every endpoint it describes keeps the
    authorization it already had. See accepted risk 12 in SECURITY-NOTES.md.
  - Read per request like every other setting, so switching it either way takes effect immediately
    with no restart. The setting governs Development too, so a fresh dev database serves 404 until
    it's enabled.
  - No migration — schema stays at version 9 and the installer needs no change.
- Tests: 293, up from 287.

## [0.6.1] - 2026-09-16
- Bump release version to 0.6.1.
- Fixed: deleting a user with any role assigned failed with "A database error occurred while
  deleting the user." The user↔role join ("GameTownUsers_Roles") never declared `ON DELETE` on
  its user-side foreign key, and the scaffolded model tried to null out the join row instead of
  deleting it — impossible, since those columns are the join's own primary key. Migration 009
  adds `ON DELETE CASCADE` on the user side only; deleting a role still refuses while it's in use,
  unchanged. Schema is now at version 9. The installer needs no change.
- Tests: 287, up from 285.

## [0.6.0] - 2026-09-12
- Bump release version to 0.6.0.
- **LAN Discord bot integration.** GameTown now talks to the LAN's game-suggestion bot: a background
  service polls it on a configurable interval, matches suggestions against the library, and pushes
  each match back so the suggestion carries a link to the game.
  - New *Administer → Settings → LAN bot* tab: bot address, API key (masked, with a live Test), poll
    interval (0 stops polling without switching the integration off), a Sync now button, and this
    server's public address.
  - New **LAN suggestions** screen for contributors and admins — a wishlist of games people asked for
    that nobody has uploaded, plus manual matching for anything the automatic pass could not identify,
    and a "set aside" for suggestions that will never be games. Contributors can check for new
    suggestions themselves rather than waiting out the poll interval.
  - Automatic matching is deliberately conservative: an unambiguous normalised-title match and nothing
    else. Case, punctuation, roman numerals and a leading "The" are handled; trailing numbers and
    subtitles are not stripped, so *Portal* and *Portal 2* stay different games.
  - Library: a mark on tiles that were suggested at a LAN, the events listed on the game page, and a
    `?lan=` filter with chips on the shelf so "what people asked for this weekend" is a pasteable URL.
  - Unlinking removes the catalogue entry GameTown created and never one the crew made by hand.
    Deleting a game does not touch the bot; the suggestion simply returns to the wishlist.
  - Migration 008 adds the `LanSuggestion` table. Schema is now at version 8. The installer needs no
    change.
  - **Ranked wishlist.** `GET /lan/suggestions/ranked` scores the whole unmatched queue against the
    library in one pass and bands each row strong/ambiguous/weak by score and margin, so the screen
    shows the confident matches, the genuine judgement calls, and the hopeless rows separately instead
    of one flat list — weak rows carry no candidates at all. Nothing links on a single click: a choice
    is staged, and "Link selected" / "Dismiss selected" apply to a whole band at once, sequenced on the
    server since the bot is rate-limited. `LibraryGamePicker` is the search fallback for suggestions
    the ranking misses entirely, such as Discord abbreviations ("CS2", "DRG"). The sidebar badge and
    the screen's own tab badge now share one count (`LanCountState`) so the two cannot disagree.
  - Contributors can refresh the wishlist and link/unlink suggestions themselves, not just admins;
    reading the sync *status* remains Admin-only, since that reports on configuration rather than
    on-going wishlist triage.
  - **Catalogue API v1.4.0.** The bot added direct per-suggestion binding and unbinding
    (`PUT`/`DELETE /suggestions/{id}/match`), which replaces the old title-match-only binding entirely:
    a suggestion already bound elsewhere — in Discord, or to a different GameTown game — can now be
    claimed directly instead of only being recorded locally with no working Discord link. A push now
    also sends the game's own canonical title, deep link and cover art (`url`/`boxArtUrl`), giving the
    `PublicBaseUrl` setting its first real consumer.
- Settings: added `PublicBaseUrl`, for links from outside this install back into it — since Catalogue
  API v1.4.0, also the source of the deep link and cover art every LAN bot push carries.
- Security notes: two new accepted risks — outbound requests to an admin-configured address (the LAN
  bot client deliberately does not use the image fetcher's private-address refusal), and player-typed
  Discord text being rendered by GameTown.
- Tests: 285, up from 207.

## [0.5.1] - 2026-08-25
- Bump release version to 0.5.1
- Added better  error handling, so a contributor wil see a proper error message if the system doesn't have write access to it library path.
## [0.5.0] - 2026-08-14
- Bump release version to 0.5.0.
- Add initial release notes for the 0.5.0 release:
  - IGDB migration and metadata provider improvements (migration 007 referenced).
  - Box art handling: improved `ImageFetcher` safety and media rehosting.
  - Zip guide writer append-only behavior and robustness improvements.
  - Tagging: slug identity enforcement and quick-add tags migration.
  - Database migrations: added safeguards and replayable scripts guidance.
  - Tests: ensured cross-platform CI compatibility; 203 tests remain the canonical suite.
  - General: documentation updates and small bug fixes across API and frontend.