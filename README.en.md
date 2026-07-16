# fnexportAPI

[日本語](README.md) | **English**

A Fortnite asset-export Web API built on **CUE4Parse**. It downloads the live
Fortnite manifest, streams the required paks/chunks from the Epic CDN, and
exposes the parsed assets (JSON / PNG / audio) over HTTP. All endpoints are
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
copy oo2core_9_win64.dll FortnitePorting/bin/Debug/net9.0/

# Linux
cp liboo2corelinux64.so.9 FortnitePorting/bin/Debug/net9.0/
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

The root `build.bat` creates a normal `net9.0` build output and copies the
native runtime DLLs next to the framework-dependent executable:

```bash
build.bat
```

Output:

```
FortnitePorting/bin/Release/net9.0/FortnitePorting.exe
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
| `CONTENT_CACHE_MB` | `0` (off) | Decompressed-bytes cache budget (MB) for content search. Enable (e.g. `8192`) to speed up re-scans on slow/network storage; off by default since it gives little benefit on warm local storage. |
| `AESFINDER_PATH` | `D:\AesFinder-main\...\AesFinder.exe` | Path to the external AesFinder tool used by `/aes` (a `.exe`, a `.dll`, or a directory containing it). |
| `AESFINDER_AUTO` | `true` | Background auto-extraction/submission of the MainAES key via AesFinder (**only acts while the main key is missing**; set `false` to disable). |

> **Mapping (.usmap) behavior**: by default the `.usmap` mapping is loaded. If `USMAP_PATH` is set and the file exists it is used; **otherwise (unset, or the file is missing) the latest mapping is auto-downloaded** (falling back to an existing local file). Only if none can be obtained is it skipped instead of failing startup (some assets cannot deserialize without mappings). Set `SKIP_MAPPING=true` to disable it explicitly.

> **Auto-update (no restart)**:<br>・**New decryption keys**: every ~30s AES keys are fetched (`api.fortniteapi.com` → `uedb.dev` on failure) and any still-required keys are submitted **by GUID**, auto-mounting the matching paks (no dependency on pak names).<br>・**New builds**: build info is polled every ~30s; when it changes the manifest is re-fetched and any newly-added VFS archives (utoc/pak) are **registered and mounted automatically**. Newly-encrypted paks mount once their key arrives (via the AES monitor above).<br>・**Mappings (.usmap)**: when a new build is detected the **latest .usmap for that build is re-downloaded and hot-swapped** (a pinned `USMAP_PATH` file is kept as-is).<br>All of this happens without restarting the process (until the external APIs publish the new build's keys/mapping, only that build's new content is unavailable — it appears automatically once they do).

## API endpoints

Base URL: `http://localhost:3849`

> **CORS**: enabled for any origin (any origin/method/header). The audio diagnostic
> headers (`X-Audio-Format` / `X-Audio-Decoded` / `X-Rada-Native-Decoder`) and
> `Content-Disposition` are exposed so browser clients can read them.

### Asset export — `/api/v1/export`

| Method & path | Description |
|---|---|
| `GET /api/v1/export?path={path}&image={bool}&audio={bool}&lang={code}` | Export an asset. JSON is returned by default, with all package exports in the `jsonOutput` array. `hash` is the SHA-256 of that array's UTF-8 JSON, `entries` is its count, and `bytes` is its byte length. `image=true` returns PNG for textures; `audio=true` returns audio for sounds; `lang` applies localization (e.g. `ja`). **If `image=true` but the asset is not a texture, JSON is returned automatically.** |
| `GET /api/v1/export/audioinfo?path={path}` | Report a sound asset's format and whether it can be decoded to WAV, without downloading the binary. |
| `GET /api/v1/export/locres?lang={code}` | Merged localization table for a language. |
| `GET /api/v1/export/locres/languages` | List available localization languages. |
| `GET /api/v1/export/filepath/{pakName}` | List file paths inside a given pak / chunk number. |

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
> **Speed**: scanning runs **in parallel across every CPU core** (tunable via `SEARCH_THREADS`), and an **identical query is cached for 15 minutes**, so repeats return instantly (the cache is keyed by the mounted file count, so a new build / new keys invalidate it automatically). On slow storage, set `CONTENT_CACHE_MB` to also cache decompressed bytes and speed up re-scans with different queries.

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

### Debug — `/api/v1/debug`

| Method & path | Description |
|---|---|
| `GET /api/v1/debug/stats?page={n}` | All loaded file paths (paginated, 1000 per page). |
| `GET /api/v1/debug/search?query={text}` | Search loaded file paths by substring. |
| `GET /api/v1/debug/paks` | List mounted pak / utoc files. |
| `GET /api/v1/debug/paks/{pakName}/files` | List files inside a mounted pak. |

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
