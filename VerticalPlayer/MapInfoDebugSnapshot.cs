using System.Collections.Generic;

namespace VerticalPlayer.Dashcam
{
    /// <summary>MapInfoProvider.GetDebugSnapshot()の戻り値。内部で保持・計算しているデータを
    /// 「情報一覧」ウィンドウの通知情報タブへそのまま出すための読み取り専用スナップショット。</summary>
    public sealed class MapInfoDebugSnapshot
    {
        // ── ルート・データ読み込み状況 ──
        public bool RouteReady { get; init; }
        public int RoutePointCount { get; init; }
        public int TunnelCount { get; init; }
        public int SaPaCount { get; init; }
        public int PlaceCount { get; init; }
        public int HighwayWayCount { get; init; }

        // ── 現在フレーム ──
        public bool HasLastFrame { get; init; }
        public double CurrentLat { get; init; }
        public double CurrentLng { get; init; }
        public bool HasGpsFix { get; init; }
        public double CurrentCumKm { get; init; }
        public double? HeadingDeg { get; init; }

        // ── フラグ ──
        public bool IsOnExpressway { get; init; }
        public bool IsPassingTunnel { get; init; }
        public bool ShouldShowOverlay { get; init; }

        // ── MapInfoState相当（表示中の値） ──
        public string HighwayName { get; init; } = "";
        public string CurrentLocationName { get; init; } = "";
        public bool HasNextSaPa { get; init; }
        public string NextSaPaName { get; init; } = "";
        public string NextSaPaType { get; init; } = "";
        public double NextSaPaDistanceKm { get; init; }
        public bool HasUpcomingTunnel { get; init; }
        public string NextTunnelName { get; init; } = "";
        public int NextTunnelLengthMeters { get; init; }
        public int DistanceToTunnelMeters { get; init; }

        // ── 近傍の生候補データ ──
        public List<MapInfoDebugTunnelRow> NearbyTunnels { get; init; } = new();
        public List<MapInfoDebugSaPaRow> NearbySaPas { get; init; } = new();
    }

    public sealed class MapInfoDebugTunnelRow
    {
        public string Name { get; init; } = "";
        public string RoadName { get; init; } = "";
        public bool IsMotorwayTunnel { get; init; }
        public int LengthMeters { get; init; }
        public double EntryCumKm { get; init; }
        public double DistanceAheadKm { get; init; }
        public bool IsPassing { get; init; }
    }

    public sealed class MapInfoDebugSaPaRow
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public string RoadName { get; init; } = "";
        public double CumKm { get; init; }
        public double DistanceAheadKm { get; init; }
    }
}
