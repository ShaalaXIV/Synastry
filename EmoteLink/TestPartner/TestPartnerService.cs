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
    // Glamourer.ApplyState flags: equipment and customization (body, face, hair); not "once", not locked.
    private const ulong GlamourerEquipmentAndCustomize = 2 | 4;

    private readonly IObjectTable objects;
    private readonly PenumbraService penumbra;
    private readonly IPluginLog log;
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
    private float originalScale = 1f;
    private ushort originalBaseOverride;
    private bool dressed;
    private long dressedAt;

    public TestPartnerService(IDalamudPluginInterface pluginInterface, IObjectTable objects, PenumbraService penumbra, IPluginLog log)
    {
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
        Name = companion->NameString is { Length: > 0 } minionName ? minionName : "Test partner";
        var character = (Character*)minion;
        originalModel = character->ModelContainer.ModelCharaId;
        originalScale = character->Scale;
        originalBaseOverride = character->Timeline.BaseOverride;

        // Its mods, in a collection of its own so nothing else changes.
        if (penumbra.CreateTemporaryCollection(Tag) is not { } created)
        {
            Status = "Penumbra couldn't make a collection for the test partner.";
            Cleanup(revert: false);
            return;
        }
        collection = created;
        var redirects = new Dictionary<string, string>(file.Files, StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in file.Swaps) redirects.TryAdd(from, to);
        var added = penumbra.AddTemporaryMod(Tag, collection, redirects, file.ManipulationData, 0);
        var assigned = penumbra.AssignTemporaryCollection(collection, minionIndex);
        if (assigned is not (0 or 1))
            log.Warning("Test partner: Penumbra assignment returned {Code} (mod {Added}).", assigned, added);

        // A human body, full size. Start from your own body description so the human model never
        // draws with an empty one if Glamourer won't dress a minion; the file's look goes on top.
        originalCustomize = character->DrawData.CustomizeData;
        character->DrawData.CustomizeData = ((Character*)local.Address)->DrawData.CustomizeData;
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
        var (success, error) = penumbra.Activate(collection, directory, modName, selections);
        if (!success)
        {
            Status = "Couldn't turn the animation on for the test partner: " + error;
            return false;
        }
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
            StopAnimation();
            try { glamourerRevert.InvokeFunc(minionIndex, 0, GlamourerEquipmentAndCustomize); } catch { /* Glamourer gone */ }
            var character = (Character*)minion;
            character->ModelContainer.ModelCharaId = originalModel;
            character->DrawData.CustomizeData = originalCustomize;
            character->Scale = originalScale;
            penumbra.Redraw(minionIndex);
        }
        if (collection != Guid.Empty) penumbra.DeleteTemporaryCollection(collection);
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

    public void Dispose() => Cleanup(revert: true);
}
