using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EmoteLink.Relay;

internal static class UploadedAnimationPackageVerifier
{
    private const int MaximumTopLevelJsonFiles = 10_000;
    private const long MaximumJsonFileBytes = 64L * 1024 * 1024;
    private const long MaximumAggregateJsonBytes = 128L * 1024 * 1024;
    private const int MaximumPayloadBytes = 120 * 1024;
    private const byte MaximumPoseIndex = 6;
    private static readonly byte[] SignaturePrefix =
        Encoding.UTF8.GetBytes(AnimationCatalogStore.SupportedSignatureAlgorithm + "\0");
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static VerifiedUploadedAnimationArtifact? Inspect(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count > MaximumTopLevelJsonFiles)
            throw new InvalidDataException("The package has too many ZIP entries for safe catalog inspection.");
        var candidates = new List<(ZipArchiveEntry Entry, string Name)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        long declaredBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var fullName = entry.FullName;
            if (fullName.Contains('\\') || fullName.Contains('\0') || fullName.StartsWith('/') ||
                fullName.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
                throw new InvalidDataException("The package contains an unsafe ZIP entry name.");
            if (!entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                !fullName.Equals(entry.Name, StringComparison.Ordinal)) continue;
            if (entry.Length is < 0 or > MaximumJsonFileBytes ||
                declaredBytes + entry.Length > MaximumAggregateJsonBytes)
                throw new InvalidDataException("Top-level package manifests exceed the safe 128 MB limit.");
            var normalizedName = entry.Name.Normalize(NormalizationForm.FormC).ToLowerInvariant();
            if (!names.Add(normalizedName))
                throw new InvalidDataException("The package contains duplicate normalized manifest names.");
            candidates.Add((entry, normalizedName));
            declaredBytes += entry.Length;
            if (candidates.Count > MaximumTopLevelJsonFiles)
                throw new InvalidDataException("The package has too many top-level JSON manifests.");
        }
        if (candidates.Count == 0) return null;

        var files = new List<(string Name, byte[] Bytes)>(candidates.Count);
        long actualBytes = 0;
        foreach (var candidate in candidates.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            using var input = candidate.Entry.Open();
            using var output = new MemoryStream(candidate.Entry.Length > int.MaxValue
                ? throw new InvalidDataException("A manifest is too large.")
                : (int)candidate.Entry.Length);
            var buffer = new byte[128 * 1024];
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                actualBytes += read;
                if (output.Length + read > MaximumJsonFileBytes ||
                    actualBytes > MaximumAggregateJsonBytes)
                    throw new InvalidDataException("Expanded package manifests exceed the safe 128 MB limit.");
                output.Write(buffer, 0, read);
            }
            if (output.Length != candidate.Entry.Length)
                throw new InvalidDataException("A package manifest length did not match its ZIP metadata.");
            files.Add((candidate.Name, output.ToArray()));
        }

        using var signature = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        signature.AppendData(SignaturePrefix);
        Span<byte> nameLength = stackalloc byte[sizeof(int)];
        Span<byte> fileLength = stackalloc byte[sizeof(long)];
        foreach (var file in files)
        {
            var nameBytes = Encoding.UTF8.GetBytes(file.Name);
            BinaryPrimitives.WriteInt32BigEndian(nameLength, nameBytes.Length);
            signature.AppendData(nameLength);
            signature.AppendData(nameBytes);
            BinaryPrimitives.WriteInt64BigEndian(fileLength, file.Bytes.LongLength);
            signature.AppendData(fileLength);
            signature.AppendData(SHA256.HashData(file.Bytes));
        }

        var payload = Extract(files);
        if (payload.PapGamePaths.Count == 0) return null;
        var payloadJson = JsonSerializer.Serialize(payload, PayloadOptions);
        if (Encoding.UTF8.GetByteCount(payloadJson) > MaximumPayloadBytes) return null;
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        return new VerifiedUploadedAnimationArtifact(
            Convert.ToHexString(signature.GetHashAndReset()), files.Count, actualBytes, payloadJson, payloadHash);
    }

    private static VerifiedPayload Extract(IEnumerable<(string Name, byte[] Bytes)> files)
    {
        var papPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var optionGroups = new List<VerifiedOptionGroup>();
        var optionPoses = new List<VerifiedOptionPose>();
        var multiGroups = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                using var document = JsonDocument.Parse(file.Bytes);
                var root = document.RootElement;
                CollectPapPaths(root, papPaths);
                if (file.Name.Equals("default_mod.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Name.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
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
                // Match the client scanner: unrelated or partially-written JSON is ignored.
            }
        }

        return new VerifiedPayload
        {
            PapGamePaths = papPaths.Select(NormalizePath).ToList(),
            Poses = DetectPoses(papPaths),
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
            MultiSelectGroups = multiGroups.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void CollectPapPaths(JsonElement element, ISet<string> paths)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("Files") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var file in property.Value.EnumerateObject())
                        if (file.Name.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                            paths.Add(NormalizePath(file.Name));
                }
                else CollectPapPaths(property.Value, paths);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CollectPapPaths(item, paths);
    }

    private static void IndexOptionGroup(JsonElement group, ICollection<VerifiedOptionGroup> groups,
        ICollection<VerifiedOptionPose> optionPoses, IDictionary<string, bool> multiGroups)
    {
        if (group.ValueKind != JsonValueKind.Object ||
            !group.TryGetProperty("Name", out var nameElement) ||
            !group.TryGetProperty("Options", out var optionsElement) ||
            optionsElement.ValueKind != JsonValueKind.Array) return;
        var groupName = nameElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(groupName) || groupName.Length > 160) return;
        var isMulti = group.TryGetProperty("Type", out var typeElement) &&
                      string.Equals(typeElement.GetString(), "Multi", StringComparison.OrdinalIgnoreCase);
        multiGroups[groupName] = isMulti;
        var options = optionsElement.EnumerateArray()
            .Select(option => option.TryGetProperty("Name", out var name) ? name.GetString()?.Trim() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name) && name!.Length <= 160)
            .Select(name => name!).Distinct(StringComparer.OrdinalIgnoreCase).Take(10_000).ToList();
        groups.Add(new VerifiedOptionGroup(groupName, options, isMulti));
        foreach (var option in optionsElement.EnumerateArray())
        {
            if (!option.TryGetProperty("Name", out var optionNameElement) ||
                !option.TryGetProperty("Files", out var filesElement) ||
                filesElement.ValueKind != JsonValueKind.Object) continue;
            var optionName = optionNameElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(optionName) || optionName.Length > 160) continue;
            var pose = DetectPoses(filesElement.EnumerateObject().Select(property => property.Name), optionName)
                .FirstOrDefault();
            if (pose is not null) optionPoses.Add(new VerifiedOptionPose(
                groupName, optionName, pose.Kind, pose.Index));
        }
    }

    private static List<VerifiedPose> DetectPoses(IEnumerable<string> paths, string optionName = "")
    {
        var poses = new List<VerifiedPose>();
        foreach (var rawPath in paths.Where(path => path.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)))
        {
            var path = NormalizePath(rawPath);
            var candidates = new[]
            {
                (Kind: VerifiedPoseKind.GroundSit, Pattern: @"j_pose(\d+)"),
                (Kind: VerifiedPoseKind.Sit, Pattern: @"s_pose(\d+)"),
                (Kind: VerifiedPoseKind.Doze, Pattern: @"l_pose(\d+)"),
                (Kind: VerifiedPoseKind.Idle, Pattern: @"(?:^|[/_])pose(\d+)")
            };
            foreach (var candidate in candidates)
            {
                var match = Regex.Match(path, candidate.Pattern, RegexOptions.IgnoreCase);
                if (match.Success && byte.TryParse(match.Groups[1].Value, out var index) &&
                    index <= MaximumPoseIndex)
                {
                    AddPose(poses, new VerifiedPose(candidate.Kind, index));
                    break;
                }
            }
            if (path.Contains("/resident/idle.pap"))
                AddPose(poses, new VerifiedPose(VerifiedPoseKind.Idle, 0));
            VerifiedPoseKind? kind = path.Contains("/jmn/") ? VerifiedPoseKind.GroundSit
                : path.Contains("/sit/") ? VerifiedPoseKind.Sit
                : path.Contains("/doze/") ? VerifiedPoseKind.Doze
                : null;
            if (kind is null) continue;
            var labelIndex = Regex.Match(optionName, @"(\d+)(?!.*\d)");
            AddPose(poses, new VerifiedPose(kind.Value,
                labelIndex.Success && byte.TryParse(labelIndex.Value, out var parsed)
                    ? (byte)Math.Clamp((int)parsed, 0, MaximumPoseIndex)
                    : (byte)0));
        }
        return poses.OrderBy(pose => pose.Kind).ThenBy(pose => pose.Index).ToList();
    }

    private static void AddPose(ICollection<VerifiedPose> poses, VerifiedPose pose)
    {
        if (!poses.Contains(pose)) poses.Add(pose);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim().ToLowerInvariant();

    private sealed class VerifiedPayload
    {
        public int SchemaVersion { get; set; } = 1;
        public string ExtractorVersion { get; set; } = "synastry-extractor-v1";
        public List<string> PapGamePaths { get; set; } = [];
        public List<VerifiedOptionGroup> OptionGroups { get; set; } = [];
        public List<VerifiedOptionPose> OptionPoses { get; set; } = [];
        public List<VerifiedPose> Poses { get; set; } = [];
        public Dictionary<string, bool> MultiSelectGroups { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private enum VerifiedPoseKind { Idle, Sit, GroundSit, Doze }
    private sealed record VerifiedPose(VerifiedPoseKind Kind, byte Index);
    private sealed record VerifiedOptionPose(
        string Group, string Option, VerifiedPoseKind Kind, byte Index);
    private sealed record VerifiedOptionGroup(string Name, List<string> Options, bool IsMultiSelect);
}

internal sealed record VerifiedUploadedAnimationArtifact(
    string Signature,
    int ManifestFileCount,
    long ManifestBytes,
    string PayloadJson,
    string PayloadSha256);
