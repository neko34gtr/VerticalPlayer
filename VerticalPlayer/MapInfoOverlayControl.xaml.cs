using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VerticalPlayer.Dashcam
{
    /// <summary>表示位置（四隅）。AppSettings.MapInfoCornerとして文字列(enum名)のまま永続化する。</summary>
    public enum MapInfoCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    /// <summary>
    /// MapInfoProvider.State / ShouldShowOverlay を受け取って表示を更新するだけの薄いView。
    /// 自分ではネットワークにもタイマーにも触れない（毎フレームApply()を呼ぶ側＝
    /// DashcamPlayerView.OnFrontFrameDisplayedが唯一の呼び出し元）。
    ///
    /// 表示位置（四隅）とサイズ（拡縮率）はSetLayout()でのみ変わる（再生設定変更時に1回呼ばれる
    /// だけで、毎フレームは呼ばれない）。ContentPanelのHorizontalAlignment/VerticalAlignment/
    /// MarginとLayoutTransformを書き換えることで実現しており、カード自体のXAML構造は変えない。
    /// </summary>
    public partial class MapInfoOverlayControl : UserControl
    {
        private const double MarginPx = 20.0;

        private MapInfoCorner _corner = MapInfoCorner.BottomLeft;
        private double _scale = 1.0;
        private double _bottomInsetPx; // フルスクリーン中、下部のコントロールOSDと被らないよう加える追加余白

        public MapInfoOverlayControl()
        {
            InitializeComponent();
        }

        /// <summary>表示位置（四隅）と拡縮率を設定する。scaleは0.5〜2.0程度を想定（呼び出し側で
        /// 妥当な範囲にクランプ済みであることを期待するが、念のためここでも軽くガードする）。</summary>
        public void SetLayout(MapInfoCorner corner, double scale)
        {
            if (double.IsNaN(scale) || scale <= 0) scale = 1.0;
            _corner = corner;
            _scale = System.Math.Clamp(scale, 0.5, 2.0);
            ApplyLayout();
        }

        /// <summary>フルスクリーン中、画面下部のコントロールバー/チャートと被らないよう追加する
        /// 下方向の余白（px、映像エリア座標系）。四隅設定がTop側のときは無視される。
        /// フルスクリーンを抜けたら0を渡して解除すること（DashcamPlayerView.SetFullScreen参照）。</summary>
        public void SetFullScreenBottomInset(double insetPx)
        {
            _bottomInsetPx = insetPx > 0 ? insetPx : 0;
            ApplyLayout();
        }

        private void ApplyLayout()
        {
            (HorizontalAlignment h, VerticalAlignment v, Thickness margin) = _corner switch
            {
                MapInfoCorner.TopLeft => (HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(MarginPx, MarginPx, 0, 0)),
                MapInfoCorner.TopRight => (HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, MarginPx, MarginPx, 0)),
                MapInfoCorner.BottomRight => (HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, MarginPx, MarginPx + _bottomInsetPx)),
                _ => (HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(MarginPx, 0, 0, MarginPx + _bottomInsetPx)),
            };

            ContentPanel.HorizontalAlignment = h;
            ContentPanel.VerticalAlignment = v;
            ContentPanel.Margin = margin;
            ContentPanel.LayoutTransform = _scale == 1.0 ? Transform.Identity : new ScaleTransform(_scale, _scale);
        }

        /// <summary>現在の状態を反映する。visible=falseの場合、stateの中身は見ずに
        /// オーバーレイ全体をCollapsedにする（直前の表示内容はTextBlock側に残るが見えない）。</summary>
        public void Apply(MapInfoState state, bool visible)
        {
            if (!visible)
            {
                Visibility = Visibility.Collapsed;
                return;
            }
            Visibility = Visibility.Visible;

            bool hasHighway = !string.IsNullOrEmpty(state.HighwayName);
            HighwayCard.Visibility = hasHighway ? Visibility.Visible : Visibility.Collapsed;
            if (hasHighway) HighwayNameText.Text = state.HighwayName;

            bool hasLocation = !string.IsNullOrEmpty(state.CurrentLocationName);
            LocationCard.Visibility = hasLocation ? Visibility.Visible : Visibility.Collapsed;
            if (hasLocation) LocationText.Text = state.CurrentLocationName;

            SaPaCard.Visibility = state.HasNextSaPa ? Visibility.Visible : Visibility.Collapsed;
            if (state.HasNextSaPa)
            {
                SaPaTypeText.Text = state.NextSaPaType;
                SaPaNameText.Text = state.NextSaPaName;
                SaPaDistanceText.Text = "この先 " + state.NextSaPaDistanceKm.ToString("F1", CultureInfo.InvariantCulture) + " km";
            }

            TunnelCard.Visibility = state.HasUpcomingTunnel ? Visibility.Visible : Visibility.Collapsed;
            if (state.HasUpcomingTunnel)
            {
                TunnelNameText.Text = string.IsNullOrEmpty(state.NextTunnelName) ? "トンネル" : state.NextTunnelName;

                // DistanceToTunnelMeters<=0 は「通過中」の合図（MapInfoProvider.ApplyPassingTunnelState参照）
                if (state.DistanceToTunnelMeters <= 0)
                {
                    TunnelDetailText.Text = "通過中";
                }
                else if (state.NextTunnelLengthMeters > 0)
                {
                    TunnelDetailText.Text = $"全長{state.NextTunnelLengthMeters}m　あと{state.DistanceToTunnelMeters}m";
                }
                else
                {
                    TunnelDetailText.Text = $"あと{state.DistanceToTunnelMeters}m";
                }
            }
        }
    }
}
