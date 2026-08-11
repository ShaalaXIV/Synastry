using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace EmoteLink;

public sealed class HowToWindow : Window
{
    private readonly ISharedImmediateTexture guide;

    public HowToWindow(ITextureProvider textures) : base("Synastry How To###EmoteLinkHowTo")
    {
        Size = new Vector2(1000, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        guide = textures.GetFromManifestResource(
            typeof(HowToWindow).Assembly,
            "EmoteLink.Assets.HowTo.synastry-start-to-finish.png");
    }

    public override void Draw()
    {
        var texture = guide.GetWrapOrEmpty();
        var available = ImGui.GetContentRegionAvail();
        var scale = MathF.Min(available.X / texture.Width, available.Y / texture.Height);
        var imageSize = new Vector2(texture.Width * scale, texture.Height * scale);

        if (imageSize.X < available.X)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (available.X - imageSize.X) / 2f);
        if (imageSize.Y < available.Y)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (available.Y - imageSize.Y) / 2f);

        ImGui.Image(texture.Handle, imageSize);
    }
}
