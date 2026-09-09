#pragma once

#include "hostConfig.h"

// fnexportAPI local patch: the line is formatted once so it can go to the console and to the
// host's log file, which is the only channel an injected DLL has back to the API.
static void UE_LOG(const char* str, ...)
{
	va_list fmt;
	va_start(fmt, str);

	char Line[4096];
	if (vsnprintf_s(Line, sizeof(Line), _TRUNCATE, str, fmt) < 0)
		Line[0] = '\0';

	va_end(fmt);

	printf("[=] %s\n", Line);
	HostConfig::LogLine(Line);
}

namespace App
{
	bool Init();
}