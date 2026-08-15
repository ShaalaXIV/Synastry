using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EmoteLink
{
    public sealed record PapAnimationInspection(
        int Index,
        string Name,
        int EmbeddedTmbBytes,
        int ActorCount,
        int TrackCount,
        int Actor0TrackCount,
        int EventCount,
        IReadOnlyList<string> AnimationOnlyNames,
        IReadOnlyList<string> Expressions,
        IReadOnlyDictionary<string, int> EventTypes);

    public sealed record PapInspection(
        string FilePath,
        long FileBytes,
        IReadOnlyList<PapAnimationInspection> Animations);

    internal static class CarrierPapPatcher
    {
        public static byte[] Patch(byte[] sourceBytes, byte[] carrierBytes)
        {
            var targetPap = PapDocument.Parse(sourceBytes);
            var templatePap = PapDocument.Parse(carrierBytes);
            if (targetPap.Animations.Count == 0)
                throw new InvalidDataException("The source PAP has no animations to patch.");
            if (templatePap.Animations.Count == 0)
                throw new InvalidDataException("The carrier PAP has no animations to copy from.");

            string templateAnimationName = templatePap.Animations[0].Name;
            if (string.IsNullOrWhiteSpace(templateAnimationName))
                throw new InvalidDataException("The carrier PAP's first animation has no name.");

            targetPap.Animations[0].Name = templateAnimationName;
            var targetTmb = TmbDocument.Parse(targetPap.Animations[0].TmbBytes);
            var templateTmb = TmbDocument.Parse(templatePap.Animations[0].TmbBytes);
            targetTmb.RetainPapAndExpressionTracks();
            targetTmb.RenamePapOnlyTracks(templateAnimationName);
            int copiedTracks = targetTmb.ImportCompatibleNonSoundTracksFromTemplate(templateTmb);
            targetTmb.RemoveSoundReferenceTracks();
            if (copiedTracks == 0)
                throw new InvalidDataException("The carrier PAP did not contain compatible timeline tracks.");

            targetPap.Animations[0].TmbBytes = targetTmb.ToBytes();
            return targetPap.ToBytes();
        }

        public const string BeesKneesBlendName = "cbem_dance16_2lp";

        private static readonly string[] RequiredTrackBundleFileNames =
        [
            "c009.tmbtrack",
            "Track3.tmbtrack",
            "Track4.tmbtrack",
            "Track5.tmbtrack",
            "Track6.tmbtrack",
            "Track7.tmbtrack",
            "Track8.tmbtrack",
            "Track9.tmbtrack"
        ];

        private static readonly string[] VfxTrackBundleFileNames = RequiredTrackBundleFileNames.Skip(1).ToArray();

        public static void PatchImportedBeesKneesPap(string papPath)
        {
            foreach (string bundleFileName in RequiredTrackBundleFileNames)
            {
                string bundlePath = GetTrackBundlePath(bundleFileName);
                if (!File.Exists(bundlePath))
                    throw new FileNotFoundException("Missing Bees Knees TMB track bundle.", bundlePath);
            }

            byte[] bytes = File.ReadAllBytes(papPath);
            var pap = PapDocument.Parse(bytes);
            if (pap.Animations.Count == 0)
                throw new InvalidDataException("The imported PAP has no animations to patch.");

            pap.Animations[0].Name = BeesKneesBlendName;
            var tmb = TmbDocument.Parse(pap.Animations[0].TmbBytes);
            tmb.RenamePapOnlyTracks(BeesKneesBlendName);
            tmb.RemoveSoundReferenceTracks();

            foreach (string bundleFileName in VfxTrackBundleFileNames)
            {
                string bundlePath = GetTrackBundlePath(bundleFileName);
                tmb.ImportTrackToActor0(File.ReadAllBytes(bundlePath));
            }

            tmb.RemoveSoundReferenceTracks();

            pap.Animations[0].TmbBytes = tmb.ToBytes();
            File.WriteAllBytes(papPath, pap.ToBytes());
        }

        public static void PatchImportedPapFromTemplate(string papPath, string templatePapPath)
        {
            if (!File.Exists(templatePapPath))
                throw new FileNotFoundException("Pick a template PAP first.", templatePapPath);

            byte[] targetBytes = File.ReadAllBytes(papPath);
            byte[] templateBytes = File.ReadAllBytes(templatePapPath);
            var targetPap = PapDocument.Parse(targetBytes);
            var templatePap = PapDocument.Parse(templateBytes);
            if (targetPap.Animations.Count == 0)
                throw new InvalidDataException("The imported PAP has no animations to patch.");
            if (templatePap.Animations.Count == 0)
                throw new InvalidDataException("The template PAP has no animations to copy from.");

            string templateAnimationName = templatePap.Animations[0].Name;
            if (string.IsNullOrWhiteSpace(templateAnimationName))
                throw new InvalidDataException("The template PAP's first animation has no name.");

            targetPap.Animations[0].Name = templateAnimationName;
            var targetTmb = TmbDocument.Parse(targetPap.Animations[0].TmbBytes);
            var templateTmb = TmbDocument.Parse(templatePap.Animations[0].TmbBytes);

            targetTmb.RetainPapAndExpressionTracks();
            targetTmb.RenamePapOnlyTracks(templateAnimationName);
            int copiedTracks = targetTmb.ImportCompatibleNonSoundTracksFromTemplate(templateTmb);
            targetTmb.RemoveSoundReferenceTracks();
            if (copiedTracks == 0)
                throw new InvalidDataException("The template PAP did not contain compatible non-sound TMB tracks to copy.");

            targetPap.Animations[0].TmbBytes = targetTmb.ToBytes();
            string temporaryPath = papPath + ".stage-writing";
            try
            {
                File.WriteAllBytes(temporaryPath, targetPap.ToBytes());
                File.Move(temporaryPath, papPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        public static bool ApplyExpression(string papPath, string expressionKey)
        {
            if (string.IsNullOrWhiteSpace(expressionKey) ||
                !expressionKey.StartsWith("cfxf_", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Choose a valid cfxf expression.", nameof(expressionKey));
            }

            byte[] originalBytes = File.ReadAllBytes(papPath);
            var pap = PapDocument.Parse(originalBytes);
            if (pap.Animations.Count == 0)
                throw new InvalidDataException("The PAP has no animations to receive an expression.");

            pap.Animations[0].TmbBytes = TmbDocument.ApplyExpressionC010(
                pap.Animations[0].TmbBytes,
                expressionKey.Trim(),
                out bool addedExpressionStructure);

            string backupPath = papPath + ".stage-original.bak";
            if (!File.Exists(backupPath))
                File.WriteAllBytes(backupPath, originalBytes);

            string temporaryPath = papPath + ".stage-writing";
            File.WriteAllBytes(temporaryPath, pap.ToBytes());
            File.Move(temporaryPath, papPath, true);
            return !addedExpressionStructure;
        }

        public static PapInspection InspectPap(string papPath)
        {
            if (!File.Exists(papPath))
                throw new FileNotFoundException("Could not find the PAP file.", papPath);

            byte[] bytes = File.ReadAllBytes(papPath);
            var pap = PapDocument.Parse(bytes);
            var animations = new List<PapAnimationInspection>();
            for (int index = 0; index < pap.Animations.Count; index++)
            {
                PapAnimation animation = pap.Animations[index];
                TmbDocument tmb = TmbDocument.Parse(animation.TmbBytes);
                animations.Add(tmb.Inspect(
                    index,
                    animation.Name,
                    animation.TmbBytes.Length));
            }

            return new PapInspection(papPath, bytes.LongLength, animations);
        }

        private static string GetTrackBundlePath(string fileName)
        {
            return Path.Combine(AppContext.BaseDirectory, "Assets", "TmbTrackBundles", "BeesKnees", fileName);
        }

        private sealed class PapDocument
        {
            private readonly byte[] _headerPrefix;
            private readonly byte[] _havokBytes;
            private readonly int _tmbMod;

            public List<PapAnimation> Animations { get; } = new();

            private PapDocument(byte[] headerPrefix, byte[] havokBytes, int tmbMod)
            {
                _headerPrefix = headerPrefix;
                _havokBytes = havokBytes;
                _tmbMod = tmbMod;
            }

            public static PapDocument Parse(byte[] bytes)
            {
                if (bytes.Length < 26 || Encoding.ASCII.GetString(bytes, 0, 4) != "pap ")
                    throw new InvalidDataException("This does not look like a PAP file.");

                short animationCount = BitConverter.ToInt16(bytes, 8);
                int infoOffset = BitConverter.ToInt32(bytes, 14);
                int havokOffset = BitConverter.ToInt32(bytes, 18);
                int tmbOffset = BitConverter.ToInt32(bytes, 22);
                if (animationCount < 0 || infoOffset < 0 || havokOffset < infoOffset || tmbOffset < havokOffset || tmbOffset > bytes.Length)
                    throw new InvalidDataException("The PAP header offsets are invalid.");

                byte[] headerPrefix = bytes.Take(26).ToArray();
                byte[] havokBytes = bytes.Skip(havokOffset).Take(tmbOffset - havokOffset).ToArray();
                var doc = new PapDocument(headerPrefix, havokBytes, tmbOffset % 4);

                for (int i = 0; i < animationCount; i++)
                {
                    int metadataOffset = infoOffset + (i * 40);
                    if (metadataOffset + 40 > bytes.Length)
                        throw new InvalidDataException("The PAP animation metadata table is truncated.");

                    byte[] metadata = bytes.Skip(metadataOffset).Take(40).ToArray();
                    doc.Animations.Add(new PapAnimation(metadata));
                }

                int pos = tmbOffset;
                for (int i = 0; i < doc.Animations.Count; i++)
                {
                    if (pos + 12 > bytes.Length || Encoding.ASCII.GetString(bytes, pos, 4) != "TMLB")
                        throw new InvalidDataException("Could not find the embedded TMB for the PAP animation.");

                    int size = BitConverter.ToInt32(bytes, pos + 4);
                    if (size <= 12 || pos + size > bytes.Length)
                        throw new InvalidDataException("The embedded TMB size is invalid.");

                    doc.Animations[i].TmbBytes = bytes.Skip(pos).Take(size).ToArray();
                    pos += size;
                    if (i < doc.Animations.Count - 1)
                    {
                        while (pos % 4 != doc._tmbMod && pos < bytes.Length)
                            pos++;
                    }
                }

                return doc;
            }

            public byte[] ToBytes()
            {
                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms, Encoding.ASCII);

                writer.Write(_headerPrefix);
                long infoOffsetPosition = 14;
                int infoOffset = (int)writer.BaseStream.Position;
                foreach (var animation in Animations)
                    writer.Write(animation.ToMetadataBytes());

                int havokOffset = (int)writer.BaseStream.Position;
                writer.Write(_havokBytes);
                PadToMod(writer, 4, _tmbMod);

                int tmbOffset = (int)writer.BaseStream.Position;
                for (int i = 0; i < Animations.Count; i++)
                {
                    writer.Write(Animations[i].TmbBytes);
                    if (i < Animations.Count - 1)
                        PadToMod(writer, 4, _tmbMod);
                }

                long end = writer.BaseStream.Position;
                writer.BaseStream.Position = infoOffsetPosition;
                writer.Write(infoOffset);
                writer.Write(havokOffset);
                writer.Write(tmbOffset);
                writer.BaseStream.Position = end;
                return ms.ToArray();
            }

            private static void PadToMod(BinaryWriter writer, int multiple, int mod)
            {
                while (writer.BaseStream.Position % multiple != mod)
                    writer.Write((byte)0);
            }
        }

        private sealed class PapAnimation
        {
            private readonly byte[] _metadata;

            public string Name
            {
                get => ReadPaddedAscii(_metadata, 0, 32);
                set => WritePaddedAscii(_metadata, 0, 32, value);
            }

            public byte[] TmbBytes { get; set; } = Array.Empty<byte>();

            public PapAnimation(byte[] metadata)
            {
                _metadata = metadata;
            }

            public byte[] ToMetadataBytes() => _metadata;
        }

        private sealed class TmbDocument
        {
            private readonly List<TmbItem> _headers = new();
            private readonly List<TmbItem> _actors = new();
            private readonly List<TmbItem> _tracks = new();
            private readonly List<TmbItem> _entries = new();
            private TmbItem? _actorList;

            private IEnumerable<TmbItem> ItemsForWrite => _headers
                .Concat(_actorList == null ? Array.Empty<TmbItem>() : new[] { _actorList })
                .Concat(_actors)
                .Concat(_tracks)
                .Concat(_entries);

            public static TmbDocument Parse(byte[] bytes)
            {
                if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "TMLB")
                    throw new InvalidDataException("This does not look like an embedded TMB.");

                int size = BitConverter.ToInt32(bytes, 4);
                int count = BitConverter.ToInt32(bytes, 8);
                if (size > bytes.Length)
                    throw new InvalidDataException("The embedded TMB is truncated.");

                var doc = new TmbDocument();
                var byId = new Dictionary<short, TmbItem>();
                int pos = 12;
                for (int i = 0; i < count; i++)
                {
                    var item = TmbItem.Parse(bytes, pos);
                    if (item.Magic == "TMDH" || item.Magic == "TMPP")
                        doc._headers.Add(item);
                    else if (item.Magic == "TMAL")
                        doc._actorList = item;
                    else if (item.Magic == "TMAC")
                        doc._actors.Add(item);
                    else if (item.Magic == "TMTR")
                        doc._tracks.Add(item);
                    else
                        doc._entries.Add(item);

                    if (item.Id.HasValue)
                        byId[item.Id.Value] = item;
                    pos += item.Size;
                }

                foreach (var item in doc.ItemsForWrite)
                    item.ResolveReferences(byId);

                return doc;
            }

            public PapAnimationInspection Inspect(
                int index,
                string animationName,
                int embeddedTmbBytes)
            {
                TmbItem? actor0 = GetActorsInTimelineOrder().FirstOrDefault();
                string[] animationOnlyNames = _entries
                    .Where(entry => entry.Magic == "C009")
                    .Select(entry => entry.GetStringField(20))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                string[] expressions = _entries
                    .Where(entry => entry.IsExpressionReference())
                    .Select(entry => entry.GetStringField(32))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var eventTypes = _entries
                    .GroupBy(entry => entry.Magic, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Count(),
                        StringComparer.OrdinalIgnoreCase);

                return new PapAnimationInspection(
                    index,
                    animationName,
                    embeddedTmbBytes,
                    _actors.Count,
                    _tracks.Count,
                    actor0?.References.Count(item => item.Magic == "TMTR") ?? 0,
                    _entries.Count,
                    animationOnlyNames,
                    expressions,
                    eventTypes);
            }

            public void ImportTrackToActor0(byte[] bundle)
            {
                var imported = ParseTrackBundle(bundle);
                if (_actors.Count == 0)
                    throw new InvalidDataException("The embedded TMB has no Actor 0 to receive imported tracks.");

                if (imported.Track.Magic != "TMTR")
                    throw new InvalidDataException("The imported TMB track bundle did not contain a TMTR track.");

                imported.Track.References.Clear();
                foreach (var entry in imported.Entries)
                {
                    if (entry.Magic == "C009")
                        entry.SetStringField(20, BeesKneesBlendName);
                    _entries.Add(entry);
                    imported.Track.References.Add(entry);
                }

                _tracks.Add(imported.Track);
                _actors[0].References.Add(imported.Track);
            }

            public int ImportCompatibleNonSoundTracksFromTemplate(TmbDocument template)
            {
                if (_actors.Count == 0)
                    throw new InvalidDataException("The embedded TMB has no Actor 0 to receive template tracks.");
                if (template._actors.Count == 0)
                    throw new InvalidDataException("The template TMB has no Actor 0 to copy from.");

                var templateSoundEntries = template._entries
                    .Where(entry => entry.IsSoundReference())
                    .ToHashSet();
                var templateTracks = template._actors[0].References
                    .Where(track => track.Magic == "TMTR")
                    .Where(track => !track.References.Any(templateSoundEntries.Contains))
                    .Where(track => !track.References.Any(entry => entry.Magic == "C009"))
                    .Where(track => !track.References.Any(entry => entry.IsExpressionReference()))
                    .ToList();

                int copied = 0;
                foreach (var templateTrack in templateTracks)
                {
                    var entryMap = new Dictionary<TmbItem, TmbItem>();
                    var copiedEntries = new List<TmbItem>();
                    foreach (var templateEntry in templateTrack.References)
                    {
                        if (templateEntry.IsSoundReference() ||
                            templateEntry.Magic == "C009" ||
                            templateEntry.IsExpressionReference())
                            continue;

                        var copiedEntry = templateEntry.CloneDetached();
                        entryMap[templateEntry] = copiedEntry;
                        copiedEntries.Add(copiedEntry);
                    }

                    if (copiedEntries.Count == 0)
                        continue;

                    var copiedTrack = templateTrack.CloneDetached();
                    copiedTrack.References.Clear();
                    foreach (var templateEntry in templateTrack.References)
                        if (entryMap.TryGetValue(templateEntry, out var copiedEntry))
                            copiedTrack.References.Add(copiedEntry);

                    _entries.AddRange(copiedEntries);
                    _tracks.Add(copiedTrack);
                    _actors[0].References.Add(copiedTrack);
                    copied++;
                }

                return copied;
            }

            public void RetainPapAndExpressionTracks()
            {
                var retainedTracks = _tracks
                    .Where(track => track.References.Any(entry =>
                        entry.Magic == "C009" || entry.IsExpressionReference()))
                    .ToHashSet();
                if (!retainedTracks.Any(track =>
                        track.References.Any(entry => entry.Magic == "C009")))
                {
                    throw new InvalidDataException(
                        "The embedded TMB does not contain an animation-only C009 track to preserve.");
                }

                foreach (var actor in _actors)
                {
                    actor.References.RemoveAll(item =>
                        item.Magic == "TMTR" && !retainedTracks.Contains(item));
                }

                _tracks.RemoveAll(track => !retainedTracks.Contains(track));

                var retainedEntries = retainedTracks
                    .SelectMany(track => track.References)
                    .ToHashSet();
                _entries.RemoveAll(entry => !retainedEntries.Contains(entry));
            }

            public void RenamePapOnlyTracks(string name)
            {
                bool renamedAny = false;
                foreach (var entry in _entries.Where(entry => entry.Magic == "C009"))
                {
                    entry.SetStringField(20, name);
                    renamedAny = true;
                }

                if (!renamedAny)
                    throw new InvalidDataException("The embedded TMB does not have an existing PAP-only C009 track to rename.");
            }

            public static byte[] ApplyExpressionC010(
                byte[] originalBytes,
                string expressionKey,
                out bool addedExpressionStructure)
            {
                var document = Parse(originalBytes);
                var orderedActors = document.GetActorsInTimelineOrder();
                if (orderedActors.Count == 0)
                    throw new InvalidDataException("The TMB has no Actor 0.");

                var actor0 = orderedActors[0];
                var expressionEntries = actor0.References
                    .Where(track => track.Magic == "TMTR")
                    .SelectMany(track => track.References)
                    .Where(entry =>
                        entry.Magic == "C010" &&
                        entry.GetStringField(32).StartsWith("cfxf_", StringComparison.OrdinalIgnoreCase))
                    .Distinct()
                    .ToList();

                if (expressionEntries.Count == 0)
                {
                    addedExpressionStructure = true;
                    return document.AddC010ExpressionTrackRaw(originalBytes, actor0, expressionKey);
                }

                addedExpressionStructure = false;

                byte[] encoded = Encoding.ASCII.GetBytes(expressionKey);
                using var output = new MemoryStream(originalBytes.Length + encoded.Length + 1);
                output.Write(originalBytes, 0, originalBytes.Length);
                int stringOffset = originalBytes.Length;
                output.Write(encoded, 0, encoded.Length);
                output.WriteByte(0);
                byte[] result = output.ToArray();

                foreach (var entry in expressionEntries)
                {
                    int relativeOffset = stringOffset - (entry.OriginalOffset + 8);
                    // Existing expression events may have animation-specific timing,
                    // duration, and frame controls. Preserve those values and replace
                    // only the expression path. The standard defaults are reserved for
                    // a newly-created C010 entry.
                    WriteInt32(result, entry.OriginalOffset + 32, relativeOffset);
                }

                WriteInt32(result, 4, result.Length);
                return result;
            }

            private List<TmbItem> GetActorsInTimelineOrder()
            {
                var ordered = _actorList?.References
                    .Where(item => item.Magic == "TMAC")
                    .ToList() ?? new List<TmbItem>();
                return ordered.Count > 0 ? ordered : _actors.ToList();
            }

            private byte[] AddC010ExpressionTrackRaw(
                byte[] originalBytes,
                TmbItem actor0,
                string expressionKey)
            {
                short nextId = GetNextAvailableId();
                short trackId = nextId++;
                short entryId = nextId;
                byte[] trackRecord = CreateRecord("TMTR", 24, trackId);
                byte[] entryRecord = CreateRecord("C010", 40, entryId);
                SetC010Settings(entryRecord, 0, 0);

                byte[] inserted = trackRecord.Concat(entryRecord).ToArray();
                byte[] result = InsertAfterTmlbHeader(originalBytes, inserted);
                int trackOffset = 12;
                int entryOffset = trackOffset + trackRecord.Length;
                int shiftedActorOffset = actor0.OriginalOffset + inserted.Length;

                var trackIds = actor0.ReferenceIds.Concat(new[] { trackId }).ToList();
                int actorTimeline = AppendIds(ref result, trackIds);
                SetReferences(result, shiftedActorOffset, 20, 24, actorTimeline, trackIds.Count);
                int trackTimeline = AppendIds(ref result, new[] { entryId });
                SetReferences(result, trackOffset, 12, 16, trackTimeline, 1);
                AppendAndPointString(ref result, entryOffset, 32, expressionKey);
                FinalizeRawTmb(result, 2);
                return result;
            }

            private static byte[] CreateRecord(string magic, int size, short id)
            {
                var record = new byte[size];
                Encoding.ASCII.GetBytes(magic).CopyTo(record, 0);
                WriteInt32(record, 4, size);
                WriteInt16(record, 8, id);
                return record;
            }

            private short GetNextAvailableId()
            {
                int maximum = ItemsForWrite
                    .Where(item => item.Id.HasValue)
                    .Select(item => (int)item.Id!.Value)
                    .DefaultIfEmpty(1)
                    .Max();
                if (maximum > short.MaxValue - 3)
                    throw new InvalidDataException("The TMB has no available timeline IDs.");
                return (short)(maximum + 1);
            }

            private static void SetC010Settings(byte[] bytes, int itemOffset, int pathRelativeOffset)
            {
                WriteInt32(bytes, itemOffset + 12, -1);
                WriteInt32(bytes, itemOffset + 16, 0);
                WriteInt32(bytes, itemOffset + 20, 1);
                WriteInt32(bytes, itemOffset + 24, BitConverter.SingleToInt32Bits(0f));
                WriteInt32(bytes, itemOffset + 28, BitConverter.SingleToInt32Bits(1f));
                WriteInt32(bytes, itemOffset + 32, pathRelativeOffset);
                WriteInt32(bytes, itemOffset + 36, 0);
            }

            private static byte[] InsertAfterTmlbHeader(byte[] originalBytes, byte[] records)
            {
                var result = new byte[originalBytes.Length + records.Length];
                Array.Copy(originalBytes, 0, result, 0, 12);
                Array.Copy(records, 0, result, 12, records.Length);
                Array.Copy(originalBytes, 12, result, 12 + records.Length, originalBytes.Length - 12);
                return result;
            }

            private static int AppendIds(ref byte[] bytes, IEnumerable<short> ids)
            {
                int offset = bytes.Length;
                byte[] encoded = ids.SelectMany(BitConverter.GetBytes).ToArray();
                Array.Resize(ref bytes, bytes.Length + encoded.Length);
                Array.Copy(encoded, 0, bytes, offset, encoded.Length);
                return offset;
            }

            private static void SetReferences(
                byte[] bytes,
                int itemOffset,
                int offsetField,
                int countField,
                int timelineOffset,
                int count)
            {
                WriteInt32(bytes, itemOffset + offsetField, timelineOffset - (itemOffset + 8));
                WriteInt32(bytes, itemOffset + countField, count);
            }

            private static void AppendAndPointString(
                ref byte[] bytes,
                int itemOffset,
                int fieldOffset,
                string value)
            {
                int offset = bytes.Length;
                byte[] encoded = Encoding.ASCII.GetBytes(value);
                Array.Resize(ref bytes, bytes.Length + encoded.Length + 1);
                Array.Copy(encoded, 0, bytes, offset, encoded.Length);
                bytes[^1] = 0;
                WriteInt32(bytes, itemOffset + fieldOffset, offset - (itemOffset + 8));
            }

            private static void FinalizeRawTmb(byte[] bytes, int addedItemCount)
            {
                WriteInt32(bytes, 4, bytes.Length);
                WriteInt32(bytes, 8, BitConverter.ToInt32(bytes, 8) + addedItemCount);
            }

            public void RemoveSoundReferenceTracks()
            {
                var soundEntries = _entries
                    .Where(entry => entry.IsSoundReference())
                    .ToHashSet();
                if (soundEntries.Count == 0)
                    return;

                var soundTracks = _tracks
                    .Where(track => track.References.Any(soundEntries.Contains))
                    .ToHashSet();
                if (soundTracks.Count == 0)
                    return;

                _tracks.RemoveAll(soundTracks.Contains);
                foreach (var actor in _actors)
                    actor.References.RemoveAll(soundTracks.Contains);

                var stillReferenced = _tracks
                    .SelectMany(track => track.References)
                    .ToHashSet();
                _entries.RemoveAll(entry => soundEntries.Contains(entry) && !stillReferenced.Contains(entry));
            }

            public byte[] ToBytes()
            {
                RefreshIds();
                var items = ItemsForWrite.ToList();
                int bodySize = items.Sum(item => item.Size);
                int extraSize = items.Sum(item => item.GetExtraSize());
                int timelineSize = items.Sum(item => item.References.Count * sizeof(short));
                var writer = new TmbPayloadWriter(bodySize, extraSize, timelineSize);

                foreach (var item in items)
                {
                    writer.StartPosition = writer.Position;
                    item.Write(writer);
                }

                using var ms = new MemoryStream();
                using var binary = new BinaryWriter(ms, Encoding.ASCII);
                binary.Write(Encoding.ASCII.GetBytes("TMLB"));
                binary.Write(0);
                binary.Write(items.Count);
                writer.WritePayload(binary);

                int finalSize = (int)ms.Length;
                ms.Position = 4;
                binary.Write(finalSize);
                return ms.ToArray();
            }

            private void RefreshIds()
            {
                short id = 2;
                foreach (var actor in _actors)
                    actor.Id = id++;
                foreach (var track in _tracks)
                    track.Id = id++;
                foreach (var entry in _entries)
                    entry.Id = id++;
            }

            private static (TmbItem Track, List<TmbItem> Entries) ParseTrackBundle(byte[] bundle)
            {
                if (bundle.Length < 12)
                    throw new InvalidDataException("The TMB track bundle is too small.");

                int count = BitConverter.ToInt32(bundle, 0);
                int pos = 4;
                TmbItem? track = null;
                var entries = new List<TmbItem>();
                var byId = new Dictionary<short, TmbItem>();
                for (int i = 0; i < count; i++)
                {
                    var item = TmbItem.Parse(bundle, pos);
                    if (item.Magic == "TMTR")
                        track = item;
                    else
                        entries.Add(item);
                    if (item.Id.HasValue)
                        byId[item.Id.Value] = item;
                    pos += item.Size;
                }

                if (track == null)
                    throw new InvalidDataException("The TMB track bundle did not contain a TMTR item.");

                track.ResolveReferences(byId);
                return (track, entries);
            }
        }

        private sealed class TmbItem
        {
            public string Magic { get; private set; } = "";
            public int Size { get; private set; }
            public short? Id { get; set; }
            public short? Time { get; private set; }
            public int OriginalOffset { get; private set; }
            public byte[] Data { get; private set; } = Array.Empty<byte>();
            public List<short> ReferenceIds { get; } = new();
            public List<TmbItem> References { get; } = new();

            private readonly Dictionary<int, string> _strings = new();
            private readonly Dictionary<int, byte[]> _extraBlocks = new();
            private bool _hasReferences;
            private int _referenceOffsetField;
            private int _referenceCountField;

            public static TmbItem Parse(byte[] bytes, int offset)
            {
                if (offset + 8 > bytes.Length)
                    throw new InvalidDataException("TMB item is truncated.");

                string magic = Encoding.ASCII.GetString(bytes, offset, 4);
                int size = BitConverter.ToInt32(bytes, offset + 4);
                if (size < 8 || offset + size > bytes.Length)
                    throw new InvalidDataException($"TMB item {magic} has an invalid size.");

                var item = new TmbItem
                {
                    Magic = magic,
                    Size = size,
                    OriginalOffset = offset,
                    Data = bytes.Skip(offset).Take(size).ToArray()
                };

                if (magic != "TMPP" && magic != "TMAL" && size >= 12)
                {
                    item.Id = BitConverter.ToInt16(item.Data, 8);
                    item.Time = BitConverter.ToInt16(item.Data, 10);
                }

                item.ReadKnownOffsets(bytes, offset);
                return item;
            }

            public int GetExtraSize() => _extraBlocks.Values.Sum(block => block.Length);

            public void ResolveReferences(Dictionary<short, TmbItem> byId)
            {
                References.Clear();
                foreach (short id in ReferenceIds)
                    if (byId.TryGetValue(id, out var item))
                        References.Add(item);
            }

            public TmbItem CloneDetached()
            {
                var clone = new TmbItem
                {
                    Magic = Magic,
                    Size = Size,
                    Id = Id,
                    Time = Time,
                    OriginalOffset = OriginalOffset,
                    Data = (byte[])Data.Clone(),
                    _hasReferences = _hasReferences,
                    _referenceOffsetField = _referenceOffsetField,
                    _referenceCountField = _referenceCountField
                };

                clone.ReferenceIds.AddRange(ReferenceIds);
                foreach (var pair in _strings)
                    clone._strings[pair.Key] = pair.Value;
                foreach (var pair in _extraBlocks)
                    clone._extraBlocks[pair.Key] = (byte[])pair.Value.Clone();
                return clone;
            }

            public void SetStringField(int fieldOffset, string value)
            {
                if (!_strings.ContainsKey(fieldOffset))
                    throw new InvalidDataException($"{Magic} does not have a string field at offset {fieldOffset}.");

                _strings[fieldOffset] = value;
            }

            public string GetStringField(int fieldOffset) =>
                _strings.TryGetValue(fieldOffset, out string? value) ? value : "";

            public bool IsSoundReference()
            {
                if (Magic != "C063")
                    return false;

                return _strings.Values.Any(value =>
                    value.StartsWith("sound/", StringComparison.OrdinalIgnoreCase) ||
                    value.EndsWith(".scd", StringComparison.OrdinalIgnoreCase));
            }

            public bool IsExpressionReference() =>
                Magic == "C010" &&
                GetStringField(32).StartsWith(
                    "cfxf_",
                    StringComparison.OrdinalIgnoreCase);

            public void Write(TmbPayloadWriter writer)
            {
                byte[] data = (byte[])Data.Clone();
                if (Id.HasValue)
                    WriteInt16(data, 8, Id.Value);
                if (Time.HasValue)
                    WriteInt16(data, 10, Time.Value);

                foreach (var (fieldOffset, block) in _extraBlocks.OrderBy(pair => pair.Key))
                    WriteInt32(data, fieldOffset, writer.WriteExtra(block));

                foreach (var (fieldOffset, value) in _strings.OrderBy(pair => pair.Key))
                    WriteInt32(data, fieldOffset, writer.WriteString(value));

                if (_hasReferences)
                {
                    WriteInt32(data, _referenceOffsetField, writer.WriteTimeline(References));
                    WriteInt32(data, _referenceCountField, References.Count);
                }

                writer.Write(data);
            }

            private void ReadKnownOffsets(byte[] fullBytes, int itemOffset)
            {
                switch (Magic)
                {
                    case "TMAL":
                        ReadReferenceTimeline(fullBytes, itemOffset, 8, 12);
                        break;
                    case "TMAC":
                        ReadReferenceTimeline(fullBytes, itemOffset, 20, 24);
                        break;
                    case "TMTR":
                        ReadReferenceTimeline(fullBytes, itemOffset, 12, 16);
                        int luaOffset = BitConverter.ToInt32(Data, 20);
                        if (luaOffset != 0)
                            throw new InvalidDataException("TMTR Lua condition tracks are not supported by the Bees Knees importer.");
                        break;
                    case "TMPP":
                        ReadString(fullBytes, itemOffset, 8);
                        break;
                    case "C009":
                        ReadString(fullBytes, itemOffset, 20);
                        break;
                    case "C010":
                        ReadString(fullBytes, itemOffset, 32);
                        break;
                    case "C012":
                        ReadString(fullBytes, itemOffset, 20);
                        ReadExtraBlock(fullBytes, itemOffset, 32, 36, 4);
                        ReadExtraBlock(fullBytes, itemOffset, 40, 44, 4);
                        ReadExtraBlock(fullBytes, itemOffset, 48, 52, 4);
                        ReadExtraBlock(fullBytes, itemOffset, 56, 60, 4);
                        break;
                    case "C063":
                        ReadString(fullBytes, itemOffset, 20);
                        break;
                    case "C173":
                        ReadString(fullBytes, itemOffset, 20);
                        break;
                    case "TMDH":
                    case "C042":
                        break;
                    default:
                        // Unknown timeline entries may drive required models, items, VFX, or
                        // other animation behavior. Preserve their payload instead of
                        // rejecting or stripping the PAP. Only explicitly identified sound
                        // reference tracks are removed by RemoveSoundReferenceTracks().
                        break;
                }
            }

            private void ReadReferenceTimeline(byte[] fullBytes, int itemOffset, int offsetField, int countField)
            {
                _hasReferences = true;
                _referenceOffsetField = offsetField;
                _referenceCountField = countField;
                int relativeOffset = BitConverter.ToInt32(Data, offsetField);
                int count = BitConverter.ToInt32(Data, countField);
                ReferenceIds.Clear();
                if (relativeOffset == 0 || count <= 0)
                    return;

                int absoluteOffset = itemOffset + 8 + relativeOffset;
                for (int i = 0; i < count; i++)
                    ReferenceIds.Add(BitConverter.ToInt16(fullBytes, absoluteOffset + (i * 2)));
            }

            private void ReadString(byte[] fullBytes, int itemOffset, int fieldOffset)
            {
                int relativeOffset = BitConverter.ToInt32(Data, fieldOffset);
                if (relativeOffset == 0)
                {
                    _strings[fieldOffset] = "";
                    return;
                }

                int absoluteOffset = itemOffset + 8 + relativeOffset;
                _strings[fieldOffset] = ReadNullTerminatedAscii(fullBytes, absoluteOffset);
            }

            private void ReadExtraBlock(byte[] fullBytes, int itemOffset, int offsetField, int countField, int bytesPerItem)
            {
                int relativeOffset = BitConverter.ToInt32(Data, offsetField);
                int count = BitConverter.ToInt32(Data, countField);
                if (relativeOffset == 0 || count <= 0)
                {
                    _extraBlocks[offsetField] = Array.Empty<byte>();
                    return;
                }

                int absoluteOffset = itemOffset + 8 + relativeOffset;
                _extraBlocks[offsetField] = fullBytes.Skip(absoluteOffset).Take(count * bytesPerItem).ToArray();
            }
        }

        private sealed class TmbPayloadWriter
        {
            private readonly int _bodySize;
            private readonly MemoryStream _body = new();
            private readonly MemoryStream _extra = new();
            private readonly MemoryStream _timeline = new();
            private readonly MemoryStream _strings = new();
            private readonly Dictionary<string, int> _writtenStrings = new(StringComparer.Ordinal);

            public int Position => (int)_body.Position;
            public int StartPosition { get; set; }

            public TmbPayloadWriter(int bodySize, int extraSize, int timelineSize)
            {
                _bodySize = bodySize;
                ExtraSize = extraSize;
                TimelineSize = timelineSize;
            }

            private int ExtraSize { get; }
            private int TimelineSize { get; }

            public void Write(byte[] data) => _body.Write(data, 0, data.Length);

            public int WriteExtra(byte[] data)
            {
                int actualOffset = (_bodySize - (StartPosition + 8)) + (int)_extra.Position;
                _extra.Write(data, 0, data.Length);
                return actualOffset;
            }

            public int WriteTimeline(List<TmbItem> items)
            {
                int actualOffset = (_bodySize - (StartPosition + 8)) + ExtraSize + (int)_timeline.Position;
                foreach (var item in items)
                    WriteInt16(_timeline, item.Id.GetValueOrDefault());
                return actualOffset;
            }

            public int WriteString(string value)
            {
                value ??= "";
                if (!_writtenStrings.TryGetValue(value, out int stringOffset))
                {
                    stringOffset = (int)_strings.Position;
                    byte[] encoded = Encoding.ASCII.GetBytes(value);
                    _strings.Write(encoded, 0, encoded.Length);
                    _strings.WriteByte(0);
                    _writtenStrings[value] = stringOffset;
                }

                return (_bodySize - (StartPosition + 8)) + ExtraSize + TimelineSize + stringOffset;
            }

            public void WritePayload(BinaryWriter writer)
            {
                writer.Write(_body.ToArray());
                writer.Write(_extra.ToArray());
                writer.Write(_timeline.ToArray());
                writer.Write(_strings.ToArray());
            }
        }

        private static string ReadPaddedAscii(byte[] bytes, int offset, int length)
        {
            int end = offset;
            while (end < offset + length && bytes[end] != 0)
                end++;
            return Encoding.ASCII.GetString(bytes, offset, end - offset);
        }

        private static void WritePaddedAscii(byte[] bytes, int offset, int length, string value)
        {
            Array.Clear(bytes, offset, length);
            byte[] encoded = Encoding.ASCII.GetBytes(value ?? "");
            Array.Copy(encoded, 0, bytes, offset, Math.Min(encoded.Length, length - 1));
        }

        private static string ReadNullTerminatedAscii(byte[] bytes, int offset)
        {
            int end = offset;
            while (end < bytes.Length && bytes[end] != 0)
                end++;
            return Encoding.ASCII.GetString(bytes, offset, end - offset);
        }

        private static void WriteInt16(byte[] bytes, int offset, short value)
        {
            byte[] encoded = BitConverter.GetBytes(value);
            Array.Copy(encoded, 0, bytes, offset, encoded.Length);
        }

        private static void WriteInt32(byte[] bytes, int offset, int value)
        {
            byte[] encoded = BitConverter.GetBytes(value);
            Array.Copy(encoded, 0, bytes, offset, encoded.Length);
        }

        private static void WriteInt16(Stream stream, short value)
        {
            byte[] encoded = BitConverter.GetBytes(value);
            stream.Write(encoded, 0, encoded.Length);
        }
    }
}
