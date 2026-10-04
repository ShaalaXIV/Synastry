using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace EmoteLink.TestPartner;

/// <summary>
/// Turns your summoned minion into a test partner, on your screen only: it is dressed from a Mare
/// character file (.mcdf) through its own temporary Penumbra collection and Glamourer, turned into a
/// full-size human, held on your spot facing your way, and can play a partner role. Line-up, contact
/// maps, bone bending and body measuring treat it as your partner. Releasing it, or the minion leaving,
/// puts everything back and deletes the unpacked files.
/// </summary>
internal sealed unsafe class TestPartnerService : IDisposable
{
    private const string Tag = "Synastry test partner";
    /// <summary>The Penumbra collection the player makes for the test partner.</summary>
    public const string CollectionName = "Synastry";
    // Glamourer.ApplyState flags: equipment and customization (body, face, hair); not "once", not locked.
    private const ulong GlamourerEquipmentAndCustomize = 2 | 4;

    private readonly IObjectTable objects;
    private readonly PenumbraService penumbra;
    private readonly IPluginLog log;
    private readonly IDataManager data;
    private readonly string workFolder;
    private readonly ICallGateSubscriber<object, int, uint, ulong, int> glamourerApply;
    private readonly ICallGateSubscriber<int, uint, ulong, int> glamourerRevert;

    private nint minion;
    private ushort minionIndex;
    private ulong minionId;
    private Guid collection;
    private string folder = "";
    private int originalModel;
    private FFXIVClientStructs.FFXIV.Client.Game.Character.CustomizeData originalCustomize;
    private readonly EquipmentModelId[] originalEquipment = new EquipmentModelId[10];
    private float originalScale = 1f;
    private ushort originalBaseOverride;
    private TemporaryAssignment? animation;
    private bool dressed;
    private long dressedAt;

    public TestPartnerService(IDalamudPluginInterface pluginInterface, IObjectTable objects, PenumbraService penumbra,
        IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.objects = objects;
        this.penumbra = penumbra;
        this.log = log;
        workFolder = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "test-partner");
        glamourerApply = pluginInterface.GetIpcSubscriber<object, int, uint, ulong, int>("Glamourer.ApplyState");
        glamourerRevert = pluginInterface.GetIpcSubscriber<int, uint, ulong, int>("Glamourer.RevertState");
        // Anything left from a crash or an unload while dressed.
        try { if (Directory.Exists(workFolder)) Directory.Delete(workFolder, true); } catch { /* best effort */ }
    }

    public bool Active => minion != 0;
    public bool Loading { get; private set; }
    public string Status { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string FileName { get; private set; } = "";
    public nint Address => minion;
    public ushort ObjectIndex => minionIndex;
    public ulong ObjectId => minionId;
    public bool Playing { get; private set; }
    /// <summary>Seconds since it was dressed: measuring waits until the models have loaded.</summary>
    public bool ReadyToMeasure => dressed && Environment.TickCount64 - dressedAt > 3000;

    /// <summary>Raised when the test partner goes away, so its measurements can be forgotten.</summary>
    public event Action<string>? Released;

    /// <summary>Dresses your summoned minion from <paramref name="mcdfPath"/>. Framework thread.</summary>
    public void Load(string mcdfPath)
    {
        if (Loading) return;
        if (objects.LocalPlayer is not { } local)
        {
            Status = "Log in first.";
            return;
        }
        var companion = ((Character*)local.Address)->ChildObject;
        if (companion is null)
        {
            Status = "Summon a minion first; Synastry turns it into your test partner.";
            return;
        }
        if (penumbra.FindCollection(CollectionName) is null)
        {
            Status = MissingCollection;
            return;
        }
        if (Active) Release();

        Loading = true;
        Status = $"Unpacking {Path.GetFileName(mcdfPath)}...";
        var target = Path.Combine(workFolder, Guid.NewGuid().ToString("N"));
        Task.Run(() => McdfFile.Extract(mcdfPath, target)).ContinueWith(task =>
        {
            Loading = false;
            if (!task.IsCompletedSuccessfully)
            {
                Status = "Couldn't read that file: " + task.Exception?.GetBaseException().Message;
                try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch { /* best effort */ }
                return;
            }
            pendingDress = (task.Result, target, Path.GetFileNameWithoutExtension(mcdfPath));
        }, TaskScheduler.Default);
    }

    private (McdfFile File, string Folder, string Name)? pendingDress;

    /// <summary>Each framework tick: finishes dressing, holds the partner in place, notices it leaving.</summary>
    public void Tick()
    {
        if (pendingDress is { } dress)
        {
            pendingDress = null;
            Dress(dress.File, dress.Folder, dress.Name);
        }
        if (!Active) return;

        var local = objects.LocalPlayer;
        var companion = local is null ? null : ((Character*)local.Address)->ChildObject;
        if (companion is null || (nint)companion != minion || companion->GetGameObjectId().ObjectId != (uint)minionId)
        {
            Status = "Your test partner left (the minion was dismissed).";
            Cleanup(revert: false);
            return;
        }

        // Stand on your spot, facing your way: couple animations share one origin.
        var character = (Character*)minion;
        var position = local!.Position;
        character->SetPosition(position.X, position.Y, position.Z);
        character->SetRotation(local.Rotation);
        if (MathF.Abs(character->Scale - 1f) > 0.001f) character->Scale = 1f;
        if (MathF.Abs(character->CharacterData.ModelScale - 1f) > 0.001f) character->CharacterData.ModelScale = 1f;
    }

    private void Dress(McdfFile file, string extracted, string name)
    {
        if (objects.LocalPlayer is not { } local) return;
        var companion = ((Character*)local.Address)->ChildObject;
        if (companion is null)
        {
            Status = "The minion was dismissed before it could be dressed.";
            try { Directory.Delete(extracted, true); } catch { /* best effort */ }
            return;
        }

        minion = (nint)companion;
        minionIndex = companion->ObjectIndex;
        minionId = companion->GetGameObjectId().ObjectId;
        folder = extracted;
        FileName = name;
        Name = $"{name} (test)"[..Math.Min(name.Length + 7, 32)];
        var character = (Character*)minion;
        originalModel = character->ModelContainer.ModelCharaId;
        originalScale = character->Scale;
        originalBaseOverride = character->Timeline.BaseOverride;

        // Its mods go into the player's own "Synastry" collection, given to the minion only. The
        // .mcdf's files join it as a temporary mod, which Penumbra never saves.
        if (penumbra.FindCollection(CollectionName) is not { } found)
        {
            Status = MissingCollection;
            Cleanup(revert: false);
            return;
        }
        collection = found;
        var redirects = new Dictionary<string, string>(file.Files, StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in file.Swaps) redirects.TryAdd(from, to);
        var added = penumbra.AddTemporaryMod(Tag, collection, redirects, file.ManipulationData, 99);
        var assigned = penumbra.SetCollectionForObject(minionIndex, collection);
        if (added is not (0 or 1) || assigned is not (0 or 1))
            log.Warning("Test partner: Penumbra returned mod {Added}, assignment {Assigned}.", added, assigned);

        // A human body, full size, wearing the file's look: its body description and gear models
        // (body mods follow the gear slots the character wore). Glamourer won't dress a minion, so
        // this is written directly; a file without a look falls back to your own body description.
        originalCustomize = character->DrawData.CustomizeData;
        for (var slot = 0; slot < 10; slot++) originalEquipment[slot] = character->DrawData.EquipmentModelIds[slot];
        var look = GlamourerState.Parse(file.GlamourerData, data);
        if (look is { HasCustomize: true })
        {
            var bytes = character->DrawData.CustomizeData.Data;
            for (var i = 0; i < 26; i++) bytes[i] = look.Customize[i];
        }
        else
        {
            character->DrawData.CustomizeData = ((Character*)local.Address)->DrawData.CustomizeData;
        }
        if (look is not null)
            for (var slot = 0; slot < 10; slot++)
                if (look.Equipment[slot] is { } model)
                {
                    var value = model;
                    character->DrawData.EquipmentModelIds[slot] = *(EquipmentModelId*)&value;
                }
        character->ModelContainer.ModelCharaId = 0;
        character->Scale = 1f;
        character->CharacterData.ModelScale = 1f;

        // The look, through Glamourer.
        var glamour = -1;
        if (file.GlamourerData.Length > 0)
        {
            try { glamour = glamourerApply.InvokeFunc(file.GlamourerData, minionIndex, 0, GlamourerEquipmentAndCustomize); }
            catch (Exception ex) { log.Warning(ex, "Test partner: Glamourer isn't available."); }
        }
        penumbra.Redraw(minionIndex);
        dressed = true;
        dressedAt = Environment.TickCount64;
        Status = glamour == 0 || glamour == 1
            ? $"{Name} is now {name}. Pick a role for it to play."
            : $"{Name} is now {name}, but Glamourer couldn't apply the look (code {glamour}); its mods are on.";
    }

    /// <summary>Turns the role's mod on for the test partner only and plays its animation.</summary>
    public bool Play(string directory, string modName, IReadOnlyDictionary<string, List<string>> selections, EmotePlayback playback)
    {
        if (!Active || collection == Guid.Empty) return false;
        if (animation is { } previous) penumbra.Remove(previous);
        var (success, error) = penumbra.Activate(collection, directory, modName, selections);
        if (!success)
        {
            Status = "Couldn't turn the animation on for the test partner: " + error;
            return false;
        }
        animation = new TemporaryAssignment(collection, directory, modName);
        StopAnimation();
        if (!ActionTimelinePlayback.Start(minion, playback, out var before))
        {
            Status = "The test partner couldn't start that animation.";
            return false;
        }
        originalBaseOverride = before;
        Playing = true;
        Status = $"{Name} is playing {modName}.";
        return true;
    }

    public void StopAnimation()
    {
        if (!Playing || !Active) return;
        ActionTimelinePlayback.Stop(minion, originalBaseOverride);
        Playing = false;
    }

    /// <summary>Puts the minion back the way it was and forgets everything about the test partner.</summary>
    public void Release()
    {
        if (!Active) return;
        Status = "Test partner released.";
        Cleanup(revert: true);
    }

    private void Cleanup(bool revert)
    {
        var name = Name;
        if (revert && minion != 0)
        {
            if (Playing) ((Character*)minion)->Timeline.BaseOverride = originalBaseOverride;
            Playing = false;
            try { glamourerRevert.InvokeFunc(minionIndex, 0, GlamourerEquipmentAndCustomize); } catch { /* Glamourer gone */ }
            var character = (Character*)minion;
            character->ModelContainer.ModelCharaId = originalModel;
            character->DrawData.CustomizeData = originalCustomize;
            for (var slot = 0; slot < 10; slot++) character->DrawData.EquipmentModelIds[slot] = originalEquipment[slot];
            character->Scale = originalScale;
            penumbra.Redraw(minionIndex);
        }
        if (collection != Guid.Empty)
        {
            if (animation is { } activated) penumbra.Remove(activated);
            penumbra.RemoveTemporaryMod(Tag, collection, 99);
            if (minionIndex != 0) penumbra.SetCollectionForObject(minionIndex, null);
        }
        animation = null;
        collection = Guid.Empty;
        if (folder.Length > 0)
        {
            var old = folder;
            _ = Task.Run(() => { try { Directory.Delete(old, true); } catch { /* best effort */ } });
        }
        folder = "";
        minion = 0;
        minionId = 0;
        dressed = false;
        Playing = false;
        Name = "";
        FileName = "";
        if (name.Length > 0) Released?.Invoke(name);
    }

    public const string MissingCollection =
        "Make a collection named \"Synastry\" in Penumbra first (Collections tab, then New). Leave it empty; " +
        "Synastry puts the character file's mods in it for your minion only, and nothing is saved.";

    /// <summary>Whether the player has made the collection yet, for the settings panel.</summary>
    public bool HasCollection() => penumbra.FindCollection(CollectionName) is not null;

    public void Dispose() => Cleanup(revert: true);
}
