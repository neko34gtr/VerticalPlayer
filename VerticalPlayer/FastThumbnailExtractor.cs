using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace VerticalPlayer.Media
{
    /// <summary>
    /// ドラレコのファイル一覧用サムネイルを、再生用プレイヤー(ThumbCapturePlayer)を経由せずに
    /// FFmpegで直接1枚だけ取り出す高速抽出器。ファイルを開いて先頭の1フレームだけをソフトウェアデコードし、
    /// 縮小してBGRA(4バイト/px)で返す。ファイルごとに独立したFFmpegコンテキストを使うため、
    /// 複数ファイルを並列に処理できる（UIスレッド不要・HWデコード初期化なし・シーク待ちなし）。
    /// 失敗した場合はnullを返す（呼び出し側が従来のプレイヤー経由の生成へフォールバックする）。
    /// </summary>
    public static unsafe class FastThumbnailExtractor
    {
        private static readonly object InitLock = new();
        private static bool _binariesReady;

        private static void EnsureBinaries()
        {
            if (_binariesReady) return;
            lock (InitLock)
            {
                if (_binariesReady) return;
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string candidate = Path.Combine(baseDir, "ffmpeg");
                ffmpeg.RootPath = Directory.Exists(candidate) ? candidate : baseDir; // AVEngineと同じ探索先
                _binariesReady = true;
            }
        }

        /// <summary>動画の先頭付近の1フレームを outW x outH のBGRAバイト列にして返す。失敗時はnull。</summary>
        public static byte[]? ExtractBgra(string path, int outW, int outH)
        {
            EnsureBinaries();

            AVFormatContext* fmt = null;
            AVCodecContext* vc = null;
            AVPacket* pkt = null;
            AVFrame* frame = null;
            AVFrame* rgbFrame = null;
            SwsContext* sws = null;
            byte* rgbBuffer = null;

            try
            {
                AVFormatContext* f = null;
                if (ffmpeg.avformat_open_input(&f, path, null, null) != 0) return null;
                fmt = f;

                if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) return null;

                AVCodec* codec = null;
                int videoIdx = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
                if (videoIdx < 0 || codec == null) return null;

                vc = ffmpeg.avcodec_alloc_context3(codec);
                if (vc == null) return null;
                ffmpeg.avcodec_parameters_to_context(vc, fmt->streams[videoIdx]->codecpar);
                vc->thread_count = 1; // 多数のファイルを並列に処理するため、1ファイルあたりは1スレッド
                vc->skip_loop_filter = AVDiscard.AVDISCARD_ALL; // 160x90への縮小用途なのでデブロッキングを省略して高速化
                if (ffmpeg.avcodec_open2(vc, codec, null) < 0) return null;

                pkt = ffmpeg.av_packet_alloc();
                frame = ffmpeg.av_frame_alloc();
                if (pkt == null || frame == null) return null;

                // 最初にデコードできた映像フレームを使う（シークしない）
                bool got = false;
                for (int guard = 0; guard < 400 && !got; guard++)
                {
                    if (ffmpeg.av_read_frame(fmt, pkt) < 0) break;
                    try
                    {
                        if (pkt->stream_index != videoIdx) continue;
                        if (ffmpeg.avcodec_send_packet(vc, pkt) < 0) continue;
                        if (ffmpeg.avcodec_receive_frame(vc, frame) == 0) got = true;
                    }
                    finally
                    {
                        ffmpeg.av_packet_unref(pkt);
                    }
                }
                if (!got)
                {
                    // パケットを使い切った場合はデコーダ内に残っているフレームを吐き出させる
                    ffmpeg.avcodec_send_packet(vc, null);
                    if (ffmpeg.avcodec_receive_frame(vc, frame) == 0) got = true;
                }
                if (!got || frame->width <= 0 || frame->height <= 0) return null;

                int srcW = frame->width, srcH = frame->height;
                sws = ffmpeg.sws_getContext(srcW, srcH, (AVPixelFormat)frame->format,
                    outW, outH, AVPixelFormat.AV_PIX_FMT_BGRA, 2 /* SWS_BILINEAR */, null, null, null);
                if (sws == null) return null;

                // AVEngineと同じ手順で、縮小先バッファを持つ出力フレームを用意する
                rgbFrame = ffmpeg.av_frame_alloc();
                int bufSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, outW, outH, 1);
                rgbBuffer = (byte*)ffmpeg.av_malloc((ulong)bufSize);
                byte_ptrArray4 dstData = new byte_ptrArray4();
                int_array4 dstLinesize = new int_array4();
                ffmpeg.av_image_fill_arrays(ref dstData, ref dstLinesize, rgbBuffer,
                    AVPixelFormat.AV_PIX_FMT_BGRA, outW, outH, 1);
                for (uint i = 0; i < 4; i++)
                {
                    rgbFrame->data[i] = dstData[i];
                    rgbFrame->linesize[i] = dstLinesize[i];
                }

                ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, srcH, rgbFrame->data, rgbFrame->linesize);

                var result = new byte[bufSize];
                Marshal.Copy((IntPtr)rgbFrame->data[0], result, 0, bufSize);
                return result;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (sws != null) ffmpeg.sws_freeContext(sws);
                if (rgbBuffer != null) ffmpeg.av_free(rgbBuffer);
                if (rgbFrame != null) { var r = rgbFrame; ffmpeg.av_frame_free(&r); }
                if (frame != null) { var fr = frame; ffmpeg.av_frame_free(&fr); }
                if (pkt != null) { var p = pkt; ffmpeg.av_packet_free(&p); }
                if (vc != null) { var v = vc; ffmpeg.avcodec_free_context(&v); }
                if (fmt != null) { var f2 = fmt; ffmpeg.avformat_close_input(&f2); }
            }
        }
    }
}
