import json
import requests

URL = "https://fortnitecentral.genxgames.gg/api/v1/aes"
OUTPUT_FILE = "enc.json"

print(">>> Fortnite AES情報を取得しています...")

try:
    response = requests.get(URL)
    response.raise_for_status()
    aes_data = response.json()

    print(">>> データ取得成功！整形処理を開始します...")

    formatted_list = []

    for item in aes_data.get("dynamicKeys", []):
        base_name = f"FortniteGame/Content/Paks/{item['name']}"

        formatted_list.append({
            "name": f"{base_name.replace('.utoc', '-WindowsClient.pak')}",
            "key": item["key"],
            "guid": item["guid"]
        })

        formatted_list.append({
            "name": base_name,
            "key": item["key"],
            "guid": item["guid"]
        })

        formatted_list.append({
            "name": base_name.replace(".utoc", ".ucas"),
            "key": item["key"],
            "guid": item["guid"]
        })

    with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
        json.dump(formatted_list, f, indent=4)

    print(f">>> enc.json の生成完了！\n保存先: {OUTPUT_FILE}")

except requests.exceptions.RequestException as e:
    print(f"⚠ HTTPエラーが発生しました: {e}")
except Exception as e:
    print(f"⚠ 予期せぬエラー: {e}")
