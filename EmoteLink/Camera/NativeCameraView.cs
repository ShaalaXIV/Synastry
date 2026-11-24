using System.Numerics;

namespace EmoteLink.Camera;

/// <summary>
/// The game's render matrix loader. It reads four rows with MOVAPS, so the matrix must be 16-byte
/// aligned: pass the camera's own ViewMatrix, never one on the stack.
/// </summary>
/// <remarks>Adapted from PoseKit by RaylaPetal (AGPL-3.0), https://github.com/RaylaPetal/PoseKit</remarks>
internal static unsafe class NativeCameraView
{
    internal delegate void Loader(nint renderCamera, Matrix4x4* matrix);

    internal static void Validate(nint renderCamera, Matrix4x4* matrix)
    {
        if (renderCamera == 0) throw new InvalidOperationException("The render camera is unavailable.");
        if (matrix is null || ((nuint)matrix & 15) != 0)
            throw new InvalidOperationException("The camera view matrix must be 16-byte aligned.");
    }

    internal static void Load(Loader loader, nint renderCamera, Matrix4x4* matrix)
    {
        Validate(renderCamera, matrix);
        loader(renderCamera, matrix);
    }
}
