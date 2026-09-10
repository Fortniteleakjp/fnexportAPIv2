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
| `BUILD_HISTORY_KEEP` | `2` | How many builds keep their manifest archived. The default `2` is "the current build plus the previous one"; anything older has its data deleted automatically on the next update (recorded changelists are kept). |
| `HISTORICAL_BUILDS_MAX` | `1` | How many archived builds may be mounted at once. A mounted build costs a few GB, so the least recently used one is dropped when this is exceeded. |
| `HISTORICAL_BUILD_IDLE_MINUTES` | `30` | How long a mounted archived build may sit unused before it is dropped. `0` disables the idle sweep. |
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
> (`X-Hotfix-Status` / `X-Hotfix-Applied`) and the icon headers (`X-Icon-Source` / `X-Icon-Name`)
> are exposed so browser clients can read them.

### Asset export — `/api/v1/export`

| Method & path | Description |
|---|---|
| `GET /api/v1/export?path={path}&image={bool}&audio={bool}&lang={code}&hotfix={bool}` | Export an asset. JSON is returned by default, with all package exports in the `jsonOutput` array. Normal Unreal property names preserve their original casing; only localized-text keys follow FortniteAPI's `namespace`, `key`, `sourceString`, and `localizedString` casing. `hash` is the SHA-256 of that array's UTF-8 JSON, `entries` is its count, and `bytes` is its byte length. `image=true` returns PNG for textures; `audio=true` returns audio for sounds; `lang` applies localization (e.g. `ja`); `hotfix=true` returns the [hotfixed content](#hotfixed-content--hotfixtrue). **If `image=true` but the asset is not a texture, JSON is returned automatically.** |
| `GET /api/v1/export/audioinfo?path={path}` | Report a sound asset's format and whether it can be decoded to WAV, without downloading the binary. |
| `GET /api/v1/export/datatable?path={path}&format={csv\|json}&rows={csv}&delimiter={d}&flatten={bool}&bom={bool}&download={bool}&hotfix={bool}` | Export a DataTable / CurveTable [as CSV](#datatable--curvetable-as-csv). |
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

#### DataTable / CurveTable as CSV

`GET /api/v1/export/datatable?path={path}` returns a `UDataTable` or `UCurveTable` (including
subclasses such as `UCompositeDataTable`) as CSV, so the table opens directly in Excel or Google
Sheets instead of having to be reshaped from JSON.

- **DataTable**: one line per row. The first column is `RowName`, followed by the union of every
  row's properties in first-seen order. Nested structs are expanded into dotted columns such as
  `Name.SourceString`; pass `flatten=false` to keep each nested value as JSON in a single cell.
  Arrays always stay as compact JSON.
- **CurveTable**: one line per **curve key** (long form). The columns are `RowName`, `Time`, and
  `Value`, then the key's own fields (`InterpMode` and so on), then the curve-level properties under
  a `Curve.` prefix. A row with no keys still produces one line, so its name is not lost.

| Parameter | Default | Description |
|---|---|---|
| `format` | `csv` | `json` returns the same table as structured `columns` + `rows`. |
| `rows` | (every row) | Comma-separated row names to keep; matched case-insensitively. |
| `delimiter` | `,` | `comma` / `tab` / `semicolon` / `pipe`, or any single character. |
| `flatten` | `true` | Expand nested structs into dotted columns. |
| `bom` | `true` | Write a UTF-8 BOM so Excel reads localized values correctly. |
| `download` | `true` | Send `Content-Disposition` so the file saves as `{assetName}.csv`. |
| `hotfix` | `false` | Apply the live cloudstorage `[AssetHotfix]` row/curve edits before exporting. |

An asset that is neither a DataTable nor a CurveTable answers `422` (use `/api/v1/export` for other
asset types). If rows are exported but the columns come out empty, the `.usmap` mapping for that row
struct is probably missing.

Examples:
```
http://localhost:3849/api/v1/export/datatable?path=FortniteGame/Content/Balance/DataTables/AthenaGameData.uasset
http://localhost:3849/api/v1/export/datatable?path=.../CurveTable.uasset&delimiter=tab&download=false
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

**`mode`**: `contains` (default) / `prefix` / `suffix` / `exact` / `wildcard` (flat `*` `?`) / `glob` (path-aware) / `regex` / `tokens` (AND of whitespace-separated words)

**`wildcard` vs `glob`**: in `wildcard` mode `*` matches anything, including `/`. `glob` is aware of
the path structure, so a pattern can address one directory level at a time.

| Syntax | Meaning |
|---|---|
| `*` | Any run of characters that does not cross `/` |
| `**` | Any run of characters, crossing `/` (`**/` also matches zero directories) |
| `?` | Any single character other than `/` |
| `[abc]` `[a-z]` `[!abc]` | Character class (`!` or `^` negates) |
| `{a,b}` | Alternation |

Both are anchored (whole-value match) and, like `regex`, are bounded by the evaluation timeout and
the pattern-length limit.
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

### Reading a specific build — `/api/v1/versions`

Every build this instance serves keeps its **manifest archived**, so files can be read from **both the
previous version and the newest one**.

No pak content is copied here. A manifest addresses chunks on the Epic CDN, so **one ~10 MB file is all
it takes** to read a whole build again. "Deleting an old version's data" therefore means deleting that
archived manifest, the AES keys archived with it, and its chunk cache.

| Method & path | Description |
|---|---|
| `GET /api/v1/versions` | Lists the known builds and whether each is still `readable` and currently `loaded`. |
| `POST /api/v1/versions/import` | Imports a manifest file (uploaded, or `path=` for one already on the server), so a build this instance never served becomes readable and comparable. |
| `POST /api/v1/versions/load?version=…` | Mounts a build from its archived manifest. |
| `DELETE /api/v1/versions/unload?version=…` | Unmounts a build; its archived manifest is kept. |
| `DELETE /api/v1/versions/data?version=…` | Deletes that build's manifest, keys and chunk cache. **Recorded changelists are kept.** |
| `GET /api/v1/versions/files?version=…` | Lists that build's virtual file paths, paginated. |
| `GET /api/v1/versions/file?version=…&path=…` | Returns **a file's content as it is in that build**. |

`version` accepts any of:

- the full build string — `++Fortnite+Release-42.10-CL-57566230-Windows`
- the version alone — `42.10`
- a changelist number — `57566230`
- `latest` (the live build) or `previous` (the one before it)

```bash
# Read the file as it was in the previous version (only builds already mounted are served)
curl "http://localhost:3849/api/v1/versions/file?version=%2B%2BFortnite%2BRelease-42.10-CL-57566230-Windows&path=FortniteGame/Config/DefaultGame.ini"
```

Only **already-loaded** builds are served by default; naming an unloaded one returns `409`. Add
`load=true` to mount it first, which takes a few minutes for a whole build.

If you already have a manifest file, you can import that build:

```bash
curl -X POST "http://localhost:3849/api/v1/versions/import"   -F "file=@++Fortnite+Release-42.00-CL-56878558-Windows.manifest"
```

### Changelists — `/api/v1/changes`

The **exact changelist** between two builds: the file paths that were added, removed or modified, and
the lines that changed inside one of them.

When an update ships, the build that is about to be replaced is **compared against the new one and the
changelist recorded** before the old build's data is deleted. Every record that ended at that build is
extended through the new one at the same time, so `v40→v41` plus `v41→v42` **automatically yields
`v40→v42`** — which stays answerable long after v41's own data is gone.

Files that did not change never appear in a record. That is what makes the composition sound: a file
listed in only one of the two steps was left alone by the other, so that step's content is what carries
through.

| Method & path | Description |
|---|---|
| `GET /api/v1/changes` | Lists the recorded changelists. |
| `GET /api/v1/changes/list?from=…&to=…` | Returns the added, removed and modified file paths, composing the pair out of the recorded chain when it was never recorded directly. `to` defaults to the live build. |
| `GET /api/v1/changes/file?from=…&to=…&path=…` | Returns **the lines that changed in one file**. `format=patch` returns unified-diff text. |
| `POST /api/v1/changes/compute?from=…&to=…` | Computes and records a changelist as a background job; poll it with the returned `jobId`. |
| `GET /api/v1/changes/jobs` / `GET /api/v1/changes/jobs/{id}` | Lists jobs, or one job's progress. |
| `DELETE /api/v1/changes/jobs/{id}` | Cancels a running computation. |
| `DELETE /api/v1/changes?from=…&to=…` | Deletes a recorded changelist. |

**How "changed lines" are derived**: text (`.ini` and friends) is diffed as it is. A `.uasset`/`.umap`
is diffed **through its JSON export**, which is what makes changed lines meaningful for an asset.
Anything that is neither is reported as byte ranges rather than invented line numbers.

```bash
# What changed between 42.00 and the live build
curl "http://localhost:3849/api/v1/changes/list?from=42.00&kind=modified&pathFilter=FortniteGame/Content/Athena"

# The changed lines of one file, as a unified diff
curl "http://localhost:3849/api/v1/changes/file?from=42.00&path=FortniteGame/Config/DefaultGame.ini&format=patch"
```

**`quick` versus `full`** (the `mode` of `POST /api/v1/changes/compute`):

| Mode | What it looks at | What it tells you |
|---|---|---|
| `quick` (default) | Virtual path, file size and containing archive. Nothing is fetched from the CDN. | Every addition and removal **exactly**, and every modification whose size changed. Files that kept their exact size are reported as a **count of unverified files** rather than claimed unchanged. |
| `full` | The above, plus reading and hashing both copies of every same-size file. | Modifications exactly as well — but each candidate is streamed from the CDN, so scope it with `pathFilter` and `maxHashFiles`. |

The changelist recorded automatically on an update is a `quick` one. Run `full` with a `pathFilter`
when you need certainty about a specific directory.

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

### Mappings — `/api/v1/mappings`

Produces and serves `.usmap` files with [`UnrealMappingsDumper`](https://github.com/TheNaeem/UnrealMappingsDumper).
There are two dump routes:

| Route | Endpoint | Needs the game running | Coverage |
|---|---|---|---|
| **Pak dump** | `POST /api/v1/mappings/dump` | no | Blueprint-side types in the paks; native `/Script` types merged in from an existing `.usmap` |
| **UEFN dump** | `POST /api/v1/mappings/dump/uefn` | yes (Windows only) | the engine's own reflection data, native types included |

The original dumper injects a DLL into the game, walks `GObjects`, and writes every `UClass`,
`UScriptStruct`, and `UEnum` it finds into a `.usmap`. There is no game process behind the pak dump, so
**the same type information is read out of the mounted paks through CUE4Parse** and written with the
dumper's own serialization (name table → enums → structs, recursive property type records, `0x30C4` header).

The UEFN dump uses the original DLL itself. Building the vendored
[`UnrealMappingsDumper/`](UnrealMappingsDumper/VENDORED.md) with `UnrealMappingsDumperuild.bat` (also
called from `build.bat`) produces `libs/UnrealMappingsDumper.dll`, which the API injects into a running
UEFN. A `.cfg` next to the DLL tells it where to write, and the DLL reports back through a terminal
`HOST_RESULT` line in its log.

| Method & path | Description |
|---|---|
| `POST /api/v1/mappings/dump?path={frag}&maxPackages={n}&timeoutSeconds={n}&merge={bool}&baseMapping={file}&version={0..4}&compression={none/zstd}&fileName={name}&load={bool}&download={bool}` | Dump a `.usmap` from the mounted build. The binary is returned by default and stored as `mappings/{build}_dumped.usmap`. `load=true` hot-loads it into the provider; `download=false` returns JSON statistics instead. |
| `GET /api/v1/mappings` | List the stored `.usmap` files (dumped, generated, or downloaded), newest first. |
| `GET /api/v1/mappings/{fileName}` | Serve one stored `.usmap`. |
| `GET /api/v1/mappings/uefn` | Report whether a UEFN dump can run right now: whether the DLL is built and where, which UEFN processes can be injected into, `ready`, and what to do next. |
| `POST /api/v1/mappings/dump/uefn?pid={n}&compression={none/oodle}&fileName={name}&console={bool}&timeoutSeconds={n}&load={bool}&download={bool}` | Dump a `.usmap` out of a running UEFN by injecting the DLL. The binary is returned by default and stored as `mappings/{build}_uefn.usmap`. The target process is detected automatically, so `pid` is rarely needed. |
| `POST /api/v1/mappings/generate?url={url}&path={path}&fileName={name}&load={bool}&verify={bool}&download={bool}` | Convert a StormForge-style mappings JSON into a `.usmap` (the pre-existing endpoint). |

```
curl -OJ -X POST "http://localhost:3849/api/v1/mappings/dump?path=FortniteGame/Content/Athena&maxPackages=2000"
curl "http://localhost:3849/api/v1/mappings/uefn"
curl -OJ -X POST "http://localhost:3849/api/v1/mappings/dump/uefn"
curl "http://localhost:3849/api/v1/mappings"
curl -OJ "http://localhost:3849/api/v1/mappings/FortniteGame_42_00_dumped.usmap"
```

> **Coverage**: cooked paks only carry Blueprint-side types (`BlueprintGeneratedClass`,
> `UserDefinedStruct`, `UserDefinedEnum`, …). Native `/Script/...` types live in the executable, not in
> the paks. So by default (`merge=true`) an existing mapping (`USMAP_PATH`, otherwise the newest file in
> `mappings/`) is **merged underneath**, with the dumped types winning. `merge=false` writes only what
> the paks yielded.
>
> **Something to merge is required**: with no base mapping to be found the request fails with `400`,
> because a pak-only mapping has no native types and reads almost nothing. Dump one from UEFN first
> (`POST /api/v1/mappings/dump/uefn`), point `USMAP_PATH` or `baseMapping` at an existing mapping, or
> pass `merge=false` to accept a Blueprint-only mapping deliberately.
>
> **Editor-only properties** are left out: cooked packages do not carry them, and counting one shifts
> every property index in the struct and in everything derived from it. The UEFN dump and the
> JSON generator apply the same rule.
>
> **Scan size**: bounded by `maxPackages` (default 5000) and `timeoutSeconds` (default 120). When either
> is hit the dump still serializes what it collected and reports `limitReached` / `timedOut`. Narrow the
> scan with `path` (e.g. `FortniteGame/Content/Athena`); `maxPackages=0` opens the whole build (~1.65M
> files) and is very slow.
>
> **Format**: `version=0` writes the exact version-0 layout UnrealMappingsDumper produces; the default
> `version=4` (latest) adds 16-bit name lengths, enums with more than 255 members, and explicit enum
> values. `compression` accepts `none` (default) and `zstd` — Oodle and Brotli compressors are not
> available in this process. Every dump is parsed back before it is served, and the counts come back in
> the `X-Usmap-*` headers (or the JSON body with `download=false`).
>
> **UEFN dump requirements**: Windows only, with UEFN (`UnrealEditorFortnite-Win64-*.exe`) running and
> fully loaded. Run the API as the same Windows user as UEFN (elevated if that is not enough). The DLL is
> looked up through `USMAP_DUMPER_DLL`, then next to the executable, then `libs/` — the same order the
> Oodle and RAD Audio libraries use. `compression=oodle` works here because the encoder lives inside the
> game. Addresses: GObjects is found by walking memory for the object array, but `FNameToString` is a
> function and only a signature scan can find it — which UE6 defeats. A candidate is accepted only if it
> actually resolves object names, and candidates are taken from, in order: the `fnameToString` query, the
> address recorded for this build in `mappings/dumper/offsets.json`, and `OFFSET_TOSTRING` from a Dumper-7
> run under `DUMPER7_DIR` (default `C:\Dumper-7`). A working address is recorded, so it is found once.
>
> The target process is picked automatically: `UnrealEditorFortnite-Win64-Shipping` is preferred, and when
> several processes share that name the one with the largest working set wins — that is the loaded editor rather
> than a helper. A candidate under 512MB is refused with `409` instead of dumping an incomplete mapping. Pass `pid`
> only to override that. `GET /api/v1/mappings/uefn` reports the choice up front as `target`.
>
> **How failures surface**: `424` when the DLL has not been built, `409` when no UEFN (or more than one)
> is running, `501` off Windows, `504` on timeout, and `502` with the tail of the log when the DLL itself
> failed. The DLL's log is kept next to the mapping as `{fileName}.usmap.log`.
>
> **Path constraint**: the DLL opens files through the ANSI C runtime, so its working directory has to be
> representable in ASCII. `mappings/dumper` is used when its path is ASCII, otherwise its 8.3 form,
> otherwise the temp directory.

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
| `GET /api/v1/cosmetics/{id}?lang={code}` | **Get one cosmetic by ID**, without naming a PAK. `id` may be the asset name (`CID_028_Athena_Commando_F`, `Character_HonestWasp`, `EID_Floss`) or just the skin ID (`HonestWasp`). Matching runs exact name, then `Prefix_ID`, then substring; the first tier that matches returns its candidates in `matches` and the chosen one in `result`. |
| `GET /api/v1/cosmetics/{id}/icon?variant={large\|small\|offercatalog}` | **Get the cosmetic's icon as PNG**. `large` (default) uses `LargeIcon`, `small` uses `Icon`, `offercatalog` uses the OfferCatalog texture. `large` and `small` fall back to the other icon and then to OfferCatalog. The texture actually used is reported in `X-Icon-Source` / `X-Icon-Name`. |
| `GET /api/v1/cosmetics/search?q={text}&category={prefix}&page={n}&pageSize={n}&lang={code}` | Search cosmetics across every mounted PAK (`category` is a prefix such as `Character` or `Backpack`). |
| `GET /api/v1/pak/{pakName}/cosmetics?page={n}&pageSize={n}&lang={code}` | For the given PAK/chunk (number accepted), extracts each cosmetic under `FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Athena/Items/Cosmetics` and each bundle/display asset under `FortniteGame/Plugins/GameFeatures/OfferCatalog/Content/DisplayAssets` (paginated, max 200/page). Cosmetic entries include ItemName/Description keys, icons, tags, and matched OfferCatalog texture paths; display asset entries include serialized exports such as `FortMtxOfferData` bundle data. |

Example (by ID, Japanese):
```
http://localhost:3849/api/v1/cosmetics/Character_HonestWasp?lang=ja
http://localhost:3849/api/v1/cosmetics/HonestWasp/icon
```

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

### Localization lookup — `/api/v1/localization`

Looks up single entries in the mounted build's `.locres` tables: **resolve a key into every
language**, or **find the `namespace`/`key` behind a string you can see in game**. Where
`/api/v1/export/locres` dumps a whole language, this searches one entry at a time.

| Method & path | Description |
|---|---|
| `GET /api/v1/localization/languages` | The language codes this build ships `.locres` files for. |
| `GET /api/v1/localization/lookup?key={key}&namespace={ns}&langs={csv}` | **Forward lookup.** Resolves the key in every language (the default) and groups the translations by the namespace it was found in. |
| `GET /api/v1/localization/lookup?text={text}&mode={mode}&lang={code}&withTranslations={bool}&maxResults={n}` | **Reverse lookup.** Returns the `namespace`, `key`, `lang`, and `value` of every entry whose translation matches. |

- Pass either `key` or `text`, never both (both, or neither, answers `400`).
- Languages come from `lang` (one) or `langs` (comma-separated; `all` / `*` for every language).
  **The default is every available language.**
- `mode` (reverse lookup): `contains` (default) / `exact` / `prefix` / `suffix` / `regex`. Add
  `caseSensitive=true` for a case-sensitive match; `regex` is bounded by a 250 ms per-entry timeout
  and a pattern-length limit.
- `withTranslations=true` resolves each reverse-lookup hit into every available language.
- Results are sorted by `namespace`, `key`, then `lang` before `maxResults` (default 50, max 500) are
  returned. `truncated: true` means the 10000-match collection cap was reached.

Examples:
```
http://localhost:3849/api/v1/localization/lookup?key=62B77828400008FD63C782B57223217D
http://localhost:3849/api/v1/localization/lookup?text=Metal%20Gear&lang=en&withTranslations=true
```
Example response (forward lookup):
```json
{
  "key": "62B77828400008FD63C782B57223217D",
  "namespace": null,
  "found": true,
  "totalMatches": 1,
  "results": [
    {
      "namespace": "",
      "key": "62B77828400008FD63C782B57223217D",
      "translations": { "en": "Metal Gear Mk. II", "ja": "メタルギアMk.II" }
    }
  ]
}
```

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
