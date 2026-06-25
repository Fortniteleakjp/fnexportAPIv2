# RAD Audio SDK (vendored subset)

These files are the minimal subset of the **RAD Audio** decode SDK that ships with
**Unreal Engine 5.6** at:

```
Engine/Source/Runtime/RadAudioCodec/SDK/
```

Only the pieces needed to build the `rada_decode` shim are vendored here:

- `Include/rada_decode.h`, `Include/rada_file_header.h`, `Include/rada_encode.h`
- `Lib/radaudio_decoder_win64.lib`   (Windows x64 prebuilt static lib)
- `Lib/libradaudio_decoder_linux64.a` (Linux x64 prebuilt static lib)

They are committed so that CI can build the native decoder without a local Unreal
Engine installation.

## License / redistribution

> **© Epic Games Tools, LLC.** These are proprietary Epic / RAD Game Tools files,
> redistributed under the terms of the Unreal Engine EULA. If this repository is or
> becomes **public**, review the UE EULA before distributing these files — you may
> need to keep the repository private or remove this folder and supply the SDK to CI
> by another means (e.g. an encrypted artifact or a self-hosted runner with UE
> installed). To regenerate this folder from a local UE install, run
> `RADADecoder/shim/vendor-sdk.bat`.
