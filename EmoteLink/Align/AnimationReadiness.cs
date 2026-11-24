using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ActionTimeline = Lumina.Excel.Sheets.ActionTimeline;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace EmoteLink.Align;

/// <summary>
/// Tells when a character is in its animation's loop rather than the intro, and hasn't just been
/// redrawn. Bones measured before that are in the wrong place.
///
/// Ready means the base timeline isn't a start or end transition, and neither it nor the drawn model
/// has changed for a short while (longer for timelines not named as loops).
/// </summary>
/// <param name="changed">Called when a character's timeline or drawn model changed.</param>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit.</remarks>
internal sealed unsafe class AnimationReadiness(IDataManager data, Action changed)
{
    private const long LoopSettleMs = 600;
    private const long OtherSettleMs = 1500;

    private readonly Dictionary<ulong, (ushort Timeline, nint DrawObject, string Key, long Since)> seen = [];

    public bool IsReady(ICharacter character)
    {
        var native = (Character*)character.Address;
        if (native is null || native->DrawObject is null) return false;

        var timeline = native->Timeline.BaseOverride != 0
            ? native->Timeline.BaseOverride
            : native->Timeline.TimelineSequencer.TimelineIds[0];
        var drawObject = (nint)native->DrawObject;
        var now = Environment.TickCount64;

        if (!seen.TryGetValue(character.GameObjectId, out var last) || last.Timeline != timeline || last.DrawObject != drawObject)
        {
            var key = data.GetExcelSheet<ActionTimeline>()?.GetRowOrDefault(timeline)?.Key.ExtractText() ?? "";
            seen[character.GameObjectId] = (timeline, drawObject, key, now);
            changed();
            return false;
        }

        var name = last.Key[(last.Key.LastIndexOf('/') + 1)..];
        if (name.Contains("_start", StringComparison.OrdinalIgnoreCase) || name.Contains("_end", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("start", StringComparison.OrdinalIgnoreCase) || name.StartsWith("end", StringComparison.OrdinalIgnoreCase))
            return false;

        var settle = last.Key.EndsWith("_loop", StringComparison.OrdinalIgnoreCase) ? LoopSettleMs : OtherSettleMs;
        return now - last.Since >= settle;
    }

    /// <summary>Forgets everyone, so the next play starts settling afresh.</summary>
    public void Reset() => seen.Clear();
}
