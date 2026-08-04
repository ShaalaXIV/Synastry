using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace EmoteLink;

public sealed class HowToWindow : Window
{
    private readonly ISharedImmediateTexture[] screenshots;
    private int page;

    private static readonly string[] Titles =
    [
        "1. Start with Penumbra",
        "2. Connect to group play",
        "3. Create or join a room",
        "4. Use the room and color guide",
        "5. Find and ready an animation",
        "6. Edit and claim actor roles",
        "7. Community labels and private mods",
        "8. Share a mod safely"
    ];

    public HowToWindow(ITextureProvider textures) : base("EmoteLink How To###EmoteLinkHowTo")
    {
        Size = new Vector2(900, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
        screenshots =
        [
            Load(textures, "01-front-page.png"),
            Load(textures, "02-connected.png"),
            Load(textures, "03-created-room.png"),
            Load(textures, "04-mod-list.png"),
            Load(textures, "05-role-labels.png"),
            Load(textures, "06-private-mod.png")
        ];
    }

    public override void Draw()
    {
        ImGui.TextUnformatted(Titles[page]);
        ImGui.Separator();

        if (ImGui.BeginChild("guide-content", new Vector2(0, -42), false))
        {
            DrawPage(page);
            ImGui.EndChild();
        }

        if (ImGui.Button("Back", new Vector2(90, 0)) && page > 0) page--;
        ImGui.SameLine();
        ImGui.TextDisabled($"{page + 1} / {Titles.Length}");
        ImGui.SameLine(ImGui.GetWindowWidth() - 110);
        if (page == Titles.Length - 1)
        {
            if (ImGui.Button("Finish", new Vector2(90, 0))) IsOpen = false;
        }
        else if (ImGui.Button("Next", new Vector2(90, 0))) page++;
    }

    private void DrawPage(int index)
    {
        switch (index)
        {
            case 0:
                Paragraph("EmoteLink launches Penumbra animation mods temporarily. Follow the numbered highlights from top to bottom.");
                Steps(
                    "Confirm Penumbra is connected; Refresh after installing or changing mods.",
                    "Align to your current target, or clear EmoteLink's temporary animation assignments.",
                    "Connect to the group-play relay.",
                    "Create folders or search your Penumbra animation library.",
                    "Expand folders to browse organized mods.");
                Image(0);
                break;
            case 1:
                Paragraph("Connecting to the relay and joining a room are two separate steps.");
                Steps(
                    "Check that the relay says Connected.",
                    "Create a new room, or enter a friend's code and press Join. Disconnect leaves the relay.",
                    "Search or organize mods while connected.",
                    "Your folders remain available in group play.");
                Image(1);
                break;
            case 2:
                Paragraph("After creating a room, click the room-code button to copy it and give that code to the other players. They connect, enter it, and press Join.");
                Steps(
                    "Click the room code to copy it. Leave exits the room; Cancel ready clears your choice; Force start is host-only.",
                    "Every room member shows as Waiting or Ready.",
                    "The legend explains mod availability and remote suggestions.",
                    "Normal playback starts when everyone readies the same mod.",
                    "Search and folder controls work normally inside a room.",
                    "Browse a folder to choose an animation.");
                Image(2);
                break;
            case 3:
                Paragraph("The room screen tells you who is ready and which mods the group can use together.");
                Steps(
                    "Use the room code and host controls.",
                    "Read the color guide: green everyone, orange some, purple suggested, cyan private, white no match.",
                    "Search or make folders without leaving the room.",
                    "Expand a folder to browse its mods.",
                    "Ready prepares group playback; Send offers the mod to another member.");
                Image(3);
                break;
            case 4:
                Paragraph("Large libraries stay manageable because folders collapse hundreds of choices into short lists.");
                Steps(
                    "Room controls remain at the top.",
                    "Check the availability colors before choosing.",
                    "Search across the complete mod list.",
                    "Expand the folder you want.",
                    "Press Ready beside the correct animation. Solo appears in expanded details and plays only for you.");
                Image(3);
                break;
            case 5:
                Paragraph("Expand a mod to see detected pose and emote triggers. Actor-role labels make multi-person animations understandable.");
                Steps(
                    "Left-click an available role button to claim or ready that role.",
                    "Purple means another room member already claimed that role.",
                    "Right-click a role button to rename it, such as Driver, Passenger, or Camera.",
                    "The remaining neutral roles are available to other actors.");
                Image(4);
                break;
            case 6:
                Paragraph("Role labels are community driven. Identifying a confusing animation once can help everyone who owns the same mod.");
                Bullets(
                    "The first useful label is distributed through the community database.",
                    "Right-click a bad shared label, enter the correction, and press Report correction.",
                    "Your correction applies immediately. Five matching installation reports replace the shared label.");
                Paragraph("Other room members cannot see or browse your complete mod list. EmoteLink only shows availability for mods you have in common. If you do not want a particular match revealed at all, mark that mod Private.");
                Steps(
                    "Right-click the mod name.",
                    "Choose Private from the context menu.",
                    "Private mods are not advertised or transferable in group play.");
                Image(5);
                ImGui.TextColored(new Vector4(0.2f, 0.85f, 0.9f, 1f),
                    "Private mods appear cyan, sort near the bottom above white entries, never upload labels, and are excluded from room matching and transfers.");
                break;
            case 7:
                Paragraph("Right-click a mod and choose Private when it should never be advertised or transferred through group play.");
                Bullets(
                    "Press Send beside a non-private mod to offer it to the room.",
                    "The recipient sees the sender, mod name, and file size before anything is installed.",
                    "Accept downloads and installs the offered mod; Decline rejects it.",
                    "Only send or accept files that everyone involved is allowed to share.",
                    "Private mods never show a Send button.");
                ImGui.Spacing();
                if (ImGui.BeginChild("transfer-example", new Vector2(360, 135), true))
                {
                    ImGui.TextUnformatted("Animation mod received");
                    ImGui.Separator();
                    ImGui.TextWrapped("A room member wants to send you:");
                    ImGui.TextUnformatted("Nightlife+ 3.1.1");
                    ImGui.TextDisabled("42.6 MB");
                    ImGui.Button("Accept", new Vector2(110, 0));
                    ImGui.SameLine();
                    ImGui.Button("Decline", new Vector2(110, 0));
                    ImGui.EndChild();
                }
                ImGui.TextWrapped("You can reopen this tutorial at any time with the How To button beside Refresh.");
                break;
        }
    }

    private static ISharedImmediateTexture Load(ITextureProvider textures, string file) =>
        textures.GetFromManifestResource(typeof(HowToWindow).Assembly, $"EmoteLink.Assets.HowTo.{file}");

    private void Image(int index)
    {
        ImGui.Spacing();
        var texture = screenshots[index].GetWrapOrEmpty();
        var available = ImGui.GetContentRegionAvail().X;
        var scale = MathF.Min(1f, available / texture.Width);
        ImGui.Image(texture.Handle, new Vector2(texture.Width * scale, texture.Height * scale));
    }

    private static void Paragraph(string text)
    {
        ImGui.TextWrapped(text);
        ImGui.Spacing();
    }

    private static void Bullets(params string[] items)
    {
        foreach (var item in items) ImGui.BulletText(item);
    }

    private static void Steps(params string[] items)
    {
        for (var index = 0; index < items.Length; index++)
        {
            ImGui.TextColored(new Vector4(0f, 0.86f, 1f, 1f), $"{index + 1}.");
            ImGui.SameLine();
            ImGui.TextWrapped(items[index]);
        }
        ImGui.Spacing();
    }
}
