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
    /// 「高画質化エンジン設計提案」2.3節・段階1〜4の実装。
    ///
    /// 【段階1】BGRAバッファをD3D11→D3D9Ex共有テクスチャ→D3DImageへ渡す土台。
    /// 【段階2】コントラスト/彩度/ガンマをCompute Shaderで適用。
    /// 【段階4】ダイナミックコントラスト（簡易オートレベル）。
    ///   本来の設計提案（5章）はヒストグラム＋CDFベースのトーンカーブ生成だが、
    ///   毎フレームのCPU読み戻し（Map+Read）はGPUパイプラインをブロックし
    ///   フレームレートを損なうリスクが高いため、今回はCPU読み戻しなしで完結する
    ///   簡易版として実装：
    ///     Pass A: 入力を32x32へ簡易ダウンサンプルしながら輝度化 (CSDownsampleLuma)
    ///     Pass B: 32x32を1スレッドで平均し、前フレームの値とEMAで時間平滑化
    ///             (CSReduceLuma、GPU上の1要素バッファに保持・全てGPU完結)
    ///     Pass C: 平滑化済み平均輝度をもとに、暗いシーンはガンマを持ち上げ、
    ///             明るいシーンはやや締める自動補正を、既存のコントラスト/彩度/ガンマ
    ///             シェーダー(CSMain)に統合して適用
    ///   厳密なヒストグラム平坦化（CLAHE等）ではなく「シーン平均輝度に応じた
    ///   自動レベル補正」という簡易版である点に留意（設計提案書に明記のうえ実装）。
    ///
    /// 【要検証事項（Windows実機でのビルド・動作確認が必須）】
    /// - Vortice.Windows のバージョンによってAPIの引数形が変わる場合がある。
    /// - D3D11とD3D9Exが同一GPUアダプタ上のデバイスであることが共有テクスチャ成立の前提。
    /// - Dispatch後のFlush()のみで同期しており、症状が出た場合はKeyedMutex方式を検討。
    /// </summary>
    public sealed class GpuFramePresenter : IFramePresenter, IDisposable
    {
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();

        public D3DImage D3DImage { get; } = new D3DImage();

        private ID3D11Device? _d3d11Device;
        private ID3D11DeviceContext? _d3d11Context;
        private IDirect3D9Ex? _d3d9;
        private IDirect3DDevice9Ex? _d3d9Device;

        private ID3D11Texture2D? _uploadTex;   // CPU→GPU転送用（Dynamic）
        private ID3D11Texture2D? _sharedTex11; // D3D11側の共有テクスチャ（Default, Shared）
        private IDirect3DTexture9? _sharedTex9; // D3D9側で同じ共有ハンドルを開いたもの
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
        private ID3D11Buffer? _avgLumaBuffer;       // RWStructuredBuffer<float>、要素数1
        private ID3D11ShaderResourceView? _avgLumaSrv;
        private ID3D11UnorderedAccessView? _avgLumaUav;
        private ID3D11ComputeShader? _lumaDownCs;
        private ID3D11ComputeShader? _lumaReduceCs;
        private ID3D11Buffer? _cbReduce;
        private bool _dynamicContrastShaderReady;
        private float _dynamicContrastStrength; // 0=無効、設計提案書どおり個別ON/OFF可能にする

        private float _contrast, _saturation, _gamma;

        private int _w, _h;
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

    // ダイナミックコントラスト（簡易版）：シーン平均輝度に応じてガンマを自動補正。
    // 暗いシーン(avgLumaが小さい)ほど持ち上げ、明るいシーンほどわずかに締める。
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

        public GpuFramePresenter()
        {
            try
            {
                InitDevices();
                _d3dReady = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"GpuFramePresenter init failed: {ex}");
                _d3dReady = false;
            }

            if (_d3dReady)
            {
                try
                {
                    InitEffectsShader();
                    _effectsShaderReady = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"GpuFramePresenter effects shader init failed: {ex}");
                    _effectsShaderReady = false;
                }

                try
                {
                    InitDynamicContrastShaders();
                    _dynamicContrastShaderReady = true;
                }
                catch (Exception ex)
                {
                    // ダイナミックコントラストだけ失敗しても、段階1/2は継続できるようにする。
                    System.Diagnostics.Trace.WriteLine($"GpuFramePresenter dynamic contrast shader init failed: {ex}");
                    _dynamicContrastShaderReady = false;
                }
            }
        }

        /// <summary>D3D9Ex/D3D11の初期化に成功したかどうか。falseならWriteableBitmap経路を使うこと。</summary>
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

        private void InitEffectsShader()
        {
            if (_d3d11Device == null) return;

            _effectsCs = CompileCs(_d3d11Device, EffectsShaderSource, "CSMain", "GpuEffects");

            // cbufferは16バイトアライメント。float4個分ちょうど16バイト。
            _cbEffects = _d3d11Device.CreateBuffer(new BufferDescription
            {
                ByteWidth = 16,
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.Write
            });
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

            // RWStructuredBuffer<float>、要素数1。初期値は中間的な明るさ(0.4)にしておき、
            // 再生開始直後の1〜2フレームだけ極端な補正がかかるのを防ぐ。
            _avgLumaBuffer = _d3d11Device.CreateBuffer(new BufferDescription
            {
                ByteWidth = 4,
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = 4
            });
            float initialLuma = 0.4f;
            _d3d11Context?.UpdateSubresource(ref initialLuma, _avgLumaBuffer);
            _avgLumaSrv = _d3d11Device.CreateShaderResourceView(_avgLumaBuffer);
            _avgLumaUav = _d3d11Device.CreateUnorderedAccessView(_avgLumaBuffer);

            _cbReduce = _d3d11Device.CreateBuffer(new BufferDescription
            {
                ByteWidth = 16,
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.Write
            });
        }

        /// <summary>コントラスト/彩度/ガンマ（-1〜1、AVEngine.SetEffectsと同じ意味）を設定する。</summary>
        public void SetEffects(double contrast, double saturation, double gamma)
        {
            _contrast = (float)contrast;
            _saturation = (float)saturation;
            _gamma = (float)gamma;
        }

        /// <summary>ダイナミックコントラスト（簡易オートレベル）の強さ。0で完全無効。
        /// 経験則として0.5〜0.7程度を推奨（強すぎると不自然な明滅感が出る）。</summary>
        public void SetDynamicContrast(float strength)
        {
            _dynamicContrastStrength = Math.Clamp(strength, 0f, 1f);
        }

        public void EnsureSize(int width, int height)
        {
            if (!_d3dReady || (width == _w && height == _h && _sharedTex11 != null)) return;
            _w = width; _h = height;
            RecreateTextures();
        }

        private void RecreateTextures()
        {
            DisposeSizedTextures();
            if (_d3d11Device == null || _d3d9Device == null || _w <= 0 || _h <= 0) return;

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
                Width = (uint)_w,
                Height = (uint)_h,
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
                (uint)_w, (uint)_h, 1, Vortice.Direct3D9.Usage.RenderTarget, Vortice.Direct3D9.Format.A8R8G8B8, Pool.Default, ref sharedHandle);
            _surface9 = _sharedTex9.GetSurfaceLevel(0);

            if (_effectsShaderReady)
            {
                _srvUpload = _d3d11Device.CreateShaderResourceView(_uploadTex);
                _uavShared = _d3d11Device.CreateUnorderedAccessView(_sharedTex11);
            }

            if (D3DImage.IsFrontBufferAvailable)
            {
                D3DImage.Lock();
                D3DImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface9.NativePointer);
                D3DImage.Unlock();
            }

            System.Diagnostics.Trace.WriteLine($"GpuFramePresenter: textures ({_w}x{_h}) recreated");
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

                if (_effectsShaderReady && _effectsCs != null && _srvUpload != null && _uavShared != null && _cbEffects != null)
                {
                    // ── 段階4：ダイナミックコントラスト用の平均輝度をGPU上だけで更新 ──
                    if (_dynamicContrastShaderReady && _dynamicContrastStrength > 0f &&
                        _lumaDownCs != null && _lumaReduceCs != null &&
                        _lumaDownUav != null && _lumaDownSrv != null &&
                        _avgLumaUav != null && _cbReduce != null)
                    {
                        // Pass A: 入力を32x32へ簡易ダウンサンプル＋輝度化
                        _d3d11Context.CSSetShader(_lumaDownCs);
                        _d3d11Context.CSSetShaderResources(0, new[] { _srvUpload });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new[] { _lumaDownUav });
                        _d3d11Context.Dispatch((uint)((LumaDownSize + 7) / 8), (uint)((LumaDownSize + 7) / 8), 1);
                        _d3d11Context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });

                        // Pass B: 32x32を1スレッドで平均し、前フレームとEMAで時間平滑化
                        UpdateReduceConstantBuffer();
                        _d3d11Context.CSSetShader(_lumaReduceCs);
                        _d3d11Context.CSSetShaderResources(0, new[] { _lumaDownSrv });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new[] { _avgLumaUav });
                        _d3d11Context.CSSetConstantBuffers(0, new[] { _cbReduce });
                        _d3d11Context.Dispatch(1, 1, 1);
                        _d3d11Context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
                        _d3d11Context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
                    }

                    // Pass C: コントラスト/彩度/ガンマ＋ダイナミックコントラストを一括適用
                    UpdateEffectsConstantBuffer();
                    _d3d11Context.CSSetShader(_effectsCs);
                    var srvs = (_dynamicContrastShaderReady && _avgLumaSrv != null)
                        ? new[] { _srvUpload, _avgLumaSrv }
                        : new[] { _srvUpload }; // AvgLuma未使用時もシェーダー自体はt1参照するため、下のフォールバックに注意
                    _d3d11Context.CSSetShaderResources(0, srvs);
                    _d3d11Context.CSSetUnorderedAccessViews(0, new[] { _uavShared });
                    _d3d11Context.CSSetConstantBuffers(0, new[] { _cbEffects });
                    _d3d11Context.Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8), 1);
                    _d3d11Context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
                    _d3d11Context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
                }
                else
                {
                    // 段階1相当：Compute Shaderが使えない場合は単純コピー（無加工）
                    _d3d11Context.CopyResource(_sharedTex11, _uploadTex);
                }
                _d3d11Context.Flush();

                if (D3DImage.IsFrontBufferAvailable)
                {
                    D3DImage.Lock();
                    D3DImage.AddDirtyRect(new Int32Rect(0, 0, width, height));
                    D3DImage.Unlock();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"GpuFramePresenter.Present failed: {ex.Message}");
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
                p[0] = 0.08f; // EMAの平滑化係数（小さいほどゆっくり追従＝ちらつきにくい）
                p[1] = p[2] = p[3] = 0f;
            }
            _d3d11Context.Unmap(_cbReduce, 0);
        }

        private void DisposeSizedTextures()
        {
            _uavShared?.Dispose(); _uavShared = null;
            _srvUpload?.Dispose(); _srvUpload = null;
            _surface9?.Dispose(); _surface9 = null;
            _sharedTex9?.Dispose(); _sharedTex9 = null;
            _sharedTex11?.Dispose(); _sharedTex11 = null;
            _uploadTex?.Dispose(); _uploadTex = null;
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
            _cbEffects?.Dispose(); _cbEffects = null;
            _effectsCs?.Dispose(); _effectsCs = null;
            _d3d9Device?.Dispose(); _d3d9Device = null;
            _d3d9?.Dispose(); _d3d9 = null;
            _d3d11Context?.Dispose(); _d3d11Context = null;
            _d3d11Device?.Dispose(); _d3d11Device = null;
        }
    }
}
