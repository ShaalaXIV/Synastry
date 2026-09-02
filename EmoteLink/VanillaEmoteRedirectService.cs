using Dalamud.Plugin.Services;
using NoireLib.Animations.PapFormat;
using System.Text;
using System.Text.Json;

namespace EmoteLink;

internal sealed record VanillaRedirectBuildResult(
    bool Success,
    string Message,
    uint CarrierEmoteId = 0,
    string CarrierCommand = "",
    bool CreatedMod = false);

/// <summary>
/// Owns the generated Penumbra mod used only for locked vanilla emote commands.
/// Normal Synastry animation mods are never written by this service.
/// </summary>
internal sealed class VanillaEmoteRedirectService
{
    public const string ModDirectory = "Synastry Redirect";
    public const string ModName = "Synastry Redirect";
    public const string MarkerFileName = "synastry-redirect.json";
    private const int SchemaVersion = 1;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public VanillaEmoteRedirectService(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public bool IsManagedMod(string? modRoot, string directory)
    {
        return directory.Equals(ModDirectory, StringComparison.OrdinalIgnoreCase) &&
               TryGetModPath(modRoot, out var modPath) &&
               File.Exists(Path.Combine(modPath, MarkerFileName));
    }

    public VanillaRedirectBuildResult Build(
        string? modRoot,
        EmoteConversionCandidate source,
        IReadOnlyList<EmoteConversionCandidate> candidates)
    {
        if (!TryGetModPath(modRoot, out var modPath))
            return Fail("Penumbra's mod folder could not be resolved safely.");

        var created = !Directory.Exists(modPath);
        if (!created && !File.Exists(Path.Combine(modPath, MarkerFileName)))
            return Fail(
                $"A Penumbra mod folder named '{ModDirectory}' already exists and is not managed by Synastry. " +
                "Rename that folder and try again.");

        try
        {
            Directory.CreateDirectory(modPath);
            WriteJson(Path.Combine(modPath, MarkerFileName), new RedirectMarker
            {
                SchemaVersion = SchemaVersion,
                SourceEmoteId = source.EmoteId,
                SourceCommand = source.Command
            });
            WriteJson(Path.Combine(modPath, "meta.json"), new
            {
                FileVersion = 3,
                Name = ModName,
                Author = "Synastry",
                Description =
                    "Managed carrier vessel for locked vanilla emote commands. " +
                    "Synastry rewrites and temporarily enables this mod as needed.",
                Version = "1.0",
                Website = "",
                Image = "",
                ModTags = new[] { "Synastry", "Managed" }
            });
        }
        catch (Exception exception)
        {
            log.Warning(exception, "Could not initialize the managed Synastry Redirect mod.");
            return Fail($"The Synastry Redirect mod could not be initialized: {exception.GetBaseException().Message}");
        }

        var reasons = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                if (!TryBuildCandidate(modPath, source, candidate, out var reason))
                {
                    reasons.Add($"{candidate.Command}: {reason}");
                    continue;
                }
                log.Information(
                    "Built Synastry Redirect for locked vanilla {SourceCommand} through {CarrierCommand}.",
                    source.Command,
                    candidate.Command);
                return new VanillaRedirectBuildResult(
                    true,
                    $"Built {source.Command} through {candidate.Command}.",
                    candidate.EmoteId,
                    candidate.Command,
                    created);
            }
            catch (Exception exception)
            {
                log.Warning(
                    exception,
                    "Could not build vanilla {SourceCommand} through candidate {CarrierCommand}.",
                    source.Command,
                    candidate.Command);
                reasons.Add($"{candidate.Command}: {exception.GetBaseException().Message}");
            }
        }

        return Fail(reasons.Count == 0
            ? $"No unlocked carrier candidate is compatible with {source.Command}."
            : $"No carrier could be built for {source.Command}. {string.Join(" | ", reasons.Take(3))}");
    }

    private bool TryBuildCandidate(
        string modPath,
        EmoteConversionCandidate source,
        EmoteConversionCandidate candidate,
        out string reason)
    {
        reason = "";
        var sourceBySlot = source.Timelines
            .Where(timeline => timeline.Key.Length > 0)
            .GroupBy(timeline => timeline.Slot)
            .ToDictionary(group => group.Key, group => group.First());
        var pairs = candidate.Timelines
            .Where(target => sourceBySlot.ContainsKey(target.Slot) && target.Key.Length > 0)
            .Select(target => (Source: sourceBySlot[target.Slot], Target: target))
            .ToList();
        if (pairs.Count == 0)
        {
            reason = "the source and carrier do not expose matching animation slots";
            return false;
        }

        var buildId = Guid.NewGuid().ToString("N");
        var relativeBuildRoot = $"files/{buildId}";
        var buildRoot = Path.Combine(modPath, "files", buildId);
        Directory.CreateDirectory(buildRoot);
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in Enumerable.Range(1, 18).Select(index => $"c{index:00}01"))
        {
            foreach (var pair in pairs)
            {
                var sourcePath = GamePath(model, pair.Source.Key);
                var targetPath = GamePath(model, pair.Target.Key);
                var sourceBytes = ReadGameFile(sourcePath);
                var targetBytes = ReadGameFile(targetPath);
                if (sourceBytes is null || targetBytes is null) continue;

                var requiredNames = PapAnimationNames.Read(targetBytes);
                if (requiredNames.Count == 0) continue;
                var output = PapRetargeter.Retarget(
                    sourceBytes,
                    requiredNames.OrderBy(name => name, StringComparer.Ordinal).ToList(),
                    removeAnimationLock: true,
                    out _);
                var fileName = $"{model}-slot{pair.Target.Slot}.pap";
                var relativePath = $"{relativeBuildRoot}/{fileName}";
                WriteAtomically(Path.Combine(buildRoot, fileName), output);
                mappings[targetPath] = relativePath;
            }
        }

        if (mappings.Count == 0)
        {
            TryDeleteBuildDirectory(buildRoot, modPath);
            reason = "the required vanilla PAP files were not available for a matching character skeleton";
            return false;
        }

        WriteJson(Path.Combine(modPath, "default_mod.json"), new
        {
            Version = 0,
            Files = mappings,
            FileSwaps = new Dictionary<string, string>(),
            Manipulations = Array.Empty<object>(),
            Name = "",
            Description = "",
            Priority = 0
        });
        WriteJson(Path.Combine(modPath, MarkerFileName), new RedirectMarker
        {
            SchemaVersion = SchemaVersion,
            SourceEmoteId = source.EmoteId,
            SourceCommand = source.Command,
            CarrierEmoteId = candidate.EmoteId,
            CarrierCommand = candidate.Command,
            BuildId = buildId
        });
        CleanupOldBuilds(modPath, buildId);
        return true;
    }

    private byte[]? ReadGameFile(string path)
    {
        try { return dataManager.GetFile(path)?.Data; }
        catch { return null; }
    }

    private static string GamePath(string model, string timelineKey) =>
        $"chara/human/{model}/animation/a0001/bt_common/{NormalizeTimelineKey(timelineKey)}.pap";

    private static string NormalizeTimelineKey(string key) =>
        key.Replace('\\', '/').Trim().Trim('/').ToLowerInvariant()
            .Replace(".pap", "", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetModPath(string? modRoot, out string modPath)
    {
        modPath = "";
        if (string.IsNullOrWhiteSpace(modRoot)) return false;
        try
        {
            var root = Path.GetFullPath(modRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(root, ModDirectory));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;
            modPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupOldBuilds(string modPath, string currentBuildId)
    {
        var filesRoot = Path.Combine(modPath, "files");
        if (!Directory.Exists(filesRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(filesRoot))
        {
            var name = Path.GetFileName(directory);
            if (name.Equals(currentBuildId, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(name, "N", out _))
                continue;
            TryDeleteBuildDirectory(directory, modPath);
        }
    }

    private static void TryDeleteBuildDirectory(string directory, string modPath)
    {
        try
        {
            var root = Path.GetFullPath(modPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(directory);
            if (candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(Path.GetFileName(candidate), "N", out _))
                Directory.Delete(candidate, true);
        }
        catch
        {
            // Stale managed build directories are harmless and can be retried later.
        }
    }

    private static void WriteJson(string path, object value) =>
        WriteAtomically(
            path,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine));

    private static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".synastry-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static VanillaRedirectBuildResult Fail(string message) => new(false, message);

    private sealed class RedirectMarker
    {
        public int SchemaVersion { get; set; }
        public uint SourceEmoteId { get; set; }
        public string SourceCommand { get; set; } = "";
        public uint CarrierEmoteId { get; set; }
        public string CarrierCommand { get; set; } = "";
        public string BuildId { get; set; } = "";
    }
}
