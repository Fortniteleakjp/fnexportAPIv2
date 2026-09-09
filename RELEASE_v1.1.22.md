# English

## Title

```
Dump .usmap straight from a running UEFN (UE6 support)
```

## Body

```markdown
Mappings can now be dumped from a running Unreal Editor for Fortnite, so a build no
longer has to wait for someone else to publish a `.usmap` for it.

`UnrealMappingsDumper` is vendored into the repository and built by `build.bat`. The
API injects it into UEFN, and the mapping comes back covering both native `/Script`
types and Blueprint types — no base mapping needed.

### New endpoints

| Method & path | Description |
|---|---|
| `POST /api/v1/mappings/dump/uefn` | Dump a `.usmap` out of a running UEFN. Returns the binary by default and stores it as `mappings/{build}_uefn.usmap`. |
| `GET /api/v1/mappings/uefn` | Whether a dump can run right now: the dumper DLL, the UEFN process that would be targeted, and what to do next. |

The target process is identified automatically, so `pid` is normally unnecessary.
`compression=oodle` uses the encoder the editor already has loaded; the default is
uncompressed.

Verified on 42.10: **52,211 structs / 7,355 enums in 0.9 s**, and assets read back
identically to a mapping produced by Dumper-7.

### UE6 compatibility

The vendored dumper dates from 2022 and did not work on UE 6.0. Eleven separate
things had to change, all of which failed silently rather than raising an error:

- **UEFN was routed to the generic profile** — upstream only recognised
  `FortniteClient`, so the editor got the wrong GObjects patterns and the wrong FName
  layout.
- **UEFN is a modular build** — the executable is a stub and the engine lives in a
  DLL, so scans ran against a module holding none of what they were looking for.
- **GObjects is found by shape, not by signature** — none of the byte patterns match
  on UE6, so the object array is recognised by its own invariants instead.
- **`FUObjectItem`** gained a 64-bit flags word, moving the object pointer to +8.
- **`FChunkedFixedUObjectArray`** swapped Num/Max and moved `PreAllocatedObjects`.
- **`FName`** is three words on this build; carrying only two made every name resolve
  as `None`.
- **`FFieldVariant`** packs its tag into the pointer, so `FField` lost a word and the
  property chain read garbage.
- **`FFieldClass`** moved the cast flags behind an `EClassFlags`, which made every
  property come out as an unknown type.
- **`FArrayProperty`** declares its flags before the element type.
- **Editor-only properties** are excluded: the mapping describes cooked packages,
  which do not carry them, and counting one shifts every property index.
- **The `.usmap` is written as version 3** — version 0 stores an enum's member count
  in a single byte, and UE6 Fortnite has enums with more than 255 members.

Offsets whose position depends on build configuration are measured at runtime rather
than assumed, so the same approach should carry to later builds.

### Performance and safety

- A dump takes **0.9 s** (4.8 s with Oodle compression). The first run on a new build
  locates GObjects by walking process memory; the result is recorded in
  `mappings/dumper/offsets.json` and reused, so later runs skip the walk entirely.
- Addresses can also be seeded from a Dumper-7 run under `DUMPER7_DIR`
  (default `C:\Dumper-7`).
- Nothing unverified is executed inside the editor. `FNameToString` cannot be
  identified without calling it, so an address that was not vouched for is not called
  unless `probeSignatures=true` is passed.
- Upstream downloaded an Oodle DLL from a dead link and called the result of
  `GetProcAddress` on a null module. It now uses the copy the editor has already
  loaded, or the one shipped in `libs/`.

### Also in this release

- `POST /api/v1/mappings/dump` (the pak-side dump) applies the same editor-only rule,
  and now fails with a clear `400` when `merge=true` finds no base mapping instead of
  quietly producing a mapping that reads almost nothing.
- The mappings endpoints are documented in Japanese in Swagger.
- `.locres` namespace keys were read through `FTextKey.ToString()`, which is not
  overridden, so they came back as the type name.

### Requirements and limits

- Windows only. UEFN has to be running and finished loading, and the API has to run as
  the same Windows user.
- The first dump of a new build needs the `FNameToString` address once — taken from a
  Dumper-7 run when one is present, or passed as `fnameToString`. It is recorded
  afterwards and not needed again for that build.
- The pak-side dump still needs a base mapping to merge under: cooked archives carry
  Blueprint types only.
```

---

# 日本語

## タイトル

```
起動中の UEFN から直接 .usmap を生成（UE6 対応）
```

## 本文

```markdown
起動中の Unreal Editor for Fortnite からマッピングをダンプできるようになりました。
新しいビルドが出たときに、誰かが `.usmap` を公開してくれるのを待つ必要がありません。

`UnrealMappingsDumper` をリポジトリに取り込み、`build.bat` でビルドします。API が
それを UEFN へ注入し、ネイティブの `/Script` 型と Blueprint 型の両方を含む
マッピングが返ります。土台となる既存マッピングは不要です。

### 追加したエンドポイント

| メソッド & パス | 説明 |
|---|---|
| `POST /api/v1/mappings/dump/uefn` | 起動中の UEFN から `.usmap` をダンプします。既定でバイナリを返し、同時に `mappings/{build}_uefn.usmap` へ保存します。 |
| `GET /api/v1/mappings/uefn` | 今すぐ実行できるかを返します。ダンパー DLL の有無、対象になる UEFN プロセス、次に何をすればよいかを含みます。 |

対象プロセスは自動で特定するため、通常 `pid` の指定は不要です。`compression=oodle`
はエディタが既に読み込んでいるエンコーダを使います。既定は非圧縮です。

42.10 で確認済み: **52,211 structs / 7,355 enums を 0.9 秒**。Dumper-7 が生成した
マッピングと同じ内容でアセットを読めます。

### UE6 対応

取り込んだダンパーは 2022 年のもので、UE 6.0 では動きませんでした。11 箇所の修正が
必要で、いずれも例外を出さずに誤った結果を返す種類の問題でした。

- **UEFN が汎用プロファイルへ落ちていた** — 上流は `FortniteClient` しか判定して
  おらず、エディタには誤った GObjects パターンと誤った FName レイアウトが
  適用されていました。
- **UEFN はモジュラービルド** — 実行ファイルはスタブで、エンジン本体は DLL 側に
  あります。探しているものが存在しないモジュールを走査していました。
- **GObjects を署名ではなく構造で探す** — UE6 ではどのバイトパターンも一致しない
  ため、オブジェクト配列自身の不変条件から見つけます。
- **`FUObjectItem`** の先頭に 64bit のフラグが入り、オブジェクトポインタが +8 へ
  移動していました。
- **`FChunkedFixedUObjectArray`** は Num/Max が入れ替わり、`PreAllocatedObjects` が
  末尾へ移動していました。
- **`FName`** はこのビルドでは 3 ワードです。2 ワードしか持たないと、すべての名前が
  `None` になります。
- **`FFieldVariant`** はタグをポインタに畳み込むため 8 バイトになり、`FField` が
  1 ワード縮んでプロパティ連鎖が壊れていました。
- **`FFieldClass`** はキャストフラグを `EClassFlags` の後ろへ移動しており、全
  プロパティが「不明な型」になっていました。
- **`FArrayProperty`** は要素型の前にフラグを宣言します。
- **エディタ専用プロパティを除外** — マッピングが説明するのは cooked パッケージで、
  そこには存在しません。数に入れると全プロパティ番号がずれます。
- **`.usmap` をバージョン 3 で出力** — バージョン 0 は enum のメンバー数を 1 バイト
  で持ちますが、UE6 の Fortnite には 256 個以上のメンバーを持つ enum があります。

位置がビルド構成に依存するオフセットは決め打ちせず実行時に実測しているので、今後の
ビルドにも同じ方法で追従できるはずです。

### 速度と安全性

- ダンプは **0.9 秒**（Oodle 圧縮ありで 4.8 秒）です。新しいビルドでの初回のみ
  プロセスメモリを走査して GObjects を探し、結果は
  `mappings/dumper/offsets.json` に記録して再利用するため、以降は走査しません。
- `DUMPER7_DIR`（既定 `C:\Dumper-7`）に Dumper-7 の出力があれば、そこからアドレスを
  取得します。
- 素性の分からないものをエディタ内で実行しません。`FNameToString` は呼んでみる以外に
  見分ける方法がないため、指定されていないアドレスは `probeSignatures=true` を
  明示しない限り呼びません。
- 上流は失効したリンクから Oodle をダウンロードし、失敗すると null モジュールに対する
  `GetProcAddress` の結果を呼んでいました。エディタが既に読み込んでいるものか、
  `libs/` に同梱しているものを使います。

### そのほかの変更

- `POST /api/v1/mappings/dump`（pak 側のダンプ）にも同じエディタ専用プロパティの
  判定を適用しました。`merge=true` で土台が見つからない場合は、ほとんど読めない
  マッピングを黙って作らず `400` で止めます。
- マッピング関連のエンドポイントを Swagger で日本語表示にしました。
- `.locres` の名前空間キーを `FTextKey.ToString()` で読んでいましたが、これは
  オーバーライドされていないため型名が返っていました。

### 前提と制限

- Windows 専用です。UEFN が起動しきっている必要があり、API は UEFN と同じ Windows
  ユーザーで実行してください。
- 新しいビルドの初回だけ `FNameToString` のアドレスが必要です。Dumper-7 の出力が
  あればそこから取得し、無ければ `fnameToString` で指定します。一度通れば記録され、
  そのビルドでは以降不要です。
- pak 側のダンプは土台となるマッピングが必要です。cooked アーカイブが持つのは
  Blueprint 型だけです。
```
