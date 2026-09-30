using FFmpeg.AutoGen;
using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VerticalPlayer.Media
{
    /// <summary>スクリーンショットの保存形式。ScreenshotFormatSettingと文字列(enum名)で対応させて永続化する。</summary>
    public enum ScreenshotFormat
    {
        Png,
        Jpg,
        WebP,
        Avif,
    }

    /// <summary>
    /// スクリーンショットをPNG/JPG/WebP/AVIFで保存する。
    /// PNG/JPGはWPF標準のエンコーダー、WebP/AVIFは本体が読み込んでいるFFmpeg(AutoGen)のエンコーダー
    /// (libwebp / libsvtav1 / libaom-av1)で書き出す。使用中のFFmpegにそのエンコーダーが無い場合は
    /// 例外にせずJPGへフォールバックし、理由をfallbackNoteで返す。
    /// </summary>
    public static unsafe class ScreenshotEncoder
    {
        private const int JpegQuality = 95;
        private const string WebpQuality = "92";
        private const string AvifCrf = "24";

        public static string GetExtension(ScreenshotFormat format) => format switch
        {
            ScreenshotFormat.Png => ".png",
            ScreenshotFormat.Jpg => ".jpg",
            ScreenshotFormat.WebP => ".webp",
            ScreenshotFormat.Avif => ".avif",
            _ => ".png",
        };

        /// <summary>保存して、実際に保存したファイルのパスを返す。別スレッドから呼んでよい
        /// （bitmapはFreeze済みであること）。</summary>
        public static string Save(BitmapSource bitmap, string directory, string baseName,
            ScreenshotFormat format, out string? fallbackNote)
        {
            Directory.CreateDirectory(directory);
            fallbackNote = null;
            var actual = format;

            if (format is ScreenshotFormat.WebP or ScreenshotFormat.Avif)
            {
                string path = UniquePath(directory, baseName, GetExtension(format));
                try
                {
                    EncodeWithFfmpeg(bitmap, path, format);
                    return path;
                }
                catch (Exception ex)
                {
                    TryDelete(path);
                    fallbackNote = $"{format}形式で保存できなかったため、JPGで保存しました。\n（{ex.Message}）";
                    actual = ScreenshotFormat.Jpg;
                }
            }

            string savePath = UniquePath(directory, baseName, GetExtension(actual));
            SaveWithWpf(bitmap, savePath, actual);
            return savePath;
        }

        private static string UniquePath(string directory, string baseName, string ext)
        {
            string path = Path.Combine(directory, baseName + ext);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(directory, $"{baseName}_{i}{ext}");
            return path;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void SaveWithWpf(BitmapSource bitmap, string path, ScreenshotFormat format)
        {
            BitmapEncoder encoder;
            BitmapSource source = bitmap;
            if (format == ScreenshotFormat.Jpg)
            {
                // JPEGはアルファを持てないのでBgr24へ変換してから保存する
                source = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
                encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            }
            else
            {
                encoder = new PngBitmapEncoder();
            }
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);
        }

        // ── FFmpegによるWebP / AVIF書き出し ──

        private static void EncodeWithFfmpeg(BitmapSource bitmap, string path, ScreenshotFormat format)
        {
            // YUV420Pは幅・高さが偶数である必要があるため、奇数なら1pxだけ切り落とす
            int w = bitmap.PixelWidth & ~1;
            int h = bitmap.PixelHeight & ~1;
            if (w < 2 || h < 2) throw new InvalidOperationException("画像サイズが小さすぎます");

            var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int stride = bgra.PixelWidth * 4;
            byte[] pixels = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(pixels, stride, 0);

            string[] candidates = format == ScreenshotFormat.WebP
                ? new[] { "libwebp" }
                : new[] { "libsvtav1", "libaom-av1" };

            var errors = new System.Text.StringBuilder();
            foreach (var name in candidates)
            {
                try
                {
                    EncodeOnce(pixels, stride, w, h, path, name);
                    return;
                }
                catch (Exception ex)
                {
                    errors.Append(name).Append(": ").Append(ex.Message).Append(' ');
                    TryDelete(path);
                }
            }
            throw new NotSupportedException(errors.ToString().Trim());
        }

        private static void Check(int result, string what)
        {
            if (result < 0) throw new InvalidOperationException($"{what}に失敗 (code {result})");
        }

        private static void EncodeOnce(byte[] pixels, int stride, int w, int h, string path, string codecName)
        {
            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(codecName);
            if (codec == null) throw new NotSupportedException($"{codecName}エンコーダーが使えません");

            AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(codec);
            AVFormatContext* fmt = null;
            AVFrame* frame = null;
            AVFrame* srcFrame = null;
            AVPacket* pkt = null;
            SwsContext* sws = null;
            AVDictionary* opts = null;
            bool ioOpened = false;

            try
            {
                ctx->width = w;
                ctx->height = h;
                ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                ctx->time_base = new AVRational { num = 1, den = 1 };
                // swscaleの既定変換(BT.601・リミテッドレンジ)に合わせて色情報を明示する
                ctx->colorspace = AVColorSpace.AVCOL_SPC_SMPTE170M;
                ctx->color_range = AVColorRange.AVCOL_RANGE_MPEG;
                ctx->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
                ctx->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1;

                switch (codecName)
                {
                    case "libwebp":
                        ffmpeg.av_dict_set(&opts, "quality", WebpQuality, 0);
                        break;
                    case "libsvtav1":
                        ffmpeg.av_dict_set(&opts, "crf", AvifCrf, 0);
                        ffmpeg.av_dict_set(&opts, "preset", "6", 0);
                        break;
                    case "libaom-av1":
                        ctx->bit_rate = 0; // CRFによる固定品質にする
                        ffmpeg.av_dict_set(&opts, "crf", AvifCrf, 0);
                        ffmpeg.av_dict_set(&opts, "cpu-used", "6", 0);
                        break;
                }
                Check(ffmpeg.avcodec_open2(ctx, codec, &opts), "エンコーダーの初期化");

                // 出力先の形式は拡張子(.webp / .avif)から自動判別する
                ffmpeg.avformat_alloc_output_context2(&fmt, null, null, path);
                if (fmt == null) throw new NotSupportedException("この拡張子のファイル形式(muxer)が使えません");

                AVStream* stream = ffmpeg.avformat_new_stream(fmt, null);
                if (stream == null) throw new InvalidOperationException("出力ストリームの作成に失敗");
                Check(ffmpeg.avcodec_parameters_from_context(stream->codecpar, ctx), "出力パラメーターの設定");
                stream->time_base = ctx->time_base;

                if ((fmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                {
                    Check(ffmpeg.avio_open(&fmt->pb, path, ffmpeg.AVIO_FLAG_WRITE), "ファイルを開く");
                    ioOpened = true;
                }
                Check(ffmpeg.avformat_write_header(fmt, null), "ヘッダー書き込み");

                // BGRA → YUV420P
                frame = ffmpeg.av_frame_alloc();
                frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
                frame->width = w;
                frame->height = h;
                frame->colorspace = ctx->colorspace;
                frame->color_range = ctx->color_range;
                frame->color_primaries = ctx->color_primaries;
                frame->color_trc = ctx->color_trc;
                Check(ffmpeg.av_frame_get_buffer(frame, 0), "フレームの確保");
                frame->pts = 0;

                srcFrame = ffmpeg.av_frame_alloc();
                fixed (byte* p = pixels)
                {
                    srcFrame->data[0] = p;
                    srcFrame->linesize[0] = stride;
                    sws = ffmpeg.sws_getContext(w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                        w, h, AVPixelFormat.AV_PIX_FMT_YUV420P, 2, null, null, null);
                    if (sws == null) throw new InvalidOperationException("色変換の初期化に失敗");
                    ffmpeg.sws_scale(sws, srcFrame->data, srcFrame->linesize, 0, h, frame->data, frame->linesize);
                }

                Check(ffmpeg.avcodec_send_frame(ctx, frame), "フレーム送信");
                Check(ffmpeg.avcodec_send_frame(ctx, null), "エンコード完了通知");

                pkt = ffmpeg.av_packet_alloc();
                bool wrote = false;
                while (ffmpeg.avcodec_receive_packet(ctx, pkt) >= 0)
                {
                    pkt->stream_index = stream->index;
                    ffmpeg.av_packet_rescale_ts(pkt, ctx->time_base, stream->time_base);
                    Check(ffmpeg.av_interleaved_write_frame(fmt, pkt), "データ書き込み");
                    ffmpeg.av_packet_unref(pkt);
                    wrote = true;
                }
                if (!wrote) throw new InvalidOperationException("エンコード結果が空でした");
                Check(ffmpeg.av_write_trailer(fmt), "終端書き込み");
            }
            finally
            {
                if (ioOpened && fmt != null) ffmpeg.avio_closep(&fmt->pb);
                if (fmt != null) ffmpeg.avformat_free_context(fmt);
                if (sws != null) ffmpeg.sws_freeContext(sws);
                if (pkt != null) { var pp = pkt; ffmpeg.av_packet_free(&pp); }
                if (srcFrame != null) { var sf = srcFrame; ffmpeg.av_frame_free(&sf); }
                if (frame != null) { var f = frame; ffmpeg.av_frame_free(&f); }
                if (opts != null) ffmpeg.av_dict_free(&opts);
                if (ctx != null) { var c = ctx; ffmpeg.avcodec_free_context(&c); }
            }
        }
    }
}
