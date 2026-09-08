#include "pch.h"

#include "app.h"
#include "dumper.h"
#include "hostConfig.h"

void WINAPI Main(HMODULE Module)
{
	// fnexportAPI local patch: the host drops a .cfg next to this DLL to say where the .usmap
	// goes. Without it the upstream defaults still apply (.\Mappings.usmap, console only).
	HostConfig::Load(Module);

	if (HostConfig::Console)
	{
		AllocConsole();
		FILE* f;
		freopen_s(&f, "CONOUT$", "w", stdout);
	}

	UE_LOG("Unreal Mappings Dumper created by OutTheShade");
	UE_LOG("Output: %s", HostConfig::Output.c_str());

	// Every failure is reported through HOST_RESULT so the host can say why a dump produced no
	// file, and nothing is allowed to escape into the game as an unhandled exception.
	try
	{
		if (!App::Init())
		{
			UE_LOG("Failed to initialize the dumper. Returning.");
			UE_LOG("HOST_RESULT failed initialization failed (unsupported build or signature mismatch)");
			FreeLibraryAndExitThread(Module, NULL);
			return;
		}

		auto Start = std::chrono::steady_clock::now();

		Dumper::Run(HostConfig::Compression);

		auto End = std::chrono::steady_clock::now();

		UE_LOG("Successfully generated mappings file in %.02f ms", (End - Start).count() / 1000000.);
		UE_LOG("HOST_RESULT ok %s", HostConfig::Output.c_str());
	}
	catch (const std::exception& Exception)
	{
		UE_LOG("HOST_RESULT failed %s", Exception.what());
	}
	catch (...)
	{
		UE_LOG("HOST_RESULT failed unknown exception");
	}

	FreeLibraryAndExitThread(Module, NULL);
}

BOOL APIENTRY DllMain(
	HMODULE hModule,
	DWORD  ul_reason_for_call,
	LPVOID lpReserved
)
{
	switch (ul_reason_for_call)
	{
	case DLL_PROCESS_ATTACH:
		CloseHandle(CreateThread(nullptr, 0, (LPTHREAD_START_ROUTINE)Main, hModule, 0, nullptr));
	case DLL_THREAD_ATTACH:
	case DLL_THREAD_DETACH:
	case DLL_PROCESS_DETACH:
		break;
	}
	return TRUE;
}
