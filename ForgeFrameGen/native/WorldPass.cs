using System;
using ForgeCore;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace ForgeFrameGen;

/// <summary>DLSS on the local player's world camera (the camera <c>FPSCamera</c> drives).
/// The camera renders into a render-size target of this pass (the screen's size at DLAA): the deferred world, transparents, the first-person
/// weapon and the post-processing stack (its own anti-aliasing off) all run at render size. The game's HUD compositor UI_Apply is switched
/// off on that camera. At AfterImageEffects this pass copies colour, depth and motion vectors and DLSS writes the screen-size image. A presenter
/// camera of this pass, drawn right after the world camera, carries a UI_Apply of its own that stands in for the world camera's
/// (ForgeCore <c>Frame.StandInForHud</c>): its frame is the DLSS output, so the game's compositor puts that with the HUD on the screen at full resolution (a command-buffer blit to the screen
/// never reached it in game, plan §9). Light clusters and the fog volume follow the render size (<c>ClusteredRendering.OnResolutionchange</c>),
/// screen points of the world camera are scaled back to the screen and the indirect light volume counts render pixels (<see cref="ScreenSpace"/>), and textures get the DLSS mip bias
/// (<see cref="MipBias"/>). The pass jitters the projection itself (Halton 2,3). While frame generation
/// composites the HUD itself, the game's HUD compositor is run over a black and a white frame each frame: what it makes of the two is the
/// HUD's colour and opacity (<see cref="MeasureHud"/>).</summary>
internal static unsafe partial class WorldPass
{
    private const CameraEvent EvaluateEvent = CameraEvent.AfterImageEffects;

    private const DepthTextureMode InputTextures = DepthTextureMode.Depth | DepthTextureMode.MotionVectors;

    private static readonly int DepthTextureId = Shader.PropertyToID("_CameraDepthTexture"), MotionTextureId = Shader.PropertyToID("_CameraMotionVectorsTexture");
    private static readonly RenderTargetIdentifier CameraTargetId = new(BuiltinRenderTextureType.CameraTarget);
    private static CommandBuffer? _evaluate;
    // What does not change from frame to frame is read from the game once, when the pass starts or its targets are made: every read of a
    // game property is a call into the game, and this pass runs before every frame's culling.
    private static IntPtr _cameraPointer, _presenterPointer;
    private static RenderTargetIdentifier _colorId, _depthId, _motionId;
    private static float _jitterScaleX, _jitterScaleY;
    private static bool _jitterMeasured, _reversedDepth, _linearColour;
    private static IDisposable? _standIn;
    private static readonly Func<RenderTexture?> PresentedFrameStep = PresentedFrame;
    private static Camera? _camera, _presenter;
    private static PostProcessLayer? _layer;
    private static PostProcessLayer.Antialiasing _layerMode;
    private static UI_Apply? _hud, _presentHud;
    private static Material? _hudMaterial;
    private static RenderTexture? _world, _color, _depth, _motion, _output, _hudOnBlack, _hudOnWhite;
    // The targets' native textures, read once when the targets are made: with multi-threaded rendering GetNativeTexturePtr waits for the
    // render thread to finish everything queued, so asking every frame would stall the main thread behind the previous frame's present.
    private static IntPtr _colorPointer, _depthPointer, _motionPointer, _outputPointer, _hudOnBlackPointer, _hudOnWhitePointer;
    private static DlssMode _mode;
    private static int _renderWidth, _renderHeight, _outputWidth, _outputHeight, _phases = 8, _lastFrame = -1, _jitterIndex;
    private static bool _active, _reset, _presenting, _measureHud, _jittered;
    private static IDisposable? _preCull;

    // Development readback: whether FPSCamera changes the field of view after this pass's pre-cull (then the custom projection keeps last
    // frame's value for one frame), and sign multipliers for the jitter and motion conventions, set only through the development driver.
    private static float _fovAtPreCull;
    private static int _fovFrames, _fovLate;
    private static float _signJitterX = 1f, _signJitterY = 1f, _signMotionX = 1f, _signMotionY = 1f;

    internal static void Load()
    {
        if (!DlssBridge.Loaded) return;
        Frame.OnLateUpdate(Plugin.Id, FollowWorldCamera);
        // Another upscaler: the pass stops (its next frame starts it again at the new upscaler's render size) and the bridge restarts.
        Plugin.Upscaler.SettingChanged += (_, _) =>
        {
            Stop();
            DlssBridge.Restart();
            SyncPreCull();
        };
        SyncPreCull();
        GameEvents.StateChanged += () =>
        {
            if (_active) MipBias.Apply(Bias());
            // One line per game state for whoever reads the log after a session: how many presents frame generation left out, and why.
            LogCounters();
        };
        Frame.OnQuit(Plugin.Id, LogCounters); // and once more at shutdown; the native lines on each switch carry them in between
    }

    private static void LogCounters()
    {
        if (ForgeCore.Log.Developer && DlssBridge.Bridged) Plugin.Info.LogInfo("Frame generation so far: " + DlssBridge.Counters());
    }

    /// <summary>Subscribes to the cameras' pre-cull while DLSS is on or still running, and not otherwise: every subscribed call makes the
    /// interop wrap its camera, for every camera of every frame, so with the upscaler Off and the pass stopped nothing is subscribed.</summary>
    private static void SyncPreCull()
    {
        bool wanted = Plugin.UpscalerOn || _active;
        if (wanted && _preCull == null) _preCull = Frame.OnPreCull(Plugin.Id, PreCull);
        else if (!wanted && _preCull != null)
        {
            _preCull.Dispose();
            _preCull = null;
        }
    }

    /// <summary>Gives the camera its plain projection back after the pass jittered it for the frame's render: <see cref="ScreenSpace"/>,
    /// ForgeCore's <c>WorldView</c> and the game's own screen-point calls read the camera between frames and must not see the jitter.</summary>
    private static void RestoreProjection()
    {
        if (!_jittered) return;
        _jittered = false;
        if (!NativeObjects.Gone(_camera)) _camera.ResetProjectionMatrix();
    }

    /// <summary>Before the frame's cameras draw: the pass ends when the camera it took over is destroyed (back in the lobby, a level
    /// change), and the presenter draws only while that camera does. It stands in for the camera's HUD compositor: with the world
    /// camera off (a full-screen menu) the game composites no HUD there, so the presenter must not either. One read a frame.</summary>
    private static void FollowWorldCamera()
    {
        if (!_active) return;
        if (NativeObjects.Gone(_camera))
        {
            Stop();
            return;
        }
        RestoreProjection(); // the presenter's pre-cull did it already unless the presenter did not draw
        bool draws = _camera.isActiveAndEnabled;
        if (draws == _presenting || _presenter == null || _presenter.WasCollected) return;
        _presenter.enabled = _presenting = draws;
    }

    /// <summary>One line of state for the development driver (<c>call static:ForgeFrameGen.WorldPass Readback</c>).</summary>
    internal static string Readback()
    {
        var camera = _camera != null && !_camera.WasCollected ? _camera : null;
        var clustered = ClusteredRendering.Current;
        return "status=" + DlssBridge.Status + " upscaler=" + DlssBridge.Upscaler().Why + " evaluations=" + DlssBridge.Evaluations + " active=" + _active + " mode=" + Plugin.Mode.Value
               + " render=" + _renderWidth + "x" + _renderHeight + " output=" + _outputWidth + "x" + _outputHeight + " phases=" + _phases
               + " target=" + (camera?.targetTexture == null ? "screen" : camera.targetTexture.width + "x" + camera.targetTexture.height)
               + " pixels=" + (camera == null ? "none" : camera.pixelWidth + "x" + camera.pixelHeight)
               + " clusters=" + (clustered == null ? "none" : clustered.m_width + "x" + clustered.m_height)
               + " hudApply=" + (_hud == null || _hud.WasCollected ? "none" : _hud.enabled ? "on" : "off") + " hudMaterial=" + (_hudMaterial != null)
               + " presenting=" + (_presentHud != null && !_presentHud.WasCollected && _presentHud.enabled && _presenter != null && _presenter.enabled)
               + " mipTextures=" + MipBias.Count + " fovLate=" + _fovLate + "/" + _fovFrames
               + " hdr=" + (camera != null && camera.allowHDR) + " stackAA=" + (_layer == null || _layer.WasCollected ? "none" : _layer.antialiasingMode.ToString())
               + " playerAA=" + _layerMode + " model=" + Plugin.Preset.Value + " exposure=" + Plugin.Exposure.Value
               + " hudMeasured=" + _measureHud + " focus=" + Frame.Focus;
    }

    /// <summary>Development driver: switch the mode at run time (written to the config like any setting).</summary>
    internal static string SetMode(string mode)
    {
        Plugin.Mode.Value = (DlssMode)Enum.Parse(typeof(DlssMode), mode);
        return Readback();
    }

    /// <summary>Development driver: sign multipliers (1 or −1) for the jitter and the motion vectors, to compare conventions in one session.</summary>
    internal static string Signs(float jitterX, float jitterY, float motionX, float motionY)
    {
        (_signJitterX, _signJitterY, _signMotionX, _signMotionY) = (jitterX, jitterY, motionX, motionY);
        _reset = true;
        return "signs=" + jitterX + "," + jitterY + "," + motionX + "," + motionY;
    }

    private static Camera? WorldCamera()
    {
        var fps = Players.Local?.FPSCamera;
        return fps == null || fps.WasCollected ? null : fps.m_camera;
    }

    private static void PreCull(Camera camera)
    {
        var pointer = camera.Pointer;
        if (pointer == _presenterPointer)
        {
            // The presenter draws right after the world camera (its depth): that camera's render is over, so the jitter goes.
            RestoreProjection();
            if (ForgeCore.Log.Developer) CountLateFieldOfView();
            return;
        }
        if (_active)
        {
            // Running, the pass knows its camera by its native object (the wrapper this pass holds keeps that address from being used
            // again), so the other cameras of a frame cost one comparison. FollowWorldCamera ends the pass when the camera is destroyed.
            if (pointer != _cameraPointer) return;
        }
        else
        {
            if (!Plugin.UpscalerOn) return; // off and not running: nothing is read from the game
            var world = WorldCamera();
            if (world == null || world.Pointer != pointer) return;
            _camera = camera;
        }
        var mode = Plugin.Mode.Value;
        var status = DlssBridge.Status;
        if (!Plugin.UpscalerOn || status is not (DlssBridge.State.Idle or DlssBridge.State.Ready or DlssBridge.State.Running))
        {
            Stop();
            return;
        }
        if (status == DlssBridge.State.Idle)
        {
            DlssBridge.Initialize(Texture2D.whiteTexture); // NGX starts on the render thread; the pass begins once it reports ready
            return;
        }
        // One read of the camera's target a frame; "not set" is the wrapper being null, which needs no call into the game.
        var target = camera.targetTexture;
        if (target is not null && (_world is null || target.Pointer != _world.Pointer))
        {
            Plugin.Logger.ErrorOnce("framegen-camera-target", "The world camera already renders into " + target.name + " (not the screen); DLSS stays off while it does.");
            Stop();
            return;
        }
        if (!_active && camera.GetComponent<UI_Apply>() == null)
        {
            Plugin.Logger.ErrorOnce("framegen-hud-missing", "The world camera has no UI_Apply, through which the DLSS image is presented; DLSS stays off.");
            return;
        }

        int outputWidth = Frame.ScreenWidth, outputHeight = Frame.ScreenHeight; // ForgeCore reads the screen's size once a frame
        bool bridged = DlssBridge.Bridged;
        // The HUD drawn on black and on white is DLSS Frame Generation's HUD layer; AMD FSR's takes the HUD-less colour alone.
        bool measureHud = bridged && Plugin.FrameGenerationOn && Plugin.HudInGeneratedFrames.Value && DlssBridge.FrameGenerationBackend == 1;
        bool made = false;
        if (!_active || mode != _mode || outputWidth != _outputWidth || outputHeight != _outputHeight || measureHud != _measureHud)
        {
            _measureHud = measureHud;
            (_renderWidth, _renderHeight) = DlssBridge.RenderSize(outputWidth, outputHeight, mode);
            (_mode, _outputWidth, _outputHeight) = (mode, outputWidth, outputHeight);
            float ratio = (float)_outputWidth / _renderWidth;
            _phases = Math.Max(8, (int)Math.Ceiling(8f * ratio * ratio - 1e-4f)); // DLSS guide: 8 phases times the output/render area ratio
            EnsureTargets();
            if (_active) MipBias.Apply(Bias());
            _reset = made = true;
        }
        else if (!_world!.IsCreated())
        {
            // Unity dropped the targets' textures (all of them at once, on a display change): make them again, their native textures
            // with them. One target is asked, not each.
            ReleaseTargets();
            EnsureTargets();
            _reset = made = true;
        }
        if (!_active) Start(camera);
        if (made || target is null) camera.targetTexture = _world;
        ScreenSpace.Route(camera, _renderWidth, _renderHeight, _outputWidth, _outputHeight);
        var clustered = ClusteredRendering.Current;
        if (clustered != null && (clustered.m_width != _renderWidth || clustered.m_height != _renderHeight)) clustered.OnResolutionchange(_renderWidth, _renderHeight);

        int now = Time.frameCount;
        if (now - _lastFrame > 1) _reset = true;
        _lastFrame = now;
        KeepStackAntiAliasingOff();
        var textures = camera.depthTextureMode;
        if ((textures & InputTextures) != InputTextures) camera.depthTextureMode = textures | InputTextures;
        if (ForgeCore.Log.Developer) _fovAtPreCull = camera.fieldOfView;

        // Jitter: Halton (2, 3). The projection is offset by (x, y) render pixels.
        _jitterIndex = _jitterIndex % _phases + 1;
        float x = (Halton(_jitterIndex, 2) - 0.5f) * _signJitterX, y = (Halton(_jitterIndex, 3) - 0.5f) * _signJitterY;
        camera.ResetProjectionMatrix();
        var projection = camera.projectionMatrix;
        camera.nonJitteredProjectionMatrix = projection;
        var jittered = projection;
        jittered.m02 -= 2f * x / _renderWidth;
        jittered.m12 -= 2f * y / _renderHeight;
        camera.projectionMatrix = jittered;
        _jittered = true;
        if (!_jitterMeasured) MeasureJitter(projection);

        var frame = DlssBridge.NextFrame();
        frame->Color = _colorPointer;
        frame->Depth = _depthPointer;
        frame->Motion = _motionPointer;
        frame->Output = _outputPointer;
        frame->RenderWidth = (uint)_renderWidth;
        frame->RenderHeight = (uint)_renderHeight;
        frame->OutputWidth = (uint)_outputWidth;
        frame->OutputHeight = (uint)_outputHeight;
        frame->JitterX = x * _jitterScaleX;
        frame->JitterY = y * _jitterScaleY;
        // Unity's motion texture holds current minus previous in UV units; DLSS wants previous minus current in render pixels.
        frame->MotionScaleX = -_renderWidth * _signMotionX;
        frame->MotionScaleY = -_renderHeight * _signMotionY;
        frame->Reset = _reset ? 1 : 0;
        frame->Hdr = 1; // the copied colour is linear (after the stack's tone mapping, still in a half-float target)
        frame->DepthInverted = _reversedDepth ? 1 : 0;
        frame->Quality = DlssBridge.Quality(_mode);
        frame->Exposure = (int)Plugin.Exposure.Value;
        frame->Preset = DlssBridge.Preset(Plugin.Preset.Value);
        (frame->CameraNear, frame->CameraFar, frame->CameraFov) = Planes(projection);
        _reset = false;

        var command = _evaluate!;
        command.Clear();
        command.Blit(CameraTargetId, _colorId);
        CopyGlobal(command, DepthTextureId, _depthId);
        CopyGlobal(command, MotionTextureId, _motionId);
        command.IssuePluginEventAndData(DlssBridge.RenderEvent, DlssBridge.EventEvaluate, (IntPtr)frame);
        if (bridged)
            command.IssuePluginEventAndData(DlssBridge.RenderEvent, DlssBridge.EventFrameGeneration,
                (IntPtr)GenerationFrame(camera, projection, frame->JitterX, frame->JitterY, frame->Reset != 0, now));
    }

    /// <summary>What DLSS is told for a jitter of one render pixel each way, measured once a start on the GPU projection Unity uses for a
    /// render target (its Y flip on Direct3D included), as RiskOfRainUpscaling does (plan §4.2). A point's shift is linear in the
    /// projection's offset, so the two factors hold for every later frame.</summary>
    private static void MeasureJitter(Matrix4x4 projection)
    {
        var shifted = projection;
        shifted.m02 -= 2f / _renderWidth;
        shifted.m12 -= 2f / _renderHeight;
        var anchor = new Vector4(0f, 0f, -1f, 1f);
        Vector4 plain = GL.GetGPUProjectionMatrix(projection, true) * anchor, moved = GL.GetGPUProjectionMatrix(shifted, true) * anchor;
        _jitterScaleX = (moved.x / moved.w - plain.x / plain.w) * _renderWidth * 0.5f;
        _jitterScaleY = -(moved.y / moved.w - plain.y / plain.w) * _renderHeight * 0.5f;
        _jitterMeasured = true;
    }

    /// <summary>Copies the texture a global shader property holds into this pass's target. The texture object is taken at pre-cull: an identifier
    /// made from the property's name, and Unity's built-in <c>Depth</c>, both read the camera colour at AfterImageEffects in game (plan §9).</summary>
    private static void CopyGlobal(CommandBuffer command, int property, RenderTargetIdentifier target)
    {
        var texture = Shader.GetGlobalTexture(property);
        if (texture is not null) command.Blit(texture, target);
        else Plugin.Logger.ErrorOnce("framegen-input-missing", "The camera's depth or motion texture is not set; DLSS runs without it this frame.");
    }

    /// <summary>The frame the presenter's <c>UI_Apply</c> composites (ForgeCore stand-in): the DLSS output in a frame the world camera rendered;
    /// the game's compositor then blits it with the HUD to the presenter's destination, the screen.</summary>
    private static RenderTexture? PresentedFrame()
    {
        if (!_active || _lastFrame != Time.frameCount) return null;
        if (_measureHud) MeasureHud();
        return _output;
    }

    /// <summary>The HUD as a layer of its own for frame generation. The game's compositor lays the HUD over whatever frame it is given, so
    /// over a black frame it leaves the HUD's own light and over a white one that plus what the HUD lets through; the bridge turns the two
    /// into colour and opacity. Run with the compositor's own material, so the helmet glass and every HUD layer are in it as shown.</summary>
    private static void MeasureHud()
    {
        var active = RenderTexture.active;
        Graphics.Blit(Texture2D.blackTexture, _hudOnBlack, _hudMaterial);
        Graphics.Blit(Texture2D.whiteTexture, _hudOnWhite, _hudMaterial);
        RenderTexture.active = active;
    }

    /// <summary>The presenter's pre-cull, development readback: counts frames where FPSCamera changed the field of view after the world camera's pre-cull.</summary>
    private static void CountLateFieldOfView()
    {
        if (!_active || _lastFrame != Time.frameCount || NativeObjects.Gone(_camera)) return;
        _fovFrames++;
        if (_camera.fieldOfView != _fovAtPreCull) _fovLate++;
    }

    private static void Start(Camera camera)
    {
        _evaluate ??= new CommandBuffer { name = "ForgeFrameGen DLSS" };
        camera.AddCommandBuffer(EvaluateEvent, _evaluate);
        if (_presenter == null || _presenter.WasCollected)
        {
            var holder = new GameObject("ForgeFrameGen presenter");
            UnityEngine.Object.DontDestroyOnLoad(holder);
            _presenter = holder.AddComponent<Camera>();
            _presenter.cullingMask = 0;
            _presenter.clearFlags = CameraClearFlags.Nothing;
            _presenter.allowHDR = false;
            _presenter.allowMSAA = false;
            _presenter.useOcclusionCulling = false;
            _presenter.renderingPath = RenderingPath.Forward;
            _presenterPointer = _presenter.Pointer;
        }
        _cameraPointer = camera.Pointer;
        _reversedDepth = SystemInfo.usesReversedZBuffer;
        _linearColour = QualitySettings.activeColorSpace == ColorSpace.Linear;
        _jitterMeasured = false;
        camera.useJitteredProjectionMatrixForTransparentRendering = true;
        _presenter.depth = camera.depth + 0.001f;
        _presenter.enabled = _presenting = true;

        var adapter = Players.Local?.FPSCamera?.postProcessing;
        _layer = adapter == null || adapter.WasCollected ? null : adapter.m_ppLayer;
        if (_layer == null || adapter!.m_useOldStack)
            Plugin.Logger.ErrorOnce("framegen-postprocess-missing", "The world camera has no post-processing v2 layer to take TAA off; DLSS runs, but the game's own anti-aliasing may run on top of it.");
        else _layerMode = _layer.antialiasingMode;

        _hud = camera.GetComponent<UI_Apply>()!; // checked in PreCull before the pass starts
        _hudMaterial = _hud.GetUIBlitterInstance(); // the game's own getter: makes the instance when there is none or it was destroyed
        _hud.enabled = false;
        // The game's compositor on the presenter, with the world camera's HUD material instance: borrowed, and handed back in Stop, which
        // destroys the component.
        _presentHud = _presenter.gameObject.AddComponent<UI_Apply>();
        _presentHud.m_UIBlitterMaterial = _hud.m_UIBlitterMaterial;
        _presentHud.m_UIBlitterMaterial_Instance = _hudMaterial;
        _presentHud.enabled = true;
        // Composite steps of other mods (the outline) see the world camera's UI_Apply and lay over the full-size picture shown.
        _standIn = Frame.StandInForHud(Plugin.Id, _hud, _presentHud, PresentedFrameStep);
        MipBias.Apply(Bias());
        _active = true;
        _reset = true;
    }

    private static void Stop()
    {
        if (!_active) return;
        _active = false;
        _cameraPointer = IntPtr.Zero;
        if (_camera != null && !_camera.WasCollected)
        {
            if (_evaluate != null) _camera.RemoveCommandBuffer(EvaluateEvent, _evaluate);
            if (_world != null && _camera.targetTexture != null && _camera.targetTexture.Pointer == _world.Pointer) _camera.targetTexture = null;
            _camera.ResetProjectionMatrix();
            _jittered = false;
            _camera.useJitteredProjectionMatrixForTransparentRendering = false;
        }
        var clustered = ClusteredRendering.Current;
        if (clustered != null) clustered.OnResolutionchange(Math.Max(16, Screen.width), Math.Max(16, Screen.height));
        _standIn?.Dispose();
        _standIn = null;
        if (_hud != null && !_hud.WasCollected) _hud.enabled = true;
        _hud = null;
        if (_presenter != null && !_presenter.WasCollected) _presenter.enabled = false;
        _presenting = false;
        // The material instance belongs to the world camera's UI_Apply, which destroys it in its OnDestroy (the player is despawned on leaving
        // a session), so the presenter's compositor must not keep it; and a compositor without one must not stay either: the game adds every
        // UI_Apply to its static list in Start, takes it out only in OnDestroy, and UI_Apply.SetMenuStyle (each focus change) sets a keyword
        // on every listed one's material without a check. The instance is taken off first, so the component's OnDestroy destroys nothing of
        // the camera's; the next start adds a new one. Destroy, not DestroyImmediate: Stop runs in camera callbacks.
        if (_presentHud != null && !_presentHud.WasCollected)
        {
            _presentHud.enabled = false;
            _presentHud.m_UIBlitterMaterial_Instance = null;
            UnityEngine.Object.Destroy(_presentHud);
        }
        _presentHud = null;
        _hudMaterial = null;
        if (_layer != null && !_layer.WasCollected) _layer.antialiasingMode = _layerMode;
        _layer = null;
        ScreenSpace.Clear();
        MipBias.Restore();
        ReleaseTargets(); // off, the pass holds no video memory; the next start makes them again
        DlssBridge.ReleaseFrameGeneration(); // and frame generation's shared inputs and resources go with it
        SyncPreCull();
    }

    private static float Bias() => _renderWidth >= _outputWidth ? 0f : Mathf.Log((float)_renderWidth / _outputWidth, 2f) - 1f;

    /// <summary>The player's anti-aliasing setting can be applied again at any time (settings menu); while DLSS runs the stack's mode stays None
    /// and the player's choice is remembered for when DLSS stops.</summary>
    private static void KeepStackAntiAliasingOff()
    {
        if (_layer is null) return; // the layer lives and dies with the camera, and reading its field calls nothing
        var mode = _layer.antialiasingMode;
        if (mode == PostProcessLayer.Antialiasing.None) return;
        _layerMode = mode;
        _layer.antialiasingMode = PostProcessLayer.Antialiasing.None;
    }

    /// <summary>(Re)creates the targets for the current render and output sizes.</summary>
    private static void EnsureTargets()
    {
        if (_world == null || _world.width != _renderWidth || _world.height != _renderHeight)
        {
            if (_world != null && _camera != null && !_camera.WasCollected && _camera.targetTexture != null && _camera.targetTexture.Pointer == _world.Pointer)
                _camera.targetTexture = null;
            Release(ref _world); Release(ref _color); Release(ref _depth); Release(ref _motion);
            _world = Target(_renderWidth, _renderHeight, 24, RenderTextureFormat.ARGBHalf, false, "ForgeFrameGen world");
            _color = Target(_renderWidth, _renderHeight, 0, RenderTextureFormat.ARGBHalf, false, "ForgeFrameGen colour");
            _depth = Target(_renderWidth, _renderHeight, 0, RenderTextureFormat.RFloat, false, "ForgeFrameGen depth");
            _motion = Target(_renderWidth, _renderHeight, 0, RenderTextureFormat.RGHalf, false, "ForgeFrameGen motion");
            (_colorPointer, _depthPointer, _motionPointer) = (_color.GetNativeTexturePtr(), _depth.GetNativeTexturePtr(), _motion.GetNativeTexturePtr());
            (_colorId, _depthId, _motionId) = (new RenderTargetIdentifier(_color), new RenderTargetIdentifier(_depth), new RenderTargetIdentifier(_motion));
        }
        if (_output == null || _output.width != _outputWidth || _output.height != _outputHeight)
        {
            Release(ref _output);
            _output = Target(_outputWidth, _outputHeight, 0, RenderTextureFormat.ARGBHalf, true, "ForgeFrameGen output");
            _outputPointer = _output.GetNativeTexturePtr();
        }
        if (!_measureHud || (_hudOnBlack != null && (_hudOnBlack.width != _outputWidth || _hudOnBlack.height != _outputHeight)))
        {
            Release(ref _hudOnBlack); Release(ref _hudOnWhite);
            _hudOnBlackPointer = _hudOnWhitePointer = IntPtr.Zero;
        }
        if (_measureHud && _hudOnBlack == null)
        {
            // Stored as the back buffer stores the picture (sRGB encoded, 8 bits a channel).
            _hudOnBlack = Target(_outputWidth, _outputHeight, 0, RenderTextureFormat.ARGB32, false, "ForgeFrameGen HUD on black", RenderTextureReadWrite.sRGB);
            _hudOnWhite = Target(_outputWidth, _outputHeight, 0, RenderTextureFormat.ARGB32, false, "ForgeFrameGen HUD on white", RenderTextureReadWrite.sRGB);
            (_hudOnBlackPointer, _hudOnWhitePointer) = (_hudOnBlack.GetNativeTexturePtr(), _hudOnWhite.GetNativeTexturePtr());
        }
    }

    private static void ReleaseTargets()
    {
        if (_world != null && _camera != null && !_camera.WasCollected && _camera.targetTexture != null && _camera.targetTexture.Pointer == _world.Pointer)
            _camera.targetTexture = null;
        Release(ref _world); Release(ref _color); Release(ref _depth); Release(ref _motion); Release(ref _output);
        Release(ref _hudOnBlack); Release(ref _hudOnWhite);
        _colorPointer = _depthPointer = _motionPointer = _outputPointer = _hudOnBlackPointer = _hudOnWhitePointer = IntPtr.Zero;
    }

    private static RenderTexture Target(int width, int height, int depth, RenderTextureFormat format, bool unordered, string name,
        RenderTextureReadWrite space = RenderTextureReadWrite.Linear)
    {
        var texture = new RenderTexture(width, height, depth, format, space) { name = name, enableRandomWrite = unordered, useMipMap = false };
        texture.Create();
        UnityEngine.Object.DontDestroyOnLoad(texture);
        return texture;
    }

    /// <summary>Frees the target's texture and destroys the object: <c>Release</c> alone leaves a <c>RenderTexture</c> behind for every resize.</summary>
    private static void Release(ref RenderTexture? texture)
    {
        if (texture != null && !texture.WasCollected)
        {
            texture.Release();
            UnityEngine.Object.Destroy(texture);
        }
        texture = null;
    }

    private static float Halton(int index, int radix)
    {
        float result = 0f, fraction = 1f / radix;
        for (; index > 0; index /= radix, fraction /= radix) result += index % radix * fraction;
        return result;
    }
}
