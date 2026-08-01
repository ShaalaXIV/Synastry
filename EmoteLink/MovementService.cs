using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace EmoteLink;

public sealed unsafe class MovementService : IDisposable
{
    private delegate void RmiWalkDelegate(void* self, float* left, float* forward,
        float* turnLeft, byte* backwardOrStrafe, byte* unknown, byte additive);

    [Signature("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D", DetourName = nameof(Detour))]
    private Hook<RmiWalkDelegate>? hook { get; init; }

    private readonly IObjectTable objects;
    private Vector3 destination;
    private Action? arrived;
    private long started;
    public bool IsWalking { get; private set; }

    public MovementService(IGameInteropProvider interop, IObjectTable objects)
    {
        this.objects = objects;
        interop.InitializeFromAttributes(this);
        hook?.Enable();
    }

    public void WalkTo(Vector3 point, Action onArrival)
    {
        destination = point;
        arrived = onArrival;
        started = Environment.TickCount64;
        IsWalking = true;
    }

    public void Cancel()
    {
        IsWalking = false;
        arrived = null;
    }

    private void Finish()
    {
        IsWalking = false;
        var callback = arrived;
        arrived = null;
        callback?.Invoke();
    }

    private void Detour(void* self, float* left, float* forward, float* turnLeft,
        byte* backwardOrStrafe, byte* unknown, byte additive)
    {
        hook!.Original(self, left, forward, turnLeft, backwardOrStrafe, unknown, additive);
        if (!IsWalking) return;
        if (Environment.TickCount64 - started > 2000) { Finish(); return; }
        if (*left != 0 || *forward != 0) { Cancel(); return; }
        if (additive != 0) return;

        var player = objects.LocalPlayer;
        if (player is null) { Cancel(); return; }
        var delta = destination - player.Position;
        var distance = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
        if (distance <= 0.05f) { Finish(); return; }

        var direction = MathF.Atan2(delta.X, delta.Z);
        var camera = CameraManager.Instance()->GetActiveCamera();
        var reference = camera is null ? player.Rotation : *(float*)((byte*)camera + 0x140) + MathF.PI;
        var relative = direction - reference;
        var scale = distance < 0.3f ? MathF.Max(distance / 0.3f, 0.15f) : 1f;
        *forward = MathF.Cos(relative) * scale;
        *left = MathF.Sin(relative) * scale;
    }

    public void Dispose()
    {
        Cancel();
        hook?.Dispose();
    }
}
