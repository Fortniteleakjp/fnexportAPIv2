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

### Build a single self-contained `.exe` (Windows)

The project is configured to publish **everything — runtime, managed
dependencies, and native libraries — into one executable**:

```bash
dotnet publish FortnitePorting/FortnitePorting.csproj -c Release -r win-x64
```

Output:

```
FortnitePorting/bin/Release/net9.0/win-x64/publish/FortnitePorting.exe
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

> **Mapping (.usmap) behavior**: by default the `.usmap` mapping is loaded. If `USMAP_PATH` is set and the file exists it is used; **otherwise (unset, or the file is missing) the latest mapping is auto-downloaded** (falling back to an existing local file). Only if none can be obtained is it skipped instead of failing startup (some assets cannot deserialize without mappings). Set `SKIP_MAPPING=true` to disable it explicitly.

> **Auto-update (no restart)**:<br>・**New decryption keys**: every ~30s AES keys are fetched (`api.fortniteapi.com` → `uedb.dev` on failure) and any still-required keys are submitted **by GUID**, auto-mounting the matching paks (no dependency on pak names).<br>・**New builds**: build info is polled every ~3 min; when it changes the manifest is re-fetched and any newly-added VFS archives (utoc/pak) are **registered and mounted automatically**. Newly-encrypted paks mount once their key arrives (via the AES monitor above). The process is not auto-restarted.

## API endpoints

Base URL: `http://localhost:3849`

> **CORS**: enabled for any origin (any origin/method/header). The audio diagnostic
> headers (`X-Audio-Format` / `X-Audio-Decoded` / `X-Rada-Native-Decoder`) and
> `Content-Disposition` are exposed so browser clients can read them.

### Asset export — `/api/v1/export`

| Method & path | Description |
|---|---|
| `GET /api/v1/export?path={path}&image={bool}&audio={bool}&lang={code}` | Export an asset. Default returns JSON (**all exports of the package, as an array**); `image=true` returns PNG for textures; `audio=true` returns audio for sounds; `lang` applies localization (e.g. `ja`). **If `image=true` but the asset is not a texture, JSON is returned automatically.** |
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
| `GET /api/v1/pak/{pakName}/cosmetics?page={n}&pageSize={n}&lang={code}` | For the given PAK/chunk (number accepted), extracts from each cosmetic under `FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Athena/Items/Cosmetics`: the `ItemName` / `ItemDescription` / `ItemShortDescription` Keys (plus localized text when `lang` is given), the `LargeIcon` and `Icon` `AssetPathName`, and the `Tags` (paginated, max 200/page). |

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
