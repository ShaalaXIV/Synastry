using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EmoteLink;

public sealed class SettingsWindow : Window
{
    private static readonly Vector4 AccentColor = new(0.88f, 0.62f, 0.18f, 1f);
    private readonly Plugin plugin;
    private string newReceiveFolder = "";
    private string receiveFolderStatus = "";
    private List<string> receiveFolders = [];

    public SettingsWindow(Plugin plugin) : base("Synastry Settings###SynastrySettings")
    {
        this.plugin = plugin;
        Size = new Vector2(560, 380);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(490, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Open()
    {
        RefreshReceiveFolders(true);
        IsOpen = true;
    }

    public override void Draw()
    {
        DrawSectionHeading("PLAYBACK");
        var automaticSync = plugin.AutomaticEmoteSyncEnabled;
        if (ImGui.Checkbox("Auto EmoteSync", ref automaticSync)) plugin.SetAutomaticEmoteSync(automaticSync);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Automatically run lobby-only EmoteSync six seconds after synchronized room playback starts.");

        var anywhere = plugin.SitDozeAnywhereEnabled;
        if (!plugin.SitDozeAnywhereAvailable) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Sit/doze anywhere", ref anywhere)) plugin.SetSitDozeAnywhere(anywhere);
        if (!plugin.SitDozeAnywhereAvailable) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(plugin.SitDozeAnywhereAvailable
                ? "Allow chair-sit and doze animations to start without nearby furniture."
                : "Unavailable because the required game hooks could not be initialized.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawSectionHeading("RECEIVED ANIMATIONS");
        ImGui.TextWrapped("Choose the Penumbra mod-list folder used to organize accepted Synastry animation transfers.");

        var selectedFolder = plugin.ReceivedModFolder;
        var preview = selectedFolder.Length == 0 ? "Top level (default)" : selectedFolder;
        ImGui.SetNextItemWidth(360f);
        if (ImGui.BeginCombo("Mod-list folder", preview))
        {
            if (ImGui.Selectable("Top level (default)", selectedFolder.Length == 0))
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
        if (ImGui.Button("Refresh folders")) RefreshReceiveFolders(true);

        if (ImGui.Button("Use dedicated Synastry folder")) SelectReceiveFolder("Synastry");

        ImGui.SetNextItemWidth(360f);
        ImGui.InputTextWithHint("##newReceiveFolder", "New Penumbra mod-list folder", ref newReceiveFolder, 160);
        ImGui.SameLine();
        if (ImGui.Button("Use new folder") && SelectReceiveFolder(newReceiveFolder))
        {
            newReceiveFolder = "";
            RefreshReceiveFolders();
        }
        ImGui.TextDisabled("A new folder appears in Penumbra after its first received mod is placed there.");
        if (receiveFolderStatus.Length > 0)
            ImGui.TextWrapped(receiveFolderStatus);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawSectionHeading("TOOLS");
        if (ImGui.Button("Community labels")) plugin.DownloadCommunityTags();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Download accepted community role labels. Your manually entered labels are preserved.");
        ImGui.SameLine();
        if (ImGui.Button("How To")) plugin.OpenHowTo();
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
        receiveFolderStatus = $"Selected {selected}. The next accepted Synastry transfer will be organized there.";
        RefreshReceiveFolders();
        return true;
    }

    private void RefreshReceiveFolders(bool announce = false)
    {
        var penumbraFolders = plugin.GetPenumbraModFolders();
        receiveFolders = penumbraFolders
            .Append(plugin.ReceivedModFolder)
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (announce)
            receiveFolderStatus = $"Loaded {penumbraFolders.Count:N0} mod-list folder(s) from Penumbra.";
    }

    private static void DrawSectionHeading(string label) => ImGui.TextColored(AccentColor, label);
}
