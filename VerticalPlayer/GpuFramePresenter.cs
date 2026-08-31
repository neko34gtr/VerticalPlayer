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
    /// 「高画質化エンジン設計提案」2.3節・段階1〜2の実装。
    ///
    /// 【段階1】AVEngineが作るBGRAバッファ(managed byte[])を、WriteableBitmapの代わりに
    /// D3D11テクスチャへアップロード → D3D9Ex共有テクスチャへコピー → WPFのD3DImageへ
    /// バックバッファとして渡す土台。
    ///
    /// 【段階2】コントラスト/彩度/ガンマを、CPU(AVEngine.ApplyEffects)ではなく
    /// Compute Shaderで適用するように変更。アップロード用テクスチャ(SRV)から
    /// 共有テクスチャ(UAV)への「コピー」自体をCompute ShaderのDispatchに置き換える形で、
    /// 追加のテクスチャ・追加コピーを増やさずに済ませている。
    /// シェーダーのコンパイルに失敗した場合は段階1の単純コピー（無加工）へ自動的に
    /// フォールバックする（既存のHW→SWフォールバックと同じ思想）。
    ///
    /// 【要検証事項（Windows実機でのビルド・動作確認が必須）】
    /// - Vortice.Windows のバージョンによってAPIの引数形が変わる場合がある。
    ///   コンパイルエラーが出た場合はIntelliSenseでシグネチャを確認して合わせてほしい。
    /// - D3D11とD3D9Exが同一GPUアダプタ上のデバイスであることが共有テクスチャ成立の前提
    ///   （マルチGPU環境では明示的にアダプタを揃える必要がある。本実装は既定アダプタのみ対応）。
    /// - Dispatch後のFlush()のみでD3D9側の読み取りとの同期を取っており、
    ///   理論上は将来的にティアリング/ちらつきが出る可能性がある（症状が出た場合は
    ///   KeyedMutex方式への変更を検討）。
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

        private float _contrast, _saturation, _gamma;

        private int _w, _h;
        private bool _d3dReady;

        private const string EffectsShaderSource = @"
cbuffer EffectsCB : register(b0)
{
    float Contrast;
    float Saturation;
    float Gamma;
    float _Pad;
};

Texture2D<float4> InputTex : register(t0);
RWTexture2D<float4> OutputTex : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint w, h;
    OutputTex.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;

    float4 c = InputTex.Load(int3(id.xy, 0));

    float gammaExp = exp2(Gamma);
    float factor = 1.0 + Contrast;

    float3 v = saturate(c.rgb);
    v = pow(v, 1.0 / gammaExp);
    v = (v - 0.5019608) * factor + 0.5019608;

    float gray = dot(v, float3(0.299, 0.587, 0.114));
    v = gray + (v - gray) * (1.0 + Saturation);

    OutputTex[id.xy] = float4(saturate(v), c.a);
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
                // D3D9Ex/D3D11が使えない環境（古いGPU/ドライバ等）では、
                // 呼び出し側がWriteableBitmap経路へフォールバックできるよう例外を握りつぶす。
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
                    // シェーダーだけ失敗しても、段階1（単純コピー・無加工表示）は継続できるようにする。
                    System.Diagnostics.Trace.WriteLine($"GpuFramePresenter effects shader init failed: {ex}");
                    _effectsShaderReady = false;
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

        private void InitEffectsShader()
        {
            if (_d3d11Device == null) return;

            Compiler.Compile(EffectsShaderSource, "CSMain", "GpuEffects", "cs_5_0",
                out Vortice.Direct3D.Blob? shaderBlob, out Vortice.Direct3D.Blob? errorBlob);
            if (shaderBlob == null)
            {
                string msg = errorBlob != null ? System.Text.Encoding.ASCII.GetString(errorBlob.AsSpan()) : "unknown";
                throw new InvalidOperationException($"HLSL compile failed: {msg}");
            }
            _effectsCs = _d3d11Device.CreateComputeShader(shaderBlob.AsSpan());

            // cbufferは16バイトアライメント。float4個分ちょうど16バイト。
            _cbEffects = _d3d11Device.CreateBuffer(new BufferDescription
            {
                ByteWidth = 16,
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.Write
            });
        }

        /// <summary>コントラスト/彩度/ガンマ（-1〜1、AVEngine.SetEffectsと同じ意味）を設定する。
        /// 実際のGPUへの反映は次回Present時にまとめて行う。</summary>
        public void SetEffects(double contrast, double saturation, double gamma)
        {
            _contrast = (float)contrast;
            _saturation = (float)saturation;
            _gamma = (float)gamma;
        }

        public void EnsureSize(int width, int height)
        {
            if (!_d3dReady || (width == _w && height == _h && _sharedTex11 != null)) return;
            _w = width; _h = height;
            RecreateTextures();
        }

        private void RecreateTextures()
        {
            DisposeTextures();
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

            // UnorderedAccessを追加：Compute Shaderの出力先(UAV)として直接書き込むため。
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

            if (_effectsShaderReady && _d3d11Device != null)
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
                    UpdateEffectsConstantBuffer();
                    _d3d11Context.CSSetShader(_effectsCs);
                    _d3d11Context.CSSetShaderResources(0, new[] { _srvUpload });
                    _d3d11Context.CSSetUnorderedAccessViews(0, new[] { _uavShared });
                    _d3d11Context.CSSetConstantBuffers(0, new[] { _cbEffects });
                    _d3d11Context.Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8), 1);
                    // 他のパスとの干渉を避けるため明示的にアンバインド
                    _d3d11Context.CSSetShaderResources(0, new ID3D11ShaderResourceView?[] { null });
                    _d3d11Context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView?[] { null });
                }
                else
                {
                    // 段階1相当：Compute Shaderが使えない場合は単純コピー（無加工）
                    _d3d11Context.CopyResource(_sharedTex11, _uploadTex);
                }
                _d3d11Context.Flush();

                // D3DImageの操作は必ずUIスレッドで行うこと（このメソッド自体、
                // AVEngine側でDispatcher経由のUIスレッドコールバックから呼ばれる前提）。
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
                p[3] = 0f;
            }
            _d3d11Context.Unmap(_cbEffects, 0);
        }

        private void DisposeTextures()
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
            DisposeTextures();
            _cbEffects?.Dispose(); _cbEffects = null;
            _effectsCs?.Dispose(); _effectsCs = null;
            _d3d9Device?.Dispose(); _d3d9Device = null;
            _d3d9?.Dispose(); _d3d9 = null;
            _d3d11Context?.Dispose(); _d3d11Context = null;
            _d3d11Device?.Dispose(); _d3d11Device = null;
        }
    }
}
