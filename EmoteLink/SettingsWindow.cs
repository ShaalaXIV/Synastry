using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EmoteLink;

public sealed class SettingsWindow : Window
{
    private readonly Plugin plugin;
    private string newReceiveFolder = "";
    private string receiveFolderStatus = "";
    private List<string> receiveFolders = [];
    private string renameMesh = "";

    public SettingsWindow(Plugin plugin) : base("Settings###SynastrySettings")
    {
        this.plugin = plugin;
        Size = new Vector2(560, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Open()
    {
        IsOpen = true;
        RefreshReceiveFolders(true);
    }

    public override void PreDraw()
    {
        Theme.Push();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(24f, 18f) * Theme.Scale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        Theme.Pop();
    }

    public override void Draw()
    {
        var s = Theme.Scale;
        Theme.Heading("Playing");
        ImGui.Dummy(new Vector2(0, 2f * s));

        var automaticSync = plugin.AutomaticEmoteSyncEnabled;
        if (Theme.Toggle("##auto-sync", "Sync emotes six seconds after a room starts", ref automaticSync))
            plugin.SetAutomaticEmoteSync(automaticSync);


        var anywhere = plugin.SitDozeAnywhereEnabled;
        if (!plugin.SitDozeAnywhereAvailable) ImGui.BeginDisabled();
        if (Theme.Toggle("##anywhere", "Sit and doze anywhere, without furniture", ref anywhere))
            plugin.SetSitDozeAnywhere(anywhere);
        if (!plugin.SitDozeAnywhereAvailable) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !plugin.SitDozeAnywhereAvailable)
            ImGui.SetTooltip("Unavailable: its game hooks couldn't be set up.");

        // Indented under its parent and only usable while the parent is on.
        var dozeOnly = plugin.DozeAnywhereOnlyEnabled;
        var parentOn = anywhere && plugin.SitDozeAnywhereAvailable;
        ImGui.Indent(28f * s);
        if (!parentOn) ImGui.BeginDisabled();
        if (Theme.Toggle("##doze-only", "Doze only", ref dozeOnly))
            plugin.SetDozeAnywhereOnly(dozeOnly);
        if (!parentOn) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(parentOn
                ? "Doze plays in place anywhere; sitting still needs a real chair."
                : "Turn on sit and doze anywhere first.");
        ImGui.Unindent(28f * s);

        ImGui.Dummy(new Vector2(0, 6f * s));
        var convertedCount = plugin.ConvertedAnimationCount;
        if (convertedCount == 0) ImGui.BeginDisabled();
        if (Theme.Outline($"Restore converted animations ({convertedCount:N0})"))
            ImGui.OpenPopup("Restore originals###RestoreAllConvertedAnimations");
        if (convertedCount == 0) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(convertedCount == 0
                ? "Nothing has been converted to a carrier emote."
                : "Put back the creator's original files for every animation Synastry moved onto a carrier emote.");

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22f, 20f) * s);
        if (ImGui.BeginPopupModal("Restore originals###RestoreAllConvertedAnimations",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
        {
            Theme.Heading("Restore the originals?");
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 360f * s);
            Theme.Wrapped(
                $"{plugin.ConvertedAnimationCount:N0} animation(s) go back to their creator's files. " +
                "Folders, options, role names and commands stay as they are.", Theme.Soft);
            ImGui.PopTextWrapPos();
            ImGui.Dummy(new Vector2(0, 4f * s));
            if (Theme.Primary("Restore all"))
            {
                plugin.RestoreAllConvertedAnimations();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (Theme.Text("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();

        Divider();
        Theme.Heading("Animations people send you");
        Theme.Wrapped("The Penumbra folder they're filed under when you install them.", Theme.Soft);
        ImGui.Dummy(new Vector2(0, 2f * s));

        var selectedFolder = plugin.ReceivedModFolder;
        var preview = selectedFolder.Length == 0 ? "Top level" : selectedFolder;
        ImGui.SetNextItemWidth(320f * s);
        if (ImGui.BeginCombo("##receive-folder", preview))
        {
            if (ImGui.Selectable("Top level", selectedFolder.Length == 0))
                SelectReceiveFolder("");
            foreach (var folder in receiveFolders)
            {
                var selected = folder.Equals(selectedFolder, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(folder, selected)) SelectReceiveFolder(folder);
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (Theme.Text("Reload list")) RefreshReceiveFolders(true);
        if (Theme.Text("Use a “Synastry” folder")) SelectReceiveFolder("Synastry");

        ImGui.SetNextItemWidth(320f * s);
        ImGui.InputTextWithHint("##newReceiveFolder", "Or type a new folder name", ref newReceiveFolder, 160);
        ImGui.SameLine();
        if (Theme.Outline("Use it") && SelectReceiveFolder(newReceiveFolder))
        {
            newReceiveFolder = "";
            RefreshReceiveFolders();
        }
        Theme.Quiet("A new folder shows in Penumbra once something is filed in it.");
        if (receiveFolderStatus.Length > 0) Theme.Wrapped(receiveFolderStatus, Theme.Ash);

        Divider();
        Theme.Heading("Community");
        if (Theme.Outline("Download shared role names")) plugin.DownloadCommunityTags();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fetch role names other players agreed on. Names you typed yourself are kept.");
        ImGui.SameLine();
        if (Theme.Text("How it works")) plugin.OpenHowTo();

        Divider();
        DrawAdvanced();
    }

    /// <summary>Bending bones so couple animations meet, and the body setup that tells it where skin is.</summary>
    private void DrawAdvanced()
    {
        var s = Theme.Scale;
        Theme.Heading("Experimental");
        Theme.Wrapped("Still being tested, so everything here starts switched off. During a couple animation you " +
                      "started with Synastry, these can line you up with your partner and bend bones so you actually " +
                      "meet. Bones only turn at their joints, nothing stretches, and only someone in your room or your " +
                      "target is affected.", Theme.Ash);
        ImGui.Dummy(new Vector2(0, 4f * s));

        var lineUp = plugin.AutomaticLineUpEnabled;
        if (Theme.Toggle("##auto-line-up", "Line up couple animations automatically", ref lineUp))
            plugin.SetAutomaticLineUp(lineUp);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When a couple animation starts, Synastry lines up penis with mouth, vagina or anus\n" +
                             "through Simple Heels. Only the receiving partner moves.");
        if (!plugin.SimpleHeelsAvailable)
        {
            ImGui.SameLine();
            Theme.Quiet("needs Simple Heels");
        }


        if (!plugin.ContactIkAvailable)
        {
            Theme.Wrapped(plugin.ContactIkStatus, Theme.AzureText);
            ImGui.BeginDisabled();
        }
        var shaft = plugin.BendShaftEnabled;
        var openings = plugin.BendOpeningsEnabled;
        var hands = plugin.BendHandsEnabled;
        var changed = Theme.Toggle("##bend-shaft", "Aim the penis at your partner", ref shaft);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The shaft bends at its joints to point through the mouth, vagina or anus it's\n" +
                             "lined up with, keeping its depth. Bigger gaps still use Line up.");
        changed |= Theme.Toggle("##bend-openings", "Open the mouth, vagina or anus to fit", ref openings);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When the tip arrives, the opening widens a little for its thickness, within what the\n" +
                             "body's own bones allow. Very large sizes will still clip.");
        changed |= Theme.Toggle("##bend-hands", "Rest hands on your partner and grip", ref hands);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A hand hovering near your partner's thigh, hip, waist, chest or arm settles onto the skin,\n" +
                             "turns its palm to it and closes its fingers.");
        if (changed) plugin.SetBendOptions(shaft, openings, hands);
        if (!plugin.ContactIkAvailable) ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 8f * s));
        Theme.Label("Your body");
        var profile = plugin.OwnBodyProfile;
        var mesh = plugin.ActiveBodyMeshName;
        Theme.Wrapped(profile is null
            ? "Not set up yet. Setup reads your skeleton and the body models Penumbra has loaded for you, " +
              "measures where your skin is, and shares it with your room so hands land on you and openings are found."
            : $"{mesh} mesh: {Plugin.DescribeBody(profile)}. Measure again after changing body mods. " +
              "Save other bodies (Male, Female, Futa…) from the Current mesh list in the room.", Theme.Soft);
        if (plugin.BodySetupRunning) ImGui.BeginDisabled();
        if (Theme.Primary(plugin.BodySetupRunning ? "Measuring..." : profile is null ? "Set up my body" : $"Measure {mesh} again"))
            plugin.SetUpBody();
        if (plugin.BodySetupRunning) ImGui.EndDisabled();
        if (profile is not null)
        {
            ImGui.SameLine();
            if (Theme.Text("Rename", Theme.Ash))
            {
                renameMesh = mesh;
                ImGui.OpenPopup("Rename mesh###SettingsRenameMesh");
            }
        }
        if (profile is not null && plugin.BodyMeshNames.Count > 1)
        {
            ImGui.SameLine();
            if (Theme.Text($"Delete {mesh}", Theme.Ash)) plugin.DeleteBodyMesh(mesh);
        }
        if (ImGui.BeginPopup("Rename mesh###SettingsRenameMesh"))
        {
            Theme.Label($"Rename {mesh}");
            ImGui.SetNextItemWidth(220f * s);
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
            var submit = ImGui.InputTextWithHint("##settings-rename-mesh", "Male, Female, Futa…", ref renameMesh, 24,
                ImGuiInputTextFlags.EnterReturnsTrue);
            if ((Theme.Primary("Rename") || submit) && renameMesh.Trim().Length > 0 && plugin.RenameBodyMesh(mesh, renameMesh))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (profile is null) return;
        ImGui.Dummy(new Vector2(0, 6f * s));
        var gap = profile.Tuning.ContactGap * 100f;
        var grip = profile.Tuning.GripStrength * 100f;
        var opening = profile.Tuning.OpeningAmount * 100f;
        ImGui.SetNextItemWidth(260f * s);
        var tuned = ImGui.SliderFloat("Contact gap (cm-ish)", ref gap, -5f, 5f, "%.1f");
        var released = ImGui.IsItemDeactivatedAfterEdit();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Space left between touching skin. Negative presses in a little.");
        ImGui.SetNextItemWidth(260f * s);
        tuned |= ImGui.SliderFloat("Grip", ref grip, 0f, 150f, "%.0f%%");
        released |= ImGui.IsItemDeactivatedAfterEdit();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("How far your fingers close when your hand rests on someone.");
        ImGui.SetNextItemWidth(260f * s);
        tuned |= ImGui.SliderFloat("Opening", ref opening, 0f, 150f, "%.0f%%");
        released |= ImGui.IsItemDeactivatedAfterEdit();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("How much your openings may widen, compared with what was measured.");
        if (tuned) plugin.SetBodyTuning(gap / 100f, grip / 100f, opening / 100f);
        if (released) plugin.ShareBodyProfile();
    }

    private static void Divider()
    {
        var s = Theme.Scale;
        ImGui.Dummy(new Vector2(0, 10f * s));
        Theme.HorizontalLine(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos(), ImGui.GetContentRegionAvail().X);
        ImGui.Dummy(new Vector2(0, 10f * s));
    }

    private bool SelectReceiveFolder(string folder)
    {
        if (!plugin.SetReceivedModFolder(folder))
        {
            receiveFolderStatus = plugin.Status;
            return false;
        }

        var selected = plugin.ReceivedModFolder.Length == 0
            ? "the top level"
            : $"“{plugin.ReceivedModFolder}”";
        receiveFolderStatus = $"Filing new animations under {selected}.";
        RefreshReceiveFolders();
        return true;
    }

    private void RefreshReceiveFolders(bool announce = false)
    {
        IReadOnlyList<string> penumbraFolders;
        try
        {
            penumbraFolders = plugin.GetPenumbraModFolders();
        }
        catch (Exception ex)
        {
            // Settings must stay usable even when Penumbra can't list its folders right now.
            penumbraFolders = [];
            receiveFolderStatus = "Couldn't read Penumbra's folders: " + ex.GetBaseException().Message;
            announce = false;
        }
        receiveFolders = penumbraFolders
            .Append(plugin.ReceivedModFolder)
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (announce)
            receiveFolderStatus = $"Found {penumbraFolders.Count:N0} folder(s) in Penumbra.";
    }
}
