using System;
using System.Runtime.InteropServices;

namespace VerticalPlayer.Media
{
    /// <summary>
    /// CUDA Runtime (cudart64_12.dll、CUDA Toolkit 12.x前提) のD3D11相互運用機能を
    /// P/Invokeで薄くラップしたもの。
    ///
    /// 段階6-3-3: D3D11のID3D11Buffer（ShaderResource/UnorderedAccess用）をCUDAへ登録し、
    /// マップ中はCUDAデバイスポインタとして直接読み書きできるようにする。これにより、
    /// ONNX Runtime(TensorRT EP)のIOBindingへCPUを介さず直接GPUメモリを渡せるようになる
    /// （組み込みは段階6-3-4）。
    ///
    /// 対象はTexture2Dではなく「Buffer」であること。D3D11の相互運用ではBufferは
    /// cudaGraphicsResourceGetMappedPointerで生のデバイスポインタが取れるが、Texture2Dは
    /// cudaArray（cudaGraphicsSubResourceGetMappedArray）経由になりcudaMemcpy2DFromArray等の
    /// 追加コピーが必要でORTのIOBindingにそのまま渡せない。段階6-3-2でDNNの入出力を
    /// あえてID3D11Buffer（R16_Floatビュー）で持たせているのはこのため。
    ///
    /// NOTE: このクラスは実機での動作検証ができない状態（Windows/CUDA環境が手元に無い）で
    /// 作成している。構造体のフィールドレイアウトや呼び出し規約に誤りがある可能性があるため、
    /// 実機ビルド・実行時のエラーメッセージを元に調整が必要になることを前提とする。
    /// 参照しているCUDA Toolkitのバージョンが12.x以外の場合はCudartDllの値を変更すること
    /// （例: CUDA 11.xなら"cudart64_110"や"cudart64_111"等、実際のDLL名に合わせる）。
    /// </summary>
    internal static class CudaD3D11Interop
    {
        private const string CudartDll = "cudart64_12";

        /// <summary>cudaError_t。詳細なコードは今のところ使わないため、成功判定と
        /// エラー文字列取得（cudaGetErrorString）が引ければ十分という割り切りで最小限のみ定義。</summary>
        public enum cudaError : int
        {
            cudaSuccess = 0,
        }

        [Flags]
        public enum cudaGraphicsRegisterFlags : uint
        {
            None = 0,
            ReadOnly = 1,
            WriteDiscard = 2,
            SurfaceLoadStore = 4,
            TextureGather = 8,
        }

        // cudaGraphicsResource* はCUDA側の不透明ポインタなのでIntPtrとして扱う。
        [DllImport(CudartDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern cudaError cudaGraphicsD3D11RegisterResource(
            out IntPtr resource, IntPtr pD3DResource, cudaGraphicsRegisterFlags flags);

        [DllImport(CudartDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern cudaError cudaGraphicsUnregisterResource(IntPtr resource);

        [DllImport(CudartDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern cudaError cudaGraphicsMapResources(int count, IntPtr[] resources, IntPtr stream);

        [DllImport(CudartDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern cudaError cudaGraphicsUnmapResources(int count, IntPtr[] resources, IntPtr stream);

        [DllImport(CudartDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern cudaError cudaGraphicsResourceGetMappedPointer(
            out IntPtr devPtr, out UIntPtr size, IntPtr resource);

        [DllImport(CudartDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr cudaGetErrorString(cudaError error);

        [DllImport(CudartDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern cudaError cudaDeviceSynchronize();

        public static string GetErrorString(cudaError error)
        {
            try
            {
                IntPtr p = cudaGetErrorString(error);
                return p == IntPtr.Zero ? error.ToString() : (Marshal.PtrToStringAnsi(p) ?? error.ToString());
            }
            catch
            {
                return error.ToString();
            }
        }

        public static void ThrowIfError(cudaError err, string context)
        {
            if (err != cudaError.cudaSuccess)
                throw new InvalidOperationException($"CUDA error in {context}: {err} ({GetErrorString(err)})");
        }
    }

    /// <summary>
    /// D3D11のID3D11Buffer（生のCOMポインタ）をCUDAグラフィックスリソースとして登録し、
    /// Map()している間だけCUDAデバイスポインタとして直接アクセスできるようにするラッパー。
    ///
    /// 使い方:
    /// <code>
    /// using var reg = new CudaD3D11BufferMap(buffer.NativePointer);
    /// using (reg.Map())
    /// {
    ///     IntPtr devPtr = reg.DevicePointer;
    ///     // ここでOrtValue.CreateTensorValueFromMemory等へdevPtrを渡す（段階6-3-4）
    /// }
    /// // usingブロックを抜けると自動でUnmapされる。regをDisposeするとUnregisterされる。
    /// </code>
    /// </summary>
    internal sealed class CudaD3D11BufferMap : IDisposable
    {
        private IntPtr _cudaResource;
        private bool _mapped;

        public IntPtr DevicePointer { get; private set; }
        public ulong SizeBytes { get; private set; }

        /// <param name="d3d11ResourcePtr">ID3D11Buffer（またはID3D11Texture2D）のネイティブCOM
        /// ポインタ(IUnknown*)。VorticeのオブジェクトではNativePointerプロパティで取得できる
        /// （Vortice.Direct3D11のバージョンによりプロパティ名が異なる可能性があるため要確認）。</param>
        public CudaD3D11BufferMap(IntPtr d3d11ResourcePtr)
        {
            var err = CudaD3D11Interop.cudaGraphicsD3D11RegisterResource(
                out _cudaResource, d3d11ResourcePtr, CudaD3D11Interop.cudaGraphicsRegisterFlags.None);
            CudaD3D11Interop.ThrowIfError(err, nameof(CudaD3D11Interop.cudaGraphicsD3D11RegisterResource));
        }

        /// <summary>マップしてDevicePointer/SizeBytesを取得する。戻り値をusingで受けると
        /// スコープを抜けた時に自動でUnmapされる。</summary>
        public IDisposable Map()
        {
            var resources = new[] { _cudaResource };
            var err = CudaD3D11Interop.cudaGraphicsMapResources(1, resources, IntPtr.Zero);
            CudaD3D11Interop.ThrowIfError(err, nameof(CudaD3D11Interop.cudaGraphicsMapResources));
            _mapped = true;

            err = CudaD3D11Interop.cudaGraphicsResourceGetMappedPointer(out var devPtr, out var size, _cudaResource);
            CudaD3D11Interop.ThrowIfError(err, nameof(CudaD3D11Interop.cudaGraphicsResourceGetMappedPointer));
            DevicePointer = devPtr;
            SizeBytes = (ulong)size;

            return new UnmapScope(this);
        }

        private void Unmap()
        {
            if (!_mapped) return;
            var resources = new[] { _cudaResource };
            CudaD3D11Interop.cudaGraphicsUnmapResources(1, resources, IntPtr.Zero);
            _mapped = false;
        }

        public void Dispose()
        {
            Unmap();
            if (_cudaResource != IntPtr.Zero)
            {
                CudaD3D11Interop.cudaGraphicsUnregisterResource(_cudaResource);
                _cudaResource = IntPtr.Zero;
            }
        }

        private sealed class UnmapScope : IDisposable
        {
            private readonly CudaD3D11BufferMap _owner;
            public UnmapScope(CudaD3D11BufferMap owner) => _owner = owner;
            public void Dispose() => _owner.Unmap();
        }
    }
}
