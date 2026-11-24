using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace EmoteLink;

/// <summary>What <see cref="LibraryPanel"/> needs from the window around it.</summary>
public sealed partial class MainWindow
{
    /// <summary>An opened animation mod: its lists of animations, or its roles and options. Returns true
    /// when a role was chosen (the suggestion prompt closes then).</summary>
    internal bool DrawModDetails((string Directory, string Name) mod, bool suggestionMode = false) =>
        library.DrawDetails(mod, suggestionMode);

    internal Availability AvailabilityOf(string directory) => AvailabilityOf(directory, plugin.IsModPrivate(directory));

    /// <summary>False while the guide still waits for an option to be chosen.</summary>
    internal bool GuideAllowsPick(string directory, string animationName) => TryTutorialAnimationAction(directory, animationName);

    /// <summary>Points at what was just drawn while the guide asks for a role.</summary>
    internal void HighlightForGuide()
    {
        if (tutorialActive && tutorialStep == TutorialStep.ConfigureAnimation) HighlightLastItem();
    }

    internal static void Chevron(ImDrawListPtr draw, Vector2 center, bool open, uint color) => DrawChevron(draw, center, open, color);
}
