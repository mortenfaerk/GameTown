# Making LAN suggestion matching scale

**Status: implemented.** What follows is the plan as written, with the sections that changed during
the build marked and corrected. One threshold in it was wrong in a way the measurements caught — see
§A — and that correction is the most important thing on this page.

The wishlist screen (`GameTownApp/Pages/Lan/LanSuggestions.razor`) works at the scale it was written
against — a handful of suggestions, checked one at a time. It does not survive a real LAN's backlog.
This records what actually breaks, measured against a seeded 129-game library with 126 suggestions
(79 on the wishlist), and what to build instead.

Everything below was observed in a browser against a running build, not reasoned about from source.
The numbers are from that run.

## What breaks, and why it is not cosmetic

### 1. The candidate list is mostly wrong, and says so nowhere

`SuggestionMatcher.Rank` takes the top 10 of everything scoring above zero. Above zero is a very low
bar, so the list is **always ten items long regardless of whether any of them is right**. Quantity is
constant; quality is not; the UI renders both identically — ten evenly-weighted buttons.

Measured over all 78 wishlist rows, by the top candidate's score:

| Top score | Rows | What the list actually contains |
|---|---|---|
| ≥ 0.6 | 14 | usually the right game, first |
| 0.5 – 0.6 | 11 | right game usually present, not always first |
| 0.4 – 0.5 | 11 | coin flip |
| 0.2 – 0.4 | 10 | noise |
| < 0.2 | 32 | **pure noise — nothing in the library is this game** |

**41% of the queue (32 of 78) has no plausible match at all**, and every one of those rows still
offers a "Find match" button that opens ten wrong answers. Worked examples from the run:

- `swat 4` → Trine 4, Battlefield 4, Left 4 Dead, Europa Universalis IV, Hearts of Iron IV, Left 4
  Dead 2, Age of Empires IV, StarCraft II, Call of Duty 4, A Way Out. SWAT 4 is not in the library.
  The only correct action is to leave it or set it aside, and the screen presents ten one-click ways
  to get it wrong. Worse, the top entry carries the note *"also linked from 'Trine 4'"*, which reads
  as social proof next to the most wrong answer on screen.
- `CS2` → top candidate **Rust** (0.10). Counter-Strike 2 is in the library and does not make the
  top three. The commonest Discord shorthand there is fails completely.
- `DRG` → Darktide, DayZ, Duck Game. Deep Rock Galactic is in the library and is not offered.
- `q3a` → **Squad** (0.16) above Quake III Arena (0.092).
- `Worms 2` → **Portal 2** (0.5). Worms Armageddon is nowhere near the top.
- `asdf` → ten candidates, all at 0.1. Junk gets a full menu of wrong answers.

This is not a matcher bug. `Rank` is documented as advisory and ordering-only, and that is the right
call — see the `SuggestionMatcher` class comment and `LanMatchingTests`. The bug is that the screen
presents an advisory ordering as if it were a set of answers.

### 2. Score alone is not the confidence signal — score *and margin* are

The high-scoring rows are not uniformly safe, and this is the finding that should shape the design:

| Suggestion | Top candidate | Score | Gap to #2 | Right? |
|---|---|---|---|---|
| `helldivers` | Helldivers 2 | 0.633 | **0.488** | yes |
| `worms armageddon lan` | Worms Armageddon | 0.720 | **0.410** | yes |
| `Deep Rock` | Deep Rock Galactic | 0.600 | **0.378** | yes |
| `Age of Empires 2` | **Age of Empires IV** | 0.825 | 0.242 | **no** — AoE II: DE is in the library |
| `the jackbox party pack` | Jackbox Party Pack 8 | 0.810 | **0.000** | coin flip — Pack 9 ties exactly |
| `Battlefield 2142` | Battlefield 1942 | 0.650 | 0.025 | **no** — 2142 is not in the library |

`Age of Empires 2` scoring 0.825 on the *wrong* game is the case that kills any plan to auto-apply
above a threshold: roman-numeral folding makes `age-of-empires-2` one edit from `age-of-empires-4`,
while the correct `age-of-empires-2-definitive-edition` is penalised for length. A design that
pre-ticks on score alone ships that mistake. A design that requires a **margin** does not: gap 0.242
against a 0.488 / 0.410 / 0.378 cohort is visibly a different class of answer.

### 3. There is no way out when the ranking misses

`Rank` takes 10 and the UI shows exactly those 10. If the right game is 11th — or is `CS2` →
Counter-Strike 2, which the ranker buries — there is **no search box, no browse, no manual pick**.
The operator's only options are to link the wrong game or give up. This is the single hardest wall as
the library grows, because ranking quality falls as the library grows.

### 4. Every action reloads everything, and the list shifts under the cursor

`Link`, `Unlink` and `Dismiss` all call `Reset()` then `Load()`, which refetches the whole tab plus
the count. Observed: setting aside `quake3 arena ctf` removed the row, **shifted every row below it
up by one**, and left the cursor hovering the *next* suggestion's "Set aside" button. Two quick
clicks in the same place set aside two different suggestions, the second unintentionally. Triage —
the thing this screen exists for — is exactly the rapid-fire pattern that trips this.

There is no undo. Recovering means switching tabs, finding the row among the set-aside ones, and
clicking "Put back".

### 5. Feedback appears where nobody is looking

The success banner renders at the top of the page. Acting on a row 700px down produces **no visible
confirmation at all** — the only evidence is a row vanishing. The banner also survives a tab switch,
so the Linked tab opens carrying a stale message about a wishlist action.

### 6. The count in the sidebar goes stale

`LanNavLink` reads the count once in `OnInitializedAsync` and never again. After setting one
suggestion aside the tab badge went 79 → 78 while the sidebar badge stayed **79** until a reload.

`LanNavLink` also has a scaling problem of its own: it decides whether to render by calling
`Lan.Get("all")` whenever `unmatched == 0`. The healthy steady state — an empty wishlist — therefore
**downloads the entire suggestion list on every single page load of the app**, for one boolean.

### 7. Everything is one flat list

78 rows, no pagination, no search, no filter, no sort, no grouping by LAN event, no bulk action.
The obvious junk (`asdf`, `???`, `(placeholder)`, `test entry pls ignore`) has to be dismissed one
row at a time, each one costing a full list reload. The table has no `table-responsive` wrapper and
measures 2239px wide, so it overflows rather than adapting. The "Match" column is dead space —
an em-dash — on every unexpanded row, which is all of them.

### 8. The Linked tab is 40 rows of mostly nothing

Auto-matching requires normalised equality, so on most Linked rows the suggestion name and the game
title are the same string twice. The rows that need a human — `Not sent yet`, `Linked elsewhere in
the bot` — are buried among ~36 that need nothing. There is no count badge, no filter, no search.

## The shape to build

The codebase already solved this exact problem once, on `Pages/Admin/MetadataRelink.razor`: propose
in bulk, show confidence, pre-tick only what is safe, let a human apply. Reuse that shape rather than
inventing a second vocabulary for the same job.

### A. Rank once, for the whole queue, server-side

Add `GET /lan/suggestions/ranked` returning each unmatched suggestion **with its top candidates and a
confidence band already attached**. One request instead of one-per-row-on-demand, and it makes the
queue sortable by how much work each row is.

Band it on score *and* margin, per §2.

> **Corrected during the build.** The plan said `strong` = top ≥ 0.6 **and margin ≥ 0.15**. Measured
> against a real shelf, that puts `Age of Empires 2` → **Age of Empires IV** (0.825, margin 0.242) in
> the `strong` band — pre-ticked, one bulk click from Discord, with the correct *Age of Empires II:
> Definitive Edition* sitting right behind it. The exact mistake this mechanism exists to prevent,
> shipped by the mechanism itself. The margins turn out to separate cleanly with nothing in between:
>
> | | margins |
> |---|---|
> | correct | 0.500, 0.527, 0.600, 0.886 |
> | wrong or a coin flip | 0.000, 0.025, 0.242, 0.269 |
>
> The bar has to sit above 0.269 and below 0.500. **0.35** is the middle of that gap and is what
> shipped.

A second rule was added that the plan did not anticipate, and it is the same idea at the other end.
`swat 4` leads at 0.414 with Battlefield 4 at 0.392 behind it; `mario kart` leads at 0.200 with
Magicka 2 tied exactly. Neither game is in the library, so every candidate is wrong — a **flat field
of equally-bad matches is the matcher having no opinion**, and a score bar low enough to catch it
would also hide the real choices. So:

- `strong` — top ≥ 0.6 **and** margin ≥ 0.35
- `weak` — top < 0.2, **or** (top < 0.5 **and** margin < 0.1). **No candidate list at all**, just the
  suggestion, a library search and "set aside".
- `ambiguous` — everything else.

A third trim was added for the rows that *are* shown: candidates within 50% of the leader's score, at
most five. Banding removes the rows where every candidate is noise; this removes the noise *tail* on
the rows worth showing. `Battlefield 2142` led with four Battlefield titles and then listed Half-Life
2, Borderlands 2, Castle Crashers, Dirt Rally 2.0 and FIFA 23 at 0.10–0.13, every one of them there
because it ends in a digit.

The thresholds live in `API/Services/Lan/MatchConfidence.cs` with the measurements that set them, out
of the automatic path — this bands what a person is shown, and nothing else. The negatives are pinned
in `LanMatchingTests`: `Age of Empires 2` must not band `strong`, and `swat 4` and `mario kart` must
band `weak`.

### B. Three queues instead of one list

Replace the flat wishlist with sections ordered by how much judgement each needs:

1. **Likely matches** (`strong`) — a checkbox list, pre-ticked, showing suggestion → proposed game
   with box art and score. One **"Link selected"** button. This is where the 14 rows go, and it turns
   fourteen expand-read-click-reload cycles into one review and one press.
2. **Needs a decision** (`ambiguous`) — expanded inline, candidates as a **radio group with the score
   and a "none of these" option**, not as ten buttons that each fire immediately.
3. **Nothing in the library** (`weak`) — the 32-row bulk of the queue, rendered compactly with no
   candidates. Multi-select → **"Set aside selected"**. Junk clears in one action.

`BoxArtUrl` is already on `LanCandidateContract` and is currently **ignored by the UI**. Render it —
`MetadataRelink` already does this with `GamesService.ResolveMedia` and a 2.5rem thumbnail. Two games
with similar names are indistinguishable as bare text and obvious as covers.

### C. Never link on a single click

Linking pushes to Discord and is awkward to undo. Selecting a candidate should *stage* it; an
explicit "Link" commits. This also removes the mis-click hazard of ten 20px-apart buttons.

### D. Search the library, as a first-class path

Every ambiguous and weak row gets a **"Search the library"** input that queries `/GTGames/search` —
the endpoint already exists, returns `GameContract` with box art, and needs no new server code. This
is the fix for §3 and the one change that stops the screen degrading as the library grows.

### E. Stop reloading everything

Mutate the row in place on success and drop it from its section, rather than `Reset()` + `Load()`.
Nothing above the acted-on row moves, the cursor stays over what it was over, and the "Link selected"
flow reports *n* successes at once instead of *n* full reloads.

Keep `Busy` per row, not per page — one in-flight link should not grey out the rest of the queue.

### F. Feedback at the row, and an undo

Put the outcome on the row that produced it. Keep the top banner only for the bulk actions and for
`bound-elsewhere`, which genuinely needs the long explanation it already has.

Set-aside should offer **Undo** in that row-level feedback for as long as it is on screen. It is a
local-only flag, so undo is a second `Dismiss(false)` call — cheap, and it makes bulk dismissal safe
enough to actually use.

### G. Paginate and filter

Page the sections, and add a filter bar: free-text over suggestion names, and a LAN event filter
(`/lan/events` already returns the events). Give Linked a count badge, a filter, and a **"needs
attention" default** that shows `Not sent yet` and `Linked elsewhere in the bot` first — those are
the only Linked rows anyone acts on.

Wrap the table in `table-responsive`, or drop to a card layout below ~768px.

### H. Fix the two count bugs

- Have `LanNavLink` re-read the count when the LAN screen changes it — a shared state object or an
  event on the service, the same way the count is already kept in step between the tab badge and the
  header text.
- Give `LanNavLink` a cheap configured-check instead of `Lan.Get("all")`. `LanCountContract` is the
  natural place: add a `Total` alongside `Unmatched` so one small request answers both questions, and
  an empty wishlist stops costing a 126-row download on every page load.

## What shipped

All of A–H, plus three things the build turned up that the plan had not:

- **Band shortcut chips.** Sections are ordered strong → ambiguous → weak and *then* paged, so the
  queue is worked front to back. That is right for working through it and wrong for the job people
  come back to do on its own — clearing the junk, which is the last section and sits behind a page or
  two of reading. The chips filter to one band using the parameter the ranked route already took.
- **Ordering by band before paging.** Without it a page is a slice of the queue in arrival order, so
  "Likely matches" held whichever handful of strong rows happened to land on that page — one of
  fourteen, in the first run — and "Link selected" quietly meant "link the ones you can see".
- **Staged rows excluded from bulk set-aside.** Picking a game from the library search ticks the row
  so a bulk link picks it up; in the weak section the bulk button is *Set aside*, so that tick
  offered to discard the row somebody had just found the game for.

Two bugs the plan listed were confirmed fixed in the browser: the sidebar badge now tracks the tab
badge through every action, and `LanNavLink` no longer downloads the whole suggestion list to decide
whether to render.

### Measured on the same 78-row queue

| | before | after |
|---|---|---|
| rows offering candidates | 78 | 33 |
| rows offering *only wrong* candidates | 45 | 0 |
| candidates rendered across the queue | 780 | 81 |
| average candidates per row shown | 10 | 2.5 |
| pre-ticked proposals | — | 4, all correct |

`CS2` → Counter-Strike 2 and `DRG` → Deep Rock Galactic — neither reachable at all before — now take
a few seconds each through the library search.

## What must not change

- **`SuggestionMatcher`'s automatic bar stays normalised equality.** Banding is presentation. A
  `strong` band must never auto-link — `Age of Empires 2` → Age of Empires IV at 0.825 is why.
- **The pushed title stays the suggestion's text verbatim.** Title equality is what makes the bot
  bind; see the `008` migration header.
- **Nothing is ever re-pushed.** Bulk linking must still push each suggestion at most once.
- **Suggestion and event names stay plain text.** Discord input, never `MarkupString` —
  SECURITY-NOTES risk 11.
- **`GameId` and `RemoteMatchId` stay two separate claims.** A bulk "Link selected" reports per row,
  because some rows will come back `bound-elsewhere` and that is a success that puts no link in
  Discord.
