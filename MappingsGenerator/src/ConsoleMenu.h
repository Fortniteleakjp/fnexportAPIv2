#pragma once

#include "Settings.h"

namespace ConsoleMenu
{
    /* 標準入出力が両方コンソールなら対話メニューを出せる */
    bool IsAvailable();

    /* 日本語のメニューで Settings を決める。「終了」を選んだら false */
    bool Configure(RunSettings& Settings);

    /* UTF-8 の書式文字列をコンソールに出す (コードページに関係なく日本語を表示できる) */
    void Print(const char* Format, ...);

    void WaitForEnter();
}
