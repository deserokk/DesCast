using System;
using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;

namespace DesCast;

/// <summary>
/// Everything we read out of the running game: the D3D11 device, a private copy of the
/// depth buffer, the camera matrices, and which house the player is standing in.
///
/// ⭐⭐ Every one of these is a documented FFXIVClientStructs property. There are no hooks,
/// no signature scans and no writes into game memory anywhere in this plugin — which is
/// the single most important fact about the whole design, and the reason the original
/// plan's fear of "pointer surgery in the render path" turned out to be misplaced.
///
/// ⚠ The one non-obvious correctness rule: <see cref="Marshal.AddRef"/> every game-owned
/// COM pointer before wrapping it in a Vortice object, because disposing that wrapper
/// releases a reference the game still believes it owns.
/// </summary>
public sealed unsafe class GameView : IDisposable
{
    private ID3D11DeviceContext? context;
    private ID3D11Device? device;

    // Our private copy of the depth buffer, plus the view the shader samples it through.
    private ID3D11Texture2D? depthCopy;
    private ID3D11ShaderResourceView? depthSrv;
    private uint depthWidth, depthHeight;
    private Format depthFormat = Format.Unknown;
    private uint sampleCount, sampleQuality;

    public ID3D11Device? Device => device;
    public ID3D11DeviceContext? Context => context;
    public ID3D11ShaderResourceView? DepthSrv => depthSrv;

    /// <summary>Backbuffer size the game is rendering at, in pixels.</summary>
    public float RenderWidth { get; private set; }
    public float RenderHeight { get; private set; }

    /// <summary>Set when something went wrong, for the editor to show. Never silent.</summary>
    public string? Error { get; private set; }

    public bool Ready => device != null && context != null;

    public bool Initialise()
    {
        if (Ready) return true;
        try
        {
            var dev = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
            if (dev == null || dev->D3D11DeviceContext == null)
            {
                Error = "Game D3D11 device context not available yet.";
                return false;
            }

            var ctxPtr = (nint)dev->D3D11DeviceContext;
            Marshal.AddRef(ctxPtr);
            context = new ID3D11DeviceContext(ctxPtr);
            device = context.Device;
            Error = null;
            return true;
        }
        catch (Exception ex)
        {
            Error = $"D3D init failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Take this frame's depth buffer. Called from Framework.Update — early enough that
    /// the copy is of a completed scene, and off the ImGui draw path.
    ///
    /// ⚠ A straight CopyResource, not a hook. The game's depth texture is created without
    /// ShaderResource binding, so it cannot be sampled directly; our copy declares the
    /// binding we need. CopyResource only requires matching format and dimensions, not
    /// matching bind flags, which is what makes this legal.
    /// </summary>
    public void CaptureDepth()
    {
        if (!Ready) return;
        try
        {
            nint gameDepth = 0;

            var rtm = RenderTargetManager.Instance();
            if (rtm != null && rtm->DepthStencil != null && rtm->DepthStencil->D3D11Texture2D != null)
            {
                gameDepth = (nint)rtm->DepthStencil->D3D11Texture2D;
                RenderWidth = rtm->Resolution_Width;
                RenderHeight = rtm->Resolution_Height;
            }
            else
            {
                // Fallback for the window between zone load and the render manager settling.
                var dev = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
                if (dev != null && dev->SwapChain != null
                    && dev->SwapChain->DepthStencil != null
                    && dev->SwapChain->DepthStencil->D3D11Texture2D != null)
                    gameDepth = (nint)dev->SwapChain->DepthStencil->D3D11Texture2D;
            }

            if (gameDepth == 0) return;

            Marshal.AddRef(gameDepth);
            using var src = new ID3D11Texture2D(gameDepth);
            EnsureDepthCopy(src.Description);
            context!.CopyResource(depthCopy!, src);
            Error = null;
        }
        catch (Exception ex)
        {
            Error = $"Depth capture failed: {ex.Message}";
        }
    }

    /// <summary>
    /// (Re)build the copy when the game's depth target changes shape — which happens on
    /// resolution change, and on some graphics-settings changes mid-session.
    /// </summary>
    private void EnsureDepthCopy(Texture2DDescription desc)
    {
        if (depthCopy != null
            && depthWidth == desc.Width && depthHeight == desc.Height
            && depthFormat == desc.Format
            && sampleCount == desc.SampleDescription.Count
            && sampleQuality == desc.SampleDescription.Quality)
            return;

        depthSrv?.Dispose();
        depthCopy?.Dispose();
        depthSrv = null;
        depthCopy = null;

        depthWidth = desc.Width;
        depthHeight = desc.Height;
        depthFormat = desc.Format;
        sampleCount = desc.SampleDescription.Count;
        sampleQuality = desc.SampleDescription.Quality;

        depthCopy = device!.CreateTexture2D(new Texture2DDescription
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1u,
            ArraySize = 1u,
            Format = desc.Format,
            SampleDescription = new SampleDescription(sampleCount, sampleQuality),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.DepthStencil,
            CPUAccessFlags = CpuAccessFlags.None,
        });

        // Depth textures are created typeless so they can be bound two different ways;
        // the SRV has to name the readable half of the pair explicitly.
        var srvFormat = desc.Format switch
        {
            Format.R24G8_Typeless => Format.R24_UNorm_X8_Typeless,
            Format.R32_Typeless => Format.R32_Float,
            Format.R32G8X24_Typeless => Format.R32_Float_X8X24_Typeless,
            Format.D24_UNorm_S8_UInt => Format.R24_UNorm_X8_Typeless,
            Format.D32_Float => Format.R32_Float,
            _ => desc.Format,
        };

        depthSrv = device.CreateShaderResourceView(depthCopy, new ShaderResourceViewDescription
        {
            Format = srvFormat,
            ViewDimension = sampleCount > 1
                ? ShaderResourceViewDimension.Texture2DMultisampled
                : ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1u, MostDetailedMip = 0u },
        });
    }

    /// <summary>What the shader needs to know about where the eye is this frame.</summary>
    public readonly record struct CameraState(
        Matrix4x4 ViewProjection,
        Matrix4x4 InverseViewProjection,
        Vector3 Position);

    /// <summary>
    /// Read the active camera. Chain, all documented:
    /// CameraManager → active Camera → CameraBase → SceneCamera → RenderCamera, which
    /// carries the real view and projection matrices the game is drawing with. Taking
    /// the game's own matrices rather than rebuilding them from FoV means the panel
    /// agrees with the scene automatically, including in gpose and cutscenes.
    /// </summary>
    public CameraState? GetCamera()
    {
        var mgr = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
        if (mgr == null) return null;

        var active = mgr->GetActiveCamera();
        if (active == null) return null;

        RenderCamera* rc = active->CameraBase.SceneCamera.RenderCamera;
        if (rc == null) return null;

        // ⚠⚠ The game's view matrix is NOT a clean transform — its fourth column carries
        // values that are not the 0,0,0,1 a projection expects. Multiply it by the
        // projection as-is and every point comes back with a nonsense w, which collapses
        // the perspective divide: on the CPU the panel's screen bounds vanish, and in the
        // shader every comparison becomes not-a-number, so nothing is ever discarded and
        // the whole screen fills with a single clamped texel. Cost one red screen to find.
        // Xiv Media Player performs exactly this fix-up, which is what identified it.
        var view = rc->ViewMatrix;
        view.M14 = 0f;
        view.M24 = 0f;
        view.M34 = 0f;
        view.M44 = 1f;

        var proj = rc->ProjectionMatrix;
        var viewProj = view * proj;

        if (!Matrix4x4.Invert(viewProj, out var inv)) return null;

        // ⚠ Eye position stays on the camera's own Origin field. Xiv Media Player derives
        // it by inverting the view matrix instead, which is more principled — but Origin
        // is what this plugin has actually been observed rendering correctly with, and
        // swapping a working input for a better-looking one is how a fix becomes a
        // regression. Revisit only if first-person or gpose turns out to disagree.
        return new CameraState(viewProj, inv, rc->Origin);
    }

    /// <summary>
    /// The house the player is standing in. <see cref="Id"/> is the identity; the rest is
    /// for display only.
    ///
    /// ⚠ <see cref="WardIndex"/> and <see cref="PlotIndex"/> are **zero-based**, as their
    /// names say — the game's own UI shows them plus one. Ward 2 on screen is index 1
    /// here. Store the index, display the number.
    /// </summary>
    public readonly record struct HouseLocation(
        ulong Id, ushort WorldId, ushort TerritoryTypeId,
        byte WardIndex, byte PlotIndex, short RoomNumber)
    {
        /// <summary>What the player would call it: ward 2, plot 47.</summary>
        public int WardNumber => WardIndex + 1;
        public int PlotNumber => PlotIndex + 1;

        public bool Matches(ScreenPlacement s)
            => s.HouseId != 0
                ? s.HouseId == Id
                // Legacy fallback for screens placed before HouseId existed. Compares the
                // same components the old identity used, so an existing screen is found
                // exactly once and then upgraded by MigrateIdentity below.
                : s.Ward == WardIndex && s.Plot == PlotIndex && s.Room == RoomNumber;

        /// <summary>
        /// Stamp the real house id onto a legacy placement the first time we recognise it,
        /// so the weaker identity is used once and never again. ⭐ A migration, not a
        /// default — a changed default cannot reach a config that already exists.
        /// </summary>
        public bool MigrateIdentity(ScreenPlacement s)
        {
            if (s.HouseId != 0) return false;
            s.HouseId = Id;
            s.Ward = s.Plot = s.Room = -1;
            return true;
        }
    }

    /// <summary>
    /// ⚠ Returns null outside housing, which the caller must treat as "draw nothing".
    /// A screen with no house recorded is not a screen that shows up everywhere.
    /// </summary>
    public HouseLocation? GetLocation()
    {
        var hm = HousingManager.Instance();
        if (hm == null || !hm->IsInside()) return null;

        var id = hm->GetCurrentIndoorHouseId();
        if (id.Id == 0) return null;

        return new HouseLocation(
            id.Id, id.WorldId, id.TerritoryTypeId,
            id.WardIndex, id.PlotIndex, id.RoomNumber);
    }

    /// <summary>
    /// Whether the player may rearrange furniture here. ⭐ Chris's idea, and the right
    /// one: the game already decides who may build in a given house — FC estate, personal
    /// house, apartment, FC room — so inheriting that answer gates screen placement
    /// correctly in every case without us modelling permissions at all.
    /// </summary>
    public bool CanPlaceHere()
    {
        var hm = HousingManager.Instance();
        return hm != null && hm->IsInside() && hm->HasHousePermissions();
    }

    public void Dispose()
    {
        depthSrv?.Dispose();
        depthCopy?.Dispose();
        // ⚠ context/device are the game's, borrowed via AddRef. Release our reference and
        // nothing else — disposing them outright would take the game's renderer with us.
        context?.Dispose();
        depthSrv = null;
        depthCopy = null;
        context = null;
        device = null;
    }
}
