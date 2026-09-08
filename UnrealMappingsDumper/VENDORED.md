# ベンダリングされた UnrealMappingsDumper

このディレクトリは submodule ではなく、**ベンダリングされた（リポジトリ内に取り込んだ）コピー**です。
`CUE4Parse/` と同じ扱いで、クローン 1 回で `UnrealMappingsDumper\build.bat` を実行できます。

`POST /api/v1/mappings/dump/uefn` は、ここからビルドした `UnrealMappingsDumper.dll` を
起動中の UEFN に注入して `.usmap` を生成します（`FortnitePorting/Services/MappingsDumper/UefnDumperInjector.cs`）。

## 取り込み元

| 対象 | 上流 | コミット |
| --- | --- | --- |
| UnrealMappingsDumper | https://github.com/TheNaeem/UnrealMappingsDumper | `4da8c66c23ce66ef86d75962d66b12cf39185092` |
| Dependencies/Memcury | https://github.com/kem0x/Memcury | `2f75b4cf90ed64328ba14ff38f41b8d249b80edc` |

上流の DLL は「コンソールを開き、カレントディレクトリへ `Mappings.usmap` を書く」ことしかできません。
注入されたモジュールから呼び出し元へ戻る経路が無いため、API から使うには出力先の指定と結果の受け取りが必要で、
そのために以下のローカルパッチを当てています。**上流を取り込み直すときは、この一覧が残っているか必ず確認してください。**

## ローカルパッチ

| ファイル | 内容 |
| --- | --- |
| `UnrealMappingsDumper/hostConfig.h` | 新規。DLL の隣に置かれた `<dll>.cfg`（`output` / `compression` / `console`）を読み、ログを `<output>.log` へ書き出します。 |
| `UnrealMappingsDumper/dllmain.cpp` | `HostConfig::Load()` を呼び、コンソール開閉と圧縮方式を設定に従わせます。処理全体を try/catch で包み、最終行に `HOST_RESULT ok <path>` / `HOST_RESULT failed <理由>` を出します。ホストはこの行を待ちます。 |
| `UnrealMappingsDumper/app.h` | `UE_LOG` の出力をコンソールと `HostConfig::LogLine()` の両方へ流します（注入先からホストへ届く唯一の経路）。 |
| `UnrealMappingsDumper/dumper.cpp` | 出力先を `HostConfig::Output` に変更。開けなければ例外を投げ、`HOST_RESULT failed` に理由が載るようにします。 |
| `UnrealMappingsDumper/writer.h` | `FileWriter::m_File` を `nullptr` 初期化し、デストラクタで NULL チェック。`IsOpen()` を追加。書き込めないパスでゲームを巻き込んで落とさないためです。 |
| `UnrealMappingsDumper/framework.h` | 上記の例外送出のため `<stdexcept>` を追加。 |
| `UnrealMappingsDumper/app.cpp` | `UnrealEditorFortnite-Win64-*`（UEFN）を Fortnite プロファイルへ振り分け。上流は `FortniteClient` しか判定しておらず、UEFN が汎用パス（誤った GObjects パターンと、`UE_FNAME_OUTLINE_NUMBER=1` を前提としない FName レイアウト）へ落ちていました。 |
| `UnrealMappingsDumper/unrealVersion.h` | 設定で指定されたアドレスを走査より優先し、どの候補が当たったかをモジュール相対アドレスで記録。Fortnite プロファイルの GObjects 候補も追加。 |
| `UnrealMappingsDumper/unrealTypes.h` | `ObjObjects` を固定 struct ではなく検出したレイアウト経由で読むよう変更。UE6 は `FChunkedFixedUObjectArray` の Num/Max を入れ替え、`PreAllocatedObjects` を末尾へ移動し、`FUObjectItem` の先頭に 64bit の `FlagsAndRefCount` を足してオブジェクトポインタを +8 へ押し出しています（さらに packed の場合あり）。いずれも例外ではなく無言で壊れるため、実メモリで検証して確定させます。 |
| `UnrealMappingsDumper/unrealVersion.h`（モジュール切替） | GObjects を見つけた時点で、それを含むモジュールへ以降の走査対象を切り替えます。UEFN はモジュラービルドで、実行ファイルは小さなスタブ（`.data` が 1KB）でしかなく、エンジン本体は DLL 側にあります。上流はプロセスのメインモジュールしか見ないため、探しているものが存在しない場所を走査していました。 |
| `UnrealMappingsDumper/scanning.{h,cpp}` | 設定で渡されたモジュール相対アドレスを解決する `ManualAddressScanObject` と、`GetScanModuleBase()` を追加（Memcury のヘッダは非 inline 関数を含むため 1 つの翻訳単位からしか include できません）。 |
| `UnrealMappingsDumper/*.vcxproj{,.filters}` | `hostConfig.h` をプロジェクトに追加。 |
| `build.bat` | 新規。MSBuild を解決して x64 Release をビルドし、`libs/UnrealMappingsDumper.dll` へ配置します。 |

設定ファイルが無い場合は上流どおりの挙動（`.\Mappings.usmap`、コンソールのみ）に戻ります。
