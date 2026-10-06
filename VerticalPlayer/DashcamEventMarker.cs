using System;

namespace VerticalPlayer.Dashcam
{
    /// <summary>シークバーに描画するイベントの種別。</summary>
    public enum DashcamEventType
    {
        /// <summary>トンネル区間（地図データ由来、または測位ロスト区間からの推定）。</summary>
        Tunnel,
        /// <summary>SA/PAの通過地点。</summary>
        SaPa,
        /// <summary>Gセンサーの急変点（急減速・急加速・急ハンドル・上下衝撃）。</summary>
        GSensor,
    }

    /// <summary>イベントの細分類（一覧の「イベント種別」列やフィルターの判定に使う）。</summary>
    public enum DashcamEventSubType
    {
        None,
        /// <summary>地図データ由来のトンネル。</summary>
        MapTunnel,
        /// <summary>測位ロスト区間から推定したトンネル。</summary>
        GpsLossTunnel,
        /// <summary>SA/PAの通過地点。</summary>
        SaPaPass,
        /// <summary>前後G: 急減速。</summary>
        HardBrake,
        /// <summary>前後G: 急加速。</summary>
        HardAccel,
        /// <summary>左右G: 急ハンドル。</summary>
        Steer,
        /// <summary>上下G: 衝撃。</summary>
        VerticalImpact,
    }

    /// <summary>
    /// 1ファイル分の再生位置上のイベント（トンネル・SA/PA・Gセンサー急変点）。
    /// ファイルを開いた時にバックグラウンドで一括生成し、シークバー描画（DashcamAccelChart）と、
    /// 将来のイベント一覧パネル（データバインド）の両方から同じ集合を参照する。
    /// </summary>
    public sealed class DashcamEventMarker
    {
        /// <summary>イベント発生時刻（センサーフレームのTimestamp。取得できない場合はnull）。</summary>
        public DateTime? Timestamp { get; init; }

        /// <summary>動画先頭からの再生位置(秒)。区間イベントでは区間の開始位置。</summary>
        public double VideoOffsetSeconds { get; init; }

        /// <summary>区間の長さ(秒)。トンネル等の区間イベントのみ。単一ポイント(SA/PA・Gセンサー)は0。</summary>
        public double DurationSeconds { get; init; }

        public DashcamEventType EventType { get; init; }

        /// <summary>細分類。一覧(イベントタブ)で「急加減速」と「衝撃検知」などを区別するために使う。</summary>
        public DashcamEventSubType SubType { get; init; }

        /// <summary>符号付きの値。GSensor=ピーク軸の符号付きG（例: -0.42）。それ以外は0。</summary>
        public double SignedValue { get; init; }

        /// <summary>表示名（例: "椿原トンネル（1,234m）" / "飛騨白川PA" / "急減速 -0.42G"）。</summary>
        public string Label { get; init; } = string.Empty;

        /// <summary>強度・規模。GSensor=ピークの|G|、Tunnel=全長(m。不明なら0)、SaPa=0。
        /// 描画の色分け（Gの強弱）や、一覧での並べ替えに使う。</summary>
        public double Magnitude { get; init; }

        // ── 一覧パネルへのバインド用の読み取り専用プロパティ ──

        /// <summary>区間の終了位置(秒)。単一ポイントでは開始位置と同じ。</summary>
        public double EndOffsetSeconds => VideoOffsetSeconds + Math.Max(0.0, DurationSeconds);

        public bool IsRange => DurationSeconds > 0.0;

        /// <summary>再生位置の表示用（hh:mm:ss）。</summary>
        public string OffsetText => TimeSpan.FromSeconds(Math.Max(0.0, VideoOffsetSeconds)).ToString(@"hh\:mm\:ss");

        /// <summary>種別の表示用。</summary>
        public string TypeText => EventType switch
        {
            DashcamEventType.Tunnel => "トンネル",
            DashcamEventType.SaPa => "SA/PA",
            DashcamEventType.GSensor => "Gセンサー",
            _ => string.Empty
        };

        public override string ToString() => $"{OffsetText} {Label}";
    }
}
