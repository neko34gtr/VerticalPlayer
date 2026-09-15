using System;
using System.Collections.Generic;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// NMEAから抽出した1サンプル分のセンサーデータ。
    /// </summary>
    public record DashcamSensorFrame(
        TimeSpan VideoOffset,     // 動画開始からの相対時間
        DateTime Timestamp,       // UTC/JST時刻
        double Latitude,          // 緯度（現状バイナリフォーマット未解読のため常に0。NmeaSensorParserのコメント参照）
        double Longitude,         // 経度（同上）
        double SpeedKmh,          // 速度 (km/h)（換算係数は暫定値、要校正）
        double AccelX,            // 左右G（軸割り当ては暫定、要校正）
        double AccelY,            // 前後G（同上）
        double AccelZ,            // 上下G（同上）
        bool HasGpsFix = false    // GPS/レコード有効フラグ（バイナリのHeader値に基づく）
    );

    /// <summary>
    /// ドライブ選択ComboBoxの1項目。実ドライブのほか、SSD/HDD等にコピーしたSDカードの
    /// 中身を直接指すための任意フォルダ、および「フォルダを選択...」の選択肢自体も
    /// この型で表す。
    /// </summary>
    public sealed class DriveOrFolderOption
    {
        public string DisplayName { get; init; } = string.Empty;
        public string? RootPath { get; init; }
        public bool IsBrowseOption { get; init; }

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// Front/Rear動画とNMEAをタイムスタンプキーでペアリングした1トリップ分のセット。
    /// サムネイル(Thumbnail)はバックグラウンドで後から埋まるため、リストUIで表示更新を
    /// 反映できるようINotifyPropertyChangedを実装している。
    /// </summary>
    public sealed class DashcamMediaGroup : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string TimestampKey { get; init; } = string.Empty;
        public DateTime? Timestamp { get; init; }

        public string? FrontVideoPath { get; set; }
        public string? RearVideoPath { get; set; }
        public string? FrontNmeaPath { get; set; }
        public string? RearNmeaPath { get; set; }

        public bool HasFront => FrontVideoPath != null;
        public bool HasRear => RearVideoPath != null;

        private System.Windows.Media.ImageSource? _thumbnail;
        /// <summary>サムネイル画像（未生成の間はnull）。生成完了時にセットしてUIへ通知する。</summary>
        public System.Windows.Media.ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (ReferenceEquals(_thumbnail, value)) return;
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }

        /// <summary>リスト表示用のラベル（日時が読み取れればそれを、無理ならキーそのものを表示）。</summary>
        public string DisplayLabel => Timestamp?.ToString("yyyy/MM/dd HH:mm:ss") ?? TimestampKey;

        /// <summary>リストのカード表示用の短縮ラベル（yyMMddHHmmssの12桁、日時不明ならキー先頭12文字）。</summary>
        public string ShortLabel => Timestamp?.ToString("yyMMddHHmmss")
            ?? (TimestampKey.Length > 12 ? TimestampKey[..12] : TimestampKey);

        public override string ToString() => DisplayLabel;
    }

    /// <summary>
    /// 現在位置に対応するセンサーフレームを二分探索で取得するための補助。
    /// </summary>
    public static class DashcamSensorLookup
    {
        public static DashcamSensorFrame? FindNearest(IReadOnlyList<DashcamSensorFrame> frames, TimeSpan position)
        {
            if (frames == null || frames.Count == 0)
                return null;

            int lo = 0, hi = frames.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (frames[mid].VideoOffset <= position)
                    lo = mid;
                else
                    hi = mid - 1;
            }

            return frames[lo];
        }
    }
}
