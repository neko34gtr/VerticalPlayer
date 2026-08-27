using FFmpeg.AutoGen;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VerticalPlayer.Media
{
    /// <summary>
    /// FFmpeg.AutoGen (v8.1.0 / shared dll) による「映像デコード専用」エンジン。
    ///
    /// 【方針転換】
    /// 音声デコード(NAudio/WASAPI)を自前実装すると、マスタークロックの算出やシーク時の
    /// バッファ整合など破綻しやすい要素が多いため撤去。音声再生・シーク・速度制御・
    /// Position（＝再生マスタークロック）は、上位の FfmpegMediaElement が保持する
    /// 非表示 MediaElement（音声専用として流用）に一本化する。
    ///
    /// このクラスはもはや音声を一切扱わない。外部（FfmpegMediaElement）から
    /// SetExternalClock() で定期的に「本当の再生位置」を受け取り、映像フレームの
    /// 表示タイミングをそれに追従させる（早ければ待つ／40ms以上遅れたら間引く）だけの、
    /// 純粋な「映像デコード＋描画」コンポーネント。
    /// </summary>
    public sealed unsafe class AVEngine : IDisposable
    {
        public event Action<int, int, TimeSpan>? Opened;
        public event Action<Exception>? Failed;

        private readonly Dispatcher _ui;
        private int _generation;

        private Thread? _decodeThread;
        private volatile bool _paused = true;
        private double _speedRatio = 1.0;

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

        /// <summary>再生速度（マスタークロックの外挿計算に使うだけ。実際の速度制御はMediaElement側）。</summary>
        public void SetSpeedRatio(double ratio) => _speedRatio = Math.Clamp(ratio, 0.1, 4.0);

        /// <summary>
        /// 外部（音声用MediaElement）の現在位置を通知する。isPlaying=true の間は
        /// Stopwatchで外挿し、次の通知が来るまで滑らかに時間を進める。
        /// </summary>
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

            var t = new Thread(() => OpenAndRun(source.LocalPath, myGen))
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

            var t = _decodeThread;
            _decodeThread = null;
            if (t != null && !t.Join(2000))
                Trace($"Stop(): decode thread did not exit within 2000ms ({t.ManagedThreadId})");
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
        // デコードスレッド本体（映像のみ）
        // ─────────────────────────────────────────────────────────────
        private void OpenAndRun(string path, int myGen)
        {
            AVFormatContext* fmt = null;
            AVCodecContext* vctx = null;
            SwsContext* sws = null;
            int videoIdx = -1;

            AVPacket* pkt = null;
            AVFrame* frame = null;
            AVFrame* rgbFrame = null;
            byte* rgbBuffer = null;

            try
            {
                OpenStreams(path, out fmt, out vctx, out sws, out videoIdx);

                int w = vctx->width, h = vctx->height;
                double durSec = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
                var duration = TimeSpan.FromSeconds(durSec);

                if (myGen != _generation) return;

                _ui.BeginInvoke(new Action(() =>
                {
                    if (myGen != _generation) return;
                    Bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                    VideoWidth = w; VideoHeight = h; Duration = duration;
                    Opened?.Invoke(w, h, duration);
                }));

                Trace($"Opened(gen={myGen}): {path} {w}x{h} dur={duration}");

                pkt = ffmpeg.av_packet_alloc();
                frame = ffmpeg.av_frame_alloc();
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
                            while (ffmpeg.avcodec_receive_frame(vctx, frame) == 0)
                            {
                                ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, h,
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
                                    drop = true; // 映像が40ms以上遅れている→描画せず追いつく
                                }

                                if (!drop)
                                {
                                    int stride = rgbFrame->linesize[0];
                                    System.Runtime.InteropServices.Marshal.Copy((IntPtr)rgbFrame->data[0], managedBuf, 0, bufSize);
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
                if (rgbFrame != null) { var f3 = rgbFrame; ffmpeg.av_frame_free(&f3); }
                if (pkt != null) { var p2 = pkt; ffmpeg.av_packet_free(&p2); }
                if (sws != null) ffmpeg.sws_freeContext(sws);
                if (vctx != null) { var v = vctx; ffmpeg.avcodec_free_context(&v); }
                if (fmt != null) { var f = fmt; ffmpeg.avformat_close_input(&f); }
                Trace($"DecodeThread(gen={myGen}) fully exited");
            }
        }

        private static void OpenStreams(string path,
            out AVFormatContext* fmt, out AVCodecContext* vctx, out SwsContext* sws, out int videoIdx)
        {
            sws = null;

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
            if (ffmpeg.avcodec_open2(vc, vcodec, null) < 0)
                throw new InvalidOperationException("avcodec_open2(video) failed");
            vctx = vc;

            sws = ffmpeg.sws_getContext(
                vctx->width, vctx->height, vctx->pix_fmt,
                vctx->width, vctx->height, AVPixelFormat.AV_PIX_FMT_BGRA,
                2 /* SWS_BILINEAR */, null, null, null);
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
