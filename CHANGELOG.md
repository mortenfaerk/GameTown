# Changelog

All notable changes to this project will be documented in this file.
## [0.6.0] - 2026-09-11
- Bump release version to 0.6.0.
- **LAN Discord bot integration.** GameTown now talks to the LAN's game-suggestion bot: a background
  service polls it on a configurable interval, matches suggestions against the library, and pushes
  each match back so the suggestion carries a link to the game.
  - New *Administer → Settings → LAN bot* tab: bot address, API key (masked, with a live Test), poll
    interval (0 stops polling without switching the integration off), a Sync now button, and this
    server's public address.
  - New **LAN suggestions** screen for contributors and admins — a wishlist of games people asked for
    that nobody has uploaded, plus manual matching for anything the automatic pass could not identify,
    and a "set aside" for suggestions that will never be games.
  - Automatic matching is deliberately conservative: an unambiguous normalised-title match and nothing
    else. Case, punctuation, roman numerals and a leading "The" are handled; trailing numbers and
    subtitles are not stripped, so *Portal* and *Portal 2* stay different games.
  - Library: a mark on tiles that were suggested at a LAN, the events listed on the game page, and a
    `?lan=` filter with chips on the shelf so "what people asked for this weekend" is a pasteable URL.
  - Unlinking removes the catalogue entry GameTown created and never one the crew made by hand.
    Deleting a game does not touch the bot; the suggestion simply returns to the wishlist.
  - Migration 008 adds the `LanSuggestion` table. Schema is now at version 8. The installer needs no
    change.
- Settings: added `PublicBaseUrl`, for links from outside this install back into it.
- Security notes: two new accepted risks — outbound requests to an admin-configured address (the LAN
  bot client deliberately does not use the image fetcher's private-address refusal), and player-typed
  Discord text being rendered by GameTown.
- Tests: 254, up from 207.

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