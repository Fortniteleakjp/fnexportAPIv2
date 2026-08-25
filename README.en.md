# fnexportAPI

[日本語](README.md) | **English**

A Fortnite asset-export Web API built on **CUE4Parse**. It keeps itself up to date from
GitHub releases. It downloads the live
Fortnite manifest, streams the required paks/chunks from the Epic CDN, and
exposes the parsed assets (JSON / PNG / audio) over HTTP. It also serves an FModel
backup (`.fbkp`) of the mounted build. All endpoints are
documented and explorable through **Swagger UI** (Japanese and English).

## Requirements

### 1. Oodle compression library

Fortnite paks use Oodle compression, so the **Oodle native library** is required.

Obtain it from an Unreal Engine installation (or another tool such as FModel):

- **Windows**: `oo2core_9_win64.dll`
- **Linux**: `liboo2corelinux64.so.9`

There are three ways to make it available (checked in this order):

**Option A — place it next to the executable / build output**
```bash
# Windows
copy oo2core_9_win64.dll FortnitePorting/bin/Debug/net10.0/

# Linux
cp liboo2corelinux64.so.9 FortnitePorting/bin/Debug/net10.0/
```

**Option B — place it in the project `libs/` directory**
```bash
mkdir -p libs
copy oo2core_9_win64.dll libs/      # Windows
cp liboo2corelinux64.so.9 libs/     # Linux
```

**Option C — point an environment variable at it**
```bash
# Windows
set OODLE_DLL_PATH=C:\path\to\oo2core_9_win64.dll

# Linux
export OODLE_DLL_PATH=/path/to/liboo2corelinux64.so.9
```

> The same lookup applies to `zlib-ng2.dll` (Windows) / `libz-ng.so` (Linux).

### 2. RAD Audio decode library (optional — only for RADA → WAV)

Most modern Fortnite sounds are encoded as **RADA** (RAD Audio). Decoding RADA to
WAV requires the native RAD Audio decode library (`rada_decode.dll`). It is resolved
the same way as Oodle (app folder, `libs/`, or `RADA_DLL_PATH`), under any of these
names: `rada_decode`, `radaudio` (`.dll` on Windows, `lib*.so` on Linux).

The library is bundled in this repository (`libs/rada_decode.dll`). It was built by
wrapping the RAD Audio decoder that ships with Unreal Engine 5.6
(`Engine/Source/Runtime/RadAudioCodec/SDK`) in a thin shim. To rebuild it, run
[`RADADecoder/shim/build.bat`](RADADecoder/shim/build.bat) (requires VS 2022 and a
UE 5.6 install; the shim also strips Fortnite's inline "SEEK" chunks before decoding).

If the library is **not** present, the API still works: the `audio=true` endpoint
returns the **raw RADA stream** (HTTP 200) instead of failing, and the response
header `X-Audio-Decoded: false` indicates it was not converted. Other formats
(PCM/ADPCM → WAV, BINKA, OPUS, OGG, WEM, AT9) are served regardless.

## Running

### Local (development)

```bash
cd FortnitePorting
dotnet run
```

The API listens on `http://0.0.0.0:3849` by default (override with the `PORT`
environment variable). Open **http://localhost:3849/swagger** to explore and try
every endpoint; the root path `/` redirects there.

### Build a normal Windows output

The root `build.bat` creates a normal `net10.0` build output and copies the
native runtime DLLs next to the framework-dependent executable:

```bash
build.bat
```

Output:

```
FortnitePorting/bin/Release/net10.0/FortnitePorting.exe
```

> The Oodle / zlib-ng / RAD Audio native libraries are acquired at runtime and are
> **not** baked into the executable. Place `oo2core_9_win64.dll`, `zlib-ng2.dll`
> (and optionally the RAD Audio library) next to `FortnitePorting.exe`.

### Docker

```bash
docker build -t fnexportapi .
docker run -p 3849:3849 \
    -v fnexport-libs:/app/libs \
    -v fnexport-manifest:/app/manifest \
    -v fnexport-cache:/app/chunk_cache \
    -v fnexport-mappings:/app/mappings \
    -e PROJECT_ROOT=/app \
    fnexportapi
```

## Environment variables

| Variable | Default | Description |
|---|---|---|
| `PORT` | `3849` | Listening port. |
| `PROJECT_ROOT` | (auto) | Root used to resolve `libs/`, `manifest/`, `chunk_cache/`, `mappings/`. Set to `/app` in Docker. |
| `OODLE_DLL_PATH` | – | Explicit path to the Oodle native library. |
| `RADA_DLL_PATH` | – | Explicit path to the RAD Audio decode library (enables RADA → WAV). |
| `USMAP_PATH` | – | Explicit path to the `.usmap` to load. When set, that file is used (**if it does not exist, the latest mapping is auto-downloaded**). |
| `SKIP_MAPPING` | `false` | Fully skip loading the `.usmap` mappings (lower memory; some assets won't deserialize). |
| `LOAD_ALL_VFS` | `false` | Mount every VFS file instead of a curated subset. |
| `SEARCH_THREADS` | (CPU count) | Content-search scan parallelism. Defaults to the logical CPU count (use every core). |
| `CONTENT_CACHE_MB` | `unlimited` | Cache decompressed bytes read during content search. Unlimited by default until the mounted PAK state changes; set `0` to disable or a positive value to impose an MB limit. |
| `SEARCH_CONTENT_CACHE_MINUTES` | `1440` (24h) | Sliding lifetime, in minutes, of a cached content-search response (`/api/v1/search/content`). `0` disables the cache. |
| `SEARCH_PATH_CACHE_MINUTES` | `1440` (24h) | Sliding lifetime, in minutes, of a cached path-search response (`/api/v1/search`). `0` disables the cache. |
| `SEARCH_CACHE_MAX_MINUTES` | `10080` (7d) | Absolute ceiling on a cached search response, so a repeatedly hit query cannot pin its memory indefinitely. |
| `HOTFIX_CLOUDSTORAGE_URL` | `https://api.fljpapi.jp/api/v2/cloudstorage` | Cloudstorage listing read when `hotfix=true`; each file is fetched from `{URL}/{uniqueFilename}`. |
| `HOTFIX_CACHE_MINUTES` | `10` | Minutes before the hotfix listing is checked again. |
| `HOTFIX_CACHE_DIR` | `<PROJECT_ROOT>/hotfix_cache` | Where downloaded hotfix config files are stored; reused across restarts. |
| `HOTFIX_DISK_CACHE` | `true` | Set to `false` to disable the disk cache and download every time. |
| `AESFINDER_PATH` | `D:\AesFinder-main\...\AesFinder.exe` | Path to the external AesFinder tool used by `/aes` (a `.exe`, a `.dll`, or a directory containing it). |
| `AESFINDER_AUTO` | `true` | Background auto-extraction/submission of the MainAES key via AesFinder (**only acts while the main key is missing**; set `false` to disable). |
| `AUTO_UPDATE` | (unset) | `true` = always update without asking, `false` = never contact GitHub, **unset = ask (y/n) at startup, but only when an update exists**. |
| `UPDATE_CHECK_ONLY` | `false` | Report a newer release but never install it. |
| `UPDATE_RESTART` | `true` | Relaunch after the swap. `false` swaps the files and leaves starting it to you. |
| `UPDATE_REPO` | `Fortniteleakjp/fnexportAPIv2` | The `owner/name` releases are read from (for forks). |
| `GITHUB_TOKEN` | – | Optional; lifts the anonymous GitHub API rate limit (60 requests/hour). |

> **Mapping (.usmap) behavior**: by default the `.usmap` mapping is loaded. If `USMAP_PATH` is set and the file exists it is used; **otherwise (unset, or the file is missing) the latest mapping is auto-downloaded** (falling back to an existing local file). Only if none can be obtained is it skipped instead of failing startup (some assets cannot deserialize without mappings). Set `SKIP_MAPPING=true` to disable it explicitly.

> **Auto-update (no restart)**:<br>・**New decryption keys**: every ~30s the monitor reads the local `/api/v1/archives/keys` endpoint and submits any still-required keys **by GUID**, auto-mounting the matching paks (no dependency on pak names). That endpoint aggregates the current archives and external keychain data.<br>・**New builds**: build info is polled every ~30s; when the build or the manifest id changes the manifest is re-fetched and **every VFS archive of the previous build is dropped and re-registered/mounted from the new manifest** (exactly what a restart used to do). An update rewrites the existing `pakchunk*.utoc/.ucas` under the same names, so mounting only the archives that are *new* would keep serving the previous build's content. The other endpoints answer `503` (`Retry-After: 30`) while the rebuild runs, and every cache derived from the old build (responses, search, localization) is cleared afterwards. Newly-encrypted paks mount once their key arrives (via the AES monitor above).<br>・**Mappings (.usmap)**: when a new build is detected the **latest .usmap for that build is re-downloaded and hot-swapped** (a pinned `USMAP_PATH` file is kept as-is).<br>All of this happens without restarting the process (until the external APIs publish the new build's keys/mapping, only that build's new content is unavailable — it appears automatically once they do).

## API endpoints

Base URL: `http://localhost:3849`

> **CORS**: enabled for any origin (any origin/method/header). The audio diagnostic
> headers (`X-Audio-Format` / `X-Audio-Decoded` / `X-Rada-Native-Decoder`) and
> `Content-Disposition`, the backup headers (`X-Backup-Entries` / `X-Backup-Version`), and the hotfix headers
> (`X-Hotfix-Status` / `X-Hotfix-Applied`) are exposed so browser clients can read them.

### Asset export — `/api/v1/export`

| Method & path | Description |
|---|---|
| `GET /api/v1/export?path={path}&image={bool}&audio={bool}&lang={code}&hotfix={bool}` | Export an asset. JSON is returned by default, with all package exports in the `jsonOutput` array. Normal Unreal property names preserve their original casing; only localized-text keys follow FortniteAPI's `namespace`, `key`, `sourceString`, and `localizedString` casing. `hash` is the SHA-256 of that array's UTF-8 JSON, `entries` is its count, and `bytes` is its byte length. `image=true` returns PNG for textures; `audio=true` returns audio for sounds; `lang` applies localization (e.g. `ja`); `hotfix=true` returns the [hotfixed content](#hotfixed-content--hotfixtrue). **If `image=true` but the asset is not a texture, JSON is returned automatically.** |
| `GET /api/v1/export/audioinfo?path={path}` | Report a sound asset's format and whether it can be decoded to WAV, without downloading the binary. |
| `GET /api/v1/export/locres?lang={code}` | Merged localization table for a language. |
| `GET /api/v1/export/locres/languages` | List available localization languages. |
| `GET /api/v1/export/filepath/{pakName}` | List file paths inside a given pak / chunk number. |

#### Hotfixed content — `hotfix=true`

Fortnite does not run the values baked into the paks as-is: the cloudstorage config files rewrite
DataTable and CurveTable contents, and displayed text, first. With `hotfix=true` the export applies
those edits, so the JSON describes the asset as the game currently runs it. The default is `false`,
which returns the pak contents unchanged.

Two sections are read:

- `[AssetHotfix]` — DataTable / CurveTable / CurveFloat content edits, addressed per asset.
- `[/Script/FortniteGame.FortTextHotfixConfig]` — `+TextReplacements=` FText overrides, addressed by namespace and key.

Every file listed by `https://api.fljpapi.jp/api/v2/cloudstorage` is scanned, not just
`DefaultGame.ini` — `[AssetHotfix]` sections also appear in `DefaultBlastberryGame.ini`,
`IOS_Game.ini` and others, and `+TextReplacements=` lines also appear in per-platform files such as
`PS5_Game.ini`. The set is cached for 10 minutes by default, and a change to it invalidates the
cached export responses automatically.

##### Caching

Downloaded files are kept in `hotfix_cache/` and are **not fetched again after a restart**. A
cloudstorage `uniqueFilename` changes whenever Epic republishes the file, so the cache is
content-addressed and can never go stale — only changed files arrive, under a new name.

- The listing (`listing.json`) is stored too, so hotfixes still work **on a cold start while cloudstorage is unreachable**.
- Each cached file is checked against the listing's size and SHA-256 on read; a damaged one is re-downloaded.
- Files the listing no longer references are deleted.

| | Time | Downloads |
|---|---|---|
| First build (cold cache) | ~5.1 s | 62 files |
| Later builds (warm cache) | ~0.08 s | none |

| Environment variable | Default | Description |
|---|---|---|
| `HOTFIX_CLOUDSTORAGE_URL` | `https://api.fljpapi.jp/api/v2/cloudstorage` | Listing URL; each file is fetched from `{URL}/{uniqueFilename}`. |
| `HOTFIX_CACHE_MINUTES` | `10` | Minutes before the listing is checked again (in-memory index lifetime). |
| `HOTFIX_CACHE_DIR` | `<PROJECT_ROOT>/hotfix_cache` | Where the downloaded files are stored. |
| `HOTFIX_DISK_CACHE` | `true` | Set to `false` to skip the disk cache and download every time. |

Supported directives:

| Line | Effect |
|---|---|
| `+CurveTable=Path;RowUpdate;Row;KeyTime;Value` | Sets one curve key of one row, inserting the key when it does not exist. |
| `+CurveTable=Path;TableUpdate;"[{...}]"` | Replaces every row of the curve table. |
| `+DataTable=Path;RowUpdate;Row;Property;Value` | Sets one property of one row. Struct literals such as `(X=1,Y=3)` are merged member by member. |
| `+DataTable=Path;AddRow;"{...}"` | Adds the row supplied as JSON. |
| `+DataTable=Path;TableUpdate;"[{...}]"` | Replaces every row of the data table. |
| `+CurveFloat=Path;CurveUpdate;"{...}"` | Replaces the curve of a `UCurveFloat`. |
| `+TextReplacements=(Category=…, Namespace="", Key="…", NativeString="…", LocalizedStrings=(("ja","…"),…))` | Sets `SourceString` to `NativeString` and `LocalizedString` to the translation for the requested `lang`, on every FText with that namespace and key. |

A `RowUpdate` targeting a row the pak does not contain is ignored, as it is in game, and reported
as `rowNotFound`.

Text replacements are not bound to one asset: every FText in the exported JSON is matched by
namespace and key. They are applied *after* `.locres` localization, so a hotfixed string wins over
the locres value. When `lang` has no exact translation the fallback order is another region of the
same language (`pt` → `pt-BR`), then `en`, then `NativeString`. When several files publish the same
key (per-platform wording, for example), the last one in file-name order is used.

Example (a hotfix that rewrites a curve from `0.0` to `1.0`):
```
http://localhost:3849/api/v1/export?path=/SpriteBoons_Ch7S4/DataTables/SpriteBoons_Ch7S4GameData&lang=ja&hotfix=true
```

The response shape is identical with and without `hotfix`: the usual `hash`, `entries`, `bytes`, and
`jsonOutput`, with only the values inside `jsonOutput` reflecting the hotfixes (`hash` is computed
from the hotfixed JSON).

Headers report what happened:

- `X-Hotfix-Status` — `applied` (at least one line changed something), `none` (nothing changed: no hotfix targets this asset, or the targeted rows are not in the pak), or `unavailable` (cloudstorage unreachable).
- `X-Hotfix-Applied` — how many lines actually changed something.

If cloudstorage cannot be reached the export still succeeds: the un-hotfixed asset is returned with
`X-Hotfix-Status: unavailable`, and that response is not cached. `POST /api/v1/export/batch` accepts
the same switch as a `hotfix` field in its request body.

#### Audio output

`audio=true` decodes/serves a `USoundWave` or Wwise (`UAkMediaAssetData`) asset:

| Source format | Output | Content-Type |
|---|---|---|
| PCM / ADPCM | WAV (RIFF/WAVE, served as-is) | `audio/wav` |
| RADA | WAV when the RAD Audio library is present; otherwise the raw `.rada` stream | `audio/wav` / `audio/x-rada` |
| BINKA / OPUS / OGG / WEM / AT9 | raw encoded stream | `audio/x-binka`, `audio/opus`, `audio/ogg`, `audio/x-wwise`, `audio/x-at9` |

Response headers describe what happened:

- `X-Audio-Format` — the source audio format (e.g. `RADA`).
- `X-Audio-Decoded` — `true` if converted to WAV, `false` if the raw stream was returned.
- `X-Rada-Native-Decoder` — `available` / `unavailable`.

Example:
```
http://localhost:3849/api/v1/export?path=FortniteGame/Content/.../MySound.uasset&audio=true
```

### Item lookup — `/api/v1/items`

Find and inspect assets whose file name starts with one of
`WID_`, `AGID_`, `Athena_`, `Figment_Athena_` (override with `prefixes`).

| Method & path | Description |
|---|---|
| `GET /api/v1/items/files?prefixes={csv}&page={n}&pageSize={n}&ext={ext}` | Paths of files matching the prefixes (defaults to `.uasset`). |
| `GET /api/v1/items/properties?prefixes={csv}&page={n}&pageSize={n}` | For each matching asset, extract `Properties.ItemName.SourceString`, `DataList → Traits`, and `LargeIcon.AssetPathName` (paginated). |
| `GET /api/v1/items/properties/single?path={path}` | Same extraction for a single asset path. |

Example response (`/api/v1/items/properties/single`):
```json
{
  "path": "FortniteGame/Content/Athena/Items/Consumables/AppleSun/WID_Athena_AppleSun.uasset",
  "name": "WID_Athena_AppleSun",
  "exportType": "FortWeaponRangedItemDefinition",
  "itemName": "Crash Pad",
  "traits": ["Item.Trait.AllowEmptyFinalStack", "Item.Trait.Transient"],
  "largeIcon": "/Game/UI/Foundation/Textures/Icons/Athena/T-T-Icon-BR-AppleSunGadget-L.T-T-Icon-BR-AppleSunGadget-L"
}
```

### String search — `/api/v1/search`

Type a word, string, or codename and search across **every loaded file**. Provides fast
path/name search plus a bounded full-text search inside asset contents (properties).

| Method & path | Description |
|---|---|
| `GET /api/v1/search?q={text}&mode={mode}&field={field}&ext={csv}&dir={dir}&dedupe={bool}&caseSensitive={bool}&page={n}&pageSize={n}` | Search the paths/names of all files. Returns matching files (`path`/`name`/`ext`) with a total count (paginated, max 10000/page). |
| `GET /api/v1/search/content?q={text}&dir={dir}&pathContains={text}&ext={csv}&maxScan={n}&maxResults={n}&snippetsPerFile={n}&caseSensitive={bool}` | Search the string inside file **contents**. Assets (`.uasset`/`.umap`) are parsed and their exports serialized to JSON; config/text/binary files (`.ini`/`.bin`/`.json`, etc.) are decoded from raw bytes. Returns matching files and snippet lines. The default set is assets + text/config; `ext=*` searches every file, `ext=.ini` restricts. **Scans every file (~1.65M, ~11 GB) by default in about 40 s** (allocation-free byte scan, parallel across cores). Scan order: **(1) path contains the query, (2) neighbour assets (same plugin/folder), (3) text/config, (4) other assets**. Pass a smaller `maxScan` for a faster partial scan. |

**`mode`**: `contains` (default) / `prefix` / `suffix` / `exact` / `wildcard` (`*` `?` glob) / `regex` / `tokens` (AND of whitespace-separated words)
**`field`**: `path` (full path, default) / `name` (file name) / `stem` (name without extension)

Examples (search by codename):
```
http://localhost:3849/api/v1/search?q=HonestWasp
http://localhost:3849/api/v1/search?q=WID_&mode=prefix&field=name&dedupe=true
http://localhost:3849/api/v1/search?q=*Athena*Soldier*&mode=wildcard&field=name&ext=.uasset
```
Example response (`/api/v1/search`):
```json
{
  "query": "HonestWasp",
  "mode": "contains",
  "field": "path",
  "totalMatches": 7,
  "totalPages": 1,
  "currentPage": 1,
  "pageSize": 100,
  "results": [
    { "path": "FortniteGame/.../Character_HonestWasp.uasset", "name": "Character_HonestWasp.uasset", "ext": ".uasset" }
  ]
}
```

> **Note**: The path search scans all files (~2.4M). `regex` is bounded by a per-evaluation timeout (250 ms), an overall time budget, and a pattern-length limit. The content search (`/content`) covers **assets plus config/text files** (`.ini`/`.bin`/`.json`, etc.) and scans in the order: path-contains-query → **neighbour assets (same plugin/folder)** → text/config → other assets, up to `maxScan`. Detection is an allocation-free byte scan run across all cores, so it **scans every file (~1.65M, ~11 GB) by default in about 40 s** — so a plain `?q=RankedTier` finds scattered, path-less matches (12 widgets across many plugins) with no tuning. For a quick check pass a small `maxScan` (e.g. `maxScan=2000`) to scan partially from the top, or narrow with `dir` / `pathContains` / `ext` when you know the target.
>
> **Speed**: scanning runs **in parallel across every CPU core** (tunable via `SEARCH_THREADS`), and an **identical query is cached for 24 hours by default**, so repeats return instantly (a sliding lifetime from the last hit, capped by the 7-day `SEARCH_CACHE_MAX_MINUTES`). The cache key includes the mounted file count, and a provider rebuild for a new build clears the response cache outright, so **a stale build's result can never be served**. Tune the lifetime with `SEARCH_CONTENT_CACHE_MINUTES` / `SEARCH_PATH_CACHE_MINUTES`, or set `0` to disable. A path search that the wall-clock budget cut short is cached for **5 minutes only**, so a retry can still produce the complete answer. Decompressed bytes are also cached without a limit by default, reducing re-reads and re-decompression for different queries.

### AES key extraction — `/aes`

| Method & path | Description |
|---|---|
| `GET /aes` | Downloads `UnrealEditorFortnite-Common-Win64-Shipping.dll` from the live **Fortnite_Studio (UEFN)** manifest and runs the external **AesFinder** tool on it to **extract the MainAES key** (no game launch, no injection), then **submits the key to the provider and mounts** matching paks. Returns `{ mainKey, version, build, fullVersion, submitted, mountedNewFiles, totalFiles, ... }`. |
| `GET /aes?submit=false` | Return the key only; do not submit/mount (default is `submit=true`). |
| `GET /aes?noApi=true` | Don't consult fortnite-api; take the **highest-entropy candidate** straight from the binary. |
| `GET /aes?force=true` | Ignore the cache and re-download the Common DLL. |

> The MainAES key lives in the Common DLL in plaintext as `mov [rbp+d], imm32` instruction immediates (the AESDumpster pattern) — it is neither a contiguous 32-byte blob nor a key schedule, so a naive byte search or schedule scan won't find it. This endpoint extracts it with the external AesFinder tool (set via `AESFINDER_PATH`). The Common DLL is downloaded once and cached, and **a new build is fetched automatically when detected**.
>
> **Automatic submission (fallback):** the background `AesFinderKeyService` extracts and submits the main key **only while it is missing** (e.g. a fresh build whose key the external AES API hasn't published yet), mounting the paks automatically. In normal operation, when the key is already applied, it **stays idle and downloads nothing** (disable with `AESFINDER_AUTO=false`). This lets the API follow a new build without waiting for the external AES API. **Dynamic (per-GUID) keys** are out of scope for AesFinder and remain handled by the external AES monitor (`api.fortniteapi.com` / `uedb.dev`).
>
> The built-in schedule scanners (`GET /api/v1/aes/extract`, `/api/v1/aes/scan/local`, `/api/v1/aes/finder/selftest`) are also available as helpers.

### Build status — `/api/v1/build`

| Endpoint | Description |
|---|---|
| `GET /api/v1/build` | Returns the build currently served (`appliedBuild` / `appliedManifestId`), the build the manifest points at, the mounted VFS count, how many keys are still missing, and whether a rebuild is running (`reloading`). It keeps answering during a rebuild. |
| `POST /api/v1/build/reload` | Rebuilds the provider from the newest manifest immediately instead of waiting for the ~30s poll. Other endpoints return `503` while it runs. |

### FModel backup — `/api/v1/backup`

Returns the mounted build's file list as an **FModel backup (`.fbkp`)**. Loading it in FModel
("Load → All But New" / "All But Modified") lists only what a later build added or changed
relative to this one.

| Method & path | Description |
|---|---|
| `GET /api/v1/backup/fbkp?includePayloads={bool}&compress={bool}` | Downloads the `.fbkp`. The file is named after the **mounted build** (for example `FortniteGame_42_00.fbkp`). |
| `GET /api/v1/backup?includePayloads={bool}` | Reports the entry count, version, suggested file name, and current build without generating the file. |

```
curl -OJ http://localhost:3849/api/v1/backup/fbkp
```

> **Format**: an LZ4 frame wrapping the magic `FBKP` (`0x504B4246`), backup version `2` (`PerfectPath`),
> the entry count (int32), then per file the size (int64), the encrypted flag (bool), and the path
> (7-bit length-prefixed string) — byte for byte what
> [FModel's `BackupManagerViewModel.CreateBackup`](https://github.com/4sval/FModel/blob/63a7cbccd9fbaae9db45240069a49bd6a3a00b73/FModel/ViewModels/BackupManagerViewModel.cs#L23) writes.
>
> **Contents**: like FModel, `.uexp` / `.ubulk` / `.uptnl` payloads are excluded (`includePayloads=true` keeps them).
> `compress=false` writes the plain body; FModel sniffs the LZ4 magic first, so it loads either form.
> The entry count and version are also returned in the `X-Backup-Entries` / `X-Backup-Version` headers.
>
> **File name**: derived from the mounted build (`++Fortnite+Release-42.00-CL-...`) as
> `FortniteGame_42_00.fbkp`. It falls back to FModel's date form (`FortniteGame_MM_dd_yyyy.fbkp`)
> only while the build version is still unknown.

### Auto-update — `/api/v1/update`

**At startup the API queries the GitHub releases API**
(`https://api.github.com/repos/{owner}/{repo}/releases/latest`) **and, when a newer release exists,
downloads it, swaps it in, and restarts.** The check runs before the Fortnite build is mounted, so an
update never pays for an initialization it is about to discard.

**You are asked to confirm, but only when there is something to install** (with `AUTO_UPDATE` unset):

```
Auto-update: current 1.1.0, v1.1.14 is available
Update to v1.1.14 now? [Y/n] (Y after 30s):
```

- `y`, Enter, or 30 seconds of silence installs it and restarts.
- `n` prints how to set `AUTO_UPDATE`, then **continues the normal startup after 5 seconds** without updating.
- Nothing is asked when the build is already current.
- Setting `AUTO_UPDATE=true`/`false` stops the prompt for good. It is also skipped when stdin is not a
  terminal (a service, a container, a pipe), where the previous non-interactive behaviour applies.

| Method & path | Description |
|---|---|
| `GET /api/v1/update` | Reports the running version, the newest GitHub release, and whether an update applies (with the reason when it does not). |
| `POST /api/v1/update?force={bool}` | Installs the newest release now instead of at the next startup. The process shuts down so the swap can complete. |

```
curl http://localhost:3849/api/v1/update
```

> **How the swap works**: a running executable cannot overwrite itself, so the release asset
> (`FortnitePorting-win-x64.zip` / `FortnitePorting-linux-x64.tar.gz`) is extracted into
> `.update/staging`, and a script (`apply.cmd` / `apply.sh`) that waits for this process to exit is
> started before shutdown. The swap **copies** rather than mirrors: the Oodle and zlib-ng natives,
> `libs/`, `mappings/`, `chunk_cache/`, and local configuration are not in the archive and survive.
>
> **When it does not update** (the reason is reported by `GET /api/v1/update`):
> <br>- **Local builds**: a build the release workflow did not stamp (`0.0.0-dev`) has no version to
> compare, and overwriting a development working copy with a release archive would be destructive.
> <br>- **Containers**: the image runs `dotnet FortnitePorting.dll` while the release assets are
> self-contained builds, and anything written to the container layer is lost on the next run. Pull a new image.
> <br>- **A version that failed to apply**: if the process comes back up still on the old version, it is
> not retried automatically (that would loop). `POST /api/v1/update?force=true` clears the guard.

### Debug — `/api/v1/debug`

| Method & path | Description |
|---|---|
| `GET /api/v1/debug/stats?page={n}` | All loaded file paths (paginated, 1000 per page). |
| `GET /api/v1/debug/search?query={text}` | Search loaded file paths by substring. |
| `GET /api/v1/debug/paks` | List mounted pak / utoc files. |
| `GET /api/v1/debug/paks/{pakName}/files` | List files inside a mounted pak. |

### Archive information and AES — `/api/v1/archives`

| Endpoint | Description |
|---|---|
| `GET /api/v1/archives` | Returns metadata for registered `.pak` / `.utoc` archives, including name, size, file count, mount point, encryption state, GUID, and compression methods. |
| `GET /api/v1/archives/keys` | Returns an AES response with `version`, `mainKey`, `dynamicKeys`, and `unloaded`, including GUIDs, AES keys, keychain strings, file counts, and sizes. GUIDs are matched against the live mapping from `https://fljpapi.jp/api/v2/keychain?rou=false`; the provider's loaded key is used for Main AES and other missing entries. |

### Cosmetics extraction — `/api/v1/pak`

| Method & path | Description |
|---|---|
| `GET /api/v1/pak/{pakName}/cosmetics?page={n}&pageSize={n}&lang={code}` | For the given PAK/chunk (number accepted), extracts each cosmetic under `FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Athena/Items/Cosmetics` and each bundle/display asset under `FortniteGame/Plugins/GameFeatures/OfferCatalog/Content/DisplayAssets` (paginated, max 200/page). Cosmetic entries include ItemName/Description keys, icons, tags, and matched OfferCatalog texture paths; display asset entries include serialized exports such as `FortMtxOfferData` bundle data. |

Example (chunk number 30, Japanese):
```
http://localhost:3849/api/v1/pak/30/cosmetics?pageSize=50&lang=ja
```
Example response (one item, `lang=ja`):
```json
{
  "name": "Backpack_AbstractMirror",
  "exportType": "AthenaBackpackItemDefinition",
  "itemNameKey": "62B77828400008FD63C782B57223217D",
  "itemName": "メタルギアMk.II",
  "itemDescriptionKey": "1D0C41FF4E978741F86512A2027568AC",
  "itemDescription": "...",
  "itemShortDescriptionKey": "EC0A76294172A6021A503DB756D8D8A3",
  "itemShortDescription": "バックアクセサリー",
  "largeIcon": "/BRCosmetics/UI/Foundation/Textures/Icons/Backpacks/S28/T-Icon-Backpacks-AbstractMirror-L.T-Icon-Backpacks-AbstractMirror-L",
  "icon": "/BRCosmetics/UI/Foundation/Textures/Icons/Backpacks/S28/T-Icon-Backpacks-AbstractMirror.T-Icon-Backpacks-AbstractMirror",
  "tags": ["Cosmetics.Filter.Season.28", "Cosmetics.Set.HidingTime", "Cosmetics.Source.Season29.BattlePass.Paid"]
}
```
Omit `lang` (or use `en`) and `itemName` etc. contain the English source text (SourceString).

When the PAK also contains `FortniteGame/Plugins/GameFeatures/OfferCatalog/Content/Textures`, each cosmetic gets an `offerCatalog` field with the texture path matching its **skin ID** (the asset name after the first `_`, e.g. `Character_HonestWasp` → `HonestWasp`). Textures are matched as `T_Athena{Category}_{ID}` (`Character` → `Soldiers`; other prefixes use the prefix itself), e.g. `Character_HonestWasp` → `T_AthenaSoldiers_HonestWasp`, `Backpack_HonestWasp` → `T_AthenaBackpack_HonestWasp`. `null` when there is no match (or it is ambiguous).

## RAD Audio decoder (`RADADecoder` / `RADADecoder-cs`)

`RADADecoder-cs` is a managed wrapper around the native RAD Audio decode library,
consumed by the API via `RadaDecoder.TryDecodeToWav(byte[], out byte[])`:

- The native library is located automatically (app folder, `libs/`, `PROJECT_ROOT`,
  or `RADA_DLL_PATH`) via a `DllImport` resolver.
- `RadaDecoder.IsNativeAvailable` reports whether decoding is possible.
- The decoder never throws for missing-library / corrupt-input cases — it returns
  `false`, and the API degrades to serving the raw stream.

`RADADecoder` (C++) is the standalone reference CLI; it requires the RAD Audio SDK
to build.

## Swagger / OpenAPI

The UI exposes two documents, selectable from the dropdown in the top-right:
**日本語** (default) and **English**.

- Swagger UI: `http://localhost:3849/swagger`
- OpenAPI JSON (Japanese): `http://localhost:3849/swagger/ja/swagger.json`
- OpenAPI JSON (English): `http://localhost:3849/swagger/en/swagger.json`
