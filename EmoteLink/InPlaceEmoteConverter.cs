using Dalamud.Plugin.Services;
using NoireLib.Animations.PapFormat;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EmoteLink;

internal sealed record EmoteConversionTimeline(int Slot, string Key, bool IsPersistentLoop);

internal sealed record EmoteConversionCandidate(
    uint EmoteId,
    string Command,
    IReadOnlyList<EmoteConversionTimeline> Timelines);

internal sealed record InPlaceConversionResult(
    bool Success,
    string Message,
    uint CarrierEmoteId = 0,
    string CarrierCommand = "",
    bool ChangedFiles = false);

/// <summary>
/// Permanently retargets PAP files inside an existing Penumbra mod. The byte rewrite uses
/// NoireLib's PAP retargeter, the same underlying method used by Aspher0/BypassEmote.
/// No second Penumbra mod is created.
/// </summary>
internal sealed class InPlaceEmoteConverter
{
    public const string MarkerFileName = "synastry-conversion.json";
    private const int SchemaVersion = 1;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly string backupRoot;

    private static readonly JsonSerializerOptions MarkerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true
    };

    private static readonly JsonDocumentOptions ManifestReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public InPlaceEmoteConverter(IDataManager dataManager, IPluginLog log, string configDirectory)
    {
        this.dataManager = dataManager;
        this.log = log;
        backupRoot = Path.Combine(configDirectory, "conversion-backups");
    }

    public bool IsConverted(string? modRoot, string directory) =>
        TryGetModPath(modRoot, directory, out var modPath) &&
        File.Exists(Path.Combine(modPath, MarkerFileName));

    public bool TryGetOriginalIdentity(
        string? modRoot,
        string directory,
        out string syncKey,
        out string fingerprint)
    {
        syncKey = "";
        fingerprint = "";
        if (!TryGetModPath(modRoot, directory, out var modPath) ||
            !TryReadMarker(modPath, out var marker) ||
            marker.SchemaVersion != SchemaVersion ||
            marker.OriginalSyncKey.Length == 0 ||
            !IsFingerprint(marker.OriginalFingerprint) ||
            !MarkerFilesMatch(modPath, marker) ||
            !BackupMatches(marker))
            return false;
        syncKey = marker.OriginalSyncKey;
        fingerprint = marker.OriginalFingerprint;
        return true;
    }

    private bool BackupMatches(ConversionMarker marker)
    {
        var backupDirectory = SafeBackupDirectory(marker.BackupId);
        if (backupDirectory is null || !Directory.Exists(backupDirectory)) return false;
        foreach (var file in marker.Files)
            if (!TryResolveInside(backupDirectory, file.BackupFile, out var backup) ||
                !File.Exists(backup) ||
                !HashFile(backup).Equals(file.BeforeSha256, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    public InPlaceConversionResult Convert(
        string? modRoot,
        string directory,
        string modName,
        string originalSyncKey,
        string originalFingerprint,
        uint sourceEmoteId,
        string sourceCommand,
        IReadOnlyList<EmoteConversionTimeline> sourceTimelines,
        IReadOnlyList<EmoteConversionCandidate> candidates)
    {
        if (!TryGetModPath(modRoot, directory, out var modPath))
            return Fail("Penumbra's mod folder could not be resolved safely.");
        if (!Directory.Exists(modPath))
            return Fail($"The installed folder for {modName} was not found.");
        if (!IsFingerprint(originalFingerprint) || originalSyncKey.Length == 0)
            return Fail("The original Synastry identity is not available yet. Refresh the library and try again.");

        var restoredPreviousConversion = false;
        string? previousSourceCommand = null;
        if (TryReadMarker(modPath, out var existing))
        {
            if (!MarkerFilesMatch(modPath, existing) || !BackupMatches(existing))
                return Fail(
                    "The converted mod or its local backup changed after Synastry created it. " +
                    "Restore or reinstall the original mod before rebuilding the conversion.");
            if (existing.SourceEmoteId == sourceEmoteId)
            {
                var carrier = candidates.FirstOrDefault(candidate => candidate.EmoteId == existing.CarrierEmoteId);
                if (carrier is not null)
                    return new InPlaceConversionResult(
                        true,
                        $"Using the existing permanent conversion through {carrier.Command}.",
                        carrier.EmoteId,
                        carrier.Command);
            }

            previousSourceCommand = existing.SourceCommand;
            var restore = Restore(modRoot, directory, modName);
            if (!restore.Success)
                return Fail(
                    $"The previous {existing.SourceCommand} redirect through {existing.CarrierCommand} " +
                    $"could not be restored: {restore.Message}");
            restoredPreviousConversion = true;
            log.Information(
                "Restored the previous {SourceCommand} redirect through {CarrierCommand} for {ModName} " +
                "before redirecting {NewSourceCommand}.",
                existing.SourceCommand,
                existing.CarrierCommand,
                modName,
                sourceCommand);
        }

        if (sourceTimelines.Count == 0)
            return Fail(
                $"{sourceCommand} has no PAP-backed action timelines to convert.",
                restoredPreviousConversion);
        if (candidates.Count == 0)
            return Fail(
                "No compatible unlocked carrier emote is available for this animation.",
                restoredPreviousConversion);

        List<ManifestDocument> documents;
        try
        {
            documents = LoadManifests(modPath);
        }
        catch (Exception exception)
        {
            log.Warning(exception, "Could not read manifests for in-place conversion of {ModName}.", modName);
            return Fail("The mod's Penumbra manifests could not be read safely.", restoredPreviousConversion);
        }

        if (documents.Count == 0)
            return Fail("The mod has no readable top-level Penumbra manifests.", restoredPreviousConversion);

        string lastReason = "No compatible carrier could be mapped onto this mod.";
        foreach (var candidate in candidates)
        {
            if (!TryBuildPlan(modPath, documents, sourceTimelines, candidate, out var plan, out lastReason))
                continue;

            try
            {
                var marker = WritePlan(
                    modPath,
                    plan,
                    originalSyncKey,
                    originalFingerprint,
                    sourceEmoteId,
                    sourceCommand,
                    candidate);
                log.Information(
                    "Converted {ModName} in place: {SourceCommand} now plays through {CarrierCommand}; " +
                    "{FileCount} file(s) changed.",
                    modName,
                    sourceCommand,
                    candidate.Command,
                    marker.Files.Count);
                return new InPlaceConversionResult(
                    true,
                    previousSourceCommand is null
                        ? $"Converted permanently through {candidate.Command}."
                        : $"Moved the permanent redirect from {previousSourceCommand} to {sourceCommand} " +
                          $"through {candidate.Command}.",
                    candidate.EmoteId,
                    candidate.Command,
                    true);
            }
            catch (Exception exception)
            {
                log.Error(exception, "In-place emote conversion failed for {ModName}.", modName);
                return Fail(
                    $"The conversion was rolled back: {exception.GetBaseException().Message}",
                    restoredPreviousConversion);
            }
        }

        return Fail(lastReason, restoredPreviousConversion);
    }

    public InPlaceConversionResult Restore(string? modRoot, string directory, string modName)
    {
        if (!TryGetModPath(modRoot, directory, out var modPath) || !Directory.Exists(modPath))
            return Fail($"The installed folder for {modName} was not found.");
        if (!TryReadMarker(modPath, out var marker))
            return Fail($"{modName} does not have a Synastry in-place conversion.");

        var backupDirectory = SafeBackupDirectory(marker.BackupId);
        if (backupDirectory is null || !Directory.Exists(backupDirectory))
            return Fail("The conversion backup is missing, so Synastry will not overwrite the mod.");

        try
        {
            foreach (var file in marker.Files)
            {
                if (!TryResolveInside(modPath, file.Path, out var destination) ||
                    !TryResolveInside(backupDirectory, file.BackupFile, out var backup) ||
                    !File.Exists(backup))
                    throw new InvalidDataException($"Backup data for '{file.Path}' is missing or unsafe.");
                WriteAtomically(destination, File.ReadAllBytes(backup));
            }
            File.Delete(Path.Combine(modPath, MarkerFileName));
            Directory.Delete(backupDirectory, true);
            return new InPlaceConversionResult(true, $"Restored the original files for {modName}.", ChangedFiles: true);
        }
        catch (Exception exception)
        {
            log.Error(exception, "Could not restore the original files for {ModName}.", modName);
            return Fail($"The original files could not be restored: {exception.GetBaseException().Message}");
        }
    }

    private bool TryBuildPlan(
        string modPath,
        IReadOnlyList<ManifestDocument> documents,
        IReadOnlyList<EmoteConversionTimeline> sourceTimelines,
        EmoteConversionCandidate candidate,
        out ConversionPlan plan,
        out string reason)
    {
        plan = null!;
        reason = "";
        var targetBySlot = candidate.Timelines
            .Where(timeline => timeline.Key.Length > 0)
            .GroupBy(timeline => timeline.Slot)
            .ToDictionary(group => group.Key, group => group.First());
        var sourceByKey = sourceTimelines
            .Where(timeline => timeline.Key.Length > 0 && targetBySlot.ContainsKey(timeline.Slot))
            .OrderByDescending(timeline => timeline.Key.Length)
            .ToList();
        if (sourceByKey.Count == 0)
        {
            reason = $"{candidate.Command} does not expose the same animation slots as the selected emote.";
            return false;
        }

        var additions = new List<ManifestAddition>();
        var requirements = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var references = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
        {
            foreach (var files in EnumerateFilesObjects(document.Root))
            {
                var mappings = files.ToList();
                foreach (var mapping in mappings)
                {
                    if (mapping.Value is not JsonValue value ||
                        !value.TryGetValue<string>(out var localPath) ||
                        string.IsNullOrWhiteSpace(localPath))
                        continue;
                    if (IsCanonicalActionPath(mapping.Key) &&
                        TryResolveInside(modPath, localPath, out var referencedFile) &&
                        referencedFile.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!references.TryGetValue(referencedFile, out var gamePaths))
                            references[referencedFile] = gamePaths = [];
                        gamePaths.Add(mapping.Key);
                    }
                    if (!TryMatchSourcePath(mapping.Key, sourceByKey, out var sourceTimeline))
                        continue;
                    var targetTimeline = targetBySlot[sourceTimeline.Slot];
                    var targetPath = ReplaceTimelineSuffix(mapping.Key, sourceTimeline.Key, targetTimeline.Key);
                    if (targetPath is null)
                        continue;
                    if (files.TryGetPropertyValue(targetPath, out var occupied) &&
                        occupied is JsonValue occupiedValue &&
                        occupiedValue.TryGetValue<string>(out var occupiedPath) &&
                        !occupiedPath.Equals(localPath, StringComparison.OrdinalIgnoreCase))
                    {
                        reason = $"{candidate.Command} is already replaced by another file in this mod option.";
                        additions.Clear();
                        break;
                    }
                    if (!TryResolveInside(modPath, localPath, out var sourceFile) ||
                        !File.Exists(sourceFile) ||
                        !sourceFile.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = $"The manifest points to a missing or unsafe PAP file: {localPath}.";
                        additions.Clear();
                        break;
                    }
                    var requiredNames = ReadRequiredNames(targetPath);
                    if (requiredNames.Count == 0)
                    {
                        reason = $"The game data for carrier {candidate.Command} is missing at {targetPath}.";
                        additions.Clear();
                        break;
                    }
                    if (!requirements.TryGetValue(sourceFile, out var names))
                        requirements[sourceFile] = names = new HashSet<string>(StringComparer.Ordinal);
                    names.UnionWith(requiredNames);
                    additions.Add(new ManifestAddition(document, files, targetPath, localPath));
                }
                if (reason.Length > 0) break;
            }
            if (reason.Length > 0) break;
        }

        if (additions.Count == 0)
        {
            if (reason.Length == 0)
                reason =
                    "No PAP mapping for the selected emote was found in this mod. " +
                    "Choose the option that contains the animation, then try again.";
            return false;
        }

        foreach (var sourceFile in requirements.Keys)
        {
            var unrelated = references.GetValueOrDefault(sourceFile, [])
                .FirstOrDefault(gamePath => !TryMatchSourcePath(gamePath, sourceByKey, out _));
            if (unrelated is null) continue;
            reason =
                $"The PAP used for this emote is also shared by '{unrelated}'. " +
                "Synastry will not overwrite a shared file in this beta.";
            return false;
        }

        var papOutputs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (sourceFile, names) in requirements)
            {
                var sourceBytes = File.ReadAllBytes(sourceFile);
                papOutputs[sourceFile] = PapRetargeter.Retarget(
                    sourceBytes,
                    names.OrderBy(name => name, StringComparer.Ordinal).ToList(),
                    removeAnimationLock: true,
                    out _);
            }
        }
        catch (Exception exception)
        {
            reason = $"The existing PAP could not be retargeted safely: {exception.GetBaseException().Message}";
            return false;
        }

        plan = new ConversionPlan(documents, additions, papOutputs);
        return true;
    }

    private IReadOnlyList<string> ReadRequiredNames(string targetPath)
    {
        foreach (var path in VanillaFallbackPaths(targetPath))
        {
            try
            {
                if (dataManager.GetFile(path)?.Data is { Length: > 0 } bytes)
                {
                    var names = PapAnimationNames.Read(bytes);
                    if (names.Count > 0) return names;
                }
            }
            catch (Exception exception)
            {
                log.Debug(exception, "Could not read vanilla PAP names from {PapPath}.", path);
            }
        }
        return [];
    }

    private static IEnumerable<string> VanillaFallbackPaths(string targetPath)
    {
        yield return targetPath;
        var fallback = Regex.Replace(
            targetPath,
            @"(?<=chara/human/)c\d{4}(?=/animation/)",
            "c0101",
            RegexOptions.IgnoreCase);
        if (!fallback.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
            yield return fallback;
    }

    private ConversionMarker WritePlan(
        string modPath,
        ConversionPlan plan,
        string originalSyncKey,
        string originalFingerprint,
        uint sourceEmoteId,
        string sourceCommand,
        EmoteConversionCandidate candidate)
    {
        foreach (var addition in plan.Additions)
            addition.Files[addition.TargetPath] = addition.LocalPath;

        var outputs = new Dictionary<string, byte[]>(plan.PapOutputs, StringComparer.OrdinalIgnoreCase);
        foreach (var document in plan.Additions.Select(addition => addition.Document).Distinct())
            outputs[document.Path] = Encoding.UTF8.GetBytes(document.Root.ToJsonString(ManifestOptions) + Environment.NewLine);

        var backupId = Guid.NewGuid().ToString("N");
        var backupDirectory = SafeBackupDirectory(backupId) ??
                              throw new InvalidDataException("A safe backup directory could not be created.");
        Directory.CreateDirectory(backupDirectory);
        var records = new List<ConversionFileRecord>(outputs.Count);
        var index = 0;
        foreach (var (path, bytes) in outputs.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryMakeRelative(modPath, path, out var relative))
                throw new InvalidDataException($"'{path}' is outside the selected mod.");
            var backupFile = $"{index++:D4}.bin";
            var backupPath = Path.Combine(backupDirectory, backupFile);
            File.Copy(path, backupPath, false);
            records.Add(new ConversionFileRecord
            {
                Path = relative,
                BackupFile = backupFile,
                BeforeSha256 = HashFile(path),
                AfterSha256 = HashBytes(bytes)
            });
        }

        var marker = new ConversionMarker
        {
            SchemaVersion = SchemaVersion,
            OriginalSyncKey = originalSyncKey,
            OriginalFingerprint = originalFingerprint,
            SourceEmoteId = sourceEmoteId,
            SourceCommand = sourceCommand,
            CarrierEmoteId = candidate.EmoteId,
            CarrierCommand = candidate.Command,
            BackupId = backupId,
            Files = records
        };

        var written = new List<ConversionFileRecord>();
        try
        {
            foreach (var record in records)
            {
                if (!TryResolveInside(modPath, record.Path, out var destination))
                    throw new InvalidDataException($"Unsafe converted path '{record.Path}'.");
                WriteAtomically(destination, outputs[destination]);
                written.Add(record);
            }
            var markerBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(marker, MarkerOptions) + Environment.NewLine);
            WriteAtomically(Path.Combine(modPath, MarkerFileName), markerBytes);
            return marker;
        }
        catch
        {
            foreach (var record in written)
            {
                if (TryResolveInside(modPath, record.Path, out var destination) &&
                    TryResolveInside(backupDirectory, record.BackupFile, out var backup) &&
                    File.Exists(backup))
                {
                    try { WriteAtomically(destination, File.ReadAllBytes(backup)); }
                    catch { }
                }
            }
            try { Directory.Delete(backupDirectory, true); } catch { }
            throw;
        }
    }

    private List<ManifestDocument> LoadManifests(string modPath)
    {
        var documents = new List<ManifestDocument>();
        foreach (var path in Directory.EnumerateFiles(modPath, "*.json", SearchOption.TopDirectoryOnly)
                     .Where(path => !Path.GetFileName(path).Equals(MarkerFileName, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path), null, ManifestReadOptions);
                if (root is not null) documents.Add(new ManifestDocument(path, root));
            }
            catch (JsonException exception)
            {
                log.Debug(exception, "Ignoring unrelated or invalid JSON file {ManifestPath}.", path);
            }
        }
        return documents;
    }

    private static IEnumerable<JsonObject> EnumerateFilesObjects(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
            {
                if (pair.Key.Equals("Files", StringComparison.OrdinalIgnoreCase) && pair.Value is JsonObject files)
                    yield return files;
                else if (pair.Value is not null)
                    foreach (var nested in EnumerateFilesObjects(pair.Value))
                        yield return nested;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                if (child is not null)
                    foreach (var nested in EnumerateFilesObjects(child))
                        yield return nested;
        }
    }

    private static bool TryMatchSourcePath(
        string gamePath,
        IReadOnlyList<EmoteConversionTimeline> timelines,
        out EmoteConversionTimeline timeline)
    {
        var normalized = NormalizeGamePath(gamePath);
        if (!IsCanonicalActionPath(normalized))
        {
            timeline = null!;
            return false;
        }
        foreach (var candidate in timelines)
        {
            var key = NormalizeTimelineKey(candidate.Key);
            if (normalized.EndsWith("/" + key + ".pap", StringComparison.OrdinalIgnoreCase))
            {
                timeline = candidate with { Key = key };
                return true;
            }
        }
        timeline = null!;
        return false;
    }

    private static string? ReplaceTimelineSuffix(string gamePath, string sourceKey, string targetKey)
    {
        var normalized = NormalizeGamePath(gamePath);
        var sourceSuffix = "/" + NormalizeTimelineKey(sourceKey) + ".pap";
        if (!normalized.EndsWith(sourceSuffix, StringComparison.OrdinalIgnoreCase)) return null;
        return normalized[..^sourceSuffix.Length] + "/" + NormalizeTimelineKey(targetKey) + ".pap";
    }

    private static string NormalizeTimelineKey(string key) =>
        key.Replace('\\', '/').Trim().Trim('/').ToLowerInvariant().Replace(".pap", "", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeGamePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();

    private static bool IsCanonicalActionPath(string path) =>
        NormalizeGamePath(path).StartsWith("chara/", StringComparison.OrdinalIgnoreCase);

    private static bool MarkerFilesMatch(string modPath, ConversionMarker marker)
    {
        foreach (var file in marker.Files)
            if (!TryResolveInside(modPath, file.Path, out var path) ||
                !File.Exists(path) ||
                !HashFile(path).Equals(file.AfterSha256, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private static bool TryReadMarker(string modPath, out ConversionMarker marker)
    {
        marker = null!;
        try
        {
            var path = Path.Combine(modPath, MarkerFileName);
            if (!File.Exists(path)) return false;
            marker = JsonSerializer.Deserialize<ConversionMarker>(File.ReadAllText(path), MarkerOptions)!;
            return marker is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetModPath(string? modRoot, string directory, out string modPath)
    {
        modPath = "";
        if (string.IsNullOrWhiteSpace(modRoot) || string.IsNullOrWhiteSpace(directory)) return false;
        var root = Path.GetFullPath(modRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, directory));
        if (!IsInside(root, candidate)) return false;
        modPath = candidate;
        return true;
    }

    private string? SafeBackupDirectory(string backupId)
    {
        if (backupId.Length != 32 || !backupId.All(Uri.IsHexDigit)) return null;
        var root = Path.GetFullPath(backupRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, backupId));
        return IsInside(root, candidate) ? candidate : null;
    }

    private static bool TryResolveInside(string root, string relative, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
        var normalizedRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(normalizedRoot, candidate)) return false;
        fullPath = candidate;
        return true;
    }

    private static bool TryMakeRelative(string root, string path, out string relative)
    {
        relative = "";
        var normalizedRoot = Path.GetFullPath(root);
        var normalizedPath = Path.GetFullPath(path);
        if (!IsInside(normalizedRoot, normalizedPath)) return false;
        relative = Path.GetRelativePath(normalizedRoot, normalizedPath).Replace('\\', '/');
        return true;
    }

    private static bool IsInside(string root, string candidate)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFingerprint(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return System.Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string HashBytes(byte[] bytes) =>
        System.Convert.ToHexString(SHA256.HashData(bytes));

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        var temporary = path + ".synastry-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
        }
    }

    private static InPlaceConversionResult Fail(string message, bool changedFiles = false) =>
        new(false, message, ChangedFiles: changedFiles);

    private sealed record ManifestDocument(string Path, JsonNode Root);

    private sealed record ManifestAddition(
        ManifestDocument Document,
        JsonObject Files,
        string TargetPath,
        string LocalPath);

    private sealed record ConversionPlan(
        IReadOnlyList<ManifestDocument> Documents,
        IReadOnlyList<ManifestAddition> Additions,
        IReadOnlyDictionary<string, byte[]> PapOutputs);
}

internal sealed class ConversionMarker
{
    public int SchemaVersion { get; set; }
    public string OriginalSyncKey { get; set; } = "";
    public string OriginalFingerprint { get; set; } = "";
    public uint SourceEmoteId { get; set; }
    public string SourceCommand { get; set; } = "";
    public uint CarrierEmoteId { get; set; }
    public string CarrierCommand { get; set; } = "";
    public string BackupId { get; set; } = "";
    public List<ConversionFileRecord> Files { get; set; } = [];
}

internal sealed class ConversionFileRecord
{
    public string Path { get; set; } = "";
    public string BackupFile { get; set; } = "";
    public string BeforeSha256 { get; set; } = "";
    public string AfterSha256 { get; set; } = "";
}
