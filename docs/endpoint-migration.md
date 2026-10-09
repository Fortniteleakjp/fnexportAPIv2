# 旧URLからの移行

重複する旧URLを削除し、68のHTTP操作を53に統合しました。旧URLの別名ルートは残していません。既存クライアントは下記の移行先に変更してください。ルートが存在しない場合は404、別のメソッドだけが一致する場合は405です。

| 旧メソッド・URL | 移行先 | 注意点 |
|---|---|---|
| `GET /api/v1/debug/stats` | `GET /api/v1/files` | ページングはpage/pageSizeで指定。 |
| `GET /api/v1/debug/search?query=...` | `GET /api/v1/search?q=...` | queryをqに変更。結果はページングされます。 |
| `GET /api/v1/debug/paks` | `GET /api/v1/paks` | 一覧とメタデータを統合。 |
| `GET /api/v1/debug/paks/{pakName}/files` | `GET /api/v1/paks/{pakName}/files` | ページング対応の既存ルートへ統合。 |
| `GET /api/v1/export/filepath/{pakName}` | `GET /api/v1/paks/{pakName}/files` | チャンク番号でも絞り込めます。 |
| `GET /api/v1/archives` | `GET /api/v1/paks?state=all` | 未マウントを含むメタデータ。配列を直接返さず、paks配列とページ情報を返します。 |
| `GET /api/v1/archives/keys` | `GET /api/v1/aes/keys` | mainKey/dynamicKeys/unloadedなどの形式を維持。 |
| `GET /aes` | `GET /api/v1/aes` | AESルート配下へ配置。クエリの変更なし。 |
| `POST /api/v1/mappings/dump` | `POST /api/v1/mappings/generate` | 同じ生成処理に統一。 |
| `POST /api/v1/mappings/dump/uefn` | `POST /api/v1/mappings/generate` | 同じ生成処理に統一。 |
| `POST /api/v1/mappings/dump/local` | `POST /api/v1/mappings/generate` | dirでUEFN DLLのディレクトリーを選択。 |
| `GET /api/v1/items/files` | `GET /api/v1/files` | 旧既定のアイテム一覧はprefixesとextを明示（下記）。 |
| `GET /api/v1/items/properties/single?path=...` | `GET /api/v1/items/properties?path=...` | 単一応答の形式を維持。path指定時は一覧条件を適用しません。 |
| `GET /api/v1/search/content?q=...` | `GET /api/v1/search?target=content&q=...` | 内容検索用のほかのクエリは同じ。 |
| `GET /api/v1/cosmetics/search` | `GET /api/v1/cosmetics` | q/category/lang/page/pageSizeは同じ。 |
| `GET /api/v1/pak/{pakName}/cosmetics` | `GET /api/v1/cosmetics?pakName={pakName}&includeOffers=true` | バンドル表示アセットも取得。共通のtotal/resultsへ統一。 |
| `GET /api/v1/export/locres?lang=...` | `GET /api/v1/localization?lang=...` | namespace/key/valueテーブルを返します。 |
| `GET /api/v1/export/locres/languages` | `GET /api/v1/localization/languages` | 言語一覧を一本化。 |
| `GET /api/v1/localization/lookup` | `GET /api/v1/localization` | key/textのクエリと順引き・逆引きの形式を維持。 |
| `GET /api/v1/backup/fbkp` | `GET /api/v1/backup?format=fbkp` | includePayloads/compressは同じ。情報取得はformat=json。 |

## よく使う呼び出し

```text
GET /api/v1/files
GET /api/v1/files?prefixes=WID_,AGID_,Athena_,Figment_Athena_&ext=.uasset
GET /api/v1/search?q=Item&field=name
GET /api/v1/search?target=content&q=DisplayName&ext=uasset
GET /api/v1/paks?state=all&page=1&pageSize=200
GET /api/v1/paks/55/files?page=1&pageSize=1000
GET /api/v1/cosmetics?pakName=55&includeOffers=true&lang=ja
GET /api/v1/items/properties?path=FortniteGame/Content/.../WID_Item.uasset
GET /api/v1/localization?lang=ja
GET /api/v1/localization?key=MyKey&langs=ja,en
GET /api/v1/localization?text=表示名&lang=ja
GET /api/v1/backup?format=fbkp
POST /api/v1/mappings/generate?compression=zstd
```

`/files` は省略時に全ファイル・全拡張子を返します。旧 `/items/files` の既定値を再現するには、上記のprefixesとextを指定します。`/paks` は既定でマウント済みのみ。旧 `/archives` と同じ対象は `state=all` で選択し、全件必要ならページを順に取得します（最大200件/ページ）。

コスメ一覧はPAK指定の有無にかかわらず `total`、`totalCosmetics`、`totalOfferCatalogDisplayAssets`、`results` の形です。`totalResults`、`totalBRCosmetics`を参照していたクライアントは対応する新しいキーを使ってください。`includeOffers` の既定はfalseで、従来の全PAK検索と同じくコスメ定義だけを返します。

バックアップ情報の `downloadUrl` と、マッピングの `downloadUrl` は現行ルートを指します。全ルートは [エンドポイント一覧](endpoints.md) で確認できます。
