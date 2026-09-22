using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// MapInfoProvider.State / ShouldShowOverlay を受け取って表示を更新するだけの薄いView。
    /// 自分ではネットワークにもタイマーにも触れない（毎フレームApply()を呼ぶ側＝
    /// DashcamPlayerView.OnFrontFrameDisplayedが唯一の呼び出し元）。
    /// </summary>
    public partial class MapInfoOverlayControl : UserControl
    {
        public MapInfoOverlayControl()
        {
            InitializeComponent();
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
