using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EmoteLink;

/// <summary>
/// Filesystem-only animation manifest inspection. Every method in this type is intended to run
/// on the refresh worker, never from Dalamud's framework callback.
/// </summary>
internal static class AnimationManifestScanner
{
    public const string SignatureAlgorithm = "synastry-manifest-v1";
    public const int PayloadSchemaVersion = 1;
    public const string ExtractorVersion = "synastry-extractor-v1";
    public const int MaximumPortablePayloadBytes = 120 * 1024;
    private const int MaximumTopLevelManifestFiles = 10_000;
    private const long MaximumManifestFileBytes = 64L * 1024 * 1024;
    private const long MaximumManifestBytes = 128L * 1024 * 1024;
    private static readonly byte[] SignaturePrefix = Encoding.UTF8.GetBytes(SignatureAlgorithm + "\0");
    private static readonly JsonSerializerOptions PayloadSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ManifestFileSet Inspect(string modPath, string modName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(modPath)) throw new DirectoryNotFoundException(modPath);
        var files = Directory.EnumerateFiles(modPath, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumTopLevelManifestFiles + 1)
            .ToArray();
        if (files.Length > MaximumTopLevelManifestFiles)
            throw new InvalidDataException(
                $"The mod has more than {MaximumTopLevelManifestFiles:N0} top-level JSON manifests.");
        cancellationToken.ThrowIfCancellationRequested();
        return new ManifestFileSet(
            modPath,
            modName,
            AnimationIndexCache.BuildSourceStamp(modName, files),
            files);
    }

    public static AnimationManifestSnapshot Capture(
        ManifestFileSet fileSet,
        CancellationToken cancellationToken)
    {
        using var signature = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        signature.AppendData(SignaturePrefix);
        var files = new List<AnimationManifestFile>(fileSet.Files.Count);
        long manifestBytes = 0;
        var length = new byte[sizeof(long)];
        var canonicalFiles = fileSet.Files
            .Select(file => (File: file,
                Name: file.Name.Normalize(NormalizationForm.FormC).ToLowerInvariant()))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var item in canonicalFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.File.Length > MaximumManifestFileBytes ||
                manifestBytes + item.File.Length > MaximumManifestBytes)
                throw new InvalidDataException(
                    "The mod's top-level JSON manifests exceed the safe 128 MB indexing limit.");
            var bytes = File.ReadAllBytes(item.File.FullName);
            if (bytes.LongLength > MaximumManifestFileBytes ||
                manifestBytes + bytes.LongLength > MaximumManifestBytes)
                throw new InvalidDataException(
                    "The mod's top-level JSON manifests changed while being read or exceed the safe indexing limit.");
            AppendLengthPrefixed(signature, Encoding.UTF8.GetBytes(item.Name));
            BinaryPrimitives.WriteInt64BigEndian(length, bytes.LongLength);
            signature.AppendData(length);
            signature.AppendData(SHA256.HashData(bytes));
            manifestBytes += bytes.LongLength;
            files.Add(new AnimationManifestFile(item.Name, bytes));
        }

        return new AnimationManifestSnapshot(
            Convert.ToHexString(signature.GetHashAndReset()),
            fileSet.SourceStamp,
            files,
            manifestBytes);
    }

    public static PortableAnimationIndexPayload Extract(AnimationManifestSnapshot snapshot)
    {
        var papGamePaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var optionPoses = new List<PortableOptionPose>();
        var optionGroups = new List<PortableOptionGroup>();
        var multiGroups = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshot.Files)
        {
            try
            {
                using var document = JsonDocument.Parse(file.Bytes);
                var root = document.RootElement;
                CollectPapGamePaths(root, papGamePaths);
                if (file.NormalizedName.Equals("default_mod.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.NormalizedName.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("Groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                        foreach (var group in groups.EnumerateArray())
                            IndexOptionGroup(group, optionGroups, optionPoses, multiGroups);
                    continue;
                }

                IndexOptionGroup(root, optionGroups, optionPoses, multiGroups);
            }
            catch (JsonException)
            {
                // Penumbra folders sometimes contain unrelated or partially-written JSON. The
                // legacy scanner ignored those files, so the portable extractor does as well.
            }
        }

        return new PortableAnimationIndexPayload
        {
            SchemaVersion = PayloadSchemaVersion,
            ExtractorVersion = ExtractorVersion,
            PapGamePaths = papGamePaths.Select(NormalizeGamePath).ToList(),
            Poses = DetectPoseTargets(papGamePaths),
            OptionGroups = optionGroups
                .GroupBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            OptionPoses = optionPoses
                .DistinctBy(pose => (pose.Group.ToUpperInvariant(), pose.Option.ToUpperInvariant()))
                .OrderBy(pose => pose.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(pose => pose.Option, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            MultiSelectGroups = multiGroups
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
        };
    }

    public static string SerializePayload(PortableAnimationIndexPayload payload) =>
        JsonSerializer.Serialize(payload, PayloadSerializerOptions);

    public static PortableAnimationPayloadSubmissionDto? CreateSubmission(
        PortableAnimationIndexPayload payload,
        out string json)
    {
        json = SerializePayload(payload);
        if (Encoding.UTF8.GetByteCount(json) > MaximumPortablePayloadBytes) return null;
        return new PortableAnimationPayloadSubmissionDto(PayloadSchemaVersion, ExtractorVersion, json);
    }

    public static bool TryReadRelayPayload(
        PortableAnimationPayloadDto payload,
        out PortableAnimationIndexPayload value)
    {
        value = null!;
        if (payload.SchemaVersion != PayloadSchemaVersion ||
            !payload.ExtractorVersion.Equals(ExtractorVersion, StringComparison.Ordinal) ||
            payload.VerificationReports < 1 ||
            Encoding.UTF8.GetByteCount(payload.Json) > MaximumPortablePayloadBytes ||
            !Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.Json)))
                .Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<PortableAnimationIndexPayload>(
                payload.Json, PayloadSerializerOptions);
            if (parsed is null || parsed.SchemaVersion != PayloadSchemaVersion ||
                !parsed.ExtractorVersion.Equals(ExtractorVersion, StringComparison.Ordinal) ||
                parsed.PapGamePaths.Count is 0 or > 50_000 ||
                parsed.PapGamePaths.Any(path => path.Length is 0 or > 512 ||
                    !path.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)) ||
                parsed.OptionGroups.Count > 2_000 ||
                parsed.OptionGroups.Any(group => group.Name.Length is 0 or > 160 ||
                    group.Options.Count > 10_000 ||
                    group.Options.Any(option => option.Length is 0 or > 160)) ||
                parsed.OptionPoses.Count > 10_000 ||
                parsed.OptionPoses.Any(pose => pose.Group.Length is 0 or > 160 ||
                    pose.Option.Length is 0 or > 160 || !Enum.IsDefined(pose.Kind)) ||
                parsed.Poses.Count > 1_000 || parsed.Poses.Any(pose => !Enum.IsDefined(pose.Kind)) ||
                parsed.MultiSelectGroups.Count > 2_000 ||
                parsed.MultiSelectGroups.Keys.Any(group => group.Length is 0 or > 160))
                return false;
            parsed.PapGamePaths = parsed.PapGamePaths
                .Select(NormalizeGamePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            value = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AppendLengthPrefixed(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static void CollectPapGamePaths(JsonElement element, ISet<string> paths)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("Files") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var file in property.Value.EnumerateObject())
                        if (file.Name.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                            paths.Add(NormalizeGamePath(file.Name));
                }
                else
                {
                    CollectPapGamePaths(property.Value, paths);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) CollectPapGamePaths(item, paths);
        }
    }

    private static void IndexOptionGroup(
        JsonElement group,
        ICollection<PortableOptionGroup> optionGroups,
        ICollection<PortableOptionPose> optionPoses,
        IDictionary<string, bool> multiGroups)
    {
        if (group.ValueKind != JsonValueKind.Object ||
            !group.TryGetProperty("Name", out var nameElement) ||
            !group.TryGetProperty("Options", out var optionsElement) ||
            optionsElement.ValueKind != JsonValueKind.Array) return;
        var groupName = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(groupName)) return;
        groupName = groupName.Trim();
        var isMulti = group.TryGetProperty("Type", out var typeElement) &&
            string.Equals(typeElement.GetString(), "Multi", StringComparison.OrdinalIgnoreCase);
        multiGroups[groupName] = isMulti;
        var optionNames = optionsElement.EnumerateArray()
            .Select(option => option.TryGetProperty("Name", out var optionName) ? optionName.GetString()?.Trim() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        optionGroups.Add(new PortableOptionGroup(groupName, optionNames, isMulti));
        foreach (var option in optionsElement.EnumerateArray())
        {
            if (!option.TryGetProperty("Name", out var optionNameElement) ||
                !option.TryGetProperty("Files", out var filesElement) ||
                filesElement.ValueKind != JsonValueKind.Object) continue;
            var optionName = optionNameElement.GetString();
            if (string.IsNullOrWhiteSpace(optionName)) continue;
            var pose = DetectPoseTargets(filesElement.EnumerateObject().Select(property => property.Name), optionName)
                .FirstOrDefault();
            if (pose is not null)
                optionPoses.Add(new PortableOptionPose(groupName, optionName.Trim(), pose.Kind, pose.Index));
        }
    }

    private static List<PoseTarget> DetectPoseTargets(IEnumerable<string> paths, string optionName = "")
    {
        var poses = new List<PoseTarget>();
        foreach (var rawPath in paths.Where(path => path.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)))
        {
            var path = NormalizeGamePath(rawPath);
            var candidates = new[]
            {
                (Kind: PoseKind.GroundSit, Pattern: @"j_pose(\d+)"),
                (Kind: PoseKind.Sit, Pattern: @"s_pose(\d+)"),
                (Kind: PoseKind.Doze, Pattern: @"l_pose(\d+)"),
                (Kind: PoseKind.Idle, Pattern: @"(?:^|[/_])pose(\d+)")
            };
            foreach (var candidate in candidates)
            {
                var match = Regex.Match(path, candidate.Pattern, RegexOptions.IgnoreCase);
                if (match.Success && byte.TryParse(match.Groups[1].Value, out var index) &&
                    index <= PoseService.MaxPoseIndex)
                {
                    var pose = new PoseTarget(candidate.Kind, index);
                    if (!poses.Contains(pose)) poses.Add(pose);
                    break;
                }
            }
            if (path.Contains("/resident/idle.pap") && !poses.Contains(new PoseTarget(PoseKind.Idle, 0)))
                poses.Add(new PoseTarget(PoseKind.Idle, 0));

            PoseKind? kind = path.Contains("/jmn/") ? PoseKind.GroundSit
                : path.Contains("/sit/") ? PoseKind.Sit
                : path.Contains("/doze/") ? PoseKind.Doze
                : null;
            if (kind is null) continue;
            var labelIndex = Regex.Match(optionName, @"(\d+)(?!.*\d)");
            var fallback = new PoseTarget(kind.Value,
                labelIndex.Success && byte.TryParse(labelIndex.Value, out var parsed)
                    ? PoseService.ClampIndex(parsed)
                    : (byte)0);
            if (!poses.Contains(fallback)) poses.Add(fallback);
        }
        return poses.OrderBy(pose => pose.Kind).ThenBy(pose => pose.Index).ToList();
    }

    private static string NormalizeGamePath(string path) =>
        path.Replace('\\', '/').Trim().ToLowerInvariant();
}

internal sealed record ManifestFileSet(
    string ModPath,
    string ModName,
    string SourceStamp,
    IReadOnlyList<FileInfo> Files);

internal sealed record AnimationManifestFile(string NormalizedName, byte[] Bytes);

internal sealed record AnimationManifestSnapshot(
    string Signature,
    string SourceStamp,
    IReadOnlyList<AnimationManifestFile> Files,
    long ManifestBytes)
{
    public int ManifestFileCount => Files.Count;
}

internal sealed class PortableAnimationIndexPayload
{
    public int SchemaVersion { get; set; } = AnimationManifestScanner.PayloadSchemaVersion;
    public string ExtractorVersion { get; set; } = AnimationManifestScanner.ExtractorVersion;
    public List<string> PapGamePaths { get; set; } = [];
    public List<PortableOptionGroup> OptionGroups { get; set; } = [];
    public List<PortableOptionPose> OptionPoses { get; set; } = [];
    public List<PoseTarget> Poses { get; set; } = [];
    public Dictionary<string, bool> MultiSelectGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed record PortableOptionPose(string Group, string Option, PoseKind Kind, byte Index);
internal sealed record PortableOptionGroup(string Name, List<string> Options, bool IsMultiSelect);
