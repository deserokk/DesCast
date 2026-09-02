using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DesCast;

/// <summary>
/// Draws the panels. One offscreen D3D11 pass per frame; the result is handed to ImGui as
/// a single full-screen image.
///
/// ⭐⭐ The mechanism, because it is not obvious from the code: we do <b>not</b> transform a
/// quad's four corners and stretch a texture over them. We draw one triangle covering the
/// whole screen and let the pixel shader ask, per pixel, "does the camera ray through me
/// hit this panel's rectangle, and if so where?" Every pixel is solved independently,
/// which is why the picture is perspective-correct with no warping at oblique angles, and
/// why the occlusion test is a free extra line — by the time we know the ray hit the
/// panel, we already know exactly how far away that hit was.
/// </summary>
public sealed class ScreenRenderer : IDisposable
{
    private const int MaxTextureDim = 8192;

    private readonly GameView game;

    private ID3D11VertexShader? vs;
    private ID3D11PixelShader? ps;
    private ID3D11Buffer? cb;
    private ID3D11SamplerState? sampler;
    private ID3D11BlendState? blend;
    private ID3D11RasterizerState? raster;
    private ID3D11DepthStencilState? noDepth;

    private ID3D11Texture2D? target;
    private ID3D11RenderTargetView? targetRtv;
    private ID3D11ShaderResourceView? targetSrv;
    private int targetW, targetH;

    public bool Initialised { get; private set; }

    // ⭐ Compiling HLSL costs ~170ms and was doing it inside the draw callback, which is a
    // visible stall the first time you walk into a room with a screen. The compiler is
    // pure CPU and never touches the D3D device, so it moves to a worker thread safely —
    // ⚠ but creating the shader objects does touch the device and stays on this thread.
    // That split is the whole point: compile off-thread, create on-thread.
    private System.Threading.Tasks.Task<(ReadOnlyMemory<byte> Vs, ReadOnlyMemory<byte> Ps)>? compileTask;

    /// <summary>Non-null when something failed. Surfaced in the editor — never swallowed.</summary>
    public string? Error { get; private set; }

    /// <summary>The composited frame, for ImGui to draw. Null until something renders.</summary>
    public nint OutputHandle => targetSrv?.NativePointer ?? 0;

    public ScreenRenderer(GameView game) => this.game = game;

    /// <summary>
    /// Constant buffer layout. ⚠ Must match the cbuffer in the HLSL below exactly, and
    /// every member is padded to 16 bytes because that is how D3D11 packs constants.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 InvViewProj;
        public Vector4 CamPos;   // xyz = eye
        public Vector4 Center;   // xyz = panel centre, w = opacity
        public Vector4 AxisX;    // xyz = half-width vector
        public Vector4 AxisY;    // xyz = half-height vector
        public Vector4 Flags;    // x reverseZ, y disableOcclusion, z depthW, w depthH
    }

    private const string Hlsl = @"
cbuffer Params : register(b0)
{
    row_major float4x4 ViewProj;
    row_major float4x4 InvViewProj;
    float4 CamPos;
    float4 Center;
    float4 AxisX;
    float4 AxisY;
    float4 Flags;
};

Texture2D<float4> Content     : register(t0);
Texture2D<float>  SceneDepth  : register(t1);
SamplerState      LinearClamp : register(s0);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

// Fullscreen triangle from the vertex id alone — no vertex or index buffer exists.
VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv  = uv;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    // Rebuild this pixel's camera ray by unprojecting a point at mid-depth. Mid-depth
    // rather than the near or far plane so the maths is identical whether or not the
    // game uses a reversed depth range.
    float2 ndc = float2(i.uv.x * 2.0 - 1.0, 1.0 - i.uv.y * 2.0);
    float4 p = mul(float4(ndc, 0.5, 1.0), InvViewProj);
    p /= p.w;
    float3 dir = normalize(p.xyz - CamPos.xyz);

    float3 ax = AxisX.xyz;
    float3 ay = AxisY.xyz;
    float3 n  = normalize(cross(ax, ay));

    float denom = dot(dir, n);
    if (abs(denom) < 1e-6) discard;          // ray parallel to the panel

    float t = dot(Center.xyz - CamPos.xyz, n) / denom;
    if (t <= 0.0) discard;                    // panel is behind the eye

    float3 hit   = CamPos.xyz + dir * t;
    float3 local = hit - Center.xyz;

    // Position within the panel, in units of half-width / half-height.
    float u = dot(local, ax) / dot(ax, ax);
    float v = dot(local, ay) / dot(ay, ay);
    if (abs(u) > 1.0 || abs(v) > 1.0) discard;

    // Occlusion. The hit point is projected with the game's own view-projection, so its
    // depth is directly comparable with the depth buffer without any linearisation.
    if (Flags.y < 0.5)
    {
        float4 clip = mul(float4(hit, 1.0), ViewProj);
        float ndcZ = clip.z / clip.w;

        int2 dcoord = int2(i.uv * float2(Flags.z, Flags.w));
        float scene = SceneDepth.Load(int3(dcoord, 0));

        // With reversed depth, nearer geometry holds the larger value.
        bool occluded = (Flags.x > 0.5) ? (ndcZ < scene) : (ndcZ > scene);
        if (occluded) discard;
    }

    float2 uv = float2(u * 0.5 + 0.5, 0.5 - v * 0.5);
    float4 c = Content.SampleLevel(LinearClamp, uv, 0);
    c.a *= Center.w;
    return c;
}
";

    public bool Initialise()
    {
        if (Initialised) return true;
        if (!game.Ready) return false;

        // ⚠ Compiled at runtime rather than shipped as bytecode: the shader source stays
        // readable next to the code that explains it, instead of being an opaque blob.
        // Started once, on a worker; every frame until it finishes simply draws nothing.
        compileTask ??= System.Threading.Tasks.Task.Run(() =>
        {
            if (!TryCompile("VSMain", "vs_5_0", out var v)) throw new InvalidOperationException(Error ?? "vertex shader");
            if (!TryCompile("PSMain", "ps_5_0", out var p)) throw new InvalidOperationException(Error ?? "pixel shader");
            return (v, p);
        });

        if (!compileTask.IsCompleted) return false;

        if (compileTask.IsFaulted)
        {
            Error ??= compileTask.Exception?.GetBaseException().Message ?? "Shader compilation failed.";
            return false;
        }

        try
        {
            var dev = game.Device!;
            var (vsCode, psCode) = compileTask.Result;

            vs = dev.CreateVertexShader(vsCode.Span);
            ps = dev.CreatePixelShader(psCode.Span);

            cb = dev.CreateBuffer(
                (uint)Marshal.SizeOf<Params>(),
                BindFlags.ConstantBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write);

            sampler = dev.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MaxLOD = float.MaxValue,
            });

            var bd = new BlendDescription();
            bd.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = Blend.SourceAlpha,
                DestinationBlend = Blend.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.InverseSourceAlpha,
                BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All,
            };
            blend = dev.CreateBlendState(bd);

            // ⭐ Scissor ON, deliberately — see the bounds calculation in Render(). It is
            // the difference between paying for every pixel on screen and paying only for
            // the rectangle a panel actually covers.
            raster = dev.CreateRasterizerState(new RasterizerDescription
            {
                CullMode = CullMode.None,
                FillMode = FillMode.Solid,
                DepthClipEnable = false,
                ScissorEnable = true,
            });

            // We do the depth comparison ourselves in the shader, against a copy — the
            // pipeline's own depth test stays off.
            noDepth = dev.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = false,
                DepthWriteMask = DepthWriteMask.Zero,
                StencilEnable = false,
            });

            Initialised = true;
            Error = null;
            return true;
        }
        catch (Exception ex)
        {
            Error = $"Renderer init failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Compile one entry point. ⚠ A shader that fails to compile must say why — the error
    /// blob carries the line and the reason, and losing it turns a typo into "the screens
    /// just don't work".
    /// </summary>
    private bool TryCompile(string entryPoint, string profile, out ReadOnlyMemory<byte> code)
    {
        code = default;
        Blob? blob = null, errors = null;
        try
        {
            var result = Compiler.Compile(Hlsl, entryPoint, "DesCast", profile, out blob, out errors);
            if (result.Failure || blob is null)
            {
                var detail = errors?.AsString()?.Trim();
                Error = string.IsNullOrEmpty(detail)
                    ? $"Shader {entryPoint} failed to compile ({result.Description})."
                    : $"Shader {entryPoint} failed to compile: {detail}";
                return false;
            }
            // ⚠ Copy, do not hand back blob.AsMemory() — that memory is owned by the blob
            // and the finally below frees it. A few KB once, in exchange for not reading
            // freed memory at shader-creation time.
            code = blob.AsSpan().ToArray();
            return true;
        }
        finally
        {
            errors?.Dispose();
            blob?.Dispose();
        }
    }

    private bool EnsureTarget(int w, int h)
    {
        if (w <= 0 || h <= 0 || w > MaxTextureDim || h > MaxTextureDim) return false;
        if (target != null && targetW == w && targetH == h) return true;

        targetSrv?.Dispose();
        targetRtv?.Dispose();
        target?.Dispose();
        targetSrv = null; targetRtv = null; target = null;

        var dev = game.Device!;
        target = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1u,
            ArraySize = 1u,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
        });
        targetRtv = dev.CreateRenderTargetView(target);
        targetSrv = dev.CreateShaderResourceView(target);
        targetW = w; targetH = h;
        return true;
    }

    /// <summary>
    /// One rectangle to draw, resolved to pure geometry.
    ///
    /// ⭐⭐ Note what is absent: no name, no file path, no playback state, no idea where the
    /// pixels came from. Xiv Media Player's equivalent call takes about forty parameters
    /// and that is exactly why status boards cannot be added to it. Keep this struct
    /// boring — a locally-drawn noticeboard, a hosted poster and a video frame must all be
    /// indistinguishable by the time they arrive here.
    /// </summary>
    public readonly record struct Panel(
        Vector3 Center,
        Vector3 AxisX,
        Vector3 AxisY,
        float Opacity,
        nint ContentSrv);

    /// <summary>
    /// Composite every visible panel into the offscreen target.
    /// Returns false if nothing was drawn, in which case the caller must not present.
    /// </summary>
    public bool Render(
        int width, int height,
        GameView.CameraState cam,
        ReadOnlySpan<Panel> screens,
        bool reverseDepth, bool disableOcclusion)
    {
        if (!Initialised || screens.Length == 0) return false;
        if (game.DepthSrv == null && !disableOcclusion) return false;
        if (!EnsureTarget(width, height)) return false;

        var ctx = game.Context!;

        // ⚠ Borrowing the game's context means putting back exactly what we found. Save
        // the bound render targets before touching anything.
        var savedRtvs = new ID3D11RenderTargetView[1];
        ctx.OMGetRenderTargets(1u, savedRtvs, out var savedDsv);

        try
        {
            ctx.ClearRenderTargetView(targetRtv!, new Color4(0f, 0f, 0f, 0f));
            ctx.OMSetRenderTargets(targetRtv!);
            ctx.RSSetViewport(0, 0, width, height);
            ctx.RSSetState(raster);
            ctx.OMSetBlendState(blend, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
            ctx.OMSetDepthStencilState(noDepth, 0);

            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.IASetInputLayout(null);
            ctx.VSSetShader(vs);
            ctx.PSSetShader(ps);
            ctx.PSSetSampler(0, sampler);
            ctx.PSSetConstantBuffer(0, cb);
            ctx.PSSetShaderResource(1, game.DepthSrv);

            var drew = false;
            foreach (var panel in screens)
            {
                if (panel.ContentSrv == 0) continue;

                var p = new Params
                {
                    ViewProj = cam.ViewProjection,
                    InvViewProj = cam.InverseViewProjection,
                    CamPos = new Vector4(cam.Position, 0f),
                    Center = new Vector4(panel.Center, Math.Clamp(panel.Opacity, 0f, 1f)),
                    AxisX = new Vector4(panel.AxisX, 0f),
                    AxisY = new Vector4(panel.AxisY, 0f),
                    Flags = new Vector4(
                        reverseDepth ? 1f : 0f,
                        disableOcclusion ? 1f : 0f,
                        game.RenderWidth > 0 ? game.RenderWidth : width,
                        game.RenderHeight > 0 ? game.RenderHeight : height),
                };

                // ⭐ Only shade the pixels this panel can possibly cover. The pass is a
                // full-screen triangle, so without a scissor every panel costs a full
                // screen of pixel shader whether it fills the view or is a postcard across
                // the room. Projecting the four corners and clipping to their bounding box
                // turns that into "roughly the panel's own area", which is what makes a
                // still image effectively free. Falls back to the whole screen when any
                // corner is behind the eye, where the projection is not trustworthy.
                if (!TryGetScissor(panel, cam, width, height, out var sx, out var sy, out var sw, out var sh))
                    (sx, sy, sw, sh) = (0, 0, width, height);
                ctx.RSSetScissorRect(sx, sy, sw, sh);

                var mapped = ctx.Map(cb!, MapMode.WriteDiscard);
                unsafe { *(Params*)mapped.DataPointer = p; }
                ctx.Unmap(cb!, 0);

                Marshal.AddRef(panel.ContentSrv); // matched by the using-dispose below
                using var content = new ID3D11ShaderResourceView(panel.ContentSrv);
                ctx.PSSetShaderResource(0, content);

                ctx.Draw(3, 0);
                drew = true;
            }

            return drew;
        }
        catch (Exception ex)
        {
            Error = $"Render failed: {ex.Message}";
            return false;
        }
        finally
        {
            // Unbind our resources before handing the context back — leaving an SRV bound
            // that the game later wants as a render target is a silent, confusing failure.
            ctx.PSSetShaderResource(0, null);
            ctx.PSSetShaderResource(1, null);
            ctx.OMSetRenderTargets(savedRtvs, savedDsv);
            foreach (var r in savedRtvs) r?.Dispose();
            savedDsv?.Dispose();
        }
    }

    /// <summary>
    /// Screen-space bounding box of the panel's four corners, in pixels.
    /// Returns false when any corner is behind the camera — the projection flips sign
    /// there and the resulting box would be nonsense, so the caller widens to full screen.
    /// </summary>
    private static bool TryGetScissor(
        Panel panel, GameView.CameraState cam, int width, int height,
        out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;

        var c = panel.Center;
        var ax = panel.AxisX;
        var ay = panel.AxisY;

        Span<Vector3> corners = stackalloc Vector3[4];
        corners[0] = c - ax + ay;
        corners[1] = c + ax + ay;
        corners[2] = c + ax - ay;
        corners[3] = c - ax - ay;

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        foreach (var world in corners)
        {
            var clip = Vector4.Transform(new Vector4(world, 1f), cam.ViewProjection);
            if (clip.W <= 0.0001f) return false;

            var px = (clip.X / clip.W * 0.5f + 0.5f) * width;
            var py = (0.5f - clip.Y / clip.W * 0.5f) * height;

            minX = MathF.Min(minX, px); maxX = MathF.Max(maxX, px);
            minY = MathF.Min(minY, py); maxY = MathF.Max(maxY, py);
        }

        // One pixel of slack so the panel's own edge is never clipped by rounding.
        var l = Math.Clamp((int)MathF.Floor(minX) - 1, 0, width);
        var t = Math.Clamp((int)MathF.Floor(minY) - 1, 0, height);
        var r = Math.Clamp((int)MathF.Ceiling(maxX) + 1, 0, width);
        var b = Math.Clamp((int)MathF.Ceiling(maxY) + 1, 0, height);

        if (r <= l || b <= t) return false;

        x = l; y = t; w = r - l; h = b - t;
        return true;
    }

    public void Dispose()
    {
        targetSrv?.Dispose();
        targetRtv?.Dispose();
        target?.Dispose();
        noDepth?.Dispose();
        raster?.Dispose();
        blend?.Dispose();
        sampler?.Dispose();
        cb?.Dispose();
        ps?.Dispose();
        vs?.Dispose();
        Initialised = false;
    }
}
