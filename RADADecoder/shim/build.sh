#!/usr/bin/env bash
# ============================================================================
# Builds the native RAD Audio decode shim (librada_decode.so) for Linux x64.
#
# Usage:  ./build.sh [output_dir]
#   output_dir : where to place librada_decode.so (default: <repo>/libs)
#
# SDK resolution order:
#   1. vendored: RADADecoder/shim/sdk            (used by CI)
#   2. local Unreal Engine install (UE_ROOT env var, if set)
#
# The Epic-shipped Linux static lib is built against libc++, so we compile the
# shim with clang++/-stdlib=libc++ to match its ABI.
# ============================================================================
set -euo pipefail

SHIMDIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# --- Resolve the SDK ---
SDK="$SHIMDIR/sdk"
if [ ! -f "$SDK/Include/rada_decode.h" ]; then
    if [ -n "${UE_ROOT:-}" ] && [ -f "$UE_ROOT/Engine/Source/Runtime/RadAudioCodec/SDK/Include/rada_decode.h" ]; then
        SDK="$UE_ROOT/Engine/Source/Runtime/RadAudioCodec/SDK"
    else
        echo "ERROR: RAD Audio SDK not found. Run vendor-sdk or set UE_ROOT." >&2
        exit 1
    fi
fi
LIB="$SDK/Lib/libradaudio_decoder_linux64.a"
if [ ! -f "$LIB" ]; then
    echo "ERROR: libradaudio_decoder_linux64.a not found under $SDK/Lib." >&2
    exit 1
fi

# --- Output directory ---
OUT="${1:-$SHIMDIR/../../libs}"
mkdir -p "$OUT"

# --- Compiler (prefer clang++ to match the lib's libc++ ABI) ---
CXX="${CXX:-clang++}"
if ! command -v "$CXX" >/dev/null 2>&1; then
    echo "ERROR: $CXX not found. Install clang (and libc++-dev)." >&2
    exit 1
fi

echo "Building librada_decode.so  (SDK: $SDK)  ->  $OUT  (CXX: $CXX)"
"$CXX" -shared -fPIC -O2 -std=c++17 \
    -DRADA_WRAP=UERA \
    -stdlib=libc++ \
    -I"$SDK/Include" \
    "$SHIMDIR/rada_shim.cpp" \
    "$LIB" \
    -lc++ -lc++abi -lm -lpthread -ldl \
    -o "$OUT/librada_decode.so"

echo "OK: $OUT/librada_decode.so"
