#include "pch.h"

#include "app.h"
#include "unrealVersion.h"

struct GameInstance
{
	std::filesystem::path GamePath;
	float Version = 0;
};

static std::optional<GameInstance> TryGetGameInfo()
{
	wchar_t GameFilePath[MAX_PATH];
	GetModuleFileName(0, GameFilePath, MAX_PATH);

	DWORD verHandle = 0;
	DWORD verSize = GetFileVersionInfoSize(GameFilePath, &verHandle);

	if (!verSize)
		return std::nullopt;

	std::string VerData(verSize, '\0');
	uint32_t size = 0;
	VS_FIXEDFILEINFO* VersionInfo = nullptr;

	if (!GetFileVersionInfo(GameFilePath, verHandle, verSize, VerData.data()) ||
		!VerQueryValue(VerData.data(), L"\\", (VOID FAR * FAR*) & VersionInfo, &size) ||
		!size ||
		VersionInfo->dwSignature != 0xfeef04bd)
	{
		return std::nullopt;
	}

	auto VersionStr = std::format("{:d}.{:d}.{:d}.{:d}",
		(VersionInfo->dwFileVersionMS >> 16) & 0xffff,
		(VersionInfo->dwFileVersionMS >> 0) & 0xffff,
		(VersionInfo->dwFileVersionLS >> 16) & 0xffff,
		(VersionInfo->dwFileVersionLS >> 0) & 0xffff);

	GameInstance Ret;
	Ret.GamePath = std::filesystem::path(GameFilePath);
	Ret.Version = std::stof(VersionStr);

	return Ret;
}

static bool InitEngine(GameInstance& Game)
{
	// if an engine version has a different type or offset, set it here

	auto FileName = Game.GamePath.filename().string();

	// fnexportAPI local patch: UEFN (UnrealEditorFortnite-Win64-Shipping.exe) is the same engine and
	// build family as the game client, so it needs the same profile. Upstream only matched
	// FortniteClient, which silently sent the editor down the generic path: the wrong GObjects
	// patterns AND the wrong FName layout, since Fortnite builds with UE_FNAME_OUTLINE_NUMBER=1.
	auto IsFortnite =
		FileName.starts_with("FortniteClient-Win64-") ||
		FileName.starts_with("UnrealEditorFortnite-Win64-");

	if (IsFortnite and Game.Version >= 5.0)
	{
		UE_LOG("Using the Fortnite profile (outline-number FName, Fortnite GObjects patterns)");
		return IUnrealVersion::InitTypes<Version_FortniteLatest>();
	}

	UE_LOG("Using the generic Unreal profile");
	return IUnrealVersion::InitTypes<UnrealVersionBase>();
}

bool App::Init()
{
	auto GameOpt = TryGetGameInfo();

	if (!GameOpt.has_value())
		return false;

	auto& Game = GameOpt.value();

	UE_LOG("Detected Unreal Engine version %f", Game.Version);
	UE_LOG("Detected game %s", Game.GamePath.filename().string().c_str());

	return InitEngine(Game);
}