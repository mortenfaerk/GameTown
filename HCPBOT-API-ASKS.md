# HcpBot Catalogue API — outstanding asks

**Status as of 2026-09-12: all six landed in Catalogue API v1.4.0.** The bot's author replied on
2026-09-12 (two of the six differently than asked, both explained below) and GameTown has since
implemented against them, verified against the actual OpenAPI document at
`https://dev-api-hcpbot.znoozles.net/openapi/v1.json` rather than the reply's prose alone. This file
is kept as the historical record of *why* — the same treatment `IGDB-MIGRATION-PLAN.md` gets — not
because anything here is still outstanding.

Dev environment: `https://dev-api-hcpbot.znoozles.net`. The OpenAPI document and Scalar UI sit behind
HTTP Basic (credentials from the bot's author); `/api/v1/*` needs only the `X-Api-Key` header, which
is stored as the `LanBotApiKey` setting and is **not** in this repository.

---

## What the bot actually does (superseded — see "What landed" below)

This section described the pre-1.4.0 bot and is kept only so the asks below still make sense as
asks. It is no longer how the bot behaves.

1. `PUT /api/v1/games/{matchId}` replaces the catalogue entry's **title**. An entry holds exactly one.
2. It then binds every suggestion whose name equals that title **and is currently unbound**, and
   reports how many in `suggestionsBound`. An already-bound suggestion is left alone and the count
   comes back `0` — even for a PUT carrying its exact name.
3. It never unbinds and never re-points anything, not even when the new title matches no suggestion
   at all. Only `DELETE` on the entry removes bindings, and it removes *every* binding on that entry.

### Already settled

- **`matchId` is the GameTown game's own `Guid`.** Confirmed with the bot's author on 2026-09-11, so
  it is no longer an assumption. It is what makes `{gametown}/game/{matchId}` resolve directly. Still
  true in 1.4.0.
- **`source` reflects the API key, not the client.** An entry created with our key reads
  `source: "gametown"` whoever made it — a human poking the dev API with curl included. So it is not
  evidence GameTown created something, which is why `LanSuggestion.PushedAtUtc` (our own record of
  what we pushed) is what gates the delete on unlink. Do not "simplify" that to a `source` check. Still
  true in 1.4.0 — the bug fix under ask 3 makes `?source=` trustworthy as a *display* filter, but it is
  still not a signal GameTown itself should read.

---

## What landed (Catalogue API v1.4.0)

| # | Ask | Outcome |
|---|---|---|
| 1 | Direct bind | **Done, differently.** `PUT /api/v1/suggestions/{id}/match` (not `POST .../link`), body `{ matchId }`. Binds **unconditionally** — re-points an already-bound suggestion in one call, last-writer-wins. 400 if the matchId has no catalogue row yet; 404 for an unknown suggestion id. |
| 2 | Clear one binding | Done. `DELETE` on the same path. 204 whether or not a match existed (idempotent); 404 only for an unknown suggestion id. Never touches the catalogue entry or any other suggestion bound to it. |
| 3 | `url` + `boxArtUrl` | Done, on `UpsertGameRequest`. Both **replaced wholesale on every push — omitting one clears it.** Must be absolute http/https, max 500 chars. |
| 4 | `displayTitle` | **Not needed**, and the reasoning for it was wrong on both counts (see "Two corrections" below). |
| 5 | `unboundOnly` / `updatedSince` | `?unbound=true` landed as asked. `updatedSince` was replaced by `?afterId=<id>` (`gs.id > afterId`) — the bot has no `updated_at` on a suggestion, so a time filter would silently miss a bind or a played mark, which an id monotonically increasing does not. |
| 6 | Filter games by `source` | Done. `?source=crew|gametown`, case-insensitive; invalid value is 400. |

**Two corrections to what we had assumed** (from the bot's author, confirmed against the OpenAPI
document):

- **Adoption by title was already case-insensitive.** "Counter-Strike 2" binds a suggestion typed
  "Counter-strike 2" without `displayTitle`. What was actually missing was direct control over
  binding, which asks 1 and 2 provide — `displayTitle` would not have fixed anything a direct bind
  doesn't already fix better.
- **Binding was never API-only.** The crew binds and unbinds inside Discord too. What was missing was
  *GameTown* being able to, which is what landed.

**A bug fix that also landed:** a Crew paste inside Discord could previously overwrite a title
GameTown had pushed and mark the entry Crew-owned. A push now always wins, which is also what makes
`?source=` a trustworthy *display* filter (it still isn't a signal GameTown itself should act on — see
"Already settled" above).

### What changed in this codebase

- `API/Services/Lan/LanBotClient.cs` gained `MatchSuggestionAsync`/`UnmatchSuggestionAsync`; lost
  `DeleteGameAsync` (its only caller, `UnlinkAsync`, no longer needs a whole-entry delete) and the
  `X-GameTown-Base-Url` header (replaced by the real `url` field). `UpsertGameAsync` now takes
  `url`/`boxArtUrl` and sends GameTown's canonical title rather than the suggestion's raw text.
- `API/Services/Lan/LanSuggestionService.cs`: `LinkAsync` pushes the game then binds directly —
  the entire `"bound-elsewhere"` path is gone, since a direct bind resolves that case rather than
  merely reporting it. `UnlinkAsync` clears the suggestion's own match via the new endpoint in every
  case, so the `PushedAtUtc`-gated asymmetry between a GameTown-created entry and an adopted one is
  gone too.
- `Contracts/Lan/LanContracts.cs`: `LanSuggestionContract.BoundElsewhere` and
  `LanBulkResult.BoundElsewhere` removed; `LanLinkResult.Reason` gained `"game-not-found"` (the direct
  bind's 400) in place of `"bound-elsewhere"`.
- `GameTownApp/Pages/Lan/LanSuggestions.razor`: the amber "Linked elsewhere in the bot" badge and the
  bound-elsewhere messaging in the link/bulk-link outcomes are gone.
- `Tests/GameTown.Tests/LanIntegrationTests.cs`: `FakeLanBot` models `/suggestions/{id}/match` per the
  OpenAPI document; `Linking_a_suggestion_the_crew_already_bound_records_the_game_but_says_the_link_did_not_move`
  became `Linking_a_suggestion_the_crew_already_bound_succeeds_and_repoints_it`.

### Why `?unbound=`/`?afterId=` are still not used for reconciliation

This is the one landed ask that would be actively harmful taken at face value, exactly as originally
flagged below (see ask 5's old "the trap" note). `LanSuggestionService.ReconcileAsync` (phase 2 of
`SyncAsync`) detects bindings that have *disappeared* at the far end by noticing a suggestion the bot
no longer reports as bound — it needs the **full** suggestion list to do that. A filtered pull would
make every suggestion absent from a filtered page look unbound, and reconciliation would release the
lot. `GetSuggestionsAsync` therefore still pages through everything every sync; the filters exist for
whoever else calls this API, not for GameTown's own polling loop.

---

## The original asks, for the record

### 1. `POST /api/v1/suggestions/{id}/link { matchId }` — bind a suggestion directly

**Why.** Binding is currently a side effect of writing a catalogue title, which means we can only ever
bind a suggestion whose name we can reproduce exactly, and can never claim one that is already bound
to something else. Both are real limits, not inconveniences.

Landed as `PUT /api/v1/suggestions/{id}/match` — see "What landed" above.

### 2. A way to clear one suggestion's binding

**Why.** `DELETE` on a catalogue entry is the only lever, and it unbinds everything attached to that
entry. One mis-bound suggestion cannot be fixed without collateral damage.

Landed as `DELETE` on the same path — see "What landed" above.

### 3. `url` — and ideally `boxArtUrl` — on `UpsertGameRequest`

**Why.** We know each game's canonical link and its cover, and there is no field to send them, so the
bot has to compose links from its own configuration.

Landed as asked — see "What landed" above.

### 4. A `displayTitle` separate from the binding `title`

**Why.** Because binding is by exact title match we must send the suggestion's raw text, so the
catalogue displays "Counter-strike 2" rather than the library's "Counter-Strike 2".

Confirmed moot once ask 1 landed — see "Two corrections" above.

### 5. `unboundOnly` and/or `updatedSince` on `GET /api/v1/suggestions`

**Why.** We poll on a timer and pull the whole list every time. Politeness now; it matters more as the
list grows.

Landed as `?unbound=` and `?afterId=` — see "What landed" and "Why ... still not used" above for why
GameTown's own sync does not use either.

### 6. Minor: filter `GET /api/v1/games` by `source`

**Why.** Convenience for the crew's own view of which entries came from where.

Landed as `?source=crew|gametown`. Still nothing changes in GameTown's own code — see "Already
settled" above for why `source` is not a signal GameTown should read.
