using System;
using ForgeCore;
using UnityEngine;
using RowMatrix = System.Numerics.Matrix4x4;

namespace ForgeFrameGen;

/// <summary>Frame generation inputs of the pass (plan §3.3): render-size depth and motion and the HUD-less output-size image (the DLSS output,
/// with whatever composite steps drew on it before the HUD), with the camera's constants in the swap chain's space. Unity's render
/// textures are stored bottom row first on Direct3D, the swap chain top row first: the native side flips the rows, so the projection is
/// Unity's projection for the screen (no render-texture flip), the jitter's Y changes sign and the motion's Y keeps Unity's sign.
/// While the map or the game's menu lies over a world the camera still draws, or the player types at a terminal (its text is world
/// geometry rebuilt as it scrolls, with no motion), the frame is marked as left out (NVIDIA's guide: frame generation off in full-screen
/// menus); frame generation keeps its resources and is back with the first frame after.</summary>
internal static unsafe partial class WorldPass
{
    private static Matrix4x4 _previousViewToClip, _previousWorldToView;
    private static bool _hasPrevious;
    // Development driver: rows flipped into swap-chain order (1) or left as Unity stores them (0), for the first in-game check.
    private static int _flipRows = 1;

    private static DlssBridge.FrameGenerationParams* GenerationFrame(Camera camera, Matrix4x4 projection, float jitterX, float jitterY, bool reset, int frameNumber)
    {
        var p = DlssBridge.NextGenerationFrame();
        p->Frame = frameNumber;
        p->Enabled = !Plugin.FrameGenerationOn ? 0 : Frame.Focus is eFocusState.Map or eFocusState.MainMenu or eFocusState.ComputerTerminal ? 2 : 1;
        p->Depth = _depthPointer;
        p->Motion = _motionPointer;
        p->Hudless = _outputPointer;
        p->HudOnBlack = _hudOnBlackPointer;
        p->HudOnWhite = _hudOnWhitePointer;
        p->RenderWidth = (uint)_renderWidth;
        p->RenderHeight = (uint)_renderHeight;
        p->OutputWidth = (uint)_outputWidth;
        p->OutputHeight = (uint)_outputHeight;

        // Two reads of the camera a frame, the GPU projection and the view matrix. The rest is worked out here: every operator and
        // property of Unity's vector and matrix types, and of the camera and its transform, is a call into the game.
        var viewToClip = GL.GetGPUProjectionMatrix(projection, _flipRows == 0);
        var worldToView = camera.worldToCameraMatrix;
        // The view matrix is the camera's rotation transposed (Unity leaves the scale out) with Z negated, applied after moving the
        // camera to the origin: its rows are the camera's right, up and minus forward, its last column those axes dotted with minus
        // the position (plus, for forward).
        float rightX = worldToView.m00, rightY = worldToView.m01, rightZ = worldToView.m02;
        float upX = worldToView.m10, upY = worldToView.m11, upZ = worldToView.m12;
        float forwardX = -worldToView.m20, forwardY = -worldToView.m21, forwardZ = -worldToView.m22;
        float alongRight = -worldToView.m03, alongUp = -worldToView.m13, alongForward = worldToView.m23;
        float positionX = rightX * alongRight + upX * alongUp + forwardX * alongForward;
        float positionY = rightY * alongRight + upY * alongUp + forwardY * alongForward;
        float positionZ = rightZ * alongRight + upZ * alongUp + forwardZ * alongForward;

        // Clip-to-previous-clip without the camera's world position in the products (precision far from the origin): both view
        // matrices are taken relative to the current camera position.
        Matrix4x4 toCamera = default;
        toCamera.m00 = toCamera.m11 = toCamera.m22 = toCamera.m33 = 1f;
        (toCamera.m03, toCamera.m13, toCamera.m23) = (positionX, positionY, positionZ);
        var current = Product(viewToClip, Product(worldToView, toCamera));
        if (!_hasPrevious || reset) (_previousViewToClip, _previousWorldToView) = (viewToClip, worldToView);
        var previous = Product(_previousViewToClip, Product(_previousWorldToView, toCamera));
        p->ViewToClip = viewToClip;
        p->ClipToView = Inverse(viewToClip);
        p->ClipToPrevClip = Product(previous, Inverse(current));
        p->PrevClipToClip = Product(current, Inverse(previous));
        (_previousViewToClip, _previousWorldToView, _hasPrevious) = (viewToClip, worldToView, true);

        float flip = _flipRows != 0 ? -1f : 1f;
        p->JitterX = jitterX;
        p->JitterY = jitterY * flip;
        // Unity's motion is current minus previous in UV units; Streamline wants previous minus current, scaled to the screen.
        p->MotionScaleX = -1f * _signMotionX;
        p->MotionScaleY = -flip * _signMotionY;
        (p->CameraPosition.x, p->CameraPosition.y, p->CameraPosition.z) = (positionX, positionY, positionZ);
        (p->CameraUp.x, p->CameraUp.y, p->CameraUp.z) = (upX, upY, upZ);
        (p->CameraRight.x, p->CameraRight.y, p->CameraRight.z) = (rightX, rightY, rightZ);
        (p->CameraForward.x, p->CameraForward.y, p->CameraForward.z) = (forwardX, forwardY, forwardZ);
        (p->CameraNear, p->CameraFar, p->CameraFov) = Planes(projection);
        p->CameraAspect = projection.m11 / projection.m00;
        p->Reset = reset ? 1 : 0;
        p->DepthInverted = _reversedDepth ? 1 : 0;
        p->FlipRows = _flipRows;
        p->EncodeSrgb = _linearColour ? 1 : 0;
        return p;
    }

    /// <summary>The camera's near and far planes and vertical field of view (radians), read from the unjittered projection: Unity's own for
    /// the camera's field of view, aspect and planes (the pass resets it every frame), in OpenGL's layout: m11 = cot(fov / 2),
    /// m00 = m11 / aspect, m22 = -(far + near) / (far - near), m23 = -2 far near / (far - near). No call into the game.</summary>
    private static (float Near, float Far, float Fov) Planes(Matrix4x4 projection)
        => (projection.m23 / (projection.m22 - 1f), projection.m23 / (projection.m22 + 1f), 2f * MathF.Atan(1f / projection.m11));

    // Unity's a * b and m.inverse without the call into the game each costs. Unity stores a matrix column by column; the same sixteen
    // numbers read row by row (System.Numerics) are its transpose, and (ab)ᵀ = bᵀaᵀ, (m⁻¹)ᵀ = (mᵀ)⁻¹.
    private static Matrix4x4 Product(Matrix4x4 a, Matrix4x4 b)
    {
        var product = RowMatrix.Multiply(*(RowMatrix*)&b, *(RowMatrix*)&a);
        return *(Matrix4x4*)&product;
    }

    private static Matrix4x4 Inverse(Matrix4x4 matrix)
    {
        if (!RowMatrix.Invert(*(RowMatrix*)&matrix, out var inverse)) return default; // Unity's inverse of a singular matrix is zero too
        return *(Matrix4x4*)&inverse;
    }

    /// <summary>Development driver: frame generation inputs with their rows flipped into swap-chain order (1) or as Unity stores them (0).</summary>
    internal static string FlipRows(int flip)
    {
        _flipRows = flip;
        _reset = true;
        return "flipRows=" + flip + " " + DlssBridge.FrameGeneration();
    }
}
