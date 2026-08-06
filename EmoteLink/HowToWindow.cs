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
        "1. Learn the new interface",
        "2. Connect and enter a room",
        "3. Use room controls and readiness",
        "4. Understand colors and suggestions",
        "5. Ready, Solo, and Send animations",
        "6. Label roles and report corrections",
        "7. Keep a mod private",
        "8. Send and receive mods safely"
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
                Paragraph("EmoteLink keeps room controls and animation browsing in two clear panels. Follow the numbered highlights from top to bottom.");
                Steps(
                    "Confirm Penumbra is connected. Refresh rescans mods, Community labels downloads accepted role names, and How to reopens this guide.",
                    "Create a room, or enter a friend's code and join. Disconnect leaves the animation relay.",
                    "Align to your current target, toggle Sit/doze anywhere when furniture-free poses are needed, or clear EmoteLink's temporary animation assignment.",
                    "Search the entire animation library or create a new folder.",
                    "Expand folders to browse your organized animation mods.");
                Image(0);
                break;
            case 1:
                Paragraph("Connecting to the relay and entering a room are separate steps. The status line confirms when the relay connection is ready.");
                Steps(
                    "Check for PENUMBRA CONNECTED and a successful relay status.",
                    "Press Create room to host, or type a code and press Join room. Disconnect leaves the relay.",
                    "Use Align / teleport to target when actors need the same position and facing direction. Sit/doze anywhere is an optional direct-mode toggle, not an automatic fallback.",
                    "Search or create folders while connected; these controls do not affect the room.",
                    "Your complete folder organization stays local and remains available during group play.");
                Image(1);
                break;
            case 2:
                Paragraph("After creating a room, give its code to the other players. They connect to the relay, enter the code, and press Join room.");
                Steps(
                    "Click the room-code button to copy it.",
                    "Cancel ready clears your prepared animation, Leave room exits, and Force start is available to the host.",
                    "Each member row shows HOST when applicable and changes between WAITING and READY.",
                    "The legend always remains visible below the library; its colors are explained on the next page.");
                Image(2);
                break;
            case 3:
                Paragraph("Color communicates availability, privacy, and remote suggestions without exposing anyone's complete mod list.");
                Steps(
                    "The room code identifies the current group.",
                    "Host controls manage readiness, membership, and a manual Force start.",
                    "Orange WAITING changes to green READY after a member prepares an animation.",
                    "Green means everyone has the mod, orange means only some members have it, purple means another member suggested or selected it, cyan means your mod is private, and white means no shared match.");
                Image(2);
                break;
            case 4:
                Paragraph("Expand a folder and then a mod to expose its animation actions. The numbered example includes every important group-play choice.");
                Steps(
                    "Expand the folder that contains the animation.",
                    "Expand a mod to see its actions. Send offers a public mod to the room.",
                    "Press the large Ready button to prepare that emote or pose for synchronized playback.",
                    "Press Solo to activate the same animation only for yourself.",
                    "A purple row with Suggested by a member means that member selected an animation in this mod. It is a suggestion, not an automatic download or playback.",
                    "Expand Mod Options when the pack has variants or Penumbra option groups.");
                Image(3);
                break;
            case 5:
                Paragraph("Actor-role labels turn unclear multi-person slots into useful names. Right-click the same Ready button you use for playback.");
                Steps(
                    "Left-click the large animation button to ready it; right-click it to open the role editor.",
                    "Enter a short role such as Lead, Driver, Passenger, or Camera. Save stores it locally; Clear removes it.",
                    "If a shared label is wrong, enter a correction and press Report correction. Your corrected label applies locally immediately.",
                    "Solo remains available beside the tagged action and never readies the rest of the room.");
                Bullets(
                    "Purple action buttons indicate another room member selected that role or trigger.",
                    "The first useful public label can become the community default.",
                    "Five matching installation reports replace an established community label.");
                Image(4);
                break;
            case 6:
                Paragraph("Mark a mod Private when it should never participate in group matching, community labels, or transfers.");
                Steps(
                    "Public mods show Send while you are in a room.",
                    "A private mod row appears cyan, says Private, and does not show Send.",
                    "Right-click the mod name and toggle Private in the context menu.",
                    "The cyan legend item identifies private rows throughout the library.");
                Image(5);
                ImGui.TextColored(new Vector4(0.2f, 0.85f, 0.9f, 1f),
                    "Private mods are excluded from room matching, transfers, and community-label submissions.");
                break;
            case 7:
                Paragraph("Send is an explicit offer. No animation mod downloads or installs until the recipient accepts it.");
                Bullets(
                    "Press Send beside a non-private mod to offer it to the other room members.",
                    "Offers are limited to 75 MB and expire from relay storage after 10 minutes.",
                    "The recipient sees the sender, mod name, and file size before installation.",
                    "Accept downloads and hands the package to Penumbra; Decline rejects the offer.",
                    "Only send or accept files that everyone involved is allowed to share.",
                    "Private mods never show a Send button.");
                Image(5);
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
