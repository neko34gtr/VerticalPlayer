using FFmpeg.AutoGen;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VerticalPlayer.Media
{
    /// <summary>
    /// FFmpeg.AutoGen (v8.1.0 / shared dll) による「映像デコード専用」エンジン。
    /// 音声・シーク・速度制御・マスタークロックは上位の FfmpegMediaElement 側の
    /// 非表示 MediaElement に一本化されている（本クラスは映像デコード＋描画のみ担当）。
    ///
    /// 【今回追加】
    /// - ハードウェアデコード（D3D11VA）対応。要求時のみ有効化を試み、非対応環境や
    ///   失敗時は自動的にソフトウェアデコードへフォールバックする（例外にしない）。
    /// - 実際にどちらのモードで復号しているかを DecodeModeChanged イベントで通知。
    /// - コントラスト／彩度／ガンマをデコード後のBGRAバッファへCPUで直接適用
    ///   （WPFのEffectはMediaElement/Image双方で信頼性が低いため、生ピクセルに対して処理）。
    /// </summary>
    public sealed unsafe class AVEngine : IDisposable
    {
        public event Action<int, int, TimeSpan>? Opened;
        public event Action<Exception>? Failed;
        /// <summary>実際に使われたデコードモードを通知（例: "HW (D3D11VA)" / "SW"）。UIスレッドで発火。</summary>
        public event Action<string>? DecodeModeChanged;

        private readonly Dispatcher _ui;
        private int _generation;

        private Thread? _decodeThread;
        private volatile bool _paused = true;
        private double _speedRatio = 1.0;

        /// <summary>次にOpen()する際にハードウェアデコードを試みるかどうか。</summary>
        public bool HardwareAccelRequested { get; set; }

        // ── エフェクト（-1〜1想定。0が無効） ──
        private volatile bool _effectsActive;
        private double _contrast, _saturation, _gamma;
        private volatile bool _deinterlaceEnabled;

        /// <summary>簡易デインターレース（隣接ラインのブレンド方式）の有効/無効。</summary>
        public bool DeinterlaceEnabled
        {
            get => _deinterlaceEnabled;
            set => _deinterlaceEnabled = value;
        }

        // ── 外部マスタークロック（FfmpegMediaElement内の非表示MediaElementのPositionを反映） ──
        private readonly Stopwatch _extClock = new();
        private double _extBaseSeconds;
        private volatile bool _extPlaying;

        private readonly object _seekLock = new();
        private double _pendingSeekSeconds = -1;

        public WriteableBitmap? Bitmap { get; private set; }
        public int VideoWidth { get; private set; }
        public int VideoHeight { get; private set; }
        public TimeSpan Duration { get; private set; }

        public AVEngine(Dispatcher uiDispatcher)
        {
            _ui = uiDispatcher;
            EnsureFfmpegBinaries();
        }

        private static bool _binariesReady;
        private static void EnsureFfmpegBinaries()
        {
            if (_binariesReady) return;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string candidate = Path.Combine(baseDir, "ffmpeg");
                ffmpeg.RootPath = Directory.Exists(candidate) ? candidate : baseDir;
                _binariesReady = true;
            }
            catch (Exception ex)
            {
                Trace($"EnsureFfmpegBinaries failed: {ex.Message}");
            }
        }

        public void SetSpeedRatio(double ratio) => _speedRatio = Math.Clamp(ratio, 0.1, 4.0);

        public void SetEffects(double contrast, double saturation, double gamma)
        {
            _contrast = Math.Clamp(contrast, -1, 1);
            _saturation = Math.Clamp(saturation, -1, 1);
            _gamma = Math.Clamp(gamma, -1, 1);
            _effectsActive = _contrast != 0 || _saturation != 0 || _gamma != 0;
        }

        public void SetExternalClock(double seconds, bool isPlaying)
        {
            _extBaseSeconds = seconds;
            _extClock.Restart();
            _extPlaying = isPlaying;
        }

        private double GetMasterClockSec()
        {
            if (!_extPlaying) return _extBaseSeconds;
            return _extBaseSeconds + _extClock.Elapsed.TotalSeconds * _speedRatio;
        }

        // ─────────────────────────────────────────────────────────────
        // Open / Stop
        // ─────────────────────────────────────────────────────────────
        public void Open(Uri source)
        {
            Stop();

            int myGen = Interlocked.Increment(ref _generation);
            _paused = true;
            _extBaseSeconds = 0;
            _extClock.Restart();
            _extPlaying = false;

            bool wantHw = HardwareAccelRequested;
            var t = new Thread(() => OpenAndRun(source.LocalPath, myGen, wantHw))
            {
                IsBackground = true,
                Name = "AVEngine-VideoDecode"
            };
            _decodeThread = t;
            t.Start();
        }

        public void Stop()
        {
            Interlocked.Increment(ref _generation);
            _paused = true;

            // 旧スレッドの終了をUIスレッドで同期待ちしない。ネイティブリソースは
            // 各デコードスレッド自身のローカル変数として保持・解放されるため、
            // 旧スレッドが多少長く生き残っても他スレッドと衝突しない設計になっている。
            // ここでJoin()してUIスレッドを止めると、旧スレッドが何らかの理由で
            // 応答が遅い場合に無関係な操作までブロックしてしまう。
            var t = _decodeThread;
            _decodeThread = null;
            if (t != null)
            {
                Task.Run(() =>
                {
                    if (!t.Join(10000))
                        Trace($"Stop(): decode thread did not exit within 10000ms ({t.ManagedThreadId}) - リーク疑いあり");
                });
            }
        }

        public void Play() => _paused = false;
        public void Pause() => _paused = true;

        public void Seek(TimeSpan pos)
        {
            lock (_seekLock)
            {
                _pendingSeekSeconds = Math.Max(0, pos.TotalSeconds);
            }
        }

        public void Dispose() => Stop();

        // ─────────────────────────────────────────────────────────────
        // ハードウェアデコード用: get_format コールバック
        // ─────────────────────────────────────────────────────────────
        [ThreadStatic] private static AVPixelFormat _negotiatedHwPixFmt;
        private static readonly AVCodecContext_get_format _getHwFormatDelegate = GetHwFormat;

        private static AVPixelFormat GetHwFormat(AVCodecContext* ctx, AVPixelFormat* pixFmts)
        {
            for (var p = pixFmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == _negotiatedHwPixFmt)
                {
                    Trace($"get_format: HW形式({*p})を採用");
                    return *p;
                }
            }
            Trace($"get_format: HW形式({_negotiatedHwPixFmt})が候補に無く、先頭の{*pixFmts}にフォールバック（＝実質SW動作）");
            return *pixFmts;
        }

        // ─────────────────────────────────────────────────────────────
        // デコードスレッド本体（映像のみ）
        // ─────────────────────────────────────────────────────────────
        private void OpenAndRun(string path, int myGen, bool wantHw)
        {
            AVFormatContext* fmt = null;
            AVCodecContext* vctx = null;
            SwsContext* sws = null;
            int videoIdx = -1;
            bool hwActive = false;
            AVBufferRef* hwDeviceCtx = null;

            AVPacket* pkt = null;
            AVFrame* frame = null;
            AVFrame* swFrame = null;
            AVFrame* rgbFrame = null;
            byte* rgbBuffer = null;

            try
            {
                OpenStreamsWithHw(path, wantHw, out fmt, out vctx, out videoIdx, out hwActive, out hwDeviceCtx);

                int w = vctx->width, h = vctx->height;
                double durSec = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
                var duration = TimeSpan.FromSeconds(durSec);
                string modeLabel = hwActive ? "HW (D3D11VA)" : "SW";

                if (myGen != _generation) return;

                _ui.BeginInvoke(new Action(() =>
                {
                    if (myGen != _generation) return;
                    Bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                    VideoWidth = w; VideoHeight = h; Duration = duration;
                    Opened?.Invoke(w, h, duration);
                    DecodeModeChanged?.Invoke(modeLabel);
                }));

                Trace($"Opened(gen={myGen}): {path} {w}x{h} dur={duration} mode={modeLabel}");

                pkt = ffmpeg.av_packet_alloc();
                frame = ffmpeg.av_frame_alloc();
                swFrame = ffmpeg.av_frame_alloc();
                rgbFrame = ffmpeg.av_frame_alloc();

                int bufSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, w, h, 1);
                rgbBuffer = (byte*)ffmpeg.av_malloc((ulong)bufSize);
                byte_ptrArray4 dstData = new byte_ptrArray4();
                int_array4 dstLinesize = new int_array4();
                ffmpeg.av_image_fill_arrays(ref dstData, ref dstLinesize, rgbBuffer,
                    AVPixelFormat.AV_PIX_FMT_BGRA, w, h, 1);
                for (uint i = 0; i < 4; i++)
                {
                    rgbFrame->data[i] = dstData[i];
                    rgbFrame->linesize[i] = dstLinesize[i];
                }

                byte[] managedBuf = new byte[bufSize];

                while (myGen == _generation)
                {
                    lock (_seekLock)
                    {
                        if (_pendingSeekSeconds >= 0)
                        {
                            double target = _pendingSeekSeconds;
                            _pendingSeekSeconds = -1;
                            long ts = (long)(target / ffmpeg.av_q2d(fmt->streams[videoIdx]->time_base));
                            ffmpeg.av_seek_frame(fmt, videoIdx, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                            ffmpeg.avcodec_flush_buffers(vctx);
                            Trace($"Seek -> {target:F2}s");
                        }
                    }

                    if (_paused)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    int rr = ffmpeg.av_read_frame(fmt, pkt);
                    if (rr < 0)
                    {
                        _paused = true;
                        Trace($"DecodeLoop(gen={myGen}): end of stream");
                        continue;
                    }

                    if (pkt->stream_index == videoIdx)
                    {
                        if (ffmpeg.avcodec_send_packet(vctx, pkt) == 0)
                        {
                            while (myGen == _generation && ffmpeg.avcodec_receive_frame(vctx, frame) == 0)
                            {
                                AVFrame* srcFrame = frame;
                                if (hwActive)
                                {
                                    ffmpeg.av_frame_unref(swFrame);
                                    if (ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0) < 0)
                                    {
                                        Trace("av_hwframe_transfer_data failed - frame skipped");
                                        continue;
                                    }
                                    srcFrame = swFrame;
                                }

                                if (sws == null)
                                {
                                    sws = ffmpeg.sws_getContext(w, h, (AVPixelFormat)srcFrame->format,
                                        w, h, AVPixelFormat.AV_PIX_FMT_BGRA, 2, null, null, null);
                                    Trace($"sws_getContext created srcFmt={(AVPixelFormat)srcFrame->format}");
                                }

                                ffmpeg.sws_scale(sws, srcFrame->data, srcFrame->linesize, 0, h,
                                    rgbFrame->data, rgbFrame->linesize);

                                double ptsSeconds = frame->best_effort_timestamp == ffmpeg.AV_NOPTS_VALUE
                                    ? GetMasterClockSec()
                                    : frame->best_effort_timestamp * ffmpeg.av_q2d(fmt->streams[videoIdx]->time_base);

                                double master = GetMasterClockSec();
                                double diff = ptsSeconds - master;
                                bool drop = false;
                                if (diff > 0.001)
                                {
                                    int waitMs = (int)Math.Min(diff * 1000, 200);
                                    if (waitMs > 0) Thread.Sleep(waitMs);
                                }
                                else if (diff < -0.04)
                                {
                                    drop = true;
                                }

                                if (!drop)
                                {
                                    int stride = rgbFrame->linesize[0];
                                    System.Runtime.InteropServices.Marshal.Copy((IntPtr)rgbFrame->data[0], managedBuf, 0, bufSize);

                                    if (_deinterlaceEnabled)
                                        ApplyDeinterlaceBlend(managedBuf, w, h, stride);

                                    if (_effectsActive)
                                        ApplyEffects(managedBuf, bufSize);

                                    var localBuf = managedBuf;
                                    int frameGen = myGen;
                                    int frameW = w, frameH = h;
                                    _ui.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                                    {
                                        if (frameGen != _generation) return;
                                        try
                                        {
                                            Bitmap?.WritePixels(new Int32Rect(0, 0, frameW, frameH), localBuf, stride, 0);
                                        }
                                        catch (Exception ex)
                                        {
                                            Trace($"WritePixels skipped: {ex.Message}");
                                        }
                                    }));
                                }
                                else
                                {
                                    Trace($"Frame dropped (behind {(-diff) * 1000:F0}ms) pts={ptsSeconds:F3}");
                                    Thread.Sleep(1); // 大量ドロップ時にデコーダ/GPUを連続で叩き過ぎないようにする
                                }
                            }
                        }
                    }

                    ffmpeg.av_packet_unref(pkt);
                }
            }
            catch (Exception ex)
            {
                Trace($"DecodeThread(gen={myGen}) error: {ex}");
                int failGen = myGen;
                _ui.BeginInvoke(new Action(() =>
                {
                    if (failGen != _generation) return;
                    Failed?.Invoke(ex);
                }));
            }
            finally
            {
                if (rgbBuffer != null) ffmpeg.av_free(rgbBuffer);
                if (frame != null) { var f2 = frame; ffmpeg.av_frame_free(&f2); }
                if (swFrame != null) { var f4 = swFrame; ffmpeg.av_frame_free(&f4); }
                if (rgbFrame != null) { var f3 = rgbFrame; ffmpeg.av_frame_free(&f3); }
                if (pkt != null) { var p2 = pkt; ffmpeg.av_packet_free(&p2); }
                if (sws != null) ffmpeg.sws_freeContext(sws);
                // hw_device_ctx は avcodec_free_context() が内部で解放するため、
                // ここで自分で av_buffer_unref すると二重解放になり
                // ExecutionEngineException（ネイティブ側のメモリ破壊）の原因になる
                if (vctx != null) { var v = vctx; ffmpeg.avcodec_free_context(&v); }
                if (fmt != null) { var f = fmt; ffmpeg.avformat_close_input(&f); }
                Trace($"DecodeThread(gen={myGen}) fully exited");
            }
        }

        private static void OpenStreamsWithHw(string path, bool wantHw,
            out AVFormatContext* fmt, out AVCodecContext* vctx, out int videoIdx,
            out bool hwActive, out AVBufferRef* hwDeviceCtx)
        {
            hwActive = false;
            hwDeviceCtx = null;

            AVFormatContext* f = null;
            if (ffmpeg.avformat_open_input(&f, path, null, null) != 0)
                throw new InvalidOperationException($"avformat_open_input failed: {path}");
            fmt = f;

            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                throw new InvalidOperationException("avformat_find_stream_info failed");

            AVCodec* vcodec = null;
            videoIdx = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &vcodec, 0);
            if (videoIdx < 0 || vcodec == null)
                throw new InvalidOperationException("no video stream");

            var vc = ffmpeg.avcodec_alloc_context3(vcodec);
            ffmpeg.avcodec_parameters_to_context(vc, fmt->streams[videoIdx]->codecpar);

            Trace($"OpenStreamsWithHw: codec={ByteToString(vcodec->name)} wantHw={wantHw}");

            if (wantHw)
            {
                try
                {
                    AVPixelFormat hwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
                    int cfgCount = 0;
                    for (int i = 0; ; i++)
                    {
                        var cfg = ffmpeg.avcodec_get_hw_config(vcodec, i);
                        if (cfg == null) break;
                        cfgCount++;
                        Trace($"  hw_config[{i}]: device_type={cfg->device_type} pix_fmt={cfg->pix_fmt} methods=0x{cfg->methods:X}");
                        if ((cfg->methods & 0x01 /* AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX（AutoGenに列挙が無いため直値） */) != 0
                            && cfg->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)
                        {
                            hwPixFmt = cfg->pix_fmt;
                            break;
                        }
                    }
                    Trace($"OpenStreamsWithHw: hw_config候補数={cfgCount} 選択pix_fmt={hwPixFmt}");

                    if (hwPixFmt != AVPixelFormat.AV_PIX_FMT_NONE)
                    {
                        AVBufferRef* devCtx = null;
                        int devRet = ffmpeg.av_hwdevice_ctx_create(&devCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
                        if (devRet == 0)
                        {
                            _negotiatedHwPixFmt = hwPixFmt;
                            vc->get_format = _getHwFormatDelegate;
                            vc->hw_device_ctx = ffmpeg.av_buffer_ref(devCtx);
                            ffmpeg.av_buffer_unref(&devCtx);
                            hwDeviceCtx = vc->hw_device_ctx;
                            hwActive = true;
                            Trace("OpenStreamsWithHw: av_hwdevice_ctx_create(D3D11VA) 成功、hw_device_ctx設定完了");
                        }
                        else
                        {
                            Trace($"OpenStreamsWithHw: av_hwdevice_ctx_create(D3D11VA) 失敗 code={devRet} - SWにフォールバック");
                        }
                    }
                    else
                    {
                        Trace("OpenStreamsWithHw: このコーデックはD3D11VAの対応構成が見つからない - SWにフォールバック");
                    }
                }
                catch (Exception ex)
                {
                    Trace($"OpenStreamsWithHw: HW初期化中に例外 - SWにフォールバック: {ex.Message}");
                    hwActive = false;
                }
            }

            int openRet = ffmpeg.avcodec_open2(vc, vcodec, null);
            if (openRet < 0)
            {
                if (hwActive)
                {
                    Trace($"OpenStreamsWithHw: HWデコーダのopenに失敗 code={openRet} - SWで再試行");
                    hwActive = false;
                    vc->get_format = null;
                    if (vc->hw_device_ctx != null) { var h2 = vc->hw_device_ctx; ffmpeg.av_buffer_unref(&h2); vc->hw_device_ctx = null; }
                    if (ffmpeg.avcodec_open2(vc, vcodec, null) < 0)
                        throw new InvalidOperationException("avcodec_open2(video) failed");
                }
                else
                {
                    throw new InvalidOperationException("avcodec_open2(video) failed");
                }
            }
            Trace($"OpenStreamsWithHw: avcodec_open2完了 hwActive={hwActive} vc->pix_fmt(SW側の想定値)={vc->pix_fmt}");
            vctx = vc;
        }

        private static unsafe string ByteToString(byte* ptr)
        {
            try { return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)ptr) ?? "?"; }
            catch { return "?"; }
        }

        // ─────────────────────────────────────────────────────────────
        // 簡易デインターレース（ブレンド方式）
        // 各ラインを直下のラインと平均化し、横縞(コーミング)を軽減する。
        // yadif等のフィルタグラフを使わないため画質は簡易的だが、失敗リスクが低い。
        // ─────────────────────────────────────────────────────────────
        private void ApplyDeinterlaceBlend(byte[] buf, int width, int height, int stride)
        {
            for (int y = 0; y < height - 1; y++)
            {
                int row = y * stride;
                int nextRow = row + stride;
                for (int x = 0; x < stride; x++)
                {
                    buf[row + x] = (byte)((buf[row + x] + buf[nextRow + x]) >> 1);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // コントラスト / 彩度 / ガンマ（BGRAバッファへ直接適用）
        // ─────────────────────────────────────────────────────────────
        private byte[]? _lut;
        private double _lutContrast = double.NaN, _lutGamma = double.NaN;

        private byte[] GetLut(double contrast, double gamma)
        {
            if (_lut != null && _lutContrast == contrast && _lutGamma == gamma) return _lut;
            var lut = new byte[256];
            double gammaExp = Math.Pow(2, gamma);      // -1〜1 → 0.5〜2.0
            double factor = 1.0 + contrast;             // -1〜1 → 0〜2.0
            for (int i = 0; i < 256; i++)
            {
                double v = Math.Pow(i / 255.0, 1.0 / gammaExp) * 255.0;
                v = (v - 128) * factor + 128;
                lut[i] = (byte)Math.Clamp(v, 0, 255);
            }
            _lut = lut; _lutContrast = contrast; _lutGamma = gamma;
            return lut;
        }

        private void ApplyEffects(byte[] buf, int len)
        {
            double contrast = _contrast, saturation = _saturation, gamma = _gamma;
            byte[]? lut = (contrast != 0 || gamma != 0) ? GetLut(contrast, gamma) : null;
            double satFactor = 1.0 + saturation;

            for (int i = 0; i + 3 < len; i += 4)
            {
                byte b = buf[i], g = buf[i + 1], r = buf[i + 2]; // BGRA
                if (lut != null) { b = lut[b]; g = lut[g]; r = lut[r]; }
                if (saturation != 0)
                {
                    double gray = 0.299 * r + 0.587 * g + 0.114 * b;
                    r = (byte)Math.Clamp(gray + (r - gray) * satFactor, 0, 255);
                    g = (byte)Math.Clamp(gray + (g - gray) * satFactor, 0, 255);
                    b = (byte)Math.Clamp(gray + (b - gray) * satFactor, 0, 255);
                }
                buf[i] = b; buf[i + 1] = g; buf[i + 2] = r;
            }
        }

        private static void Trace(string msg)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trace.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} | [AVEngine] {msg}{Environment.NewLine}",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }
    }
}
