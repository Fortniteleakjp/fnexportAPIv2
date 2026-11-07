#pragma once

#include "Dumper.h"

#include <string>
#include <vector>

/*
* --extra で渡された非圧縮 .usmap から構造体を読む。
* UEFN の DLL に入っていない型 (ゲーム本体だけにある GameFeature プラグインの行構造体など) を、
* API 側が cook 済みデータから推定して渡してくるのを最終出力に含めるために使う。
* 列挙型は読み飛ばす (推定では作らない)。
*/
namespace ExtraMappings
{
    bool Load(const std::wstring& Path, std::vector<Dumper::Struct>& OutStructs, std::string& OutError);
}
