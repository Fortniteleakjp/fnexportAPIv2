#pragma once

#include "Utils.h"

/*
* 関数の先頭を Detour へのジャンプで上書きする。
* 元の関数は二度と呼ばない (UObject を実際に生成させないのが目的) ので、トランポリンは作らない。
*
* 先頭には 5 バイトの `jmp rel32` だけを書き、その飛び先 (Target から ±2GB 以内に確保したスタブ) に
* `jmp qword [rip+0]; dq Detour` を置く。これで 14 バイトに満たない短い関数 (thunk) でも安全に書き換えられる。
*/
namespace Hook
{
    bool Install(void* Target, void* Detour);
}
