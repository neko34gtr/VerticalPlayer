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
        private bool _lastInitFailed;
        private bool _loggedInferError;

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
        public bool EnsureEngine(int width, int height)
        {
            if (_session != null && _builtWidth == width && _builtHeight == height)
                return true;

            // 解像度が変わった場合は古いセッションを破棄してから作り直す
            DisposeSession();

            try
            {
                if (!File.Exists(_modelPath))
                {
                    Trace($"モデルファイルが見つかりません: {_modelPath}");
                    _lastInitFailed = true;
                    return false;
                }

                Directory.CreateDirectory(_cacheDir);

                var so = new SessionOptions();
                so.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

                // TensorRT EP: 解像度固定（min=opt=max）でエンジンをビルドし、_cacheDirへキャッシュ。
                // 2回目以降の同一解像度はキャッシュから高速ロードされる。
                // NOTE: プロバイダオプションのキー名・APIはOnnxRuntime.Gpuのバージョンに依存するため、
                // 実際に参照するバージョンのドキュメント/サンプルと突き合わせて確認すること
                // （ここではv1.20系のC# APIを想定して記述）。
                try
                {
                    var trtOptions = new OrtTensorRTProviderOptions();
                    string shapeSpec = $"1x3x{height}x{width}";
                    var trtDict = new Dictionary<string, string>
                    {
                        ["device_id"] = "0",
                        ["trt_fp16_enable"] = "1",
                        ["trt_engine_cache_enable"] = "1",
                        ["trt_engine_cache_path"] = _cacheDir,
                        ["trt_timing_cache_enable"] = "1",
                        // min=opt=max固定にすることで解像度ごとの専用エンジンとしてビルドさせる
                        ["trt_profile_min_shapes"] = $"input:{shapeSpec}",
                        ["trt_profile_opt_shapes"] = $"input:{shapeSpec}",
                        ["trt_profile_max_shapes"] = $"input:{shapeSpec}",
                    };
                    trtOptions.UpdateOptions(trtDict);
                    so.AppendExecutionProvider_Tensorrt(trtOptions);
                    Trace($"TensorRT EP追加: {width}x{height}, cache={_cacheDir}");
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
                _builtWidth = width;
                _builtHeight = height;
                _lastInitFailed = false;

                Trace($"DNN超解像エンジン初期化完了: {width}x{height} input={_inputName} output={_outputName}");
                return true;
            }
            catch (Exception ex)
            {
                Trace($"DNN超解像エンジン初期化失敗: {ex}");
                DisposeSession();
                _lastInitFailed = true;
                return false;
            }
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
                var inputTensor = new DenseTensor<OrtFloat16>(new[] { 1, 3, height, width });
                for (int y = 0; y < height; y++)
                {
                    int rowBase = y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        int i = rowBase + x * 4;
                        byte b = srcBgra[i];
                        byte g = srcBgra[i + 1];
                        byte r = srcBgra[i + 2];
                        inputTensor[0, 0, y, x] = (OrtFloat16)(r / 255f);
                        inputTensor[0, 1, y, x] = (OrtFloat16)(g / 255f);
                        inputTensor[0, 2, y, x] = (OrtFloat16)(b / 255f);
                    }
                }

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor<OrtFloat16>(_inputName, inputTensor)
                };

                using var results = _session.Run(inputs, new[] { _outputName });
                // モデル仕様: output=[1,3,H*4,W*4] float16 0-1正規化NCHW
                var outTensor = results[0].AsTensor<OrtFloat16>();
                int outH = outTensor.Dimensions[2];
                int outW = outTensor.Dimensions[3];

                var dst = new byte[outW * outH * 4];
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

        private void DisposeSession()
        {
            _session?.Dispose();
            _session = null;
            _inputName = null;
            _outputName = null;
            _builtWidth = 0;
            _builtHeight = 0;
            _loggedInferError = false;
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
