# shinymas-dl

*THE IDOLM@STER SHINY COLORS* asset downloader and extractor. Fan-made, not
affiliated with Bandai Namco Entertainment or enza. Use for research and personal
archival in accordance with the game's terms of service.

It reads the browser client's own asset map, mirrors files from the game CDN,
decrypts the text assets, and sorts everything into per-character, per-card and
per-story folders. No login, game install or third-party data is needed.

## Requirements

- .NET 8 SDK

The CDN had no region lock when this was written; requests from a US host worked.

## Usage

```sh
dotnet build ShinymasDl.slnx

shinymas-dl refresh
shinymas-dl list [filters...] [--categories ...] [--stats] [--depth n]
shinymas-dl download [filters...] [--categories ...] [--webp] [--concurrency n] [--dry-run] [--retry-missing]
shinymas-dl names
shinymas-dl extract [filters...] [--categories ...] [--overwrite] [--concurrency n]
```

Run `refresh` first and after game updates. It only fetches the asset map chunks
whose version changed.

Filters are case-insensitive substrings; a path must contain all of them.
Categories are the top-level folders: `images`, `sounds`, `spine`, `json`,
`movies`, `ae`, `particles`, `fonts`.

Every command accepts `--data <dir>` (default `data`), `--output <dir>` (default
`output`) and `--asset-root <url>`.

A typical run:

```sh
shinymas-dl refresh
shinymas-dl download --categories json      # story scripts, needed for names
shinymas-dl download 1040010010             # one card: images, spine, movies, voice
shinymas-dl extract
```

### download

Files land in `output/raw/` under their logical paths. JSON and atlas files stay
encrypted there; `extract` decrypts them. Re-runs skip files whose version has not
changed, and files the CDN does not serve are recorded so they are not retried
until `--retry-missing`.

`--webp` fetches the `.webp` twin of every `.png`. The default is PNG. Spine
atlases still name `data.png` as their page, so keep PNG if you load the
skeletons in a Spine runtime.

Asset map v442 lists 386,102 files, 35.19 GiB as reported by the manifest:

| Category | Files |
|---|---|
| sounds | 273,121 |
| images | 76,921 |
| spine | 17,163 |
| json | 10,804 |
| movies | 3,352 |
| ae | 2,524 |
| particles | 2,213 |
| fonts | 4 |

All 10,804 JSON files come to 21 MiB and took about 4 minutes at
`--concurrency 16` in one test run.

Some listed files cannot be downloaded without a game session. The client adds a
per-card hash from the logged-in API to those filenames (`<hash>_<id>.jpg`), and
the hash cannot be derived from the id. They are reported as missing. Card art is
hit hardest; a HEAD check of every card image in asset map v442 found:

| Folder | Reachable |
|---|---|
| `images/content/idols/card` | 68 of 546 |
| `images/content/support_idols/card` | 76 of 915 |

Spine models, card voices and story voices were reachable in every sample tried.

### names

Character folders are named from the game's own story scripts: each line carries
`charId`, a romanized `charLabel`, and the `speaker` name. `names` prints the
table and caches it in `data/names.json`; `extract` rebuilds it whenever scripts
are mirrored. Without scripts, folders use bare ids.

Card ids encode the character: `1 04 001 0010` is type (1 produce, 2 support),
rarity, character id and sequence.

### extract

Offline. Reads `output/raw/`, decrypts JSON and atlas files, and copies the rest.

## Output

```text
output/
├── raw/                                  # CDN mirror under logical paths
├── idols/
│   └── 001_mano/
│       └── 1040010010/                   # card.jpg, icon.png, card.mp4, ...
│           ├── spine/<kind>/             # data.json, data.atlas, data.png
│           └── voice/                    # gasha.m4a, awake_step_*.m4a, ...
├── support_idols/
│   └── 001_mano/2030010010/              # card.jpg, card_s.png, ...
├── characters/
│   └── 001_mano/                         # icons, sign, spine/, voice/
├── scenarios/
│   └── <type>/<id>/                      # script.json, script.txt, voice/
└── other/                                # everything without a rule, decrypted
```

Awake cards reuse their base card id, so their files share its folder with an
`awake_` prefix.

`script.txt` is a readable transcript: one `speaker: line` per entry, choices
marked with `>`, and the matching `voice/<id>.m4a` file when the line is voiced.

## How the CDN works

Assets live at `https://shinycolors.enza.fun/assets/`. The client's
`resource_hash` WebAssembly module handles filenames and text decryption, and
this tool reimplements both in C#.

Filenames:

```text
sha256(stem[0] + stem[^1] + "/assets/" + path)       # hex, lowercase
```

`stem` is the filename without extension. `.m4a`, `.mp4` and `.mp3` keep their
extension after the hash; everything else is the bare hash. The version from the
asset map goes in `?v=`.

Text assets (`.json`, `.atlas`) are XORed with a 54-byte key embedded in the
module and then gzipped with the trailer cut off. Images, audio, video and fonts
are served as-is.

The asset map manifest is at `asset-map-` + the hash of `asset-map.json`,
fetched without `?v=`. In v442 it names 129 chunks (`asset-map-chunk-N.json`),
each an encrypted `path -> version` object of about 3,000 entries.

A path the CDN does not have returns the game's HTML page with status 200, not a
404, so the tool checks the content type.
