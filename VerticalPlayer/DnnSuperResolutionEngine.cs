using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OrtFloat16 = Microsoft.ML.OnnxRuntime.Float16;

namespace VerticalPlayer.Media
{
    /// <summary>
    /// 高画質化エンジン設計提案 6章（TensorRT固定エンジンによるDNN超解像）の実装。
    ///
    /// 段階6-1: まず正しさ優先でCPU経由（BGRA byte[] → 正規化float16 NCHWテンソル → 推論 →
    /// float NCHW → BGRA byte[]）のパイプラインとして実装する。ZiViewのONNX Runtime基盤
    /// （TensorRT→CUDA→CPUフォールバック順、trt_cache運用）の設計思想を踏襲しつつ、
    /// モデル・推論ループ自体は動画向けに作り直したもの（設計提案1章の結論どおり）。
    ///
    /// VerticalPlayerは動画ごとに解像度が異なるため、TensorRTエンジンは「解像度ごとに
    /// 初回再生時に自動ビルド・キャッシュ」する方式を採用（ユーザー確認済み）。
    /// 同一プロセス内で解像度が変わった場合はセッションを作り直す。
    ///
    /// スレッドモデル: 呼び出し側（AVEngineのデコードスレッド想定）から常に単一スレッド・
    /// 順次呼び出しされることを前提とする（内部でロック等は行っていない）。
    /// </summary>
    public sealed class DnnSuperResolutionEngine : IDisposable
    {
        private readonly string _modelPath;
        private readonly string _cacheDir;
        private readonly int _scale; // 4x-UltraSharpV2 = 4倍

        private InferenceSession? _session;
        private string? _inputName;
        private string? _outputName;
        private int _builtWidth;
        private int _builtHeight;

        // 16の倍数アライメント対応: TensorRTのビルド・推論エラー回避のため、実際にセッションへ
        // 渡す入力解像度は16の倍数に切り上げたものを使う（854x480 -> 864x480等）。
        // _builtWidth/_builtHeightは従来通り「呼び出し元が要求した元解像度」を保持し、
        // IsReadyFor/TryInfer系の解像度一致判定はこれまで通り元解像度で行う。
        // _alignedWidth/_alignedHeightは実際にTensorRTエンジンがビルドされた（パディング後の）
        // 解像度で、入力テンソルの確保・出力のクロップ計算に使う。
        private int _alignedWidth;
        private int _alignedHeight;

        // 解像度ごとのTensorRTキャッシュ格納先（_cacheDir直下のサブフォルダ）。
        // 以前は_cacheDir直下を全解像度で共用しており、ある解像度のビルド失敗時に
        // ClearTrtCacheFilesで_cacheDir直下を丸ごと削除すると、既にビルド済みだった
        // 他解像度のキャッシュまで巻き添えで消えてしまっていた（動画の解像度が変わる
        // たびに毎回フルリビルドが必要になっていた不具合の原因）。解像度ごとに
        // サブフォルダを分けることで、あるサブ解像度の失敗・削除が他解像度に
        // 影響しないようにする（backupDir側は元々このメソッドの対象外で無関係）。
        private string? _currentCacheSubDir;

        /// <summary>TensorRTのビルド・推論エラー回避のため、値を16の倍数へ切り上げる。</summary>
        private static int AlignUp16(int v) => (v + 15) / 16 * 16;

        /// <summary>実際にTensorRTエンジンがビルドされた（パディング後の）解像度。
        /// GpuFramePresenter側でDNN入出力用のCUDA相互運用バッファを確保する際、
        /// 本来はこのAlignedWidth/AlignedHeight×Scaleのサイズで確保する必要がある
        /// （TryInferZeroCopy/TryInferWithCudaOutputはパディング済みの固定シェイプで
        /// 推論するため）。</summary>
        public int AlignedWidth => _alignedWidth;
        public int AlignedHeight => _alignedHeight;
        public int Scale => _scale;

        private bool _lastInitFailed;
        private bool _loggedInferError;
        private bool _loggedCudaError;
        private bool _loggedZeroCopyError;
        private readonly Dictionary<IntPtr, CudaD3D11BufferMap> _cudaOutputRegistrations = new();
        // 段階6-3-1: 入力側ゼロコピー用のCUDA登録（出力用と同じ考え方、ポインタ単位でキャッシュ）
        private readonly Dictionary<IntPtr, CudaD3D11BufferMap> _cudaInputRegistrations = new();

        // IoBinding/OrtMemoryInfo/RunOptionsは解像度が同じ間は使い回せるにもかかわらず、
        // 従来は毎フレームnew→using Disposeしていた（ネイティブ相互運用オブジェクトの
        // 生成/破棄コストを毎フレーム払っていた）。セッション単位で1回だけ作り、
        // DisposeSession（＝セッション破棄）のタイミングでのみ解放する。
        private OrtMemoryInfo? _cudaMemInfo;
        private RunOptions? _runOptions;
        private OrtIoBinding? _ioBinding;

        private void EnsureRunInfra()
        {
            _cudaMemInfo ??= new OrtMemoryInfo("Cuda", OrtAllocatorType.DeviceAllocator, 0, OrtMemType.Default);
            _runOptions ??= new RunOptions();
            _ioBinding ??= _session!.CreateIoBinding();
        }

        /// <summary>利用可能か。falseの間は呼び出し側で従来のLanczos版へフォールバックすること。</summary>
        public bool IsAvailable => _session != null;

        /// <summary>指定解像度で即座に推論可能か（ビルド済みかの軽量チェック、ブロックしない）。
        /// デコードスレッドから毎フレーム呼んでよい。</summary>
        public bool IsReadyFor(int width, int height) => _session != null && _builtWidth == width && _builtHeight == height;

        /// <summary>直近のBuildOrReuse呼び出しで初期化に失敗したか（同一解像度での
        /// 再試行ループを避けるための参照用）。</summary>
        public bool LastInitFailed => _lastInitFailed;

        public DnnSuperResolutionEngine(string modelPath, string cacheDir, int scale = 4)
        {
            _modelPath = modelPath;
            _cacheDir = cacheDir;
            _scale = scale;
        }

        /// <summary>
        /// 指定解像度用のセッションを確保する。解像度が前回と同じでセッションが
        /// 既に存在する場合は何もしない（高速パス）。初回、または解像度変更時のみ
        /// TensorRTエンジンのビルド（キャッシュが無ければ数秒〜、あれば高速ロード）が走る。
        /// 呼び出し側のデコードスレッドをブロックするため、再生開始直後にまとめて
        /// 呼ぶのではなく、超解像を有効化した最初のフレームで1回だけ呼ぶこと。
        /// </summary>
        /// <returns>利用可能ならtrue。false時は_lastInitFailed=trueとなり、
        /// 呼び出し側はClassic版へフォールバックすること。</returns>
        private readonly object _buildLock = new();

        public bool EnsureEngine(int width, int height)
        {
            // 呼び出し元が複数（デコードスレッドのバックグラウンドビルドと、UIスレッドからの
            // 明示的なPrebuild呼び出し）あるため、同時実行で片方の失敗処理(DisposeSession)が
            // もう片方が正常に構築したセッションを巻き添えで破棄してしまわないようロックする。
            lock (_buildLock)
            {
                if (_session != null && _builtWidth == width && _builtHeight == height)
                    return true;

                // 解像度が変わった場合は古いセッションを破棄してから作り直す
                DisposeSession();

                if (!File.Exists(_modelPath))
                {
                    Trace($"モデルファイルが見つかりません: {_modelPath}");
                    _lastInitFailed = true;
                    return false;
                }

                Directory.CreateDirectory(_cacheDir);

                // trtcache（timing cache含む）が壊れている/古いビルドと不整合等の理由で
                // InferenceSession構築が失敗するケースがある（実機で854x480にて
                // 「TensorRT EP failed to create engine from network for fused node」を確認。
                // 別解像度では成功していたためモデル自体の問題ではなくキャッシュ側の疑いが強い）。
                // 1回目失敗時はキャッシュを全削除して2回目を試み、それでも失敗したら諦める。
                const int maxAttempts = 2;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        BuildSessionOnce(width, height);
                        _builtWidth = width;
                        _builtHeight = height;
                        _lastInitFailed = false;
                        Trace($"DNN超解像エンジン初期化完了: {width}x{height} (aligned {_alignedWidth}x{_alignedHeight}) " +
                              $"input={_inputName} output={_outputName}" +
                              (attempt > 1 ? "（trtcache削除後の再試行で成功）" : ""));
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Trace($"DNN超解像エンジン初期化失敗(試行{attempt}/{maxAttempts}): {ex}");
                        DisposeSession();

                        if (attempt < maxAttempts)
                        {
                            ClearTrtCacheFiles();
                            continue;
                        }

                        _lastInitFailed = true;
                        return false;
                    }
                }

                return false;
            }
        }

        /// <summary>現在ビルドを試みている解像度専用のキャッシュサブフォルダ内のファイルを
        /// 全削除する（フォルダ自体は残す）。同一サブフォルダ内でも特定ファイルだけを
        /// 狙い撃ちで消せるほどファイル名の対応関係が明確ではない（ハッシュベースのため）
        /// ため、そのサブフォルダ内は安全側で全削除する。
        ///
        /// 重要: 削除対象は「この解像度専用のサブフォルダ」のみであり、_cacheDir直下や
        /// 他解像度のサブフォルダ、backupDirには一切触れない。以前は_cacheDir直下を
        /// 解像度共通で使っていたため、ここで丸ごと削除すると他解像度の既存キャッシュまで
        /// 巻き添えで消えていた（＝再生解像度が変わるたびに毎回フルリビルドになる不具合）。</summary>
        private void ClearTrtCacheFiles()
        {
            string target = _currentCacheSubDir ?? _cacheDir;
            try
            {
                if (!Directory.Exists(target)) return;
                int deleted = 0;
                foreach (var f in Directory.GetFiles(target))
                {
                    try { File.Delete(f); deleted++; }
                    catch (Exception exf) { Trace($"trtcacheファイル削除失敗: {f}: {exf.Message}"); }
                }
                Trace($"trtcacheを削除して再試行します（{deleted}ファイル、{target}）");
            }
            catch (Exception ex)
            {
                Trace($"trtcache削除処理自体が失敗: {ex.Message}");
            }
        }

        /// <summary>InferenceSessionを1回構築する。失敗時は例外を投げる（EnsureEngine側で
        /// リトライを制御するため、ここでは握りつぶさない）。</summary>
        private void BuildSessionOnce(int width, int height)
        {
            // 16の倍数アライメント（Padding & Crop対応）: TensorRTは固定シェイプ
            // （min=opt=max同一）でビルドしているため、16の倍数でない解像度
            // （854x480等）だと"failed to create engine from network for fused node"
            // のようなビルド失敗を起こすことがある。実際にエンジンをビルド・推論する
            // 解像度は16の倍数へ切り上げたものとし、元解像度との差分（右・下側）は
            // 呼び出し側（TryInfer系）で黒パディング/クロップして吸収する。
            int alignedWidth = AlignUp16(width);
            int alignedHeight = AlignUp16(height);

            // 解像度ごとに専用サブフォルダへキャッシュを分離する（他解像度・backupには
            // 影響しない。ClearTrtCacheFilesのコメントも参照）。
            string cacheSubDir = Path.Combine(_cacheDir, $"{alignedWidth}x{alignedHeight}");
            Directory.CreateDirectory(cacheSubDir);
            _alignedWidth = alignedWidth;
            _alignedHeight = alignedHeight;
            _currentCacheSubDir = cacheSubDir;

            var so = new SessionOptions();
            so.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            // TensorRT EP: 解像度固定（min=opt=max、いずれもアライメント後のサイズで統一）で
            // エンジンをビルドし、解像度専用サブフォルダへキャッシュする。2回目以降の
            // 同一（アライメント後）解像度はキャッシュから高速ロードされる。
            // NOTE: プロバイダオプションのキー名・APIはOnnxRuntime.Gpuのバージョンに依存するため、
            // 実際に参照するバージョンのドキュメント/サンプルと突き合わせて確認すること
            // （ここではv1.20系のC# APIを想定して記述）。
            try
            {
                var trtOptions = new OrtTensorRTProviderOptions();
                string shapeSpec = $"1x3x{alignedHeight}x{alignedWidth}";
                var trtDict = new Dictionary<string, string>
                {
                    ["device_id"] = "0",
                    ["trt_fp16_enable"] = "1",
                    ["trt_engine_cache_enable"] = "1",
                    ["trt_engine_cache_path"] = cacheSubDir,
                    ["trt_timing_cache_enable"] = "1",
                    // min=opt=max固定にすることで解像度ごとの専用エンジンとしてビルドさせる
                    ["trt_profile_min_shapes"] = $"input:{shapeSpec}",
                    ["trt_profile_opt_shapes"] = $"input:{shapeSpec}",
                    ["trt_profile_max_shapes"] = $"input:{shapeSpec}",
                    // 追加最適化1: ビルド時（キャッシュ生成時、初回のみ）のカーネル最適化強度を
                    // 引き上げる。ランタイムコストには影響しない（ビルド結果はキャッシュされる
                    // ため）。キー名・対応レベル範囲はONNX Runtime/TensorRTのバージョンに依存する
                    // ため、未対応バージョンでは無視されるかエラーになる可能性がある点に注意。
                    ["trt_builder_optimization_level"] = "3",
                    // ビルド失敗の原因調査用。有効にしてもランタイム性能への影響は無い
                    // （ネイティブ側のTensorRTビルダーログがより詳細になるだけ）。出力先は
                    // trace.logではなくVisual Studioの出力ウィンドウ/デバッグコンソール側。
                    ["trt_detailed_build_log"] = "1",
                    // 追加最適化2: CUDA Graph Capture → 実機検証の結果、無効化した。
                    // cudaGraphicsMapResources/UnmapResources（TryInferZeroCopy/
                    // TryInferWithCudaOutputで毎フレーム呼んでいるD3D11-CUDA相互運用の
                    // Map/Unmap）はCUDAの「キャプチャ不可能な操作」に該当し、TensorRTが
                    // グラフキャプチャ中のストリームに対してこれを呼ぶと
                    // 「CUDA error 900: operation not permitted when stream is
                    // capturing」になることが実機トレースで確認された。この状態になると
                    // ゼロコピー→CUDA IOBinding→CPU入力(half出力)の全フォールバックが
                    // 連鎖的に失敗し続け、DNN側の表示更新が完全に止まる
                    // （映像フリーズ・音声継続、要再起動）不具合の原因になっていた。
                    // 現状の実装（毎フレームD3D11バッファをCUDA Map/Unmapする方式）とは
                    // 構造的に相性が悪いため、恒久的に無効のままにする。
                    // ["trt_cuda_graph_enable"] = "1",
                };
                trtOptions.UpdateOptions(trtDict);
                so.AppendExecutionProvider_Tensorrt(trtOptions);
                Trace($"TensorRT EP追加: {width}x{height} (aligned {alignedWidth}x{alignedHeight}), cache={cacheSubDir}");
            }
            catch (Exception ex)
            {
                Trace($"TensorRT EP追加失敗、CUDA EPへフォールバック: {ex.Message}");
            }

            // TensorRTが使えない/失敗環境向けにCUDA EPも積んでおく（ORTはセッション内で
            // EPを優先順に試すため、TensorRT非対応でもCUDAで動作継続できる）
            try
            {
                so.AppendExecutionProvider_CUDA(0);
            }
            catch (Exception ex)
            {
                Trace($"CUDA EP追加失敗（CPU実行にフォールバックされます）: {ex.Message}");
            }

            _session = new InferenceSession(_modelPath, so);

            // 入出力テンサー名はモデルに依存するためハードコードせず動的に取得
            var inputEnum = new List<string>(_session.InputMetadata.Keys);
            var outputEnum = new List<string>(_session.OutputMetadata.Keys);
            if (inputEnum.Count == 0 || outputEnum.Count == 0)
                throw new InvalidOperationException("モデルの入出力メタデータを取得できません");

            _inputName = inputEnum[0];
            _outputName = outputEnum[0];
        }

        /// <summary>
        /// BGRA(4byte/px, stride=width*4想定)の1フレームを推論し、_scale倍にアップスケール
        /// したBGRAバッファを返す。EnsureEngineが未成功、または解像度不一致の場合はfalseを返す。
        /// </summary>
        public bool TryInfer(byte[] srcBgra, int width, int height, out byte[] dstBgra, out int outWidth, out int outHeight)
        {
            dstBgra = Array.Empty<byte>();
            outWidth = 0;
            outHeight = 0;

            if (_session == null || _inputName == null || _outputName == null) return false;
            if (width != _builtWidth || height != _builtHeight) return false;

            // NOTE: Microsoft.ML.OnnxRuntime.Float16 のキャスト演算子(float⇔Float16)は
            // バージョンによって無い場合がある。ビルドエラーになる場合は
            // OrtFloat16.ToFloat16(x) / value.ToFloat() 等の静的/インスタンスメソッドに
            // 置き換えること（参照しているOnnxRuntime.Gpuのバージョンで要確認）。
            try
            {
                // BGRA(byte, 0-255) → RGB正規化float16(0-1) NCHW
                // モデル仕様: input=[1,3,H,W] float16 0-1正規化NCHW
                // NOTE: System.Half は OrtValue.CreateFromTensorObject 内部の型マッピングで
                // 未対応（NullReferenceException）だったため、ONNX Runtime自前のFloat16構造体を使う
                // NOTE: DenseTensorの多次元インデクサ([0,c,y,x])はアクセス毎にストライド計算＋
                // 境界チェックが走り非常に遅い（480pでも約100万回/フレーム）。Buffer.Spanへ
                // フラットオフセットで直書き/直読みすることで同じNCHWレイアウトのまま高速化する。
                // 入力側: アライメント後サイズ(_alignedWidth/_alignedHeight)でテンソルを確保し、
                // 元解像度(width/height)分だけ書き込む。右・下側の余白はDenseTensor初期化時点で
                // 0（＝正規化後の黒）のままなので、明示的なパディング処理は不要。
                var inputTensor = new DenseTensor<OrtFloat16>(new[] { 1, 3, _alignedHeight, _alignedWidth });
                var inSpan = inputTensor.Buffer.Span;
                int inPlane = _alignedWidth * _alignedHeight;
                for (int y = 0; y < height; y++)
                {
                    int rowBase = y * width * 4;
                    int rowOut = y * _alignedWidth;
                    for (int x = 0; x < width; x++)
                    {
                        int i = rowBase + x * 4;
                        byte b = srcBgra[i];
                        byte g = srcBgra[i + 1];
                        byte r = srcBgra[i + 2];
                        int o = rowOut + x;
                        inSpan[o] = (OrtFloat16)(r / 255f);
                        inSpan[inPlane + o] = (OrtFloat16)(g / 255f);
                        inSpan[inPlane * 2 + o] = (OrtFloat16)(b / 255f);
                    }
                }

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor<OrtFloat16>(_inputName, inputTensor)
                };

                using var results = _session.Run(inputs, new[] { _outputName });
                // モデル仕様: output=[1,3,alignedH*scale,alignedW*scale] float16 0-1正規化NCHW
                // （アライメントで足したパディング分もそのまま拡大されて出力に含まれる）。
                var outTensor = results[0].AsTensor<OrtFloat16>();
                int outHFull = outTensor.Dimensions[2];
                int outWFull = outTensor.Dimensions[3];

                // 出力側: パディング分を除いた本来の目標拡大サイズへクロップする。
                // アライメントは整数倍(16の倍数への切り上げ)なので、スケールは
                // outWFull/_alignedWidthで割り切れる前提。
                int scaleX = _alignedWidth > 0 ? outWFull / _alignedWidth : _scale;
                int scaleY = _alignedHeight > 0 ? outHFull / _alignedHeight : _scale;
                int outW = width * scaleX;
                int outH = height * scaleY;

                var dst = new byte[outW * outH * 4];
                if (outTensor is DenseTensor<OrtFloat16> denseOut)
                {
                    var outSpan = denseOut.Buffer.Span;
                    int outPlaneFull = outWFull * outHFull;
                    for (int y = 0; y < outH; y++)
                    {
                        int rowBase = y * outW * 4;
                        int rowIn = y * outWFull; // フル（パディング込み）側のストライドで読む
                        for (int x = 0; x < outW; x++)
                        {
                            int o = rowIn + x;
                            float rf = (float)outSpan[o];
                            float gf = (float)outSpan[outPlaneFull + o];
                            float bf = (float)outSpan[outPlaneFull * 2 + o];
                            int i = rowBase + x * 4;
                            dst[i] = (byte)Math.Clamp(bf * 255f, 0, 255);
                            dst[i + 1] = (byte)Math.Clamp(gf * 255f, 0, 255);
                            dst[i + 2] = (byte)Math.Clamp(rf * 255f, 0, 255);
                            dst[i + 3] = 255;
                        }
                    }
                }
                else
                {
                    // DenseTensorでない場合のみ、安全側のフォールバックとして従来のインデクサ経由にする
                    for (int y = 0; y < outH; y++)
                    {
                        int rowBase = y * outW * 4;
                        for (int x = 0; x < outW; x++)
                        {
                            float rf = (float)outTensor[0, 0, y, x];
                            float gf = (float)outTensor[0, 1, y, x];
                            float bf = (float)outTensor[0, 2, y, x];
                            int i = rowBase + x * 4;
                            dst[i] = (byte)Math.Clamp(bf * 255f, 0, 255);
                            dst[i + 1] = (byte)Math.Clamp(gf * 255f, 0, 255);
                            dst[i + 2] = (byte)Math.Clamp(rf * 255f, 0, 255);
                            dst[i + 3] = 255;
                        }
                    }
                }

                dstBgra = dst;
                outWidth = outW;
                outHeight = outH;
                return true;
            }
            catch (Exception ex)
            {
                if (!_loggedInferError)
                {
                    _loggedInferError = true;
                    Trace($"DNN超解像 推論失敗（詳細、以後この種のエラーは簡略ログ）: {ex}");
                }
                else
                {
                    Trace($"DNN超解像 推論失敗: {ex.GetType().Name}: {ex.Message}");
                }
                return false;
            }
        }

        /// <summary>段階6-3-2：出力側のCPU変換ループを行わず、ONNX Runtimeの出力テンソルの
        /// 生バッファ（float16ビットパターンのushort、NCHW平面レイアウト）をそのまま返す。
        /// GPU側（GpuFramePresenter.PresentDnnHalf）でCompute ShaderによりBGRAへ変換することを
        /// 前提とする。入力側の変換（BGRA→NCHW half）は従来通りCPUで行う。</summary>
        public bool TryInferToNchwHalf(byte[] srcBgra, int width, int height, out ushort[] dstNchwHalf, out int outWidth, out int outHeight)
        {
            dstNchwHalf = Array.Empty<ushort>();
            outWidth = 0;
            outHeight = 0;

            if (_session == null || _inputName == null || _outputName == null) return false;
            if (width != _builtWidth || height != _builtHeight) return false;

            // Dispose()/EnsureEngine()と同じロックで排他化し、モデル切替等による破棄と
            // 競合してセッションを掴んだまま使われるのを防ぐ。
            lock (_buildLock)
            {
                if (_session == null || _inputName == null || _outputName == null) return false;
                if (width != _builtWidth || height != _builtHeight) return false;

                try
                {
                    // NOTE: DenseTensorの多次元インデクサはアクセス毎にストライド計算＋境界
                    // チェックが走り非常に遅いため、Buffer.Spanへフラットオフセットで直書きする。
                    // 入力側パディング: TryInferと同じくアライメント後サイズで確保し、
                    // 右・下側の余白は0（黒）のまま推論へ渡す。
                    var inputTensor = new DenseTensor<OrtFloat16>(new[] { 1, 3, _alignedHeight, _alignedWidth });
                    var inSpan = inputTensor.Buffer.Span;
                    int inPlane = _alignedWidth * _alignedHeight;
                    for (int y = 0; y < height; y++)
                    {
                        int rowBase = y * width * 4;
                        int rowOut = y * _alignedWidth;
                        for (int x = 0; x < width; x++)
                        {
                            int i = rowBase + x * 4;
                            byte b = srcBgra[i];
                            byte g = srcBgra[i + 1];
                            byte r = srcBgra[i + 2];
                            int o = rowOut + x;
                            inSpan[o] = (OrtFloat16)(r / 255f);
                            inSpan[inPlane + o] = (OrtFloat16)(g / 255f);
                            inSpan[inPlane * 2 + o] = (OrtFloat16)(b / 255f);
                        }
                    }

                    var inputs = new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor<OrtFloat16>(_inputName, inputTensor)
                    };

                    using var results = _session.Run(inputs, new[] { _outputName });
                    var outTensor = results[0].AsTensor<OrtFloat16>();
                    int outHFull = outTensor.Dimensions[2];
                    int outWFull = outTensor.Dimensions[3];

                    // 出力側クロップ: パディング分を除いた本来の目標拡大サイズ(cropW x cropH)へ
                    // NCHWレイアウトのまま切り出す（チャンネル毎に行単位でコピー）。
                    int scaleX = _alignedWidth > 0 ? outWFull / _alignedWidth : _scale;
                    int scaleY = _alignedHeight > 0 ? outHFull / _alignedHeight : _scale;
                    int cropW = width * scaleX;
                    int cropH = height * scaleY;

                    // NOTE: OrtFloat16は内部的にushort1個分のビットパターンのみを保持する前提
                    // （ONNX Runtimeの一般的な実装）。DenseTensorでない場合や将来のバージョンで
                    // レイアウトが変わった場合はここで例外になり、catch節でfalseを返すだけなので
                    // 安全側に倒れる（呼び出し側は失敗時、その1フレームだけ等倍表示継続する）。
                    if (outTensor is DenseTensor<OrtFloat16> dense)
                    {
                        var fullSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<OrtFloat16, ushort>(dense.Buffer.Span);
                        if (cropW == outWFull && cropH == outHFull)
                        {
                            // パディング無し（元々16の倍数だった解像度）: クロップ不要でそのまま返す
                            dstNchwHalf = fullSpan.ToArray();
                        }
                        else
                        {
                            var cropped = new ushort[3 * cropW * cropH];
                            int fullPlane = outWFull * outHFull;
                            int cropPlane = cropW * cropH;
                            for (int c = 0; c < 3; c++)
                            {
                                int srcChBase = c * fullPlane;
                                int dstChBase = c * cropPlane;
                                for (int y = 0; y < cropH; y++)
                                {
                                    int srcRow = srcChBase + y * outWFull;
                                    int dstRow = dstChBase + y * cropW;
                                    fullSpan.Slice(srcRow, cropW).CopyTo(cropped.AsSpan(dstRow, cropW));
                                }
                            }
                            dstNchwHalf = cropped;
                        }
                    }
                    else
                    {
                        return false;
                    }

                    outWidth = cropW;
                    outHeight = cropH;
                    return true;
                }
                catch (Exception ex)
                {
                    if (!_loggedInferError)
                    {
                        _loggedInferError = true;
                        Trace($"DNN超解像(half出力) 推論失敗（詳細、以後この種のエラーは簡略ログ）: {ex}");
                    }
                    else
                    {
                        Trace($"DNN超解像(half出力) 推論失敗: {ex.GetType().Name}: {ex.Message}");
                    }
                    return false;
                }
            }
        }

        /// <summary>段階6-3-4: 出力側をゼロコピー化する。ONNX Runtimeの推論結果をCPUへ
        /// 読み戻さず、CUDA相互運用登録済みのD3D11バッファ(outputD3D11BufferPtr)へ
        /// TensorRTから直接書き込ませる。入力側は現時点ではまだCPU変換のまま
        /// （段階6-3-1未着手のため）。
        /// 失敗時（IOBinding非対応、CUDA相互運用エラー等）はfalseを返し、呼び出し側は
        /// TryInferToNchwHalf（CPU経由、段階6-3-2版）へフォールバックすること。
        /// NOTE: OrtMemoryInfo/OrtValueのCUDAメモリ直接バインド関連APIはバージョン依存が
        /// 大きい部分。以下はONNX Runtime C# APIの一般的な形を想定した実装で、実機ビルドでの
        /// 調整が必要になる可能性が高い（クラス名・メソッド名が実際のバージョンと異なる場合、
        /// コンパイルエラーのメッセージを元に直すこと）。
        ///
        /// 【16の倍数アライメント対応後の契約】入力側(srcBgra)は本メソッド内部で
        /// アライメント後サイズ(AlignedWidth/AlignedHeight)へパディングしてから推論するが、
        /// 出力(outputD3D11BufferPtr)はTensorRTが実際に書き込む生のテンソル形状
        /// （AlignedWidth*Scale x AlignedHeight*Scale）とバイト単位で一致していなければ
        /// IOBindingの形状不一致で失敗する。呼び出し側は
        /// outWidth = AlignedWidth * Scale、outHeight = AlignedHeight * Scale
        /// で確保したバッファを渡すこと（元解像度*Scaleではない点に注意）。
        /// パディング分を除いた最終表示サイズへのクロップは、この後段
        /// （GpuFramePresenter側のCompute Shader）で行うこと。</summary>
        public bool TryInferWithCudaOutput(byte[] srcBgra, int width, int height,
            IntPtr outputD3D11BufferPtr, int outWidth, int outHeight)
        {
            if (_session == null || _inputName == null || _outputName == null) return false;
            if (width != _builtWidth || height != _builtHeight) return false;
            if (outputD3D11BufferPtr == IntPtr.Zero) return false;

            lock (_buildLock)
            {
                try
                {
                    // 入力は引き続きCPUで変換（段階6-3-1未着手）。DenseTensorの多次元インデクサは
                    // アクセス毎にストライド計算＋境界チェックが走り非常に遅いため、
                    // Buffer.Spanへフラットオフセットで直書きする。
                    // 16の倍数アライメント対応: アライメント後サイズで確保し、元解像度分だけ
                    // 書き込む（右・下側は0初期化のまま＝黒パディング）。
                    var inputTensor = new DenseTensor<OrtFloat16>(new[] { 1, 3, _alignedHeight, _alignedWidth });
                    var inSpan = inputTensor.Buffer.Span;
                    int inPlane = _alignedWidth * _alignedHeight;
                    for (int y = 0; y < height; y++)
                    {
                        int rowBase = y * width * 4;
                        int rowOut = y * _alignedWidth;
                        for (int x = 0; x < width; x++)
                        {
                            int i = rowBase + x * 4;
                            byte b = srcBgra[i];
                            byte g = srcBgra[i + 1];
                            byte r = srcBgra[i + 2];
                            int o = rowOut + x;
                            inSpan[o] = (OrtFloat16)(r / 255f);
                            inSpan[inPlane + o] = (OrtFloat16)(g / 255f);
                            inSpan[inPlane * 2 + o] = (OrtFloat16)(b / 255f);
                        }
                    }

                    // 出力先バッファをCUDAへ登録（ポインタが変わらない限りキャッシュを再利用）
                    if (!_cudaOutputRegistrations.TryGetValue(outputD3D11BufferPtr, out var reg))
                    {
                        reg = new CudaD3D11BufferMap(outputD3D11BufferPtr);
                        _cudaOutputRegistrations[outputD3D11BufferPtr] = reg;
                    }

                    using var unmapScope = reg.Map();

                    EnsureRunInfra();

                    long outElemCount = (long)outWidth * outHeight * 3;
                    var outputShape = new long[] { 1, 3, outHeight, outWidth };
                    using var outputOrtValue = OrtValue.CreateTensorValueWithData(
                        _cudaMemInfo!, TensorElementType.Float16, outputShape,
                        reg.DevicePointer, outElemCount * sizeof(ushort));

                    // DenseTensorは内部的にT[]を保持しているため、.ToArray()は不要な
                    // フルコピーになる。TryGetArrayで内部配列を直接取得し、取得できない
                    // 場合のみ安全側でToArray()にフォールバックする。
                    OrtFloat16[] inputArray =
                        System.Runtime.InteropServices.MemoryMarshal.TryGetArray<OrtFloat16>(inputTensor.Buffer, out var inSeg) && inSeg.Array != null
                            ? inSeg.Array
                            : inputTensor.Buffer.ToArray();
                    using var inputOrtValue = OrtValue.CreateTensorValueFromMemory<OrtFloat16>(
                        inputArray, new long[] { 1, 3, _alignedHeight, _alignedWidth });

                    _ioBinding!.BindInput(_inputName, inputOrtValue);
                    _ioBinding!.BindOutput(_outputName, outputOrtValue);
                    _session.RunWithBinding(_runOptions!, _ioBinding);

                    // TensorRT/CUDA側の実行完了を、後段のCompute Shaderが読む前に保証する
                    CudaD3D11Interop.cudaDeviceSynchronize();

                    return true;
                }
                catch (Exception ex)
                {
                    if (!_loggedCudaError)
                    {
                        _loggedCudaError = true;
                        Trace($"DNN超解像(CUDA IOBinding) 失敗（詳細、以後この種のエラーは簡略ログ、CPU経路へフォールバック）: {ex}");
                    }
                    else
                    {
                        Trace($"DNN超解像(CUDA IOBinding) 失敗: {ex.GetType().Name}: {ex.Message}");
                    }
                    return false;
                }
            }
        }

        /// <summary>段階6-3-1+6-3-4: 入力・出力ともにCPUを介さないゼロコピー版。
        /// inputD3D11BufferPtrは、GpuFramePresenter.ConvertBgraToNchwHalfGpuによって
        /// 呼び出し側で既にBGRA→NCHW half変換済みのバッファであることが前提（このメソッド自体は
        /// 変換を一切行わない）。TryInferWithCudaOutputとの違いは入力側もCUDA相互運用経由で
        /// 直接バインドする点のみで、それ以外（IoBinding/OrtMemoryInfo/RunOptionsの使い回し、
        /// CUDA登録のキャッシュ）は同じ考え方。
        /// 失敗時（GPU側の入力変換シェーダ未対応環境等）はfalseを返し、呼び出し側は
        /// 従来のTryInferWithCudaOutput（CPU入力変換＋CUDA出力）へフォールバックすること。
        ///
        /// 【16の倍数アライメント対応後の契約】widthとheightはこのエンジンが実際にビルド
        /// された解像度、すなわちAlignedWidth/AlignedHeightと一致していなければならない
        /// （このメソッドはCPU側での黒パディングを一切行わないため）。呼び出し側の
        /// GpuFramePresenter.ConvertBgraToNchwHalfGpuで、元のデコード解像度からAlignedWidth
        /// x AlignedHeightへGPU上で黒パディングした上でinputD3D11BufferPtrへ書き込む必要が
        /// ある。outWidth/outHeightも同様にAlignedWidth*Scale / AlignedHeight*Scaleを渡し、
        /// 元解像度*Scaleへのクロップは後段のGPU側処理（表示直前）で行うこと。</summary>
        public bool TryInferZeroCopy(IntPtr inputD3D11BufferPtr, int width, int height,
            IntPtr outputD3D11BufferPtr, int outWidth, int outHeight)
        {
            if (_session == null || _inputName == null || _outputName == null) return false;
            // 契約変更: ここのwidth/heightは元解像度(_builtWidth/_builtHeight)ではなく、
            // 呼び出し側が既にGPU上でパディング済みのAlignedWidth/AlignedHeightと一致する
            // 必要がある（クラス冒頭のAlignedWidth/AlignedHeightプロパティ参照）。
            if (width != _alignedWidth || height != _alignedHeight) return false;
            if (inputD3D11BufferPtr == IntPtr.Zero || outputD3D11BufferPtr == IntPtr.Zero) return false;

            lock (_buildLock)
            {
                try
                {
                    if (!_cudaInputRegistrations.TryGetValue(inputD3D11BufferPtr, out var inReg))
                    {
                        inReg = new CudaD3D11BufferMap(inputD3D11BufferPtr);
                        _cudaInputRegistrations[inputD3D11BufferPtr] = inReg;
                    }
                    if (!_cudaOutputRegistrations.TryGetValue(outputD3D11BufferPtr, out var outReg))
                    {
                        outReg = new CudaD3D11BufferMap(outputD3D11BufferPtr);
                        _cudaOutputRegistrations[outputD3D11BufferPtr] = outReg;
                    }

                    using var unmapIn = inReg.Map();
                    using var unmapOut = outReg.Map();

                    EnsureRunInfra();

                    long inElemCount = (long)width * height * 3;
                    var inputShape = new long[] { 1, 3, height, width };
                    using var inputOrtValue = OrtValue.CreateTensorValueWithData(
                        _cudaMemInfo!, TensorElementType.Float16, inputShape,
                        inReg.DevicePointer, inElemCount * sizeof(ushort));

                    long outElemCount = (long)outWidth * outHeight * 3;
                    var outputShape = new long[] { 1, 3, outHeight, outWidth };
                    using var outputOrtValue = OrtValue.CreateTensorValueWithData(
                        _cudaMemInfo!, TensorElementType.Float16, outputShape,
                        outReg.DevicePointer, outElemCount * sizeof(ushort));

                    _ioBinding!.BindInput(_inputName, inputOrtValue);
                    _ioBinding!.BindOutput(_outputName, outputOrtValue);
                    _session.RunWithBinding(_runOptions!, _ioBinding);

                    CudaD3D11Interop.cudaDeviceSynchronize();
                    return true;
                }
                catch (Exception ex)
                {
                    if (!_loggedZeroCopyError)
                    {
                        _loggedZeroCopyError = true;
                        Trace($"DNN超解像(入力ゼロコピー) 失敗（詳細、以後この種のエラーは簡略ログ、CPU入力経路へフォールバック）: {ex}");
                    }
                    else
                    {
                        Trace($"DNN超解像(入力ゼロコピー) 失敗: {ex.GetType().Name}: {ex.Message}");
                    }
                    return false;
                }
            }
        }

        private void DisposeSession()
        {
            // TryInferWithCudaOutput/EnsureEngineと同じロックを取ることで、それらが実行中の
            // 間はDispose()が待機し、実行中のセッション/CUDA登録を横から破棄しないようにする
            // （lockは同一スレッドから再入可能なため、EnsureEngine内部からの呼び出しとは競合しない）。
            lock (_buildLock)
            {
                _session?.Dispose();
                _session = null;
                _inputName = null;
                _outputName = null;
                _builtWidth = 0;
                _builtHeight = 0;
                _alignedWidth = 0;
                _alignedHeight = 0;
                _currentCacheSubDir = null;
                _loggedInferError = false;
                _loggedCudaError = false;
                _loggedZeroCopyError = false;

                // IoBinding/RunOptionsはセッションに紐づくため、セッション破棄と一緒に解放する
                // （EnsureRunInfraが次回EnsureEngine成功時に作り直す）
                _ioBinding?.Dispose();
                _ioBinding = null;
                _runOptions?.Dispose();
                _runOptions = null;
                _cudaMemInfo?.Dispose();
                _cudaMemInfo = null;

                // 解像度変更時、GpuFramePresenter側のCUDA用バッファも作り直されて古いポインタは
                // 無効になるため、対応するCUDA登録も破棄しておく（キーがポインタなので放置すると
                // 無効ポインタをキーにしたエントリが溜まり続ける）。
                foreach (var reg in _cudaOutputRegistrations.Values)
                    reg.Dispose();
                _cudaOutputRegistrations.Clear();
                foreach (var reg in _cudaInputRegistrations.Values)
                    reg.Dispose();
                _cudaInputRegistrations.Clear();
            }
        }

        public void Dispose() => DisposeSession();

        // ── trtcacheのバックアップ/復元（cacheDirがRAMディスク等の揮発ストレージ向け） ──

        /// <summary>cacheDirが存在しない場合のみ、backupDirの内容をcacheDirへ丸ごと復元する。
        /// cacheDirが既に存在する場合、backupDirが存在しない場合は何もしない
        /// （復元に失敗・不要でもエンジンが初回ビルドし直すだけなので致命的ではない）。</summary>
        public static void RestoreCacheIfNeeded(string cacheDir, string backupDir)
        {
            try
            {
                if (Directory.Exists(cacheDir)) return;
                if (!Directory.Exists(backupDir)) return;
                Trace($"trtcacheを復元: {backupDir} → {cacheDir}");
                CopyDirectoryRecursive(backupDir, cacheDir);
            }
            catch (Exception ex)
            {
                Trace($"trtcache復元失敗（次回エンジン初回ビルドに任せます）: {ex.Message}");
            }
        }

        /// <summary>cacheDirの内容をbackupDirへ丸ごとバックアップする（既存のbackupDirは削除して置き換え）。
        /// cacheDirが存在しない（DNNが一度も使われていない）場合は何もしない。
        /// アプリ終了時に呼ぶことを想定（失敗しても握りつぶす）。</summary>
        public static void BackupCache(string cacheDir, string backupDir)
        {
            try
            {
                if (!Directory.Exists(cacheDir)) return;
                Trace($"trtcacheをバックアップ: {cacheDir} → {backupDir}");
                if (Directory.Exists(backupDir))
                    Directory.Delete(backupDir, recursive: true);
                CopyDirectoryRecursive(cacheDir, backupDir);
            }
            catch (Exception ex)
            {
                Trace($"trtcacheバックアップ失敗: {ex.Message}");
            }
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.GetDirectories(sourceDir))
                CopyDirectoryRecursive(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }

        private static void Trace(string msg)
        {
#if DEBUG
            try
            {
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trace.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} | [DnnSR] {msg}{Environment.NewLine}",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
#endif
        }
    }
}
