# UEFNStaticMappingsGenerator

UEFNのEngine・Common DLLから、ゲームを起動せずに `.usmap` version 4を生成します。
元のコードは `D:\mappings_dumper` から取り込んでいます。UHTのレイアウトはUE 6.0に対応しています。

Visual StudioのC++ツール、CMake、Ninja、Gitが必要です。
初回の構成時にzstd v1.5.7とBrotli v1.1.0を取得して静的リンクします。

```bat
MappingsGenerator\build.bat libs
libs\UEFNStaticMappingsGenerator.exe --dir "C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64" --compression zstd --no-wait
```

`--out` で出力先、`--level` で圧縮レベル、`--oodle` でOodle DLLを指定できます。
圧縮方式は `zstd`（既定）、`brotli`、`oodle`、`none` です。
その他の引数は `--help` を参照してください。

EngineとCommonをロードし、`ConstructUClass`・`ConstructUScriptStruct`・`ConstructUEnum` をフックしてUHTの型情報を収集します。
コンテナの要素、親クラス、列挙型を解決し、cook時に除去されるEditorOnlyプロパティを除いて書き出します。
圧縮後は伸長結果を照合します。API側でもCUE4Parseで検証してから保存します。

APIからは `POST /api/v1/mappings/generate` で呼び出します。
DLLのロードとフックは生成プロセス内で完結します。
