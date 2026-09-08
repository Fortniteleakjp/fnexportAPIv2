#pragma once

#include "unrealEnums.h"

// ---------------------------------------------------------------------------
// fnexportAPI local patch.
//
// The API injects this DLL into an already running UE game and afterwards has no
// channel to talk to it, so the host drops a small key=value file next to the DLL
// before injecting (<dll path without extension>.cfg):
//
//   output=D:\repo\mappings\dump.usmap   absolute path of the .usmap to write
//   compression=none|oodle               usmap compression (default: none)
//   console=true|false                   allocate a console in the game (default: true)
//   gobjects=1a2b3c4                     module-relative address of GObjects, when the scan fails
//   fnametostring=1a2b3c4                module-relative address of FNameToString, likewise
//
// Everything UE_LOG prints is mirrored to "<output>.log" so the host can report why
// a dump failed instead of only timing out. The run always ends with one terminal line
// in that log, which is what the host waits for:
//
//   HOST_RESULT ok <output path>      the .usmap was written
//   HOST_RESULT failed <reason>       nothing usable was produced
//
// Without the config file the dumper keeps its upstream behaviour: it writes
// .\Mappings.usmap and logs to the console only.
// ---------------------------------------------------------------------------
namespace HostConfig
{
	inline std::string Output = "Mappings.usmap";
	inline ECompressionMethod Compression = ECompressionMethod::None;
	inline bool Console = true;
	inline std::string LogPath;

	// Signature scans break on every engine bump, so the host can pin the two addresses the dumper
	// cannot start without. Zero means "scan for it", which is the normal path.
	inline uintptr_t GObjectsRva = 0;
	inline uintptr_t FNameToStringRva = 0;

	// Parses a hex address, with or without a 0x prefix. Returns 0 when the value is not usable,
	// which falls back to scanning rather than pointing the dumper at nothing.
	inline uintptr_t ParseRva(const std::string& In)
	{
		try
		{
			return static_cast<uintptr_t>(std::stoull(In, nullptr, 16));
		}
		catch (const std::exception&)
		{
			return 0;
		}
	}

	inline std::string Trim(const std::string& In)
	{
		auto Begin = In.find_first_not_of(" \t\r\n");
		if (Begin == std::string::npos)
			return {};

		auto End = In.find_last_not_of(" \t\r\n");
		return In.substr(Begin, End - Begin + 1);
	}

	inline void LogLine(const char* Line)
	{
		if (LogPath.empty())
			return;

		FILE* File = nullptr;
		if (fopen_s(&File, LogPath.c_str(), "a") != 0 || !File)
			return;

		std::fprintf(File, "%s\n", Line);
		std::fclose(File);
	}

	// Reads the config that sits next to the injected module. Silently keeps the
	// upstream defaults when the file is missing or unreadable.
	inline void Load(HMODULE Module)
	{
		wchar_t ModulePath[MAX_PATH];
		if (!GetModuleFileNameW(Module, ModulePath, MAX_PATH))
			return;

		auto ConfigPath = std::filesystem::path(ModulePath).replace_extension(".cfg");

		std::error_code Ec;
		if (!std::filesystem::exists(ConfigPath, Ec))
			return;

		std::ifstream Config(ConfigPath);
		if (!Config.is_open())
			return;

		std::string Line;
		while (std::getline(Config, Line))
		{
			auto Separator = Line.find('=');
			if (Separator == std::string::npos)
				continue;

			auto Key = Trim(Line.substr(0, Separator));
			auto Value = Trim(Line.substr(Separator + 1));
			if (Key.empty() || Value.empty())
				continue;

			if (Key == "output")
			{
				Output = Value;
			}
			else if (Key == "compression")
			{
				if (Value == "oodle" || Value == "Oodle")
					Compression = ECompressionMethod::Oodle;
				else
					Compression = ECompressionMethod::None;
			}
			else if (Key == "console")
			{
				Console = (Value != "false" && Value != "0");
			}
			else if (Key == "gobjects")
			{
				GObjectsRva = ParseRva(Value);
			}
			else if (Key == "fnametostring")
			{
				FNameToStringRva = ParseRva(Value);
			}
		}

		LogPath = Output + ".log";

		// Start every run with an empty log so the host never reads a stale failure.
		std::filesystem::remove(LogPath, Ec);
	}
}
