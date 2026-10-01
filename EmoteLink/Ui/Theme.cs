using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;

namespace EmoteLink;

/// <summary>Who has an animation, as the two linked circles before its name.</summary>
internal enum Availability
{
    Solo,
    Everyone,
    Some,
    OnlyYou,
    Private,
}

/// <summary>How a role chip reads: open, chosen by you, or chosen by someone else.</summary>
internal enum ChipState
{
    Open,
    Mine,
    Theirs,
    Taken,
}

/// <summary>
/// Synastry's look: warm ink, one hairline, and two accents from the logo. Rose is you and the one
/// main action in an area; azure is the room and other people. Titles use the game's own Jupiter,
/// numbers and codes its Miedinger and Trump Gothic, everything else Axis, so the plugin reads as
/// part of the game rather than a web page.
/// </summary>
internal static class Theme
{
    public static readonly Vector4 Ink = Hex(0x141312);
    public static readonly Vector4 Raised = Hex(0x1A1917);
    public static readonly Vector4 RowHover = Hex(0x191816);
    public static readonly Vector4 Selected = Hex(0x23211E);
    public static readonly Vector4 Hairline = Hex(0x2B2926);
    public static readonly Vector4 HairlineStrong = Hex(0x3A3733);
    public static readonly Vector4 Bone = Hex(0xEDE7DC);
    public static readonly Vector4 Soft = Hex(0xB3ACA1);
    public static readonly Vector4 Ash = Hex(0x9A948A);
    public static readonly Vector4 Faint = Hex(0x6E6961);
    public static readonly Vector4 Dormant = Hex(0x4A4743);
    public static readonly Vector4 Rose = Hex(0xE8789A);
    public static readonly Vector4 RoseHover = Hex(0xF29AB4);
    public static readonly Vector4 RoseActive = Hex(0xD4627F);
    public static readonly Vector4 RoseInk = Hex(0x1A1214);
    public static readonly Vector4 Azure = Hex(0x6E9CF0);
    public static readonly Vector4 AzureText = Hex(0x8FB3F5);

    private static IFontHandle? title;
    private static IFontHandle? heading;
    private static IFontHandle? numbers;
    private static IFontHandle? code;

    public static float Scale => ImGuiHelpers.GlobalScale;

    public static void Initialize(IUiBuilder ui)
    {
        title = ui.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Jupiter23));
        heading = ui.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Jupiter20));
        numbers = ui.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.MiedingerMid14));
        code = ui.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.TrumpGothic34));
    }

    public static void Dispose()
    {
        title?.Dispose();
        heading?.Dispose();
        numbers?.Dispose();
        code?.Dispose();
        title = heading = numbers = code = null;
    }

    public static IDisposable? TitleFont() => Push(title);
    public static IDisposable? HeadingFont() => Push(heading);
    public static IDisposable? NumberFont() => Push(numbers);
    public static IDisposable? CodeFont() => Push(code);

    private static IDisposable? Push(IFontHandle? handle) => handle is { Available: true } ? handle.Push() : null;

    // ---- Window style ---------------------------------------------------------------------------

    private const int ColorCount = 40;
    private const int VarCount = 13;

    /// <summary>Everything a Synastry window draws with. Pair with <see cref="Pop"/>.</summary>
    public static void Push()
    {
        var s = Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 4f * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f * s);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4f * s);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 6f * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 4f * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8f * s);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10f, 6f) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, 10f * s);

        ImGui.PushStyleColor(ImGuiCol.Text, Bone);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Ash);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Ink);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Raised);
        ImGui.PushStyleColor(ImGuiCol.Border, Hairline);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Raised);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Selected);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Selected);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Ink);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Ink);
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, Ink);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, HairlineStrong);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Faint);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Ash);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Rose);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Bone);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Rose);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Selected);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Hairline);
        ImGui.PushStyleColor(ImGuiCol.Header, Selected);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, RowHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Selected);
        ImGui.PushStyleColor(ImGuiCol.Separator, Hairline);
        ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, HairlineStrong);
        ImGui.PushStyleColor(ImGuiCol.SeparatorActive, Ash);
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, HairlineStrong);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, Ash);
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, WithAlpha(Rose, 0.3f));
        ImGui.PushStyleColor(ImGuiCol.ModalWindowDimBg, new Vector4(0.03f, 0.027f, 0.027f, 0.62f));
        ImGui.PushStyleColor(ImGuiCol.DragDropTarget, Rose);
        ImGui.PushStyleColor(ImGuiCol.NavHighlight, Rose);
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, Raised);
        ImGui.PushStyleColor(ImGuiCol.TableBorderStrong, Hairline);
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight, Hairline);
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, new Vector4(1f, 1f, 1f, 0.02f));
    }

    public static void Pop()
    {
        ImGui.PopStyleColor(ColorCount);
        ImGui.PopStyleVar(VarCount);
    }

    // ---- Text -----------------------------------------------------------------------------------

    public static void Title(string text)
    {
        using (TitleFont()) ImGui.TextUnformatted(text);
    }

    public static void Heading(string text)
    {
        using (HeadingFont()) ImGui.TextUnformatted(text);
    }

    public static void Label(string text) => ImGui.TextColored(Ash, text);

    public static void Quiet(string text) => ImGui.TextColored(Faint, text);

    public static void Number(string text, Vector4? color = null)
    {
        using (NumberFont()) ImGui.TextColored(color ?? Ash, text);
    }

    public static void Wrapped(string text, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    public static string Truncate(string text, float maxWidth)
    {
        if (maxWidth <= 0 || ImGui.CalcTextSize(text).X <= maxWidth) return text;
        const string suffix = "…";
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..middle] + suffix).X <= maxWidth) low = middle;
            else high = middle - 1;
        }
        return text[..low].TrimEnd() + suffix;
    }

    // ---- Buttons --------------------------------------------------------------------------------

    /// <summary>The one filled button in an area.</summary>
    public static bool Primary(string label, float width = 0f, float height = 0f)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Rose);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, RoseHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, RoseActive);
        ImGui.PushStyleColor(ImGuiCol.Border, Rose);
        ImGui.PushStyleColor(ImGuiCol.Text, RoseInk);
        var clicked = ImGui.Button(label, new Vector2(width, height));
        ImGui.PopStyleColor(5);
        return clicked;
    }

    public static bool Outline(string label, float width = 0f, float height = 0f)
    {
        ImGui.PushStyleColor(ImGuiCol.Border, HairlineStrong);
        var clicked = ImGui.Button(label, new Vector2(width, height));
        ImGui.PopStyleColor();
        return clicked;
    }

    /// <summary>A button that is only its words: secondary and rare actions.</summary>
    public static bool Text(string label, Vector4? color = null)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6f, 4f) * Scale);
        ImGui.PushStyleColor(ImGuiCol.Text, color ?? Soft);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, RowHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Selected);
        var clicked = ImGui.Button(label);
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar(2);
        return clicked;
    }

    /// <summary>A role button: rose when it's yours, azure when someone else picked it.</summary>
    public static bool Chip(string label, ChipState state)
    {
        var (fill, border, text) = state switch
        {
            ChipState.Mine => (Rose, Rose, RoseInk),
            ChipState.Theirs => (Vector4.Zero, Azure, AzureText),
            ChipState.Taken => (Vector4.Zero, Hairline, Faint),
            _ => (Vector4.Zero, HairlineStrong, Bone),
        };
        ImGui.PushStyleColor(ImGuiCol.Button, fill);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, state == ChipState.Mine ? RoseHover : Selected);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, state == ChipState.Mine ? RoseActive : Hairline);
        ImGui.PushStyleColor(ImGuiCol.Border, border);
        ImGui.PushStyleColor(ImGuiCol.Text, text);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(14f, 6f) * Scale);
        var clicked = ImGui.Button(label);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);
        return clicked;
    }

    /// <summary>An on/off switch with its label to the right.</summary>
    public static bool Toggle(string id, string label, ref bool value)
    {
        var s = Scale;
        var size = new Vector2(28f, 16f) * s;
        var start = ImGui.GetCursorScreenPos();
        var lineHeight = ImGui.GetFrameHeight();
        var top = start + new Vector2(0, (lineHeight - size.Y) * 0.5f);
        var clicked = ImGui.InvisibleButton(id, new Vector2(size.X + 10f * s + ImGui.CalcTextSize(label).X, lineHeight));
        if (clicked) value = !value;
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(top, top + size, ImGui.GetColorU32(value ? WithAlpha(Rose, 0.35f) : Raised), size.Y * 0.5f);
        draw.AddRect(top, top + size, ImGui.GetColorU32(value ? Rose : hovered ? Faint : HairlineStrong), size.Y * 0.5f);
        var knob = 5f * s;
        var knobCenter = new Vector2(value ? top.X + size.X - knob - 3f * s : top.X + knob + 3f * s, top.Y + size.Y * 0.5f);
        draw.AddCircleFilled(knobCenter, knob, ImGui.GetColorU32(value ? Rose : Faint), 16);
        draw.AddText(new Vector2(top.X + size.X + 10f * s, start.Y + (lineHeight - ImGui.GetTextLineHeight()) * 0.5f),
            ImGui.GetColorU32(hovered ? Bone : Soft), label);
        return clicked;
    }

    // ---- Shapes ---------------------------------------------------------------------------------

    public static void HorizontalLine(ImDrawListPtr draw, Vector2 from, float width) =>
        draw.AddLine(from, from + new Vector2(width, 0), ImGui.GetColorU32(Hairline), 1f);

    public static void VerticalLine(ImDrawListPtr draw, Vector2 from, float height) =>
        draw.AddLine(from, from + new Vector2(0, height), ImGui.GetColorU32(Hairline), 1f);

    /// <summary>Two linked circles: the left one is you, the right one the room.</summary>
    public static void AvailabilityMark(ImDrawListPtr draw, Vector2 center, Availability availability)
    {
        var s = Scale;
        var radius = 4f * s;
        var you = center - new Vector2(3.5f * s, 0);
        var room = center + new Vector2(3.5f * s, 0);
        var rose = ImGui.GetColorU32(Rose);
        var azure = ImGui.GetColorU32(Azure);
        switch (availability)
        {
            case Availability.Private:
                draw.AddCircle(you, radius - 0.6f * s, ImGui.GetColorU32(Faint), 20, 1.2f * s);
                return;
            case Availability.Solo:
                draw.AddCircleFilled(you, radius, rose, 20);
                return;
        }
        draw.AddCircleFilled(you, radius, rose, 20);
        switch (availability)
        {
            case Availability.Everyone:
                draw.AddCircleFilled(room, radius, azure, 20);
                break;
            case Availability.Some:
                draw.AddCircle(room, radius - 0.6f * s, azure, 20, 1.2f * s);
                draw.PathArcTo(room, radius - 0.6f * s, MathF.PI * 0.5f, MathF.PI * 1.5f, 12);
                draw.PathFillConvex(azure);
                break;
            default:
                draw.AddCircle(room, radius - 0.6f * s, ImGui.GetColorU32(Dormant), 20, 1.2f * s);
                break;
        }
    }

    public static void Dot(ImDrawListPtr draw, Vector2 center, Vector4 color, bool filled)
    {
        var radius = 4f * Scale;
        if (filled) draw.AddCircleFilled(center, radius, ImGui.GetColorU32(color), 16);
        else draw.AddCircle(center, radius - 0.6f * Scale, ImGui.GetColorU32(color), 16, 1.2f * Scale);
    }

    public static void Check(ImDrawListPtr draw, Vector2 topLeft, Vector4 color)
    {
        var s = Scale;
        var col = ImGui.GetColorU32(color);
        draw.AddLine(topLeft + new Vector2(0, 4.5f) * s, topLeft + new Vector2(3.5f, 8f) * s, col, 1.6f * s);
        draw.AddLine(topLeft + new Vector2(3.5f, 8f) * s, topLeft + new Vector2(10f, 1f) * s, col, 1.6f * s);
    }

    public static Vector4 WithAlpha(Vector4 color, float alpha) => new(color.X, color.Y, color.Z, alpha);

    private static Vector4 Hex(uint rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
}
