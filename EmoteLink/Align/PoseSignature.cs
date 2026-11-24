using System.Globalization;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace EmoteLink.Align;

/// <summary>Which emote, pose or timeline a character is in, for noticing that it changed.</summary>
internal static unsafe class PoseSignature
{
    /// <summary>Changes whenever a new emote, pose or timeline starts.</summary>
    public static string Of(ICharacter player)
    {
        var character = (Character*)player.Address;
        if (character is null) return "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{(byte)character->Mode}:{character->ModeParam}:{character->EmoteController.EmoteId}:{character->Timeline.BaseOverride}");
    }

    /// <summary>The loop Simple Heels ties a temporary offset to: it drops the offset when this changes.</summary>
    public static (byte Mode, byte Param) Loop(ICharacter player)
    {
        var character = (Character*)player.Address;
        return character is null ? default : ((byte)character->Mode, character->ModeParam);
    }

    /// <summary>The /cpose variant: the same loop in another variant is another animation.</summary>
    public static byte Variant(ICharacter player)
    {
        var character = (Character*)player.Address;
        return character is null ? (byte)0 : character->EmoteController.CPoseState;
    }
}
