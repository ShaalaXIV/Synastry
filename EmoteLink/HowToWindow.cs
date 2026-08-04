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
        "4. Pick, label, and share animations",
        "5. Role labels and community corrections",
        "6. Private mods and safe sharing"
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
            Load(textures, "04-mod-list.png")
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
                Paragraph("EmoteLink finds animation mods in Penumbra and lets you launch them temporarily without changing your normal collection setup.");
                Bullets(
                    "Make sure the top line says Penumbra: connected.",
                    "Use Refresh after installing or changing animation mods.",
                    "Align / teleport to target places you at your current target for paired animations.",
                    "Clear temporary animations restores the assignments EmoteLink applied.");
                Image(0);
                break;
            case 1:
                Paragraph("Press Connect under Group Play. This connects EmoteLink to the animation relay; it does not put you in a room yet.");
                Bullets(
                    "Your current character name is used as your room identity.",
                    "Create room hosts a new session.",
                    "To join someone else, enter their room code and press Join.",
                    "Disconnect leaves the relay completely.");
                Image(1);
                break;
            case 2:
                Paragraph("After creating a room, click the room-code button to copy it and give that code to the other players. They connect, enter it, and press Join.");
                Bullets(
                    "Ready means that member has selected an animation; Waiting means they have not.",
                    "Cancel ready clears your current choice.",
                    "Force start is host-only and starts the host's prepared animation for matching members.",
                    "Playback normally begins when everyone is ready on the same mod.");
                Image(2);
                break;
            case 3:
                Paragraph("Folders help organize large libraries. Expand a folder, then expand a mod to see its extra Penumbra options and detected animation triggers.");
                Bullets(
                    "Ready prepares an animation for synchronized group playback.",
                    "Solo runs it only for you and cancels your group readiness.",
                    "Send offers the mod to the room; the recipient still chooses Accept or Decline.",
                    "Use the search box to filter by mod name.");
                Image(3);
                break;
            case 4:
                Paragraph("Detected pose and emote buttons can be named for their actor role, such as Driver, Passenger, or Camera.");
                Bullets(
                    "Right-click a role button to edit its label.",
                    "Left-click the labeled button to select or ready that role.",
                    "A purple role means another room member selected it, showing which roles remain available.",
                    "The first useful label is shared to the community database automatically.",
                    "Right-click a bad community label to report it and enter a correction. Your correction applies immediately; five matching reports replace the shared label.");
                ImGui.Spacing();
                ImGui.TextWrapped("Community labels are the heart of EmoteLink: one person can identify an unclear animation slot, and everyone with the same mod can benefit from that work.");
                break;
            case 5:
                Paragraph("Right-click a mod and choose Private when it should never be advertised or transferred through group play.");
                Bullets(
                    "Private mods are cyan.",
                    "They are sorted near the bottom of the list, above unmatched white mods.",
                    "Private mods are excluded from catalog matching, community-label uploads, and Send.",
                    "Green means everyone has the mod; orange means some members; purple means suggested or claimed; white means no match.",
                    "Only accept mod transfers you recognize and are allowed to receive.");
                ImGui.Spacing();
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
}
