using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// Leaflet.js + OpenStreetMapによる走行軌跡表示・自車位置追従用コントロール。
    /// APIキー不要（OSMタイルはAPIキー不要で利用可能）。WebView2上にHTMLを1枚だけ
    /// NavigateToStringで読み込み、以後はJavaScript関数呼び出し（ExecuteScriptAsync）で
    /// 描画データを更新する方式。C#→JSの一方向がほぼ全てで、JS→C#は「ユーザーが地図を
    /// 手動でドラッグしたら追従を自動解除する」通知のみ使用する。
    ///
    /// 前提: プロジェクトにNuGetパッケージ Microsoft.Web.WebView2 の参照が必要
    /// （.csprojは未確認のため、こちらで追加してください）。
    /// </summary>
    public partial class DashcamMapControl : UserControl
    {
        private bool _isReady;
        private string? _pendingRouteJson;
        private (double lat, double lng, bool valid)? _pendingCar;

        public bool FollowEnabled
        {
            get => FollowCheck.IsChecked == true;
            set => FollowCheck.IsChecked = value;
        }

        public DashcamMapControl()
        {
            InitializeComponent();
        }

        private async void DashcamMapControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isReady) return; // Loadedが複数回呼ばれても二重初期化しない

            await Map.EnsureCoreWebView2Async();

            // 開発者ツール(F12 / 右クリック検証)およびUser-Agentを有効化
            Map.CoreWebView2.Settings.AreDevToolsEnabled = true;
            Map.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Map.CoreWebView2.Settings.UserAgent = "VerticalPlayer/1.0 (Desktop Dashcam Viewer)";

            Map.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            Map.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            Map.NavigateToString(MapHtml);
        }

        private void CoreWebView2_NavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            _isReady = true;

            // 初期化完了前に来ていた更新要求があれば、ここでまとめて反映する
            if (_pendingRouteJson != null)
            {
                _ = Map.CoreWebView2.ExecuteScriptAsync($"window.dashcam.setRoute({_pendingRouteJson})");
                _pendingRouteJson = null;
            }
            if (_pendingCar is { } car)
            {
                _ = Map.CoreWebView2.ExecuteScriptAsync(
                    $"window.dashcam.setCar({car.lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{car.lng.ToString(System.Globalization.CultureInfo.InvariantCulture)},{(car.valid ? "true" : "false")})");
                _pendingCar = null;
            }
        }

        private void CoreWebView2_WebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            var msg = e.TryGetWebMessageAsString();
            if (msg == "userDragged")
                FollowCheck.IsChecked = false; // ユーザーが手動操作したら自動追従を解除
        }

        /// <summary>走行軌跡（区間ごとの折れ線）を地図へ渡して描画・全体表示させる。</summary>
        public void SetRoute(IReadOnlyList<MapSegment> segments)
        {
            var dto = new List<object>();
            foreach (var seg in segments)
            {
                var pts = new List<double[]>();
                foreach (var p in seg.Points)
                    pts.Add(new[] { p.Lat, p.Lng });
                dto.Add(new { dashed = seg.Dashed, points = pts });
            }
            string json = JsonSerializer.Serialize(dto);

            if (!_isReady)
            {
                _pendingRouteJson = json;
                return;
            }
            _ = Map.CoreWebView2.ExecuteScriptAsync($"window.dashcam.setRoute({json})");
        }

        /// <summary>現在の自車位置マーカーを更新する。validがfalseの間は半透明表示になる（GPSロスト中）。</summary>
        public void SetCarPosition(double lat, double lng, bool valid)
        {
            if (!_isReady)
            {
                _pendingCar = (lat, lng, valid);
                return;
            }
            string latStr = lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string lngStr = lng.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = Map.CoreWebView2.ExecuteScriptAsync($"window.dashcam.setCar({latStr},{lngStr},{(valid ? "true" : "false")})");
        }

        private void FollowCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isReady) return;
            _ = Map.CoreWebView2.ExecuteScriptAsync($"window.dashcam.setFollow({(FollowEnabled ? "true" : "false")})");
        }

        private void FitAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isReady) return;
            _ = Map.CoreWebView2.ExecuteScriptAsync("window.dashcam.fitAll()");
        }

        // cdnjsからの安定配信ライブラリ + 国土地理院淡色タイルのHTML
        private const string MapHtml = @"
<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8' />
<link rel='stylesheet' href='https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/leaflet.min.css' />
<style>
  html, body, #map { height: 100%; margin: 0; padding: 0; background:#141C2E; }
</style>
</head>
<body>
<div id='map'></div>
<script src='https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/leaflet.min.js'></script>
<script>
  var map = L.map('map', { zoomControl: true, attributionControl: true }).setView([35.0, 137.0], 15);
  
// 国土地理院（淡色地図）タイル（ドメインのスペル修正: cyberjapandata）
  L.tileLayer('https://cyberjapandata.gsi.go.jp/xyz/pale/{z}/{x}/{y}.png', {
    maxZoom: 18,
    attribution: '&copy; <a href=""https://maps.gsi.go.jp/development/ichiran.html"" target=""_blank"">国土地理院</a>'
  }).addTo(map);

  var routeLayer = L.layerGroup().addTo(map);
  var car = L.circleMarker([35.0, 137.0], {
    radius: 7, color: '#3B82F6', weight: 2, fillColor: '#3B82F6', fillOpacity: 0.9
  }).addTo(map);
  var followEnabled = true;
  var hasRoute = false;
  var lastAllPoints = [];

  map.on('dragstart', function () {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage('userDragged');
    }
  });

  window.dashcam = {
    setRoute: function (segments) {
      routeLayer.clearLayers();
      var allPoints = [];
      segments.forEach(function (seg) {
        if (!seg.points || seg.points.length < 2) return;
        L.polyline(seg.points, {
          color: seg.dashed ? '#94A3B8' : '#3B82F6',
          weight: seg.dashed ? 3 : 4,
          opacity: seg.dashed ? 0.7 : 0.9,
          dashArray: seg.dashed ? '5,10' : null
        }).addTo(routeLayer);
        seg.points.forEach(function (p) { allPoints.push(p); });
      });
      // 軌跡が伸びるたびに毎回fitBoundsするとズーム率がどんどん変わってしまい、
      // 自車位置が豆粒のように見えてしまう。初回の描画時だけ全体を表示し、
      // 以降はsetCarのpanTo（ズームは変えず追従だけ）に任せる。
      // 全体を見たくなった場合はfitAll()を呼べば手動でいつでも合わせ直せる。
      if (allPoints.length > 0 && !hasRoute) {
        hasRoute = true;
        map.fitBounds(L.latLngBounds(allPoints), { padding: [20, 20] });
      }
      lastAllPoints = allPoints;
    },
    setCar: function (lat, lng, valid) {
      car.setLatLng([lat, lng]);
      car.setStyle({ fillOpacity: valid ? 0.9 : 0.35, opacity: valid ? 1 : 0.5 });
      if (followEnabled) {
        map.panTo([lat, lng], { animate: true, duration: 0.3 });
      }
    },
    setFollow: function (enabled) {
      followEnabled = enabled;
    },
    fitAll: function () {
      if (lastAllPoints && lastAllPoints.length > 0) {
        map.fitBounds(L.latLngBounds(lastAllPoints), { padding: [20, 20] });
      }
    }
  };
</script>
</body>
</html>";
    }
}
