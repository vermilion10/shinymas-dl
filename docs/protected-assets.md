# Protected card assets

How the client finds the real CDN path for card assets that `download` reports as
missing, and what a downloader needs to reproduce it. The `albums` command
implements the flow; the sections below are the research that led to it.

Evidence comes from static analysis of client build `app-7a9a95e69a346e6e7147.js`
and its 1,109 lazy-loaded chunks, plus unauthenticated HEAD checks against the CDN.
Module ids (`28512`, `83078`, ...) are webpack ids from that build and will change
between builds. The early sections come from static analysis alone; the dated
sections at the end record what a logged-in session confirmed.

## Summary

- The missing files are named `<hash>_<id>.<ext>`. The hash belongs to the card,
  not the file: one card hash unlocks that card's icon, card art, thumbnail, fes
  art, awake art, memorial scenes and movies.
- The hash arrives as a `hash` field on card records in API responses. Viewing card
  art is gated by ownership in the UI, which is why the network tab only shows those
  requests after a condition is met. The hash itself is delivered earlier.
- The character album (`POST characterAlbums/characters/{characterId}`) renders a
  hashed icon for every card of that character, owned or not. That implies one album
  call per character returns hashes for all of that character's cards.
- Support-card whisper voices use a second, per-voice `voiceHash`. It rides on the
  support card record and the client decides release state locally.
- The API codec (`request_hash` module) runs outside the browser; an encode and
  decode round trip was reproduced in Node. A downloader could replay the album calls
  with the user's session instead of the user triggering every condition by hand.

## What is protected

The path builders in module `61643` take an optional hash and prepend it:

```js
T = function (e) { return e ? "".concat(e, "_") : "" }
createImagePath(type, kind, id, hash)  ->  images/content/<type>/<kind>/<hash>_<id>.<ext>
createMoviePath(type, kind, id, hash)  ->  movies/<type>/<kind>/<hash>_<id>.mp4
```

The asset map lists these files without the prefix, so the unprefixed URL falls
through to the SPA page. `getQueryString` in module `5431` strips `[a-z0-9]{32}_`
before looking up the version, so the hash is 32 characters and the `?v=` comes from
the unprefixed entry.

HEAD checks of every entry in asset map v442, with no hash:

| Folder | Reachable |
|---|---|
| `images/content/idols/card` | 68 of 546 |
| `images/content/idols/icon` | 68 of 546 |
| `images/content/support_idols/card` | 76 of 915 |
| `images/content/support_idols/icon` | 75 of 915 |
| `sounds/voice/whisper` | 0 of 42 |

Card and icon give identical per-rarity counts (for `104`, SSR produce: 19 of 348
in both), which is what a per-card hash predicts. Older cards appear to have an empty
hash and stay reachable unprefixed.

Hash sources found in the client, all resolving to the card's own `hash`:

| Field | Where | Resolves to |
|---|---|---|
| `this.hash` | card mixins: icon, card, card_thumbnail, movies | card record |
| `idolHash` | awake steps, memorial scenes | copied from `idol.hash` (`e.idolHash = e.idol.hash`) |
| `supportIdolHash`, `idolHash` | deck and card images | card record |
| `centerIdolHash` | fes icons, normal and awake | card record |
| `voiceHash` | whisper voices | per-voice, separate |

Other hashed families (gasha banners, start backgrounds, poker designs, tap counter
events, music videos) carry their own `hash` on their own records and are out of
scope here.

## Where card hashes come from

### Character album (primary candidate)

Module `28512`:

```js
getCharacterAlbum: e => s.A.post("characterAlbums/characters/".concat(e))
```

The produce card mixin on album entries:

```js
get isJoined() { return this.hasIdol },
getIconImagePath() { return createImagePath("idols", "icon", this.id, this.hash) },
getMoviePath(e) { ... createMoviePath("idols", e, this.id, this.hash, true) ... }
```

The support card mixin is the same with `hasSupportIdol`.

The album list view (chunk `59830-bd011309a1d36958fe26.chunk.js`) builds a sprite
for every entry and only tints the unowned ones grey:

```js
var i = Sprite.new(e.getIconImagePath()).addTo(r);
e.isJoined ? (i.on("tap", ...), i.interactive = true) : i.tint = 10066329;
```

It also shows an owned/total count (`t.filter(e => e.isJoined).length, t.length`).
Icons are hash-protected at the same rate as card art, so for the grey icons to
load, unowned entries must carry a valid `hash`. Tapping, and with it the card art
request, is only enabled for owned cards. That matches the "only after a condition"
behavior in the network tab.

The character list for the album comes from `GET album/top` (module `51067`),
`characters[]`.

### Other responses that carry card hashes

These also attach hash-using mixins, but either need ownership or are tied to a
mode:

- `GET producerDesk/characters/{characterId}` (module `83078`): `idols[]` with
  `card_thumbnail` paths. The mixin has no ownership flag, which suggests it only
  lists produced cards.
- Gasha: `gashas/{id}`, `gashaGroupInfo/{id}`, foil rewards (module `28512`).
  Covers the cards in a given banner's pool.
- `userIdols`, `userSupportIdols` and their `v2/` versions: owned cards only.
- Fes, fes tower, fes raid and produce teaching modules: cards in the user's decks
  and opponents.

## Whisper voices

Module `67475`, applied to `supportIdol.idolWhisperVoices[]`:

```js
getTrackPath() { return createWhisperVoicePath("whisper", this.idolId, this.id, this.voiceHash) },
getIsReleased(stage) { /* true when stage >= condition.value for "evolution_stage" */ }
```

The support card mixin sets every whisper voice to unreleased and then filters with
`getIsReleased(this.evolutionStage)` on the client (`getReleasedIdolWhisperVoices`).
Since the full list with its release conditions reaches the client, `voiceHash` is
probably present for unreleased voices too. Not confirmed.

## API transport

Module `96525`. Every call goes to `API_ROOT` (`https://api.shinycolors.enza.fun/`)
as a single POST:

1. The client writes a raw HTTP/1.1 request as text: `GET /album/top HTTP/1.1`,
   headers, blank line, JSON body for non-GET.
2. `request_hash.encodeRequest(text)` encrypts it. The module is
   `request_hash-1379a0b21b9ec5d399cc.chunk.js` (asm.js) or the
   `request_hash.wasm` version; it contains SFMT and zlib and the tag
   `yasu_request_hash`.
3. The bytes go out as `application/octet-stream`; the response is an arraybuffer.
4. `decodeResponse(bytes, length, x-sessionid)` decrypts it, keyed with the
   `x-sessionid` response header.

Headers the client sends:

| Header | Value |
|---|---|
| `Authorization` | enza platform token from `ezpf.startLogin(GAME_ID)` |
| `x-sessionid` | copied from the previous response's header before each call |
| `x-version` | `410` in this build |
| `x-em-version` | `1907c171c3ecf981b0a3793e39b40cc48d3f747e` in this build |
| `x-app-version` | native app only; not sent from a browser |

Behavior that matters for a replaying client:

- Requests are queued and sent one at a time, and each response's `x-sessionid`
  replaces the one used for the next call.
- Status `1012` means retry with the new session id; `1090` means wait 80 ms and
  retry; both only for GET, up to 3 times.
- `x-is-banned` in a response makes the client stop and show the ban screen.
- Login is `POST login` after the platform token is set.

Reproduced offline in Node: loading the asm.js module and calling `encodeRequest`
gives the same bytes on every call for the same input, and `decodeRequest`
returns the original text. `decodeResponse` needs a real response to test.

## Can the downloader do this without manual play

Probably yes for released cards. Once a session exists:

1. `GET album/top` to list characters.
2. `POST characterAlbums/characters/{id}` for each (33 characters in
   `names.json` today, including collab).
3. Collect `id` and `hash` from produce and support entries, and whisper voices with
   `voiceHash`.
4. Build `<hash>_<id>` paths and download with the existing CDN code.

That is about 35 sequential API calls, far fewer than playing through every
unlock.

Open gaps:

- Cards with ids starting `193`, `194`, `293`, `294` (28 each, one per idol) have
  no normal rarity (`RARITIES` is N 1 to UR 5), and none were reachable. Whether the
  album lists them is unknown.
- Unreleased cards already in the asset map are unlikely to be in any album
  response.
- The session token comes from the enza platform login in a browser. The simplest
  route is the user copying a token or session from DevTools rather than the tool
  automating the login.
- Replaying API calls with a real account can trip server-side checks
  (`x-is-banned` exists). Read-only album calls are the lowest-risk requests, but the
  risk is the account owner's.
- `characterAlbums/characters/{id}` is a POST; whether it changes server state
  (for example read flags) is unknown.

## Confirm with a capture

The API bodies are encrypted, so the network tab only shows octet-stream. The page
also replaces `console.log`, `console.warn` and the `console.group` family with
no-ops, so the snippet uses `console.info`. To see
decoded responses, run this in DevTools before opening the album:

```js
const parse = JSON.parse;
JSON.parse = function (text) {
  const value = parse.apply(this, arguments);
  if (typeof text === "string" && text.includes('"hash"')) console.info("api", value);
  return value;
};
```

Then check:

1. Album for a character where you are missing a recent SSR: does the unowned entry
   (`hasIdol: false`) have a non-empty `hash`, and does
   `images/content/idols/card/<hash>_<id>.jpg` load?
2. A support card entry with whisper voices you have not released: is `voiceHash`
   present?
3. Whether any `193`/`194`/`293`/`294` ids appear in album responses.

## Live test from a cloud host (2026-09-27)

A dummy account's platform token was tried from a cloud container in the US.

Token: an RS256 JWT with claims `code`, `exp`, `id`, `is_app_installed`,
`is_apple_linked`, `is_game_user_registered`, `is_guest`, `registration_methods`
and `status`. It had no `iat`, and its `exp` had passed about two minutes before
the test, so the lifetime is short. A token saved ahead of time will not work; the
tool has to log in right after the token is captured and then keep going on the
session id.

API responses to single requests:

| Request | Client | Result |
|---|---|---|
| Random bytes, 1 to 500 | Node `fetch` | `403`, nginx page |
| Encoded `POST /login`, expired, fake or no token | Node `fetch` | `403`, nginx page |
| Single junk byte | `curl` | `500`, nginx page |
| Encoded `POST /login`, no token | `curl` | `401`, encrypted body |
| Encoded `POST /login`, no token | .NET `HttpClient` | `401`, encrypted body |

The `403` answered anything sent by Node's `fetch`, junk included, so it is a filter
on that client's request shape, not on the IP or the token. From the same host and
IP, `curl` reached the game server. Its `401` body decoded with `decodeResponse`
(empty session id) to:

```json
{"type":"","title":"Unauthorized","status":1010,"detail":"","instance":"/login","requestId":"..."}
```

So the request encoding, the transport and the response decoding all work outside
the browser, from a non-Japanese cloud IP. The user also reaches the game from a
normal, non-Japanese connection, so there is no region lock.

Consequences for login support:

- The HTTP client matters. .NET `HttpClient` passes the filter: the same no-token
  login got the game's `401` with and without a browser User-Agent.
- The command needs a fresh token at run time, read from an environment variable
  or a file under `data/`, and should log in immediately.

## Confirmed with a logged-in session (2026-09-27)

Login chain, all from the cloud host:

| Step | Request | Result |
|---|---|---|
| 1 | `GET platform-sdk.enza.fun/sessions/user_code?app_id=1&ts=<ms>` with cookie `_enza_session` | `200`, `{"token": "<game JWT>"}`, valid about 3 minutes |
| 2 | `POST login` with `Authorization: <game JWT>` | `200`, `{isCreated, user}`, first `X-Sessionid` arrives |
| 3 | `GET gameTop` | `200`, `X-Sessionid` and `X-Tutorial-Progress-Status` |
| 4 | `GET album/top` | `200`, 28 characters |
| 5 | `POST characterAlbums/characters/1` | `200`, full album |

`_enza_session` is a 32-character opaque value on `platform-sdk.enza.fun`. It is the
long-lived credential; the tool can mint a fresh game token from it on every run.
A different JWT from `portal.enza.fun` (claims `user_id`, `bn_user_id`,
`token_created_at`) is not accepted by the game, and a stale or foreign
`_enza_session` gets a guest identity (`isCreated: false`), which makes album calls
fail with `1010`.

Character 001 album:

- 22 produce cards (3 owned) and 42 support cards (4 owned). Every entry, owned or
  not, has a 32-character `hash`; none are empty, old cards included.
- All 64 card ids for character 001 in asset map v442 appear in the album,
  including the `193`, `194`, `293` and `294` groups.
- Costumes (`idolCostumes`, 23 entries) carry `hash`, `characterHash` and
  `evolutionSkinHash`.
- For unowned card `1940010010`, `images/content/idols/card/<hash>_1940010010.jpg`
  returned `200 image/jpeg` (76,235 bytes); the unprefixed URL returned the SPA page.

Whisper voices were checked on character 014; the account owns none of the support
cards that carry them.

## Whisper voices and the full crawl (2026-09-27)

Character 014 album, read with `shinymas-dl albums --characters 14`:

- 6 whisper voices on support card `2040140200`. The account does not own the card,
  so all 6 are locked, and all 6 have a `voiceHash`. Locked whispers do expose their
  hash; release is decided on the client from the evolution stage.
- Whisper entry fields: `hash`, `id`, `idolId`, `idolType`, `sort`, `time`,
  `title`, `track`, `voiceHash`. `id` is the file name, so
  `sounds/voice/whisper/2040140200/204014020001.m4a` lives at
  `.../2040140200/<voiceHash>_204014020001.m4a`.
- All 6 downloaded through the hashed path (64.6 MiB together).

Full crawl of all 28 album characters, same dummy account:

| Result | Count |
|---|---|
| Card hashes recorded | 1,440 |
| Card ids in asset map v442 (`images/content/idols` and `support_idols`) | 1,461 |
| Card ids without a hash | 21 |
| Unowned cards without a hash, any character | 0 |
| Whisper voices, all locked, all with `voiceHash` | 36 |
| Whisper files in the asset map | 42 |

- The 21 card ids without a hash are `1048...` (collaboration characters 801 to 804)
  and `20309...` (Hazuki, 091). `album/top` lists 28 characters and none of these,
  so no album page carries their hashes.
- The 6 whisper files without a hash are the `...000.m4a` file of each set
  (`204002022000`, `204008020000`, `204012019000`, `204014020000`, `204018020000`,
  `204021017000`). No album entry references them; their source is unknown.
- `idol_evolution_pieces` images use the piece's own hash, not the card's, and stay
  unreachable.
- Costumes: 23 of 23 base costumes for character 001 share their card's hash, so
  card hashes already cover costume art under `images/content/idols`.
