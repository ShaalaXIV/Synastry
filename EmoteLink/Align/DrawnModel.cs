using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace EmoteLink.Align;

/// <summary>
/// Where a character is drawn, as opposed to where the game says it stands: Simple Heels and other
/// offsets move the drawn model only. Framework thread only.
/// </summary>
internal static unsafe class DrawnModel
{
    /// <summary>The drawn model's root position and facing in the world.</summary>
    public static bool TryGetRoot(ICharacter player, out Vector3 position, out float yaw)
    {
        position = default;
        yaw = 0;
        var character = (Character*)player.Address;
        if (character is null || character->DrawObject is null ||
            character->DrawObject->GetObjectType() != ObjectType.CharacterBase) return false;
        var skeleton = ((CharacterBase*)character->DrawObject)->Skeleton;
        if (skeleton is null) return false;
        var root = skeleton->Transform.Position;
        var rotation = skeleton->Transform.Rotation;
        position = new Vector3(root.X, root.Y, root.Z);
        var forward = Vector3.Transform(Vector3.UnitZ, new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W));
        yaw = MathF.Atan2(forward.X, forward.Z);
        return float.IsFinite(yaw) && float.IsFinite(position.LengthSquared());
    }

    /// <summary>The game's current draw offset: left (+X), up (+Y) and forward (+Z) in the drawn frame.</summary>
    public static Vector3 DrawOffset(ICharacter player)
    {
        var character = (Character*)player.Address;
        if (character is null) return default;
        var offset = character->GameObject.DrawOffset;
        return new Vector3(offset.X, offset.Y, offset.Z);
    }

    /// <summary>Playing an emote or pose loop, which Simple Heels needs for anything but height.</summary>
    public static bool IsLooping(ICharacter player)
    {
        var character = (Character*)player.Address;
        return character is not null &&
               character->Mode is CharacterModes.EmoteLoop or CharacterModes.InPositionLoop;
    }

    /// <summary>An angle in radians brought into -pi to pi.</summary>
    public static float Wrap(float angle) => MathF.IEEERemainder(angle, MathF.Tau);

    /// <summary>A rotation about the vertical axis, in the game's yaw convention.</summary>
    public static Quaternion YawRotation(float yaw) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
}
