# AES Finder:
An AES Finder Writen in C#

## JSON output

```powershell
& .\AesFinder.exe "C:\path\UnrealEditorFortnite-Common-Win64-Shipping.dll" --json
```

`--json` writes one JSON object to standard output:

```json
{
  "version": "41.10",
  "build": "55335788",
  "fullVersion": "++Fortnite+Release-41.10-CL-55335788",
  "mainKey": "0x<64 hexadecimal characters>",
  "candidates": ["0x<64 hexadecimal characters>"]
}
```

`mainKey` is the first entropy-ranked candidate, **not a verified decryption key**.
`candidates` contains all distinct candidates. The API verifies candidates against
encrypted archives. Version fields are empty strings when the target and matching
sibling binaries have no version metadata.

On failure, JSON still contains these fields, with `mainKey: null`, an empty
`candidates` array and an `error` message. Exit codes: `0` for candidates found,
`1` for invalid arguments or file access errors, `2` for no candidates found.
Without `--json`, the tool prints one candidate per line. `--file` is accepted
for explicit file mode. `--no-api` is accepted for API compatibility; this tool
always scans locally and does not query an external AES API.

## Cerdits to [GHFear](https://github.com/GHFear/AESDumpster) For the Patterns
