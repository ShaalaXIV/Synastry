using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace EmoteLink;

/// <summary>
/// Gets everyone's animation files onto everyone's machine before a room starts, so nothing pops
/// in late.
///
/// Sync plugins like PlayerSync only send files a character has actually loaded, and an emote's
/// animation only loads when it plays. So on Ready this player's animation is played for a couple
/// of frames, on this client only, which makes the sync plugin pick the file up and upload it while
/// the room is still choosing. Then, through Penumbra, each client checks whether the other ready
/// members' animation files have arrived for them, and tells the relay once they have. The relay
/// starts the room only when everyone says so. Nobody waits more than 20 seconds, and a partner no
/// sync plugin handles is never waited on.
/// </summary>
internal sealed unsafe class AnimationPreloader
{
    private const long CheckIntervalMs = 250;
    private const long MaxWaitMs = 20000;
    private const long ResendMs = 2000;
    private const int WarmUpFrames = 2;

    // Sync plugins built on Mare register this call under their own names.
    private static readonly string[] HandledAddressCalls =
    [
        "PlayerSync.GetHandledAddresses",
        "MareSynchronos.GetHandledAddresses",
        "LightlessSync.GetHandledAddresses",
        "Sphene.GetHandledAddresses",
    ];

    private readonly IObjectTable objects;
    private readonly PenumbraService penumbra;
    private readonly AnimationSyncService sync;
    private readonly IPluginLog log;
    private readonly List<ICallGateSubscriber<List<nint>>> handledAddresses;

    private int warmUpFramesLeft;
    private nint warmUpActor;
    private ushort warmUpOriginalBase;
    private long lastCheck;
    private string waitSignature = "";
    private long waitStarted;
    private long sentAt;
    private bool sent;

    public AnimationPreloader(
        IDalamudPluginInterface pluginInterface,
        IObjectTable objects,
        PenumbraService penumbra,
        AnimationSyncService sync,
        IPluginLog log)
    {
        this.objects = objects;
        this.penumbra = penumbra;
        this.sync = sync;
        this.log = log;
        handledAddresses = HandledAddressCalls
            .Select(name => pluginInterface.GetIpcSubscriber<List<nint>>(name))
            .ToList();
    }

    /// <summary>Room members whose animation files haven't reached this client yet.</summary>
    public IReadOnlyList<string> WaitingFor { get; private set; } = [];

    /// <summary>
    /// Plays the timeline for a couple of frames so its file loads, and returns the file's game path
    /// when a mod replaces it for this player (otherwise empty: nobody needs to wait for it).
    /// </summary>
    public string WarmUp(ushort timelineId, string timelineKey)
    {
        if (objects.LocalPlayer is not { } player || timelineId == 0 || timelineKey.Length == 0) return "";
        var character = (Character*)player.Address;
        if (character is null) return "";

        var path = AnimationPath(character, timelineKey);
        if (path.Length == 0) return "";
        var resolved = penumbra.ResolvePlayerPath(path);
        var modded = resolved is not null && !SamePath(resolved, path);

        // Only from plain standing: playing over a sit, emote or mount would break that state.
        if (modded && character->Mode == CharacterModes.Normal && warmUpFramesLeft == 0)
        {
            warmUpActor = player.Address;
            warmUpOriginalBase = character->Timeline.BaseOverride;
            if (ActionTimelinePlayback.Play(player.Address, timelineId)) warmUpFramesLeft = WarmUpFrames;
        }
        return modded ? path : "";
    }

    public void Tick()
    {
        if (warmUpFramesLeft > 0 && --warmUpFramesLeft == 0)
        {
            if (objects.LocalPlayer is { } player && player.Address == warmUpActor)
                ActionTimelinePlayback.Stop(warmUpActor, warmUpOriginalBase);
            warmUpActor = 0;
        }

        var now = Environment.TickCount64;
        if (now - lastCheck < CheckIntervalMs) return;
        lastCheck = now;
        CheckPartners(now);
    }

    private void CheckPartners(long now)
    {
        var room = sync.Room;
        var me = room?.Members.FirstOrDefault(member => sync.IsCurrentMember(member.ConnectionId));
        if (room is null || me is null || !sync.RelaySupportsPreload || !me.Ready || !me.WaitsForAssets || me.AssetsReady)
        {
            WaitingFor = [];
            sent = false;
            waitSignature = "";
            return;
        }

        var others = room.Members
            .Where(member => !sync.IsCurrentMember(member.ConnectionId) && member.Ready && member.AssetPath.Length > 0)
            .ToList();
        var signature = string.Join("|", others.Select(member => $"{member.ConnectionId}:{member.AssetPath}"));
        if (signature != waitSignature)
        {
            waitSignature = signature;
            waitStarted = now;
            sent = false;
        }
        if (sent && now - sentAt < ResendMs) return;

        var handled = HandledAddresses();
        var missing = new List<string>();
        foreach (var member in others)
        {
            var character = objects.OfType<IPlayerCharacter>()
                .FirstOrDefault(player => player.Name.TextValue.Equals(member.DisplayName, StringComparison.OrdinalIgnoreCase));
            // Out of sight, or no sync plugin is handling them: nothing on this client to wait for.
            if (character is null || !handled.Contains(character.Address)) continue;
            var resolved = penumbra.ResolveGameObjectPath(member.AssetPath, character.ObjectIndex);
            if (resolved is null || SamePath(resolved, member.AssetPath)) missing.Add(member.DisplayName);
        }
        WaitingFor = missing;

        var timedOut = now - waitStarted >= MaxWaitMs;
        if (missing.Count > 0 && !timedOut) return;
        if (timedOut && missing.Count > 0)
            log.Information("Starting without {Members}'s animation files after waiting {Seconds}s.",
                string.Join(", ", missing), MaxWaitMs / 1000);
        sent = true;
        sentAt = now;
        _ = sync.SetAssetsReadyAsync(true).ContinueWith(
            task => log.Warning(task.Exception, "Could not tell the room this player has everyone's files."),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private HashSet<nint> HandledAddresses()
    {
        var all = new HashSet<nint>();
        foreach (var call in handledAddresses)
        {
            try
            {
                foreach (var address in call.InvokeFunc() ?? []) all.Add(address);
            }
            catch
            {
                // That sync plugin isn't installed or isn't ready.
            }
        }
        return all;
    }

    /// <summary>The body animation path the game loads for this character's race.</summary>
    private static string AnimationPath(Character* character, string timelineKey)
    {
        if (character->DrawObject is null || character->DrawObject->GetObjectType() != ObjectType.CharacterBase) return "";
        var characterBase = (CharacterBase*)character->DrawObject;
        if (characterBase->GetModelType() != CharacterBase.ModelType.Human) return "";
        var race = ((Human*)characterBase)->RaceSexId;
        return race == 0 ? "" : $"chara/human/c{race:D4}/animation/a0001/bt_common/{timelineKey}.pap";
    }

    private static bool SamePath(string left, string right) =>
        left.Replace('\\', '/').Trim('/').Equals(right.Replace('\\', '/').Trim('/'), StringComparison.OrdinalIgnoreCase);
}
