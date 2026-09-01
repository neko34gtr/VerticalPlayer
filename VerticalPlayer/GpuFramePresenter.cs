using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D9;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace VerticalPlayer
{
    /// <summary>
    /// 「高画質化エンジン設計提案」2.3節・段階1〜5の実装。
    ///
    /// 【段階1】BGRAバッファをD3D11→D3D9Ex共有テクスチャ→D3DImageへ渡す土台。
    /// 【段階2】コントラスト/彩度/ガンマをCompute Shaderで適用。
    /// 【段階4】ダイナミックコントラスト（シーン平均輝度ベースの簡易オートレベル、GPU完結）。
    /// 【段階5】超解像（設計提案書2章・案B：古典アルゴリズム版）。
    ///   DNN超解像（案A）はモデル選定・TensorRT統合が別途大きな検証項目になるため、
    ///   まずは確実に60fpsが出せる古典アルゴリズムを実装：
    ///     Pass D: 水平方向Lanczos-3（separable、2パスの1つ目）
    ///     Pass E: 垂直方向Lanczos-3（separable、2つ目）
    ///     Pass F: 軽いアンシャープマスクで解像感を補強
    ///   SR無効時（既定）は従来どおりCSMain(効果適用)が直接共有テクスチャへ書き込む
    ///   高速パスのままで、SR有効時のみ中間テクスチャ(_nativeProcessedTex等)を
    ///   経由する多段パスへ切り替わる設計（無効時の回帰リスクを避けるため）。
    ///   ウィンドウサイズに動的追従する適応的SRではなく、固定倍率(1.5x/2x)である点に注意。
    ///
    /// 【要検証事項（Windows実機でのビルド・動作確認が必須）】
    /// - Vortice.Windows のバージョンによってAPIの引数形が変わる場合がある。
    /// - D3D11とD3D9Exが同一GPUアダプタ上のデバイスであることが共有テクスチャ成立の前提。
    /// - Dispatch後のFlush()のみで同期しており、症状が出た場合はKeyedMutex方式を検討。
    /// - 段階5は新規テクスチャ・シェーダーが最も多く、これまでで最もリスクが高い変更。
    /// </summary>
    public sealed class GpuFramePresenter : IFramePresenter, IDisposable
    {
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();

        public D3DImage D3DImage { get; } = new D3DImage();

        private ID3D11Device? _d3d11Device;
        private ID3D11DeviceContext? _d3d11Context;
        private IDirect3D9Ex? _d3d9;
        private IDirect3DDevice9Ex? _d3d9Device;

        private ID3D11Texture2D? _uploadTex;   // CPU→GPU転送用（Dynamic、常に原寸）
        private ID3D11Texture2D? _sharedTex11; // D3D11側の共有テクスチャ（Default, Shared、SR有効時は拡大後サイズ）
        private IDirect3DTexture9? _sharedTex9;
        private IDirect3DSurface9? _surface9;

        private ID3D11ShaderResourceView? _srvUpload;
        private ID3D11UnorderedAccessView? _uavShared;
        private ID3D11ComputeShader? _effectsCs;
        private ID3D11Buffer? _cbEffects;
        private bool _effectsShaderReady;

        // ── 段階4：ダイナミックコントラスト用リソース ──
        private const int LumaDownSize = 32;
        private ID3D11Texture2D? _lumaDownTex;
        private ID3D11ShaderResourceView? _lumaDownSrv;
        private ID3D11UnorderedAccessView? _lumaDownUav;
        private ID3D11Buffer? _avgLumaBuffer;
        private ID3D11ShaderResourceView? _avgLumaSrv;
        private ID3D11UnorderedAccessView? _avgLumaUav;
        private ID3D11ComputeShader? _lumaDownCs;
        private ID3D11ComputeShader? _lumaReduceCs;
        private ID3D11Buffer? _cbReduce;
        private bool _dynamicContrastShaderReady;
        private float _dynamicContrastStrength;

        // ── 段階5：超解像（原寸処理用の中間テクスチャ＋Lanczosアップスケール＋アンシャープ）──
        private bool _srShaderReady;
        private float _srScale = 1f;      // 1=無効
        private float _lastSrScale = 1f;
        private ID3D11ComputeShader? _srHorizCs, _srVertCs, _srUnsharpCs;
        private ID3D11Buffer? _cbScale, _cbSharp;

        private ID3D11Texture2D? _nativeProcessedTex; // 原寸、CSMain(効果適用)の出力先（SR有効時のみ使用）
        private ID3D11ShaderResourceView? _srvNativeProcessed;
        private ID3D11UnorderedAccessView? _uavNativeProcessed;

        private ID3D11Texture2D? _srHorizTex; // 横方向だけ拡大（縦は原寸のまま）
        private ID3D11ShaderResourceView? _srHorizSrv;
        private ID3D11UnorderedAccessView? _srHorizUav;

        private ID3D11Texture2D? _srVertTex; // 縦横とも拡大後（アンシャープ適用前）
        private ID3D11ShaderResourceView? _srVertSrv;
        private ID3D11UnorderedAccessView? _srVertUav;

        private float _contrast, _saturation, _gamma;

        // ── 比較ビュー（PowerDVD TrueTheater風の左右分割表示）──
        private int _compareMode; // 0=off, 1=single-frame wipe split, 2=dual full-frame side-by-side
        private ID3D11ComputeShader? _compareCs;
        private ID3D11Buffer? _cbCompare;
        private ID3D11Texture2D? _processedFinalTex; // 出力解像度、加工済み結果の一時保存先（比較モード時のみ使用）
        private ID3D11ShaderResourceView? _srvProcessedFinal;
        private ID3D11UnorderedAccessView? _uavProcessedFinal;
        private bool _compareShaderReady;

        private int _w, _h;       // 原寸（デコード解像度）
        private int _outW, _outH; // 表示用最終サイズ（SR無効時は_w/_hと同じ）
        private bool _d3dReady;

        private const string EffectsShaderSource = @"
cbuffer EffectsCB : register(b0)
{
    float Contrast;
    float Saturation;
    float Gamma;
    float DynamicContrastStrength;
};

Texture2D<float4> InputTex : register(t0);
StructuredBuffer<float> AvgLuma : register(t1);
RWTexture2D<float4> OutputTex : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint w, h;
    OutputTex.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;

    float4 c = InputTex.Load(int3(id.xy, 0));

    float avgLuma = AvgLuma[0];
    float autoGammaBoost = lerp(0.35, -0.15, saturate(avgLuma * 1.4));
    float effectiveGamma = Gamma + autoGammaBoost * DynamicContrastStrength;

    float gammaExp = exp2(effectiveGamma);
    float factor = 1.0 + Contrast;

    float3 v = saturate(c.rgb);
    v = pow(v, 1.0 / gammaExp);
    v = (v - 0.5019608) * factor + 0.5019608;

    float gray = dot(v, float3(0.299, 0.587, 0.114));
    v = gray + (v - gray) * (1.0 + Saturation);

    OutputTex[id.xy] = float4(saturate(v), c.a);
}
";

        private const string LumaDownShaderSource = @"
Texture2D<float4> SrcTex : register(t0);
RWTexture2D<float> LumaDown : register(u0);

[numthreads(8, 8, 1)]
void CSDownsampleLuma(uint3 id : SV_DispatchThreadID)
{
    uint outW, outH;
    LumaDown.GetDimensions(outW, outH);
    if (id.x >= outW || id.y >= outH) return;

    uint srcW, srcH;
    SrcTex.GetDimensions(srcW, srcH);

    uint2 srcXY = uint2(
        (uint)((id.x + 0.5) * srcW / outW),
        (uint)((id.y + 0.5) * srcH / outH));
    srcXY = min(srcXY, uint2(srcW - 1, srcH - 1));

    float3 c = SrcTex.Load(int3(srcXY, 0)).rgb;
    LumaDown[id.xy] = dot(c, float3(0.299, 0.587, 0.114));
}
";

        private const string LumaReduceShaderSource = @"
cbuffer ReduceCB : register(b0)
{
    float SmoothAlpha;
    float3 _PadR;
};

Texture2D<float> LumaDownRO : register(t0);
RWStructuredBuffer<float> AvgLumaRW : register(u0);

[numthreads(1, 1, 1)]
void CSReduceLuma(uint3 id : SV_DispatchThreadID)
{
    uint w, h;
    LumaDownRO.GetDimensions(w, h);

    float sum = 0;
    for (uint y = 0; y < h; y++)
        for (uint x = 0; x < w; x++)
            sum += LumaDownRO.Load(int3(x, y, 0));
    float avg = sum / max(1.0, (float)(w * h));

    float prev = AvgLumaRW[0];
    AvgLumaRW[0] = lerp(prev, avg, SmoothAlpha);
}
";

        // ── 段階5：Lanczos-3 separable アップスケール ──
        private const string LanczosCommon = @"
static const float PI = 3.14159265358979323846;
float Lanczos3(float x)
{
    const float a = 3.0;
    if (abs(x) < 1e-5) return 1.0;
    if (abs(x) >= a) return 0.0;
    float px = PI * x;
    return a * sin(px) * sin(px / a) / (px * px);
}
";

        private const string SrHorizShaderSource = LanczosCommon + @"
cbuffer ScaleCB : register(b0)
{
    float ScaleX;
    float ScaleY;
    float2 _PadS;
};

Texture2D<float4> SrcTex : register(t0);
RWTexture2D<float4> DstTex : register(u0); // horizontal upscale target (height unchanged)

[numthreads(8, 8, 1)]
void CSUpscaleH(uint3 id : SV_DispatchThreadID)
{
    uint dstW, dstH;
    DstTex.GetDimensions(dstW, dstH);
    if (id.x >= dstW || id.y >= dstH) return;

    uint srcW, srcH;
    SrcTex.GetDimensions(srcW, srcH);

    float srcX = (id.x + 0.5) / ScaleX - 0.5;
    int ix = (int)floor(srcX);

    float4 sum = 0;
    float wsum = 0;
    for (int t = -2; t <= 3; t++)
    {
        int sx = clamp(ix + t, 0, (int)srcW - 1);
        float w = Lanczos3(srcX - (float)(ix + t));
        sum += SrcTex.Load(int3(sx, id.y, 0)) * w;
        wsum += w;
    }
    DstTex[id.xy] = sum / max(wsum, 0.0001);
}
";

        private const string SrVertShaderSource = LanczosCommon + @"
cbuffer ScaleCB : register(b0)
{
    float ScaleX;
    float ScaleY;
    float2 _PadS;
};

Texture2D<float4> SrcTex : register(t0);
RWTexture2D<float4> DstTex : register(u0); // vertical upscale target (both dims upscaled)

[numthreads(8, 8, 1)]
void CSUpscaleV(uint3 id : SV_DispatchThreadID)
{
    uint dstW, dstH;
    DstTex.GetDimensions(dstW, dstH);
    if (id.x >= dstW || id.y >= dstH) return;

    uint srcW, srcH;
    SrcTex.GetDimensions(srcW, srcH);

    float srcY = (id.y + 0.5) / ScaleY - 0.5;
    int iy = (int)floor(srcY);

    float4 sum = 0;
    float wsum = 0;
    for (int t = -2; t <= 3; t++)
    {
        int sy = clamp(iy + t, 0, (int)srcH - 1);
        float w = Lanczos3(srcY - (float)(iy + t));
        sum += SrcTex.Load(int3(id.x, sy, 0)) * w;
        wsum += w;
    }
    DstTex[id.xy] = sum / max(wsum, 0.0001);
}
";

        private const string SrUnsharpShaderSource = @"
cbuffer SharpCB : register(b0)
{
    float Amount;
    float3 _PadU;
};

Texture2D<float4> SrcTex : register(t0);
RWTexture2D<float4> DstTex : register(u0);

[numthreads(8, 8, 1)]
void CSUnsharp(uint3 id : SV_DispatchThreadID)
{
    uint w, h;
    DstTex.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;

    int x = (int)id.x, y = (int)id.y;
    float4 c = SrcTex.Load(int3(x, y, 0));
    float4 blur =
        (SrcTex.Load(int3(clamp(x - 1, 0, (int)w - 1), y, 0)) +
         SrcTex.Load(int3(clamp(x + 1, 0, (int)w - 1), y, 0)) +
         SrcTex.Load(int3(x, clamp(y - 1, 0, (int)h - 1), 0)) +
         SrcTex.Load(int3(x, clamp(y + 1, 0, (int)h - 1), 0))) * 0.25;
    float3 result = c.rgb + (c.rgb - blur.rgb) * Amount;
    DstTex[id.xy] = float4(saturate(result), c.a);
}
";

        private const string CompareShaderSource = @"
cbuffer CompareCB : register(b0)
{
    float Mode; // 1=single-frame wipe split, 2=dual full-frame side-by-side
    float3 _PadC;
};

Texture2D<float4> ProcessedTex : register(t0); // output resolution, fully processed frame
Texture2D<float4> OrigTex : register(t1);      // native resolution, unprocessed frame
RWTexture2D<float4> FinalTex : register(u0);   // output resolution, final canvas

[numthreads(8, 8, 1)]
void CSCompare(uint3 id : SV_DispatchThreadID)
{
    uint w, h;
    FinalTex.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;

    uint halfW = max(w / 2, 1);
    uint rightW = max(w - halfW, 1);

    if ((int)round(Mode) == 2)
    {
        // dual full-frame side-by-side: both panes show the ENTIRE frame, shrunk to half width
        if (id.x < halfW)
        {
            uint srcW, srcH;
            OrigTex.GetDimensions(srcW, srcH);
            uint2 srcXY = uint2(
                (uint)((id.x + 0.5) * srcW / halfW),
                (uint)((id.y + 0.5) * srcH / h));
            srcXY = min(srcXY, uint2(srcW - 1, srcH - 1));
            FinalTex[id.xy] = OrigTex.Load(int3(srcXY, 0));
        }
        else
        {
            uint procW, procH;
            ProcessedTex.GetDimensions(procW, procH);
            uint localX = id.x - halfW;
            uint2 srcXY = uint2(
                (uint)((localX + 0.5) * procW / rightW),
                (uint)((id.y + 0.5) * procH / h));
            srcXY = min(srcXY, uint2(procW - 1, procH - 1));
            FinalTex[id.xy] = ProcessedTex.Load(int3(srcXY, 0));
        }
    }
    else
    {
        // single-frame wipe split: one frame, left half = original crop, right half = processed crop
        if (id.x < halfW)
        {
            uint srcW, srcH;
            OrigTex.GetDimensions(srcW, srcH);
            uint2 srcXY = uint2(
                (uint)((id.x + 0.5) * srcW / w),
                (uint)((id.y + 0.5) * srcH / h));
            srcXY = min(srcXY, uint2(srcW - 1, srcH - 1));
            FinalTex[id.xy] = OrigTex.Load(int3(srcXY, 0));
        }
        else
        {
            FinalTex[id.xy] = ProcessedTex.Load(int3(id.xy, 0));
        }
    }

    if (abs((int)id.x - (int)halfW) <= 2)
        FinalTex[id.xy] = float4(1, 0, 0, 1); // divider line (red, ~5px wide)
}
";

        public GpuFramePresenter()
        {
            try
            {
                InitDevices();
                _d3dReady = true;
            }
            catch (Exception ex)
            {
                Trace($"GpuFramePresenter init failed: {ex}");
                _d3dReady = false;
            }

            if (_d3dReady)
            {
                try { InitEffectsShader(); _effectsShaderReady = true; }
                catch (Exception ex)
                {
                    Trace($"GpuFramePresenter effects shader init failed: {ex}");
                    _effectsShaderReady = false;
                }

                try { InitDynamicContrastShaders(); _dynamicContrastShaderReady = true; }
                catch (Exception ex)
                {
                    Trace($"GpuFramePresenter dynamic contrast shader init failed: {ex}");
                    _dynamicContrastShaderReady = false;
                }

                try { InitSuperResolutionShaders(); _srShaderReady = true; }
                catch (Exception ex)
                {
                    // 超解像だけ失敗しても、段階1/2/4は継続できるようにする。
                    Trace($"GpuFramePresenter SR shader init failed: {ex}");
                    _srShaderReady = false;
                }

                try { InitCompareShader(); _compareShaderReady = true; }
                catch (Exception ex)
                {
                    Trace($"GpuFramePresenter compare shader init failed: {ex}");
                    _compareShaderReady = false;
                }
            }
        }

        public bool IsAvailable => _d3dReady;

        private void InitDevices()
        {
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            D3D11.D3D11CreateDevice(
                null, Vortice.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                levels, out _d3d11Device, out _d3d11Context).CheckError();

            _d3d9 = D3D9.Direct3DCreate9Ex();
            var pp = new Vortice.Direct3D9.PresentParameters
            {
                Windowed = true,
                SwapEffect = Vortice.Direct3D9.SwapEffect.Discard,
                DeviceWindowHandle = GetDesktopWindow(),
                PresentationInterval = PresentInterval.Default,
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferFormat = Vortice.Direct3D9.Format.X8R8G8B8,
            };
            _d3d9Device = _d3d9!.CreateDeviceEx(
                0, Vortice.Direct3D9.DeviceType.Hardware, GetDesktopWindow(),
                CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                pp);
        }

        private static ID3D11ComputeShader CompileCs(ID3D11Device device, string source, string entryPoint, string debugName)
        {
            Compiler.Compile(source, entryPoint, debugName, "cs_5_0",
                out Vortice.Direct3D.Blob? shaderBlob, out Vortice.Direct3D.Blob? errorBlob);
            if (shaderBlob == null)
            {
                string msg = errorBlob != null ? System.Text.Encoding.ASCII.GetString(errorBlob.AsSpan()) : "unknown";
                throw new InvalidOperationException($"HLSL compile failed ({debugName}): {msg}");
            }
            return device.CreateComputeShader(shaderBlob.AsSpan());
        }

        private static ID3D11Buffer CreateConstBuffer(ID3D11Device device, int byteWidth) => device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)byteWidth,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        });

        private void InitEffectsShader()
        {
            if (_d3d11Device == null) return;
            _effectsCs = CompileCs(_d3d11Device, EffectsShaderSource, "CSMain", "GpuEffects");
            _cbEffects = CreateConstBuffer(_d3d11Device, 16);
        }

        private void InitDynamicContrastShaders()
        {
            if (_d3d11Device == null) return;

            _lumaDownCs = CompileCs(_d3d11Device, LumaDownShaderSource, "CSDownsampleLuma", "GpuLumaDown");
            _lumaReduceCs = CompileCs(_d3d11Device, LumaReduceShaderSource, "CSReduceLuma", "GpuLumaReduce");

            _lumaDownTex = _d3d11Device.CreateTexture2D(new Texture2DDescription
            {
                Width = LumaDownSize,
                Height = LumaDownSize,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.R16_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            });
            _lumaDownSrv = _d3d11Device.CreateShaderResourceView(_lumaDownTex);
            _lumaDownUav = _d3d11Device.CreateUnorderedAccessView(_lumaDownTex);

            _avgLumaBuffer = _d3d11Device.CreateBuffer(new BufferDescription
            {
                ByteWidth = 4,
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = 4
            });
            float initialLuma = 0.4f;
            _d3d11Context?.UpdateSubresource(in initialLuma, _avgLumaBuffer);
            _avgLumaSrv = _d3d11Device.CreateShaderResourceView(_avgLumaBuffer);
            _avgLumaUav = _d3d11Device.CreateUnorderedAccessView(_avgLumaBuffer);

            _cbReduce = CreateConstBuffer(_d3d11Device, 16);
        }

        private void InitSuperResolutionShaders()
        {
            if (_d3d11Device == null) return;
            _srHorizCs = CompileCs(_d3d11Device, SrHorizShaderSource, "CSUpscaleH", "GpuSrHoriz");
            _srVertCs = CompileCs(_d3d11Device, SrVertShaderSource, "CSUpscaleV", "GpuSrVert");
            _srUnsharpCs = CompileCs(_d3d11Device, SrUnsharpShaderSource, "CSUnsharp", "GpuSrUnsharp");
            _cbScale = CreateConstBuffer(_d3d11Device, 16);
            _cbSharp = CreateConstBuffer(_d3d11Device, 16);
        }

        private void InitCompareShader()
        {
            if (_d3d11Device == null) return;
            _compareCs = CompileCs(_d3d11Device, CompareShaderSource, "CSCompare", "GpuCompare");
            _cbCompare = CreateConstBuffer(_d3d11Device, 16);
        }

        public void SetEffects(double contrast, double saturation, double gamma)
        {
            _contrast = (float)contrast;
            _saturation = (float)saturation;
            _gamma = (float)gamma;
        }

        public void SetDynamicContrast(float strength)
        {
            _dynamicContrastStrength = Math.Clamp(strength, 0f, 1f);
        }

        /// <summary>超解像の拡大倍率。1.0以下で無効。設計提案書2章・案B（古典Lanczos＋アンシャープ）。</summary>
        public void SetSuperResolution(float scale)
        {
            _srScale = scale <= 1.01f ? 1f : scale;
        }

        /// <summary>PowerDVD TrueTheater風の比較表示モード。0=通常、1=1枚分割（ワイプ、1枚の画を
        /// 左右に切り出す）、2=2枚分割（フル画像を2面並べる）。</summary>
        public void SetCompareMode(int mode)
        {
            _compareMode = Math.Clamp(mode, 0, 2);
        }

        public void EnsureSize(int width, int height)
        {
            if (!_d3dReady) return;
            if (width == _w && height == _h && _sharedTex11 != null && Math.Abs(_lastSrScale - _srScale) < 0.001f)
                return;
            _w = width; _h = height;
            _lastSrScale = _srScale;
            RecreateTextures();
        }

        private bool SrActive => _srShaderReady && _srScale > 1f;

        private void RecreateTextures()
        {
            DisposeSizedTextures();
            if (_d3d11Device == null || _d3d9Device == null || _w <= 0 || _h <= 0) return;

            bool srActive = SrActive;
            _outW = srActive ? Math.Max(1, (int)Math.Round(_w * _srScale)) : _w;
            _outH = srActive ? Math.Max(1, (int)Math.Round(_h * _srScale)) : _h;

            _uploadTex = _d3d11Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_w,
                Height = (uint)_h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.Write,
                MiscFlags = ResourceOptionFlags.None
            });

            _sharedTex11 = _d3d11Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_outW,
                Height = (uint)_outH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget | BindFlags.UnorderedAccess,
                MiscFlags = ResourceOptionFlags.Shared,
                CPUAccessFlags = CpuAccessFlags.None
            });

            using var dxgiResource = _sharedTex11.QueryInterface<IDXGIResource>();
            IntPtr sharedHandle = dxgiResource.SharedHandle;

            _sharedTex9 = _d3d9Device.CreateTexture(
                (uint)_outW, (uint)_outH, 1, Vortice.Direct3D9.Usage.RenderTarget, Vortice.Direct3D9.Format.A8R8G8B8, Pool.Default, ref sharedHandle);
            _surface9 = _sharedTex9.GetSurfaceLevel(0);

            if (_effectsShaderReady)
            {
                _srvUpload = _d3d11Device.CreateShaderResourceView(_uploadTex);

                if (srActive)
                {
                    // 効果適用(CSMain)は常に原寸で行い、その後の拡大パスへ渡す。
                    _nativeProcessedTex = _d3d11Device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)_w,
                        Height = (uint)_h,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                        CPUAccessFlags = CpuAccessFlags.None,
                        MiscFlags = ResourceOptionFlags.None
                    });
                    _srvNativeProcessed = _d3d11Device.CreateShaderResourceView(_nativeProcessedTex);
                    _uavNativeProcessed = _d3d11Device.CreateUnorderedAccessView(_nativeProcessedTex);

                    _srHorizTex = _d3d11Device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)_outW,
                        Height = (uint)_h,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                        CPUAccessFlags = CpuAccessFlags.None,
                        MiscFlags = ResourceOptionFlags.None
                    });
                    _srHorizSrv = _d3d11Device.CreateShaderResourceView(_srHorizTex);
                    _srHorizUav = _d3d11Device.CreateUnorderedAccessView(_srHorizTex);

                    _srVertTex = _d3d11Device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)_outW,
                        Height = (uint)_outH,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                        CPUAccessFlags = CpuAccessFlags.None,
                        MiscFlags = ResourceOptionFlags.None
                    });
                    _srVertSrv = _d3d11Device.CreateShaderResourceView(_srVertTex);
                    _srVertUav = _d3d11Device.CreateUnorderedAccessView(_srVertTex);
                }

                // srActiveなら「効果適用の出力先」は_nativeProcessedTex、そうでなければ従来どおり共有テクスチャへ直接。
                _uavShared = _d3d11Device.CreateUnorderedAccessView(_sharedTex11);

                // 比較モード用：加工済み結果の一時保存先（常時確保しておき、ライブ切替可能にする）
                if (_compareShaderReady)
                {
                    _processedFinalTex = _d3d11Device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)_outW,
                        Height = (uint)_outH,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                        CPUAccessFlags = CpuAccessFlags.None,
                        MiscFlags = ResourceOptionFlags.None
                    });
                    _srvProcessedFinal = _d3d11Device.CreateShaderResourceView(_processedFinalTex);
                    _uavProcessedFinal = _d3d11Device.CreateUnorderedAccessView(_processedFinalTex);
                }
            }

            if (D3DImage.IsFrontBufferAvailable)
            {
                D3DImage.Lock();
                D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface9.NativePointer);
                D3DImage.Unlock();
            }

            Trace($"GpuFramePresenter: textures native=({_w}x{_h}) out=({_outW}x{_outH}) srActive={srActive}");
        }

        public void Present(byte[] bgra, int width, int height, int stride)
        {
            if (!_d3dReady) return;
            EnsureSize(width, height);
            if (_uploadTex == null || _sharedTex11 == null || _surface9 == null || _d3d11Context == null) return;

            try
            {
                var mapped = _d3d11Context.Map(_uploadTex, 0, Vortice.Direct3D11.MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                unsafe
                {
                    byte* dst = (byte*)mapped.DataPointer;
                    int copyBytes = Math.Min(stride, (int)mapped.RowPitch);
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(bgra, y * stride, (IntPtr)(dst + y * mapped.RowPitch), copyBytes);
                    }
                }
                _d3d11Context.Unmap(_uploadTex, 0);

                bool srActive = SrActive && _uavNativeProcessed != null && _srHorizUav != null && _srVertUav != null;
                bool compareActive = _compareMode > 0 && _compareShaderReady && _compareCs != null && _cbCompare != null &&
                    _uavProcessedFinal != null && _srvProcessedFinal != null;
                // 比較モード時は「最終パス」の出力先を一時テクスチャへ差し替え、
                // 最後にCSCompareで無加工/加工済みを左右に並べたものを共有テクスチャへ書く。
                var lastStageTarget = (compareActive ? _uavProcessedFinal : _uavShared)!;

                if (_effectsShaderReady && _effectsCs != null && _srvUpload != null && _uavShared != null && _cbEffects != null)
                {
                    if (_dynamicContrastShaderReady && _dynamicContrastStrength > 0f &&
                        _lumaDownCs != null && _lumaReduceCs != null &&
                        _lumaDownUav != null && _lumaDownSrv != null &&
                        _avgLumaUav != null && _cbReduce != null)
                    {
                        _d3d11Context.CSSetShader(_lumaDownCs);
                        _d3d11Context.CSSetShaderResources(0, new[] { _srvUpload });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new[] { _lumaDownUav });
                        _d3d11Context.Dispatch((uint)((LumaDownSize + 7) / 8), (uint)((LumaDownSize + 7) / 8), 1);
                        _d3d11Context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });

                        UpdateReduceConstantBuffer();
                        _d3d11Context.CSSetShader(_lumaReduceCs);
                        _d3d11Context.CSSetShaderResources(0, new[] { _lumaDownSrv });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new[] { _avgLumaUav });
                        _d3d11Context.CSSetConstantBuffers(0, new[] { _cbReduce });
                        _d3d11Context.Dispatch(1, 1, 1);
                        _d3d11Context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
                    }

                    // Pass C: コントラスト/彩度/ガンマ＋ダイナミックコントラスト（常に原寸=width x height）
                    UpdateEffectsConstantBuffer();
                    var effectsTarget = (srActive ? _uavNativeProcessed : lastStageTarget)!;
                    _d3d11Context.CSSetShader(_effectsCs);
                    var srvs = (_dynamicContrastShaderReady && _avgLumaSrv != null)
                        ? new[] { _srvUpload, _avgLumaSrv }
                        : new[] { _srvUpload };
                    _d3d11Context.CSSetShaderResources(0, srvs);
                    _d3d11Context.CSSetUnorderedAccessViews(0, new[] { effectsTarget });
                    _d3d11Context.CSSetConstantBuffers(0, new[] { _cbEffects });
                    _d3d11Context.Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8), 1);
                    _d3d11Context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
                    _d3d11Context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });

                    if (srActive && _srHorizCs != null && _srVertCs != null && _srUnsharpCs != null &&
                        _srvNativeProcessed != null && _srHorizSrv != null && _srVertSrv != null && _cbScale != null && _cbSharp != null)
                    {
                        UpdateScaleConstantBuffer();

                        // Pass D: 水平Lanczos（原寸高さのまま、幅だけ拡大）
                        _d3d11Context.CSSetShader(_srHorizCs);
                        _d3d11Context.CSSetShaderResources(0, new[] { _srvNativeProcessed });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new[] { _srHorizUav! });
                        _d3d11Context.CSSetConstantBuffers(0, new[] { _cbScale });
                        _d3d11Context.Dispatch((uint)((_outW + 7) / 8), (uint)((height + 7) / 8), 1);
                        _d3d11Context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });

                        // Pass E: 垂直Lanczos（拡大後サイズ）
                        _d3d11Context.CSSetShader(_srVertCs);
                        _d3d11Context.CSSetShaderResources(0, new[] { _srHorizSrv });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new[] { _srVertUav! });
                        _d3d11Context.CSSetConstantBuffers(0, new[] { _cbScale });
                        _d3d11Context.Dispatch((uint)((_outW + 7) / 8), (uint)((_outH + 7) / 8), 1);
                        _d3d11Context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });

                        // Pass F: アンシャープ→最終段（比較モード時は一時テクスチャ、通常時は共有テクスチャ）
                        UpdateSharpConstantBuffer();
                        _d3d11Context.CSSetShader(_srUnsharpCs);
                        _d3d11Context.CSSetShaderResources(0, new[] { _srVertSrv });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new[] { lastStageTarget });
                        _d3d11Context.CSSetConstantBuffers(0, new[] { _cbSharp });
                        _d3d11Context.Dispatch((uint)((_outW + 7) / 8), (uint)((_outH + 7) / 8), 1);
                        _d3d11Context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
                    }

                    // Pass G: compare view (mode 1: single-frame wipe / mode 2: dual full-frame side-by-side)
                    if (compareActive)
                    {
                        UpdateCompareConstantBuffer();
                        _d3d11Context.CSSetShader(_compareCs);
                        _d3d11Context.CSSetShaderResources(0, new[] { _srvProcessedFinal!, _srvUpload });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new[] { _uavShared });
                        _d3d11Context.CSSetConstantBuffers(0, new[] { _cbCompare! });
                        _d3d11Context.Dispatch((uint)((_outW + 7) / 8), (uint)((_outH + 7) / 8), 1);
                        _d3d11Context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
                    }
                }
                else
                {
                    _d3d11Context.CopyResource(_sharedTex11, _uploadTex);
                }
                _d3d11Context.Flush();

                if (D3DImage.IsFrontBufferAvailable)
                {
                    D3DImage.Lock();
                    D3DImage.AddDirtyRect(new Int32Rect(0, 0, _outW > 0 ? _outW : width, _outH > 0 ? _outH : height));
                    D3DImage.Unlock();
                }
            }
            catch (Exception ex)
            {
                Trace($"GpuFramePresenter.Present failed: {ex.Message}");
            }
        }

        private void UpdateEffectsConstantBuffer()
        {
            if (_d3d11Context == null || _cbEffects == null) return;
            var mapped = _d3d11Context.Map(_cbEffects, 0, Vortice.Direct3D11.MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            unsafe
            {
                float* p = (float*)mapped.DataPointer;
                p[0] = _contrast;
                p[1] = _saturation;
                p[2] = _gamma;
                p[3] = (_dynamicContrastShaderReady && _avgLumaSrv != null) ? _dynamicContrastStrength : 0f;
            }
            _d3d11Context.Unmap(_cbEffects, 0);
        }

        private void UpdateReduceConstantBuffer()
        {
            if (_d3d11Context == null || _cbReduce == null) return;
            var mapped = _d3d11Context.Map(_cbReduce, 0, Vortice.Direct3D11.MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            unsafe
            {
                float* p = (float*)mapped.DataPointer;
                p[0] = 0.08f;
                p[1] = p[2] = p[3] = 0f;
            }
            _d3d11Context.Unmap(_cbReduce, 0);
        }

        private void UpdateScaleConstantBuffer()
        {
            if (_d3d11Context == null || _cbScale == null || _w <= 0 || _h <= 0) return;
            var mapped = _d3d11Context.Map(_cbScale, 0, Vortice.Direct3D11.MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            unsafe
            {
                float* p = (float*)mapped.DataPointer;
                p[0] = (float)_outW / _w;
                p[1] = (float)_outH / _h;
                p[2] = p[3] = 0f;
            }
            _d3d11Context.Unmap(_cbScale, 0);
        }

        private void UpdateSharpConstantBuffer()
        {
            if (_d3d11Context == null || _cbSharp == null) return;
            var mapped = _d3d11Context.Map(_cbSharp, 0, Vortice.Direct3D11.MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            unsafe
            {
                float* p = (float*)mapped.DataPointer;
                p[0] = 0.5f; // アンシャープ強度（経験則。強すぎるとハローが目立つ）
                p[1] = p[2] = p[3] = 0f;
            }
            _d3d11Context.Unmap(_cbSharp, 0);
        }

        private void UpdateCompareConstantBuffer()
        {
            if (_d3d11Context == null || _cbCompare == null) return;
            var mapped = _d3d11Context.Map(_cbCompare, 0, Vortice.Direct3D11.MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            unsafe
            {
                float* p = (float*)mapped.DataPointer;
                p[0] = _compareMode;
                p[1] = p[2] = p[3] = 0f;
            }
            _d3d11Context.Unmap(_cbCompare, 0);
        }

        private void DisposeSizedTextures()
        {
            _uavShared?.Dispose(); _uavShared = null;
            _srvUpload?.Dispose(); _srvUpload = null;
            _surface9?.Dispose(); _surface9 = null;
            _sharedTex9?.Dispose(); _sharedTex9 = null;
            _sharedTex11?.Dispose(); _sharedTex11 = null;
            _uploadTex?.Dispose(); _uploadTex = null;

            _uavNativeProcessed?.Dispose(); _uavNativeProcessed = null;
            _srvNativeProcessed?.Dispose(); _srvNativeProcessed = null;
            _nativeProcessedTex?.Dispose(); _nativeProcessedTex = null;

            _srHorizUav?.Dispose(); _srHorizUav = null;
            _srHorizSrv?.Dispose(); _srHorizSrv = null;
            _srHorizTex?.Dispose(); _srHorizTex = null;

            _srVertUav?.Dispose(); _srVertUav = null;
            _srVertSrv?.Dispose(); _srVertSrv = null;
            _srVertTex?.Dispose(); _srVertTex = null;
        }

        private static void Trace(string msg)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trace.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} | [GpuFramePresenter] {msg}{Environment.NewLine}",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        public void Dispose()
        {
            DisposeSizedTextures();
            _lumaDownUav?.Dispose(); _lumaDownUav = null;
            _lumaDownSrv?.Dispose(); _lumaDownSrv = null;
            _lumaDownTex?.Dispose(); _lumaDownTex = null;
            _avgLumaUav?.Dispose(); _avgLumaUav = null;
            _avgLumaSrv?.Dispose(); _avgLumaSrv = null;
            _avgLumaBuffer?.Dispose(); _avgLumaBuffer = null;
            _cbReduce?.Dispose(); _cbReduce = null;
            _lumaReduceCs?.Dispose(); _lumaReduceCs = null;
            _lumaDownCs?.Dispose(); _lumaDownCs = null;
            _cbScale?.Dispose(); _cbScale = null;
            _cbSharp?.Dispose(); _cbSharp = null;
            _srHorizCs?.Dispose(); _srHorizCs = null;
            _srVertCs?.Dispose(); _srVertCs = null;
            _srUnsharpCs?.Dispose(); _srUnsharpCs = null;
            _cbCompare?.Dispose(); _cbCompare = null;
            _compareCs?.Dispose(); _compareCs = null;
            _cbEffects?.Dispose(); _cbEffects = null;
            _effectsCs?.Dispose(); _effectsCs = null;
            _d3d9Device?.Dispose(); _d3d9Device = null;
            _d3d9?.Dispose(); _d3d9 = null;
            _d3d11Context?.Dispose(); _d3d11Context = null;
            _d3d11Device?.Dispose(); _d3d11Device = null;
        }
    }
}
