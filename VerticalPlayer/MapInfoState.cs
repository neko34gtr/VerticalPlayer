namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// 地図情報通知オーバーレイ（MapInfoOverlayControl）が表示する内容の状態保持モデル。
    /// MapInfoProviderがGPS位置更新のたびにこのインスタンスを（値を書き換えて）更新し、
    /// DashcamPlayerView側がMapInfoOverlayControl.State ＝ このインスタンスとして反映する。
    /// 単純なPOCOであり、通知（INotifyPropertyChanged）は持たない。更新頻度が高い
    /// （再生フレームごと）ため、バインディングではなく都度Control側のUpdate(state)メソッド
    /// 呼び出しで反映する方式を想定（車速OSDのSpeedOsd.Speed/IsEstimated設定と同じ考え方）。
    /// </summary>
    public sealed class MapInfoState
    {
        /// <summary>現在の地名（例: "愛知県大府市付近"）。判定できない場合は空文字。</summary>
        public string CurrentLocationName { get; set; } = string.Empty;

        /// <summary>走行中の路線名（例: "名神高速"）。判定できない場合は空文字。</summary>
        public string HighwayName { get; set; } = string.Empty;

        /// <summary>前方にSA/PAが存在するか。falseの場合、他のSA/PA関連プロパティは無効値（既定値）。</summary>
        public bool HasNextSaPa { get; set; }

        /// <summary>次のSA/PA名（例: "刈谷ハイウェイオアシス"）。</summary>
        public string NextSaPaName { get; set; } = string.Empty;

        /// <summary>"SA" または "PA"。</summary>
        public string NextSaPaType { get; set; } = string.Empty;

        /// <summary>次のSA/PAまでの残り距離（km、道なり）。</summary>
        public double NextSaPaDistanceKm { get; set; }

        /// <summary>前方に接近中のトンネルがあるか（例: 1km以内）。falseの場合、他のトンネル関連
        /// プロパティは無効値（既定値）。</summary>
        public bool HasUpcomingTunnel { get; set; }

        /// <summary>トンネル名（例: "恵那山トンネル"）。名称不明な区間は空文字。</summary>
        public string NextTunnelName { get; set; } = string.Empty;

        /// <summary>トンネル全長（m）。不明な場合は0。</summary>
        public int NextTunnelLengthMeters { get; set; }

        /// <summary>トンネル進入までの残り距離（m、道なり）。</summary>
        public int DistanceToTunnelMeters { get; set; }
    }
}
