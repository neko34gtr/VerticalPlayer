using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VerticalPlayer
{
    /// <summary>
    /// MediaInfo.dll を直接 P/Invoke で呼ぶ軽量ラッパー。
    /// MediaInfo.dll（64bit）を実行ファイルと同じフォルダに配置してください。
    /// https://mediaarea.net/ja/MediaInfo/Download/Windows から
    /// "DLL"版をダウンロードして MediaInfo.dll を取り出し、実行プログラムのルートに配置してください。
    /// </summary>
    public sealed class MediaInfoNative : IDisposable
    {
        // ── P/Invoke ──
        [DllImport("MediaInfo.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr MediaInfo_New();
        [DllImport("MediaInfo.dll", CharSet = CharSet.Unicode)]
        private static extern void MediaInfo_Delete(IntPtr handle);
        [DllImport("MediaInfo.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr MediaInfo_Open(IntPtr handle, string fileName);
        [DllImport("MediaInfo.dll", CharSet = CharSet.Unicode)]
        private static extern void MediaInfo_Close(IntPtr handle);
        [DllImport("MediaInfo.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr MediaInfo_Get(IntPtr handle, StreamKind streamKind,
            int streamNumber, string parameter, InfoKind kindOfInfo, InfoKind kindOfSearch);

        private enum StreamKind { General = 0, Video = 1, Audio = 2 }
        private enum InfoKind { Name = 0, Text = 1 }

        private IntPtr _handle;
        public bool Success { get; }

        public MediaInfoNative(string filePath)
        {
            try
            {
                _handle = MediaInfo_New();
                if (_handle == IntPtr.Zero) return;
                var result = MediaInfo_Open(_handle, filePath);
                Success = result != IntPtr.Zero;
            }
            catch { Success = false; }
        }

        private string Get(StreamKind stream, int num, string param)
        {
            if (_handle == IntPtr.Zero) return "";
            try
            {
                var ptr = MediaInfo_Get(_handle, stream, num, param, InfoKind.Text, InfoKind.Name);
                return ptr != IntPtr.Zero ? Marshal.PtrToStringUni(ptr) ?? "" : "";
            }
            catch { return ""; }
        }

        // ── 映像情報 ──
        public double VideoFrameRate
        {
            get => double.TryParse(Get(StreamKind.Video, 0, "FrameRate"),
                       System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        public string VideoCodec => Get(StreamKind.Video, 0, "Format");
        public string VideoCodecId => Get(StreamKind.Video, 0, "CodecID");

        public long VideoBitRate
        {
            get => long.TryParse(Get(StreamKind.Video, 0, "BitRate"), out var v) ? v : 0;
        }

        public string VideoColorSpace => Get(StreamKind.Video, 0, "ColorSpace");
        public string VideoChromaSubsampling => Get(StreamKind.Video, 0, "ChromaSubsampling");

        public int VideoBitDepth
        {
            get => int.TryParse(Get(StreamKind.Video, 0, "BitDepth"), out var v) ? v : 0;
        }

        public string VideoScanType => Get(StreamKind.Video, 0, "ScanType");
        public string VideoHdrFormat => Get(StreamKind.Video, 0, "HDR_Format");

        // ── 音声情報 ──
        public string AudioCodec => Get(StreamKind.Audio, 0, "Format");

        public int AudioSampleRate
        {
            get => int.TryParse(Get(StreamKind.Audio, 0, "SamplingRate"), out var v) ? v : 0;
        }

        public int AudioChannelCount
        {
            get => int.TryParse(Get(StreamKind.Audio, 0, "Channel(s)"), out var v) ? v : 0;
        }

        public long AudioBitRate
        {
            get => long.TryParse(Get(StreamKind.Audio, 0, "BitRate"), out var v) ? v : 0;
        }

        public string AudioChannelLayout => Get(StreamKind.Audio, 0, "ChannelLayout");

        // ── コンテナ ──
        public string ContainerFormat => Get(StreamKind.General, 0, "Format");

        public long Size
        {
            get => long.TryParse(Get(StreamKind.General, 0, "FileSize"), out var v) ? v : 0;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                MediaInfo_Close(_handle);
                MediaInfo_Delete(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}
