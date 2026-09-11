namespace FortnitePorting.Models;

/// <summary>
/// One Fortnite build this instance knows about, as recorded on disk under <c>build_history/</c>.
/// The archived manifest is the only thing that has to be kept to read a build again: the pak data
/// itself is never stored locally, it is streamed from the Epic CDN through that manifest.
/// </summary>
public sealed class ArchivedBuild
{
    /// <summary>Full build version, e.g. <c>++Fortnite+Release-42.10-CL-57566230-Windows</c>.</summary>
    public string BuildVersion { get; set; } = string.Empty;

    /// <summary>The manifest file name the build info pointed at (distinguishes re-published manifests).</summary>
    public string ManifestId { get; set; } = string.Empty;

    /// <summary>Short version taken out of the build string, e.g. <c>42.10</c>.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Changelist number taken out of the build string, e.g. <c>57566230</c>.</summary>
    public string Changelist { get; set; } = string.Empty;

    /// <summary>When this build was first archived (UTC).</summary>
    public DateTime ArchivedUtc { get; set; }

    /// <summary>File name of the archived manifest inside <c>build_history/manifests/</c>, or null once pruned.</summary>
    public string? ManifestFile { get; set; }

    /// <summary>Size of the archived manifest in bytes (0 once pruned).</summary>
    public long ManifestBytes { get; set; }

    /// <summary>When the archived manifest was deleted by the retention policy (UTC), if it was.</summary>
    public DateTime? PrunedUtc { get; set; }

    /// <summary>True while the manifest is still on disk, so this build's files can be read again.</summary>
    public bool HasManifest => ManifestFile != null && PrunedUtc == null;
}

/// <summary>The persisted index of <see cref="ArchivedBuild"/> entries, newest first.</summary>
public sealed class BuildHistoryIndex
{
    public List<ArchivedBuild> Builds { get; set; } = [];
}

/// <summary>
/// AES keys supplied for an archived build. Needed for a build whose manifest was imported rather
/// than served by this instance: Fortnite rotates its keys every build and the live key APIs only
/// publish the current ones, so an imported manifest has no way to obtain the keys it needs.
/// </summary>
public sealed class ArchivedKeysRequest
{
    /// <summary>The build's main key, registered against the all-zero GUID.</summary>
    public string? MainKey { get; set; }

    /// <summary>Per-pak keys, in the same shape the AES APIs use.</summary>
    public List<DynamicKey>? DynamicKeys { get; set; }

    /// <summary>Raw GUID → key pairs, as an alternative to the two fields above.</summary>
    public Dictionary<string, string>? Keys { get; set; }
}

/// <summary>How much of a build actually mounted — what makes a comparison trustworthy or not.</summary>
/// <param name="Files">Virtual files the build exposes.</param>
/// <param name="MountedVfs">Containers that mounted.</param>
/// <param name="UnmountedVfs">Containers that did not mount, almost always for want of their AES key.</param>
/// <param name="KeysRequired">Distinct encryption GUIDs still missing.</param>
public readonly record struct MountHealth(int Files, int MountedVfs, int UnmountedVfs, int KeysRequired)
{
    /// <summary>True when every container mounted, so the file list is the build's real one.</summary>
    public bool IsComplete => UnmountedVfs == 0;
}

/// <summary>How one virtual file changed between two builds.</summary>
public enum BuildChangeKind
{
    /// <summary>The path exists only in the newer build.</summary>
    Added,

    /// <summary>The path exists only in the older build.</summary>
    Removed,

    /// <summary>The path exists in both builds and the content differs.</summary>
    Modified,

    /// <summary>
    /// The path exists in both builds with the same size and the same containing archive, so it is
    /// reported as unchanged by the quick comparison but was never hashed. Only produced in
    /// <c>quick</c> mode; <c>full</c> mode resolves every one of these into Modified or drops it.
    /// </summary>
    Unverified
}

/// <summary>One entry of a recorded changelist.</summary>
public sealed class BuildChangeEntry
{
    /// <summary>Virtual file path, e.g. <c>FortniteGame/Content/Athena/Items/Foo.uasset</c>.</summary>
    public string Path { get; set; } = string.Empty;

    public BuildChangeKind Kind { get; set; }

    /// <summary>Size in the older build, or null when the file did not exist there.</summary>
    public long? OldSize { get; set; }

    /// <summary>Size in the newer build, or null when the file no longer exists.</summary>
    public long? NewSize { get; set; }

    /// <summary>Containing archive in the older build (pak/utoc name), when known.</summary>
    public string? OldArchive { get; set; }

    /// <summary>Containing archive in the newer build (pak/utoc name), when known.</summary>
    public string? NewArchive { get; set; }

    /// <summary>SHA-256 of the older content, set only when the entry was verified by hashing.</summary>
    public string? OldHash { get; set; }

    /// <summary>SHA-256 of the newer content, set only when the entry was verified by hashing.</summary>
    public string? NewHash { get; set; }
}

/// <summary>
/// A recorded changelist between two builds. Composed diffs (v40 → v42, built out of v40 → v41 and
/// v41 → v42) carry <see cref="Via"/> so it is clear they were not computed against both builds
/// directly.
/// </summary>
public sealed class BuildDiff
{
    public string FromBuild { get; set; } = string.Empty;
    public string ToBuild { get; set; } = string.Empty;

    /// <summary><c>quick</c> (path/size/archive) or <c>full</c> (every candidate hashed).</summary>
    public string Mode { get; set; } = "quick";

    /// <summary>Builds this diff was composed through, e.g. <c>["...42.00..."]</c> for a v40 → v42 diff.</summary>
    public List<string> Via { get; set; } = [];

    public DateTime ComputedUtc { get; set; }

    /// <summary>Seconds the computation took.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Path prefix the computation was limited to, when one was given.</summary>
    public string? PathFilter { get; set; }

    /// <summary>True when the entry list was cut short by a limit and is therefore incomplete.</summary>
    public bool Truncated { get; set; }

    public int TotalFilesFrom { get; set; }
    public int TotalFilesTo { get; set; }

    /// <summary>Containers of the older build that never mounted when it was compared.</summary>
    public int UnmountedVfsFrom { get; set; }

    /// <summary>Containers of the newer build that never mounted when it was compared.</summary>
    public int UnmountedVfsTo { get; set; }

    /// <summary>
    /// Containers left out of the comparison because they could be read in only one of the two builds.
    /// Their files are absent from this changelist entirely — reporting them would mean calling a file
    /// added or removed when all that changed is whether its container could be opened.
    /// </summary>
    public List<string> ExcludedArchives { get; set; } = [];

    /// <summary>Files of the older build hidden by <see cref="ExcludedArchives"/>.</summary>
    public int ExcludedFilesFrom { get; set; }

    /// <summary>Files of the newer build hidden by <see cref="ExcludedArchives"/>.</summary>
    public int ExcludedFilesTo { get; set; }

    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }
    public int ModifiedCount { get; set; }

    /// <summary>
    /// Files that exist in both builds with the same size and archive. In <c>quick</c> mode these were
    /// never hashed, so they are reported as a count rather than as entries: almost all of them really
    /// are unchanged, but the quick comparison cannot prove it for any individual one.
    /// </summary>
    public int UnverifiedCount { get; set; }

    /// <summary>Files whose content could not be read on one side (encrypted pak, read error).</summary>
    public int UnreadableCount { get; set; }

    public List<BuildChangeEntry> Entries { get; set; } = [];
}
