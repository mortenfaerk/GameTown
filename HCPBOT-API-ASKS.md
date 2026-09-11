# HcpBot Catalogue API — outstanding asks

**Status as of 2026-09-11:** sent to the bot's author, who is working on them. Nothing here is
blocking — the integration shipped in 0.6.0 works against the API as it stands. Each ask removes a
compromise, and each one has a "what changes here" section so picking this up later does not mean
re-deriving why we wanted it.

Dev environment: `https://dev-api-hcpbot.znoozles.net`. The OpenAPI document and Scalar UI sit behind
HTTP Basic (credentials from the bot's author); `/api/v1/*` needs only the `X-Api-Key` header, which
is stored as the `LanBotApiKey` setting and is **not** in this repository.

---

## What the bot actually does

Verified against the live dev instance rather than read off the OpenAPI document, which describes
none of it. Everything below is premise for the asks, and is worth re-checking before acting on this
file — the whole point of the asks is that some of it should change.

1. `PUT /api/v1/games/{matchId}` replaces the catalogue entry's **title**. An entry holds exactly one.
2. It then binds every suggestion whose name equals that title **and is currently unbound**, and
   reports how many in `suggestionsBound`. An already-bound suggestion is left alone and the count
   comes back `0` — even for a PUT carrying its exact name.
3. It never unbinds and never re-points anything, not even when the new title matches no suggestion
   at all. Only `DELETE` on the entry removes bindings, and it removes *every* binding on that entry.

### Already settled

- **`matchId` is the GameTown game's own `Guid`.** Confirmed with the bot's author on 2026-09-11, so
  it is no longer an assumption. It is what makes `{gametown}/game/{matchId}` resolve directly.
- **`source` reflects the API key, not the client.** An entry created with our key reads
  `source: "gametown"` whoever made it — a human poking the dev API with curl included. So it is not
  evidence GameTown created something, which is why `LanSuggestion.PushedAtUtc` (our own record of
  what we pushed) is what gates the delete on unlink. Do not "simplify" that to a `source` check.

---

## The asks

### 1. `POST /api/v1/suggestions/{id}/link { matchId }` — bind a suggestion directly

**Why.** Binding is currently a side effect of writing a catalogue title, which means we can only ever
bind a suggestion whose name we can reproduce exactly, and can never claim one that is already bound
to something else. Both are real limits, not inconveniences.

**What changes here.** This is the big one — it deletes code rather than adding it:

- `LanSuggestionService.LinkAsync`'s whole `"bound-elsewhere"` path goes, along with the
  `BoundElsewhere` flag on `LanSuggestionContract`, the amber badge on the Linked tab, and the
  paragraph the screen shows when a contributor links a suggestion the crew already bound.
- `PushPendingAsync` binds per suggestion instead of writing a title and hoping, so the
  `suggestionsBound == 0` check that currently detects "the PUT landed but bound nothing" goes too.
- `LanBotClient.UpsertGameAsync` stops being the binding mechanism and becomes purely "make sure the
  catalogue entry exists", which pairs with ask 4.
- `FakeLanBot` in `LanIntegrationTests` needs its rules 2 and 3 rewritten, and
  `Linking_a_suggestion_the_crew_already_bound_records_the_game_but_says_the_link_did_not_move`
  becomes a test that it simply works.

### 2. A way to clear one suggestion's binding

**Why.** `DELETE` on a catalogue entry is the only lever, and it unbinds everything attached to that
entry. One mis-bound suggestion cannot be fixed without collateral damage.

**What changes here.** `UnlinkAsync` currently deletes the whole entry, which is only safe because it
first checks `PushedAtUtc` to be sure GameTown created it — a suggestion we merely *adopted*
(`LinkSource == "remote"`) releases locally and deliberately leaves the bot alone, because deleting
someone else's entry to undo our own link would be wildly disproportionate. With a per-suggestion
release, unlink becomes the obvious thing in every case and that asymmetry disappears from the UI.

### 3. `url` — and ideally `boxArtUrl` — on `UpsertGameRequest`

**Why.** We know each game's canonical link and its cover, and there is no field to send them, so the
bot has to compose links from its own configuration.

**What changes here.** `PublicBaseUrl` gains its first real consumer. Today it is honest but thin: the
LAN screen renders the deep link so an operator can hand it over, and `LanBotClient.CreateClientAsync`
sends it as an `X-GameTown-Base-Url` header the bot ignores. When `url` lands, that header becomes a
body field, and three pieces of hedging can go — the "no functional consumer" note in this repo's
`CLAUDE.md`, the equivalent caveat in `SettingsContracts.PublicBaseUrl`, and the explanatory
paragraph on the settings tab. `boxArtUrl` would additionally put real covers in the Discord embeds,
served from `/media` like everything else.

### 4. A `displayTitle` separate from the binding `title`

**Why.** Because binding is by exact title match we must send the suggestion's raw text, so the
catalogue displays "Counter-strike 2" rather than the library's "Counter-Strike 2".

**What changes here.** `PushPendingAsync` and `LinkAsync` send the library title as `displayTitle` and
the suggestion's text as `title`. **If ask 1 lands first this one stops mattering**, because the title
would no longer be load-bearing at all and we could simply send the canonical one.

### 5. `unboundOnly` and/or `updatedSince` on `GET /api/v1/suggestions`

**Why.** We poll on a timer and pull the whole list every time. Politeness now; it matters more as the
list grows.

**What changes here — and the trap.** `LanBotClient.GetSuggestionsAsync` would pass the filter, but
**only phase 1 of the sync can use it.** Phase 2 (`ReconcileAsync`) detects bindings that have
*disappeared* at the far end, and it does that by noticing a suggestion the bot no longer reports as
bound. Feed it a filtered list and every suggestion missing from that page looks unbound, so it would
release the lot. Either keep a full pull for reconciliation, or ask the bot to report removals
explicitly. This is the one ask that can quietly break something if taken at face value.

### 6. Minor: filter `GET /api/v1/games` by `source`

**Why.** Convenience for the crew's own view of which entries came from where.

**What changes here.** Nothing. See "Already settled" above for why `source` is not a signal GameTown
should be reading anyway.

---

## When something lands

1. Re-verify the three behaviours at the top against the dev instance — that is what the
   implementation is shaped around, and an ask landing may change more than the field it added.
2. Update `FakeLanBot` in `Tests/GameTown.Tests/LanIntegrationTests.cs` **first**. Its comment block
   is the written record of the bot's contract, and getting it wrong means every test passes against
   a bot that does not exist.
3. Update the "Three behaviours" bullet in `CLAUDE.md`'s *LAN bot* section, and this file.
