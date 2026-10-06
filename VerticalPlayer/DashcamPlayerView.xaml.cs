using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Popup = System.Windows.Controls.Primitives.Popup;
using Microsoft.Win32;
using VerticalPlayer.Media;
// AppMessageBox は親名前空間 VerticalPlayer 側にあるため using が必要
using VerticalPlayer;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// ドラレコ再生モードのメインビュー。
    /// 録画フォルダ種別(NORMAL/MANUAL/EVENT/PARKING)＋ドライブ(または任意フォルダ)選択→
    /// 自動読み込み→Front/Rear動画リスト化→リストから選択再生（Frontは連続再生、Rearは
    /// リア追従設定でFrontに連動 or 独立動作）・シーク追従・センサーHUD表示・走行軌跡マップ・
    /// レジューム再生・次ファイル先読み（OSファイルキャッシュ温め）を担当する。
    /// 動画再生・DNN超解像は既存FfmpegMediaElement/MainWindowの実装に合わせてある。
    /// AI推論(DNN超解像)はこの画面でも有効化可能だが、既定はOFF。
    ///
    /// シークバーはAccelChart（加速度/速度チャート）最上段の独立した帯に統合済み
    /// （旧SeekSliderは廃止）。センサー情報(Hud)は縦/横/自動どの地図モードでも常に
    /// AccelChartの右隣に固定表示する。地図の縦長/横長は右サイドバーの列幅だけを
    /// MapView.OrientationMode（自動/縦/横）とルート方位の自動判定結果から調整する。
    /// </summary>
    public partial class DashcamPlayerView : UserControl
    {
        private List<DashcamMediaGroup> _groups = new();
        private List<DashcamMediaGroup> _frontGroups = new();
        private List<DashcamMediaGroup> _rearGroups = new();

        private DashcamMediaGroup? _currentFrontGroup;
        private DashcamMediaGroup? _currentRearGroup;
        private List<DashcamSensorFrame> _sensorFrames = new();

        private readonly DispatcherTimer _syncTimer;
        // リア再同期: 閾値150ms・クールダウン無しだと、シーク所要時間(約120〜360ms)ぶん必ずリアが
        // 遅れて着地するため毎回(500ms周期)再シークするループに陥り、リアが約10fpsに落ちていた。
        // 閾値を広げ、再同期後はクールダウンを置き、シーク所要時間ぶんを先乗せして着地させる。
        private static readonly TimeSpan ResyncThreshold = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan ResyncCooldown = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan MaxRearLead = TimeSpan.FromMilliseconds(800);
        private readonly System.Diagnostics.Stopwatch _rearResyncWatch = new();
        private bool _rearSettleSamplePending;
        private TimeSpan _rearSeekLead = TimeSpan.FromMilliseconds(250); // 再同期時のシーク先に上乗せする量（実測で自動学習）

        private bool _suppressSelectionEvent;
        private bool _isPlaying;

        // Front/RearのMediaOpenedは非同期でタイミングがずれるため、「再生する意図」を
        // 独立フラグとして保持し、どちらのMediaOpenedが先に来ても正しく再生開始できるようにする。
        private bool _wantsPlaying;

        // 壊れた/読めないファイルが連続した場合に無限ループでスキップし続けないための保険
        private int _consecutiveFrontFailures;

        // リア表示スイッチ(RearVisibleCheck)とは独立して管理する「今リアが実際に再生可能か」の状態。
        // スイッチ自体はユーザー操作でのみ変化させ、このフラグとの組み合わせで実際の表示可否を決める。
        private bool _rearAvailable;

        // 起動時レジューム用: MediaOpened後にシークすべき秒数（該当なければnull）
        private double? _pendingResumeSeconds;

        // MediaInfoNativeでの詳細解析結果。MainWindow本体の_mediaInfoと同じ役割だが、
        // ドラレコ側は音声がFrontのみのためFront基準でのみ解析する。
        private MediaInfoNative? _mediaInfo;

        // レジューム処理中フラグ: 目的のドライブへ切り替わる前の一瞬だけ発生する空振りスキャンで
        // 「見つかりませんでした」警告を出さないようにするためのガード
        private bool _isResuming;

        // 次ファイルの先読み（OSファイルキャッシュ温め）: 同じパスを何度も読み直さないための記録
        private readonly HashSet<string> _prefetchedPaths = new();

        // サムネイル生成キュー（1件ずつ順番に処理。ThumbCapturePlayerという専用の非表示
        // プレイヤーを使い回すため並列実行はしない）
        //private readonly Queue<(DashcamMediaGroup group, string path)> _thumbnailQueue = new();
        private readonly Queue<(DashcamMediaGroup group, string path, bool fast)> _thumbnailQueue = new();
        private readonly HashSet<string> _finalizedThumbnailPaths = new(); // 正規版(DB保存対象)まで到達済みのパス
        private const int FastThumbnailCount = 8; // 初期表示ですぐ目に入る分だけ先に簡易生成する件数
        private bool _thumbnailWorkerRunning;
        private System.Threading.CancellationTokenSource _thumbCts = new(); // 再スキャン時に生成中のバッチを中断する

        // リア(PiP)ドラッグ移動・リサイズ用状態
        // ---- リア時刻ベース同期用 ----
        // Front/Rearのファイル名タイムスタンプは実際の録画開始時刻（実測: 名前+120秒≒最終書込時刻）だが、
        // リアはFrontと位相が異なる（リアの再起動等で0〜100秒超ずれる）。「同名ペア＝同時開始」と
        // みなす従来方式ではずれるため、絶対時刻で対応付ける:
        //   T = Front開始時刻 + Front再生位置、 リア再生位置 = T − リアclip開始時刻
        private sealed record RearClip(DateTime Start, string FilePath);
        private List<RearClip> _rearTimeline = new();
        private string? _currentRearClipPath;        // 時刻ベースで読み込み中のリアclip（独立選択中はnull）
        private DateTime _currentRearClipStart;
        private string? _rearExhaustedPath;          // 想定より早く終了したclip（同じclipを再読込しない）
        private string? _rearFailedPath;             // 開けなかったclip（同上）
        private DateTime? _frontStart;               // 現在のFrontファイルの開始時刻
        private const double RearClipMaxSeconds = 121; // 1clipの最大長(2分)＋余裕

        private bool _pipUserPositioned;
        // リアPiPの位置（映像エリアの空き領域に対する比率0..1、-1=未設定＝既定の左下配置）。
        // 比率で持つのはウィンドウ/フロント倍率が変わっても相対位置を保つため（永続化対象）。
        private double _pipRatioX = -1;
        private double _pipRatioY = -1;
        /// <summary>リア倍率の最小値（＝従来のPiP既定サイズ220×124 ÷ リア映像1920×1080 ≒ 0.115）。
        /// AppSettings.DashcamRearZoomScaleの既定値と揃えること。</summary>
        public const double DefaultRearZoomScale = 0.115;
        private Point? _pipDragStart;
        private Point? _pipDragStartPos;
        private Point? _pipResizeStart;
        private Size? _pipResizeStartSize;

        // ---- シーク（AccelChart最上段の独立した帯から通知される。旧SeekSliderと同等の
        //      ドラッグ間引きプレビュー・クリック即シーク・ホバー時刻プレビューを維持） ----
        private bool _isDragging;
        private bool _dragCompleting;
        private bool _wasPlayingBeforeSeekDrag;
        private bool _seekLiveBusy;
        private double? _seekLivePendingSeconds;

        // 実測fps表示（MainWindowのActualFpsLabelと同じ考え方で1秒集計）
        private int _fpsFrameCount;
        private DateTime _fpsWindowStart = DateTime.UtcNow;

        // 地図の縦長/横長: ルート方位からの自動判定結果（Autoモード用）と、実際に現在適用中の状態。
        // Hudの位置には一切影響しない（Hudは常にAccelChartの右隣に固定）。列幅だけを調整する。
        private bool _lastAutoHorizontal;
        private bool _mapHorizontal;

        /// <summary>ズーム変更・動画オープン時に、映像本来のサイズ×倍率での
        /// ウィンドウフィットをホスト(MainWindow)へ依頼する。引数は動画のネイティブ幅・高さ×倍率(px)。</summary>
        public event Action<double, double>? RequestWindowFit;

        /// <summary>再生中のFrontファイル名が変わるたびに通知する（ホスト側でウィンドウタイトル表示用）。停止時はnull。</summary>
        public event Action<string?>? CurrentFileChanged;

        /// <summary>再生中のFront/Rearファイル名が変わったときに通知する（タイトルバー中央の表示用）。
        /// 引数: Frontファイル名(無ければnull)、Rearファイル名(リアが無い区間・未検出・開けなかった場合はnull)、
        /// リア表示スイッチの状態。</summary>
        public event Action<string?, string?, bool>? PlayingFilesChanged;

        private void DashcamPlayerView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // ドラレコモードを抜けてこの画面が非表示になったら、開いているペア一覧も閉じる
            if (e.NewValue is bool visible && !visible)
                ClosePairListWindow();
        }

        private void NotifyPlayingFiles()
        {
            string? frontPath = _isStopped ? null : _currentFrontGroup?.FrontVideoPath;
            string? rearPath = _isStopped ? null : (_currentRearClipPath ?? _currentRearGroup?.RearVideoPath);
            PlayingFilesChanged?.Invoke(
                frontPath != null ? Path.GetFileName(frontPath) : null,
                rearPath != null ? Path.GetFileName(rearPath) : null,
                RearVisibleCheck?.IsChecked == true);
        }

        public bool RearLinked
        {
            get => RearLinkedCheck.IsChecked == true;
            set => RearLinkedCheck.IsChecked = value;
        }
        /// <summary>
        /// ハードウェアデコード(HW/SW)の切り替え。MainWindow本体のHwAccelCheckと共有する設定で、
        /// トグル時はメイン画面のHwAccel_Changedと同様に、現在開いているFront/Rearを
        /// 位置・再生状態を保ったまま開き直して即時反映する。
        /// </summary>
        public bool HardwareAcceleration
        {
            get => PlayerFront.HardwareAcceleration;
            set
            {
                PlayerFront.HardwareAcceleration = value;
                PlayerRear.HardwareAcceleration = value;
                ReapplyDecodeModeToOpenFiles();
            }
        }

        /// <summary>音声出力バックエンド。MainWindow本体のAudioBackendComboと共有する設定。
        /// Rearは機種仕様上そもそも音声トラックを持たないため、Frontのみに反映すれば十分だが、
        /// 将来Rearに音声が付く機種が出てきた場合に備えPlayerRearにも同じ値を設定しておく。</summary>
        public VerticalPlayer.AudioBackendKind AudioBackend
        {
            get => PlayerFront.AudioBackend;
            set
            {
                PlayerFront.AudioBackend = value;
                PlayerRear.AudioBackend = value;
                ReapplyDecodeModeToOpenFiles();
            }
        }

        /// <summary>ノイズリダクション。MainWindow本体のDenoiseCheckと共有する設定（AppSettings.Denoise）。
        /// HW/SW切替と同じく再オープンでないと反映されないため、Reopenする。</summary>
        public bool Denoise
        {
            get => PlayerFront.Denoise;
            set
            {
                PlayerFront.Denoise = value;
                PlayerRear.Denoise = value;
                NdrStatusText.Text = value ? "NDR: ON" : "NDR: OFF";
                ReapplyDecodeModeToOpenFiles();
            }
        }

        /// <summary>ダイナミックコントラスト。MainWindow本体のDynamicContrastCheckと共有
        /// する設定（AppSettings.DynamicContrast）。GPU後段処理のみのためライブ反映で再オープン不要。</summary>
        public bool DynamicContrast
        {
            get => PlayerFront.DynamicContrast;
            set
            {
                PlayerFront.DynamicContrast = value;
                PlayerRear.DynamicContrast = value;
                DcrStatusText.Text = value ? "DCR: ON" : "DCR: OFF";
            }
        }

        /// <summary>適応暗部補正のON/OFF。MainWindow本体の設定（AppSettings.AdaptiveDarkBoost）と共有し、
        /// Front/Rearの両方へ反映する。GPU後段処理のためライブ反映で再オープン不要。</summary>
        public bool AdaptiveDarkBoost
        {
            get => PlayerFront.AdaptiveDarkBoost;
            set
            {
                PlayerFront.AdaptiveDarkBoost = value;
                PlayerRear.AdaptiveDarkBoost = value;
            }
        }

        /// <summary>適応暗部補正の強さ(0〜1)。Front/Rearの両方へ反映する。</summary>
        public double AdaptiveDarkBoostStrength
        {
            get => PlayerFront.AdaptiveDarkBoostStrength;
            set
            {
                PlayerFront.AdaptiveDarkBoostStrength = value;
                PlayerRear.AdaptiveDarkBoostStrength = value;
            }
        }

        /// <summary>デインターレース。MainWindow本体のDeinterlaceCheckと共有する設定。
        /// MainWindow側もライブ反映（再オープンなし）のためこちらも合わせる。</summary>
        public bool Deinterlace
        {
            get => PlayerFront.Deinterlace;
            set
            {
                PlayerFront.Deinterlace = value;
                PlayerRear.Deinterlace = value;
                DeintStatusText.Text = value ? "De-int: ON" : "De-int: OFF";
            }
        }

        /// <summary>fpsカウンタ表示。MainWindow本体のFpsCounterCheck(AppSettings.ShowFpsCounter)と共有。</summary>
        public bool ShowFpsCounter
        {
            get => FpsText.Visibility == Visibility.Visible;
            set => FpsText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ReapplyDecodeModeToOpenFiles()
        {
            if (PlayerFront.Source != null)
            {
                var pos = PlayerFront.Position;
                bool wasPlaying = _isPlaying;
                var src = PlayerFront.Source;
                PlayerFront.Source = src;
                PlayerFront.Position = pos;
                if (wasPlaying) PlayerFront.Play();
            }

            bool rearActive = HasActiveRear();
            if (rearActive && PlayerRear.Source != null)
            {
                var pos = PlayerRear.Position;
                var src = PlayerRear.Source;
                PlayerRear.Source = src;
                PlayerRear.Position = pos;
                if (_isPlaying) PlayerRear.Play();
            }
        }

        /// <summary>リア(PiP)を表示するかどうかのユーザー設定（AppSettings.DashcamRearVisibleと連動、ホスト側で永続化）。</summary>
        public bool RearVisible
        {
            get => RearVisibleCheck.IsChecked == true;
            set => RearVisibleCheck.IsChecked = value;
        }

        /// <summary>リア(PiP)の表示倍率（永続化対象）。リア映像の等倍(オリジナル)を1.0とした縮小倍率で、
        /// 1.0=オリジナル(最大)。最小は従来のPiP既定サイズ相当(DefaultRearZoomScale)。</summary>
        public double RearZoomScale
        {
            get => RearZoomCombo.SelectedItem is ComboBoxItem ci && double.TryParse((string)ci.Tag, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : DefaultRearZoomScale;
            set
            {
                // 保存値が現在の選択肢と完全一致しない場合（選択肢を変更した後など）は最寄りの項目を選ぶ
                ComboBoxItem? best = null;
                double bestDiff = double.MaxValue;
                foreach (var obj in RearZoomCombo.Items)
                {
                    if (obj is ComboBoxItem ci && double.TryParse((string)ci.Tag, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    {
                        double diff = Math.Abs(v - value);
                        if (diff < bestDiff) { bestDiff = diff; best = ci; }
                    }
                }
                if (best != null) RearZoomCombo.SelectedItem = best;
            }
        }

        /// <summary>Front/Rearの録画時刻のズレを手動補正する値(秒、永続化対象)。nullはAUTO
        /// （従来通り、ファイル名から得たFront/Rearそれぞれの開始時刻をそのまま使う）。
        /// 正の値=リア側の時計がFrontより進んでいるとみなし、リアの参照時刻を早める方向に補正する。
        /// 負の値=リア側の時計がFrontより遅れているとみなし、リアの参照時刻を遅らせる方向に補正する。
        /// ReconcileRear()/SeekRearToFrontPositionAsync()/PlayerRear_MediaOpened()/フレーム毎の
        /// 再同期ループの4箇所で、Front時刻からRear側の目標時刻を求める際にこの値を加算する。</summary>
        public double? RearTimeOffsetSeconds
        {
            get
            {
                if (RearTimeOffsetCombo.SelectedItem is ComboBoxItem ci)
                {
                    var tag = (string)ci.Tag;
                    if (tag == "auto") return null;
                    if (double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
                }
                return null;
            }
            set
            {
                foreach (var obj in RearTimeOffsetCombo.Items)
                {
                    if (obj is not ComboBoxItem ci) continue;
                    var tag = (string)ci.Tag;
                    if (value == null)
                    {
                        if (tag == "auto") { RearTimeOffsetCombo.SelectedItem = ci; return; }
                        continue;
                    }
                    if (tag != "auto" && double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var v)
                        && Math.Abs(v - value.Value) < 0.001)
                    {
                        RearTimeOffsetCombo.SelectedItem = ci;
                        return;
                    }
                }
                // 一致する項目が無ければAUTOへフォールバック
                foreach (var obj in RearTimeOffsetCombo.Items)
                {
                    if (obj is ComboBoxItem ci2 && (string)ci2.Tag == "auto") { RearTimeOffsetCombo.SelectedItem = ci2; break; }
                }
            }
        }

        /// <summary>RearTimeOffsetSecondsをTimeSpanとして加算しやすい形で返す（null=AUTO時は0扱い）。</summary>
        private TimeSpan RearTimeOffsetSpan => TimeSpan.FromSeconds(RearTimeOffsetSeconds ?? 0.0);

        /// <summary>車速OSDの表示ON/OFF（永続化対象。AppSettings.EnableOSDと対応）。</summary>
        public bool SpeedOsdEnabled
        {
            get => SpeedOsdCheck.IsChecked == true;
            set => SpeedOsdCheck.IsChecked = value;
        }

        private MapInfoCorner _mapInfoCorner = MapInfoCorner.BottomLeft;
        /// <summary>地図情報通知オーバーレイの表示位置（四隅）。永続化対象。
        /// AppSettings.MapInfoCornerと文字列(enum名)で対応させる。</summary>
        public MapInfoCorner MapInfoCornerSetting
        {
            get => _mapInfoCorner;
            set
            {
                _mapInfoCorner = value;
                MapInfo.SetLayout(_mapInfoCorner, _mapInfoScale);
                UpdateMapInfoLayoutCombosSelection();
            }
        }

        private double _mapInfoScale = 1.0;
        /// <summary>地図情報通知オーバーレイの拡縮率（0.5〜2.0）。永続化対象。AppSettings.MapInfoScaleと対応。</summary>
        public double MapInfoScaleSetting
        {
            get => _mapInfoScale;
            set
            {
                _mapInfoScale = double.IsNaN(value) || value <= 0 ? 1.0 : Math.Clamp(value, 0.5, 2.0);
                MapInfo.SetLayout(_mapInfoCorner, _mapInfoScale);
                UpdateMapInfoLayoutCombosSelection();
            }
        }

        private void UpdateMapInfoLayoutCombosSelection()
        {
            foreach (ComboBoxItem item in MapInfoCornerCombo.Items)
            {
                if ((string)item.Tag == _mapInfoCorner.ToString()) { MapInfoCornerCombo.SelectedItem = item; break; }
            }
            string scaleTag = _mapInfoScale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            foreach (ComboBoxItem item in MapInfoScaleCombo.Items)
            {
                if ((string)item.Tag == scaleTag) { MapInfoScaleCombo.SelectedItem = item; break; }
            }
        }

        private void MapInfoCornerCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (MapInfoCornerCombo.SelectedItem is not ComboBoxItem item
                || !Enum.TryParse<MapInfoCorner>((string)item.Tag, out var corner)) return;
            _mapInfoCorner = corner;
            MapInfo?.SetLayout(_mapInfoCorner, _mapInfoScale); // InitializeComponent中はMapInfoが未構築の場合があるためnull条件で保護
        }

        private void MapInfoScaleCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (MapInfoScaleCombo.SelectedItem is not ComboBoxItem item
                || !double.TryParse((string)item.Tag, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var scale)) return;
            _mapInfoScale = scale;
            MapInfo?.SetLayout(_mapInfoCorner, _mapInfoScale); // 同上
        }

        /// <summary>トンネル進入検出方式（永続化対象。AppSettings.DashcamTunnelEntryDetectionModeと対応）。
        /// 実体はMapInfoProviderが持つ設定値をそのまま公開するラッパー。地図情報通知はドラレコ専用機能の
        /// ため、この設定もドラレコ側（このクラス）だけに置く。既定はLegacy（従来方式・実績あり）。</summary>
        public MapInfoProvider.TunnelEntryDetectionMode TunnelEntryDetectionModeSetting
        {
            get => _mapInfoProvider.EntryDetectionMode;
            set
            {
                _mapInfoProvider.EntryDetectionMode = value;
                foreach (ComboBoxItem item in TunnelEntryDetectionModeCombo.Items)
                {
                    if ((string)item.Tag == value.ToString()) { TunnelEntryDetectionModeCombo.SelectedItem = item; break; }
                }
            }
        }

        private void TunnelEntryDetectionModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (TunnelEntryDetectionModeCombo.SelectedItem is not ComboBoxItem item
                || !Enum.TryParse<MapInfoProvider.TunnelEntryDetectionMode>((string)item.Tag, out var mode)) return;
            _mapInfoProvider.EntryDetectionMode = mode;
        }

        /// <summary>リア(PiP)の水平位置（永続化対象）。映像エリアの空き幅に対する比率0..1、-1=未設定(既定の左下)。</summary>
        public double RearPipPosX
        {
            get => _pipRatioX;
            set
            {
                _pipRatioX = double.IsNaN(value) || value < 0 ? -1 : Math.Min(1, value);
                _pipUserPositioned = _pipRatioX >= 0 && _pipRatioY >= 0;
                ApplyRearPipLayout();
            }
        }

        /// <summary>リア(PiP)の垂直位置（永続化対象）。映像エリアの空き高さに対する比率0..1、-1=未設定(既定の左下)。</summary>
        public double RearPipPosY
        {
            get => _pipRatioY;
            set
            {
                _pipRatioY = double.IsNaN(value) || value < 0 ? -1 : Math.Min(1, value);
                _pipUserPositioned = _pipRatioX >= 0 && _pipRatioY >= 0;
                ApplyRearPipLayout();
            }
        }

        public double ZoomScale
        {
            get => ZoomCombo.SelectedItem is ComboBoxItem ci && double.TryParse((string)ci.Tag, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 2.0;
            set
            {
                // 【今回修正】以前は完全一致する選択肢が無い場合、何も選択されず値が無視されていた
                // （RearZoomScaleと違い最寄り項目へのフォールバックが無かった）。これが原因で
                // フロント倍率の復元がうまくいかないことがあった。RearZoomScaleと同じ「最寄りの
                // 項目を選ぶ」フォールバックを追加する。
                ComboBoxItem? best = null;
                double bestDiff = double.MaxValue;
                foreach (var obj in ZoomCombo.Items)
                {
                    if (obj is ComboBoxItem ci && double.TryParse((string)ci.Tag, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    {
                        double diff = Math.Abs(v - value);
                        if (diff < bestDiff) { bestDiff = diff; best = ci; }
                    }
                }
                if (best != null) ZoomCombo.SelectedItem = best;
            }
        }

        /// <summary>
        /// 画面内の非動画UI（上部ツールバー・下部コントロールバー・加速度チャート）の実際の合計高さを動的に取得します。
        /// </summary>
        public double NonVideoHeight
        {
            get
            {
                double h = 0;
                if (ToolbarBar != null && ToolbarBar.Visibility == Visibility.Visible)
                    h += ToolbarBar.ActualHeight > 0 ? ToolbarBar.ActualHeight : 38;
                if (ControlBar != null && ControlBar.Visibility == Visibility.Visible)
                    h += ControlBar.ActualHeight > 0 ? ControlBar.ActualHeight : 56;
                if (ChartStrip != null && ChartStrip.Visibility == Visibility.Visible)
                    h += ChartStrip.ActualHeight > 0 ? ChartStrip.ActualHeight : 130;

                return h > 0 ? h : (38 + 56 + 130);
            }
        }

        /// <summary>
        /// 画面内の非動画UI（左サイドバー・右サイドバー）の実際の合計幅を動的に取得します。
        /// </summary>
        public double NonVideoWidth
        {
            get
            {
                double left = LeftSidebarColumn != null && LeftSidebarColumn.ActualWidth > 0
                    ? LeftSidebarColumn.ActualWidth
                    : 16;

                double right = RightSidebarActualWidth > 0
                    ? RightSidebarActualWidth
                    : (_mapHorizontal ? 420 : 260);

                return left + right;
            }
        }

        private DashcamEventFolder CurrentEventFolder =>
            EventFolderCombo.SelectedItem is ComboBoxItem ci && Enum.TryParse<DashcamEventFolder>((string)ci.Tag, out var v)
                ? v : DashcamEventFolder.Normal;

        // ---- レジューム再生用の現在状態（MainWindow.SaveSettingsから読み取られる） ----
        public string? CurrentDrivePath => (DriveCombo.SelectedItem as DriveOrFolderOption)?.RootPath;
        public string? CurrentGroupKey => _currentFrontGroup?.TimestampKey;
        public double CurrentPositionSeconds => PlayerFront.NaturalDuration.HasTimeSpan ? PlayerFront.Position.TotalSeconds : 0;
        public string CurrentEventFolderName => CurrentEventFolder.ToString();

        /// <summary>右サイドバー(地図・センサー情報パネル)列の実際の幅(px)。地図の向き(縦長/横長ルート)に
        /// 応じて260/420で動的に変わる（全画面中は0になるが、RequestWindowFit側は全画面中
        /// 呼ばれないため考慮不要）。MainWindow.DashcamView_RequestWindowFitが、ウィンドウを
        /// 動画にフィットさせる計算で使う実クロム幅。ActualWidthではなく設定値そのもの
        /// (GridLength.Value)を返す。ActualWidthはレイアウト確定後でないと更新されず、
        /// 向き切替直後にRequestWindowFitが呼ばれた場合に古い値を拾うおそれがあるため。</summary>
        public double RightSidebarActualWidth => RightSidebarColumnDef.Width.Value;

        // ── 地図情報通知のレジューム保存・復元用（MainWindow.SaveSettings/RestoreSettingsから使う） ──
        public string MapInfoHighwayName => _mapInfoProvider.State.HighwayName;
        public bool MapInfoIsOnExpressway => _mapInfoProvider.IsOnExpressway;
        public string MapInfoCurrentLocationName => _mapInfoProvider.State.CurrentLocationName;

        /// <summary>起動時のレジューム復元専用。TryResumeAsyncと同じタイミングで呼ぶ想定。
        /// 実際のGPSフレームが届く前に、前回終了時点の「利用中」表示を即座に出す
        /// （詳しくはMapInfoProvider.ApplyResumedStateのコメント参照）。</summary>
        public void ApplyResumedMapInfo(string? highwayName, bool isOnExpressway, string? locationName)
        {
            _mapInfoProvider.ApplyResumedState(highwayName ?? string.Empty, isOnExpressway, locationName ?? string.Empty);
            MapInfo.Apply(_mapInfoProvider.State, _mapInfoProvider.ShouldShowOverlay);
        }

        public DashcamPlayerView()
        {
            InitializeComponent();
            IsVisibleChanged += DashcamPlayerView_IsVisibleChanged;
            InitializeMenuUi(); // 設定メニュー(ポップオーバー)・ステータスバッジ・トースト通知の配線

            // 折りたたみ中はサイドバー内容(ScrollBar/コーナー等)を不可視にする（XAML側の指定に依存しない）
            SidebarContent.Opacity = 0;

            // ドラレコは1ファイルあたり約2分間隔で次々切り替わり、かつSDカード等の低速
            // ストレージ運用が前提のため、既存の「パケット先読み（音声demuxで必須）」
            // パイプラインをこの画面のFront/Rear両方で既定ONにする。
            PlayerFront.PacketPrefetch = true;
            PlayerRear.PacketPrefetch = true;

            // サムネイル生成専用（音は絶対に鳴らさない）。Source切り替えのたびにフルの
            // XAudio2エンジンを作って捨てるのは無駄が大きく、ファイル一覧を高速に舐める
            // サムネイル生成では短時間に何十回も発生するため、音声エンジン自体を作らせない。
            ThumbCapturePlayer.AudioEnabled = false;

            // ハードウェアデコード(D3D11VA、非対応/失敗時は自動でSWへフォールバック)・
            // ノイズリダクション・ダイナミックコントラストも、MainWindow本体の既定(true)に
            // 合わせてドラレコ側でも既定ONにする。デインターレースはMainWindow本体と同じく
            // 既定OFF。
            // ※Deinterlaceプロパティ名はMainWindow側の命名規則(HardwareAcceleration/Denoise/
            //   DynamicContrastと同型のbool)から類推したもの。実際のFfmpegMediaElementの
            //   プロパティ名が異なる場合はここと下のDeintStatusText設定を要調整。
            PlayerFront.HardwareAcceleration = true;
            PlayerRear.HardwareAcceleration = true;
            PlayerFront.Denoise = true;
            PlayerRear.Denoise = true;
            PlayerFront.DynamicContrast = true;
            PlayerRear.DynamicContrast = true;
            PlayerFront.Deinterlace = false;
            PlayerRear.Deinterlace = false;

            // 実際に使われたデコードモード（"HW (D3D11VA)" / "SW"）をコントロールバーに表示して確認できるようにする
            PlayerFront.DecodeModeChanged += mode => Dispatcher.Invoke(() => DecodeModeText.Text = $"デコード: {mode}");
            DeintStatusText.Text = "De-int: OFF";
            NdrStatusText.Text = "NDR: ON";
            DcrStatusText.Text = "DCR: ON";

            PlayerFront.FrameDisplayed += OnFrontFrameDisplayed;

            // 旧SeekSliderの代わりにAccelChart自体がシーク操作を通知してくる
            AccelChart.SeekDragStarted += AccelChart_SeekDragStarted;
            AccelChart.SeekPreview += AccelChart_SeekPreview;
            AccelChart.SeekDragCompleted += AccelChart_SeekDragCompleted;
            AccelChart.HoverTimeChanged += AccelChart_HoverTimeChanged;

            // 地図情報(Overpass)の取得完了・再試行成功のたびに、トンネル/SA-PAのマーカーを作り直す
            _mapInfoProvider.ProjectionUpdated += () => Dispatcher.BeginInvoke(new Action(() => RebuildEventMarkersAsync()));

            // 情報一覧「イベント」タブのジャンプ処理（行の選択 → 該当ファイルの数秒前へシークして再生）
            EventList.JumpHandler = JumpToEventAsync;

            // 地図の表示方向をユーザーが手動変更したら、ルート方位の自動判定結果と合わせて再判定する
            MapView.OrientationModeChanged += () => UpdateEffectiveMapOrientation();

            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _syncTimer.Tick += SyncTimer_Tick;
            _syncTimer.Start();

            // 地図情報通知オーバーレイの初期配置（既定=左下・等倍）。MainWindow.RestoreSettingsが
            // 保存済みの位置・サイズを持っていれば、この直後にMapInfoCornerSetting/MapInfoScaleSetting
            // 経由で上書きされる。
            MapInfo.SetLayout(_mapInfoCorner, _mapInfoScale);
        }

        private void DashcamPlayerView_Loaded(object sender, RoutedEventArgs e)
        {
            HookMenuWindowEvents();
            UpdateStatusBadges();

            // 初回Loaded時点ではRestoreSettings側のレジューム(TryResumeAsync)/起動引数の
            // フォルダ直接読み込みがまだ完了していない場合がある。この状態でRefreshDriveList()が
            // 先頭ドライブ（C:等、通常は録画データが無い）を自動選択してスキャンしてしまうと、
            // 後から正しいドライブへ選び直されても「対応する動画が見つかりませんでした」警告が
            // 誤って一瞬表示される（通常の動画ファイル引数起動でこのダイアログが出る不具合の原因）。
            // レジューム処理中と同じ扱いにして、この自動選択によるスキャンでは警告を出さない。
            _isResuming = true;
            try
            {
                RefreshDriveList();
            }
            finally
            {
                _isResuming = false;
            }
        }

        // ---- 左サイドバー: マウスオーバーで展開 ----

        private void LeftSidebarHost_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // 列幅(LeftSidebarColumn)は常に16pxで固定のまま変更しない。展開はLeftSidebarHost自身の
            // Widthだけを広げ、Grid.ColumnSpan+Panel.ZIndexで映像の上にオーバーレイ表示する方式に
            // している（列幅そのものを変更する旧方式だと動画エリアが毎回リサイズされてしまい、
            // D3DImage経由の映像とZ順が競合して一覧が表に出てこない不具合もあったため）。
            LeftSidebarHost.Width = 220;
            CollapsedHint.Visibility = Visibility.Collapsed;
            // SidebarContent自体のVisibilityは常にVisibleのまま変更しない（下記コメント参照）。
            // 折りたたみ中はOpacity=0で不可視にしているため、展開時に表示へ戻す。
            SidebarContent.Opacity = 1;
        }

        private void LeftSidebarHost_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            LeftSidebarHost.Width = 16;
            CollapsedHint.Visibility = Visibility.Visible;
            // 幅16pxの間は各ListBoxのScrollBar(上下ボタン)がはみ出して見えてしまうため、
            // Visibilityではなく（レイアウトを維持したまま）Opacity=0で完全に不可視にする。
            SidebarContent.Opacity = 0;
        }

        // ---- 録画フォルダ種別・ドライブ選択（選択が変わったら自動で読み込み直す） ----

        private void EventFolderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DriveCombo == null) return; // InitializeComponent中の初期選択イベントは無視
            RescanCurrentSelection();
        }

        private void RefreshDrivesButton_Click(object sender, RoutedEventArgs e) => RefreshDriveList();

        private void PairListButton_Click(object sender, RoutedEventArgs e)
        {
            // ❗【トグル化】既に開いていれば内容更新はせず、そのまま閉じる（再度押したら消える）。
            // Closedイベント側で_pairListWindow=nullになる。
            if (_pairListWindow != null)
            {
                _pairListWindow.Close();
                return;
            }

            if (DriveCombo.SelectedItem is not DriveOrFolderOption option || option.IsBrowseOption || option.RootPath == null)
            {
                AppMessageBox.Show(Window.GetWindow(this), "先にドライブ/フォルダを選択してください。",
                    "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Information, isDarkMode: true);
                return;
            }

            var swPair = System.Diagnostics.Stopwatch.StartNew();
            var rows = BuildPairOverlapRows();
            long tRows = swPair.ElapsedMilliseconds;
            if (rows.Count == 0)
            {
                AppMessageBox.Show(Window.GetWindow(this), "Front/Rearのファイルが見つかりません。",
                    "ペア一覧", MessageBoxButton.OK, MessageBoxImage.Information, isDarkMode: true);
                return;
            }

            // モーダル(ShowDialog)ではなく別ウィンドウとして開く。開いている間もメイン画面は操作・再生でき、
            // メイン画面全体が無効化されてサムネイル等が暗く見えることもない。Ownerを指定しているため
            // 常にメイン画面の前面に出て、メイン画面を閉じれば一緒に閉じる。
            var win = new DashcamPairListWindow(rows, PairListNote)
            {
                Owner = Window.GetWindow(this)
            };
            long tCtor = swPair.ElapsedMilliseconds;
            win.Closed += (s, args) =>
            {
                _pairListWindow = null;
                DashcamDebugLog.Log("[PairList] 閉じた");
            };
            _pairListWindow = win;
            win.AttachEventList(EventList, EnsureEventIndex); // 「イベント」タブ（タブが表示された時に索引を始める）

            DashcamDebugLog.Log($"[PairList] 開く(別ウィンドウ) 再生中={_isPlaying} 描画Tier={System.Windows.Media.RenderCapability.Tier >> 16} " +
                $"Front(GPU)={PlayerFront.IsGpuPresenterAvailable} Rear(GPU)={PlayerRear.IsGpuPresenterAvailable}");
            win.Show();
            DashcamDebugLog.Log($"[PairList] 所要時間: 行の算出 {tRows}ms / ウィンドウ生成(InitializeComponent込み) {tCtor - tRows}ms / 接続とShow {swPair.ElapsedMilliseconds - tCtor}ms / 合計 {swPair.ElapsedMilliseconds}ms 行数={rows.Count}");
        }

        private DashcamPairListWindow? _pairListWindow;

        private const string PairListNote = "ファイル名の時刻から算出したFront/Rearの対応です（1本=最大2分と仮定した推定）。" +
            "FrontとRearは録画開始の位相がずれているため1対1にはならず、Frontに対して重なるRear、" +
            "Rearに対して重なるFrontを、重なる区間ごとに1行で表示します。「ずれ」はRear開始−Front開始(秒)、" +
            "「区間」はその行が占める各ファイル内の位置です。表示専用で、再生は同じ時刻情報で自動的に同期します。";

        /// <summary>ペア一覧を開いている場合、最新のFront/Rear情報で内容を更新する（ドライブ/フォルダの再スキャン後）。</summary>
        private void RefreshPairListWindow()
        {
            if (_pairListWindow == null) return;
            _pairListWindow.UpdateRows(BuildPairOverlapRows(), PairListNote);
        }

        /// <summary>ペア一覧を閉じる（ドラレコモードを抜けたときなど）。</summary>
        private void ClosePairListWindow()
        {
            _pairListWindow?.Close();
            _pairListWindow = null;
        }

        private const double PairListClipSeconds = 120; // 1ファイルの公称長(2分)

        /// <summary>FrontとRearのファイル名時刻から、重なる区間ごとの対応行（n対n）を算出する。
        /// 1ファイルの終端は「次のファイルの開始」と「開始+2分」のうち早い方とみなす。</summary>
        private List<DashcamPairListWindow.PairOverlapRow> BuildPairOverlapRows()
        {
            var fronts = _frontGroups
                .Select(g => (name: g.FrontVideoPath != null ? Path.GetFileName(g.FrontVideoPath) : null,
                              start: ParseFileStamp(g.FrontVideoPath) ?? g.Timestamp))
                .Where(x => x.name != null && x.start != null)
                .Select(x => (name: x.name!, start: x.start!.Value))
                .OrderBy(x => x.start)
                .ToList();
            var rears = _rearTimeline
                .Select(c => (name: Path.GetFileName(c.FilePath), start: c.Start))
                .ToList();

            DateTime EndOf(List<(string name, DateTime start)> list, int i)
            {
                DateTime end = list[i].start.AddSeconds(PairListClipSeconds);
                if (i + 1 < list.Count && list[i + 1].start < end) end = list[i + 1].start;
                return end;
            }

            // 重なり(front idx, rear idx, 開始, 終了)を全列挙
            var overlaps = new List<(int f, int r, DateTime s, DateTime e)>();
            for (int i = 0; i < fronts.Count; i++)
            {
                DateTime fs = fronts[i].start, fe = EndOf(fronts, i);
                for (int j = 0; j < rears.Count; j++)
                {
                    if (rears[j].start >= fe) break;
                    DateTime re = EndOf(rears, j);
                    DateTime s = fs > rears[j].start ? fs : rears[j].start;
                    DateTime e = fe < re ? fe : re;
                    if ((e - s).TotalSeconds > 0.5)
                        overlaps.Add((i, j, s, e));
                }
            }

            static string Pos(TimeSpan ts) => $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
            static string RangeText(DateTime origin, DateTime s, DateTime e) => $"{Pos(s - origin)} – {Pos(e - origin)}";
            static string Clock(DateTime dt) => dt.ToString("HH:mm:ss");

            var rows = new List<DashcamPairListWindow.PairOverlapRow>();

            foreach (var (f, r, s, e) in overlaps)
            {
                double offset = (rears[r].start - fronts[f].start).TotalSeconds;
                rows.Add(new DashcamPairListWindow.PairOverlapRow
                {
                    Kind = DashcamPairListWindow.RowKind.Overlap,
                    FrontFileName = fronts[f].name,
                    FrontStartText = Clock(fronts[f].start),
                    RearFileName = rears[r].name,
                    RearStartText = Clock(rears[r].start),
                    OffsetText = ((int)Math.Round(offset)).ToString("+0;-0;0"),
                    FrontRangeText = RangeText(fronts[f].start, s, e),
                    RearRangeText = RangeText(rears[r].start, s, e),
                    FrontStart = fronts[f].start,
                    RearStart = rears[r].start,
                    SpanStart = s,
                });
            }

            // Frontのうち、Rearが無い区間
            for (int i = 0; i < fronts.Count; i++)
            {
                DateTime fs = fronts[i].start, fe = EndOf(fronts, i);
                DateTime cursor = fs;
                foreach (var o in overlaps.Where(o => o.f == i).OrderBy(o => o.s))
                {
                    if ((o.s - cursor).TotalSeconds > 0.5)
                        rows.Add(new DashcamPairListWindow.PairOverlapRow
                        {
                            Kind = DashcamPairListWindow.RowKind.FrontOnly,
                            FrontFileName = fronts[i].name,
                            FrontStartText = Clock(fs),
                            FrontRangeText = RangeText(fs, cursor, o.s),
                            FrontStart = fs,
                            SpanStart = cursor,
                        });
                    if (o.e > cursor) cursor = o.e;
                }
                if ((fe - cursor).TotalSeconds > 0.5)
                    rows.Add(new DashcamPairListWindow.PairOverlapRow
                    {
                        Kind = DashcamPairListWindow.RowKind.FrontOnly,
                        FrontFileName = fronts[i].name,
                        FrontStartText = Clock(fs),
                        FrontRangeText = RangeText(fs, cursor, fe),
                        FrontStart = fs,
                        SpanStart = cursor,
                    });
            }

            // Rearのうち、Frontが無い区間
            for (int j = 0; j < rears.Count; j++)
            {
                DateTime rs = rears[j].start, re = EndOf(rears, j);
                DateTime cursor = rs;
                foreach (var o in overlaps.Where(o => o.r == j).OrderBy(o => o.s))
                {
                    if ((o.s - cursor).TotalSeconds > 0.5)
                        rows.Add(new DashcamPairListWindow.PairOverlapRow
                        {
                            Kind = DashcamPairListWindow.RowKind.RearOnly,
                            RearFileName = rears[j].name,
                            RearStartText = Clock(rs),
                            RearRangeText = RangeText(rs, cursor, o.s),
                            RearStart = rs,
                            SpanStart = cursor,
                        });
                    if (o.e > cursor) cursor = o.e;
                }
                if ((re - cursor).TotalSeconds > 0.5)
                    rows.Add(new DashcamPairListWindow.PairOverlapRow
                    {
                        Kind = DashcamPairListWindow.RowKind.RearOnly,
                        RearFileName = rears[j].name,
                        RearStartText = Clock(rs),
                        RearRangeText = RangeText(rs, cursor, re),
                        RearStart = rs,
                        SpanStart = cursor,
                    });
            }

            return rows;
        }

        private void RefreshDriveList()
        {
            string? selectedPath = (DriveCombo.SelectedItem as DriveOrFolderOption)?.RootPath;

            var options = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new DriveOrFolderOption
                {
                    DisplayName = string.IsNullOrEmpty(d.VolumeLabel) ? d.Name : $"{d.Name} ({d.VolumeLabel})",
                    RootPath = d.RootDirectory.FullName
                })
                .ToList();

            // 過去にブラウズで選んだ任意フォルダが現在選択中なら、一覧から消えないよう残しておく
            if (DriveCombo.ItemsSource is IEnumerable<DriveOrFolderOption> current)
            {
                foreach (var custom in current.Where(o => !o.IsBrowseOption && o.RootPath != null &&
                                                           !options.Any(x => string.Equals(x.RootPath, o.RootPath, StringComparison.OrdinalIgnoreCase))))
                {
                    options.Add(custom);
                }
            }

            options.Add(new DriveOrFolderOption { DisplayName = "フォルダを選択...", IsBrowseOption = true });

            DriveCombo.ItemsSource = options;

            var restore = options.FirstOrDefault(o => !o.IsBrowseOption && string.Equals(o.RootPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            DriveCombo.SelectedItem = restore ?? options.FirstOrDefault(o => !o.IsBrowseOption);
        }

        private void DriveCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DriveCombo.SelectedItem is not DriveOrFolderOption option) return;

            if (option.IsBrowseOption)
            {
                var dlg = new OpenFolderDialog { Title = "ドラレコ映像フォルダ(ROOT)を選択（SSD/HDD等にコピーした場合はそのフォルダ）" };
                if (dlg.ShowDialog() != true)
                {
                    // キャンセル時は選択肢がブラウズ項目のままにならないよう、直前の実ドライブへ戻す
                    var fallback = (DriveCombo.ItemsSource as IEnumerable<DriveOrFolderOption>)?.FirstOrDefault(o => !o.IsBrowseOption);
                    if (fallback != null) DriveCombo.SelectedItem = fallback;
                    return;
                }

                var custom = new DriveOrFolderOption { DisplayName = dlg.FolderName, RootPath = dlg.FolderName };
                var list = new List<DriveOrFolderOption>((DriveCombo.ItemsSource as IEnumerable<DriveOrFolderOption>)!);
                list.Insert(list.Count - 1, custom); // 末尾の「フォルダを選択...」の手前に挿入
                DriveCombo.ItemsSource = list;
                DriveCombo.SelectedItem = custom; // ここで再度SelectionChangedが呼ばれ、下のRescanへ進む
                return;
            }

            RescanCurrentSelection();
        }

        // 直近にスキャンしたドライブ/フォルダ（別のドライブ/フォルダへの切替を検出するため）
        private string? _scannedRoot;
        private DashcamEventFolder? _scannedFolder;

        private void RescanCurrentSelection()
        {
            if (DriveCombo.SelectedItem is not DriveOrFolderOption option || option.IsBrowseOption || option.RootPath == null)
                return;

            // 物理ドライブ(または対象フォルダ)が別のものへ変わった場合は、再生を止めてからファイルリストの
            // 処理へ進む。再生中のまま切り替えると、旧ドライブの再生と新ドライブのサムネイル生成が並行して
            // 走り、サムネイルが黒いまま残る等の不具合になっていた。同じドライブの再スキャン（更新ボタン等）では止めない。
            bool targetChanged = _scannedRoot != null
                && (!string.Equals(_scannedRoot, option.RootPath, StringComparison.OrdinalIgnoreCase)
                    || _scannedFolder != CurrentEventFolder);
            if (targetChanged && !_isResuming)
                StopPlayback();

            _scannedRoot = option.RootPath;
            _scannedFolder = CurrentEventFolder;
            ScanDrive(option.RootPath, CurrentEventFolder);

            // レジューム再生の内部処理中は、目的のドライブへ切り替わる前の一瞬だけ発生する
            // 「（まだ違う）先頭ドライブが自動選択された状態」での空振りスキャンでも
            // このメッセージが出てしまっていた（実際には直後に正しいドライブへ切り替わり成功する）。
            // レジューム処理中はこの警告を出さないようにする。
            if (!_isResuming && _frontGroups.Count == 0 && _rearGroups.Count == 0)
            {
                AppMessageBox.Show(Window.GetWindow(this),
                    $"対応する動画が見つかりませんでした（{CurrentEventFolder}フォルダ等を確認してください）。",
                    "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Warning, isDarkMode: true);
            }
        }

        private void ScanDrive(string rootPath, DashcamEventFolder folder)
        {
            _groups = DashcamFileScanner.Scan(rootPath, folder);
            _frontGroups = _groups.Where(g => g.HasFront).ToList();
            _rearGroups = _groups.Where(g => g.HasRear).ToList();
            BuildRearTimeline();
            RefreshPairListWindow();

            FrontList.ItemsSource = _frontGroups;
            RearList.ItemsSource = _rearGroups;

            // 再スキャンでグループ実体が作り直されるため、再生中のグループを新しいリストの
            // 同一キーの実体へ付け替え、選択状態も復元する（レジューム直後にLoadedの
            // RefreshDriveList等が再スキャンすると、選択が消え_currentFrontGroupがリストに
            // 見つからなくなり「次のシーン」が進まなくなっていた）。
            RebindCurrentGroupsAfterScan();

            _thumbnailQueue.Clear(); // 別ドライブ/別フォルダへ切り替えたら古いキューは破棄する
            _thumbCts.Cancel();      // 生成中のバッチも中断する
            _thumbCts = new System.Threading.CancellationTokenSource();
            _finalizedThumbnailPaths.Clear();
            EnqueueThumbnails(_groups);

            // 別のドライブ/フォルダへ切り替わった時だけイベント一覧を作り直す（同じ場所の再スキャンでは、
            // 再生済みファイルの精密な結果を捨てない）
            string scanKey = rootPath + "|" + folder;
            if (scanKey != _eventScanKey)
            {
                _eventScanKey = scanKey;
                CancelEventIndex();
                EventList.Clear();
                _eventIndexStarted = false;
            }
            if (_eventIndexWanted && !_eventIndexStarted) StartEventIndex();
        }

        // ---- サムネイル生成（中サイズ・SQLiteキャッシュ） ----
        // ファイルパス＋更新日時をキーにSQLiteへキャッシュし、次回以降は再生成せず即表示する。
        // 未キャッシュの場合のみ、非表示のThumbCapturePlayerで対象動画を開き2秒付近の1フレームを
        // RenderTargetBitmapで撮影する（既存のスクリーンショット機能と同じ描画経路を流用）。
        // 1件ずつ順番に処理するため、大量のファイルがあってもUIスレッドや他のデコーダを
        // 圧迫しない（ただし全件そろうまでは後ろの方の項目ほど時間がかかる）。

        private void EnqueueThumbnails(IEnumerable<DashcamMediaGroup> groups)
        {
            var withPath = groups
                .Where(g => g.Thumbnail == null)
                .Select(g => (group: g, path: g.FrontVideoPath ?? g.RearVideoPath))
                .Where(x => x.path != null)
                .Select(x => (x.group, path: x.path!))
                .ToList();

            // リスト順（上から）に積む。ワーカーはFFmpegで直接・並列に生成するため、
            // 従来のfast/finalの二段階は不要（先頭から順に着手され、キャッシュ命中分は一瞬で出る）。
            foreach (var (group, path) in withPath)
                _thumbnailQueue.Enqueue((group, path, fast: false));

            if (!_thumbnailWorkerRunning)
                _ = RunThumbnailWorkerAsync();
        }

        private const int ThumbWidth = 160;
        private const int ThumbHeight = 90;

        /// <summary>
        /// サムネイル生成ワーカー。キューを丸ごと取り出し、複数ファイルを並列に処理する:
        ///   1) DBキャッシュ(パス+更新日時キー)を全件まとめて引き、命中分を並列デコードして一斉に表示（一瞬）
        ///   2) 無ければFFmpegで先頭フレームを直接デコード・縮小（FastThumbnailExtractor。UIスレッド不要）
        ///   3) 抽出に失敗したファイルだけ、従来のプレイヤー経由の生成へフォールバック（逐次）
        /// 生成したサムネイルはDBへ保存する（保存は1本のバックグラウンドタスクで逐次。SQLiteの書き込み競合回避）。
        /// </summary>
        private async Task RunThumbnailWorkerAsync()
        {
            _thumbnailWorkerRunning = true;
            try
            {
                while (_thumbnailQueue.Count > 0)
                {
                    var batch = new List<(DashcamMediaGroup group, string path)>();
                    while (_thumbnailQueue.Count > 0)
                    {
                        var (g, p, _) = _thumbnailQueue.Dequeue();
                        if (_finalizedThumbnailPaths.Add(p)) // 二重生成防止（同じパスは1回だけ）
                            batch.Add((g, p));
                    }
                    if (batch.Count == 0) continue;

                    var ct = _thumbCts.Token;

                    // ここから追加：ドライブごとの表示速度差(I:は一瞬、J:は黒いまま遅い)の原因切り分け用ログ
                    // （debug.logに[Thumb]として出力、Debugビルドのみ）
                    var swBatch = System.Diagnostics.Stopwatch.StartNew();
                    string driveLabel = Path.GetPathRoot(batch[0].path) ?? "?";
                    // ここまで

                    // 1) DBキャッシュを全件まとめて引き、命中分は並列にデコードして一斉に表示する（一瞬で出る）
                    var cachedMap = await Task.Run(() =>
                    {
                        try { return DashcamThumbnailCache.TryGetMany(batch.Select(b => b.path).ToList()); }
                        catch (Exception ex)
                        {
                            DashcamPlayErrorLogger.Log($"[Thumb] {driveLabel} キャッシュ照会に失敗: {ex.GetType().Name}: {ex.Message}");
                            return null;
                        }
                    });
                    if (ct.IsCancellationRequested) continue;

                    var hits = new List<(DashcamMediaGroup group, byte[] data)>();
                    var missing = new List<(DashcamMediaGroup group, string path)>();
                    foreach (var item in batch)
                    {
                        if (cachedMap != null && cachedMap.TryGetValue(item.path, out var data)) hits.Add((item.group, data));
                        else missing.Add(item);
                    }
                    DashcamDebugLog.Log($"[Thumb] {driveLabel} 対象{batch.Count}件 キャッシュ命中{hits.Count} 未命中{missing.Count} DB照会{swBatch.ElapsedMilliseconds}ms");

                    if (hits.Count > 0)
                    {
                        await Task.Run(() => Parallel.ForEach(hits,
                            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                            h =>
                            {
                                var src = BytesToImageSource(h.data);
                                var grp = h.group;
                                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                                    new Action(() => grp.Thumbnail = src));
                            }));
                    }

                    if (hits.Count > 0)
                        DashcamDebugLog.Log($"[Thumb] {driveLabel} 命中{hits.Count}件の表示指示完了 {swBatch.ElapsedMilliseconds}ms");

                    // 2) 無かった分だけFFmpegで直接生成する
                    int[] extractOk = { 0 }, extractFail = { 0 }, firstLogged = { 0 };
                    var fallback = new System.Collections.Concurrent.ConcurrentQueue<(DashcamMediaGroup group, string path)>();
                    var toSave = new System.Collections.Concurrent.ConcurrentQueue<(string path, byte[] jpeg)>();
                    int degree = Math.Clamp(Environment.ProcessorCount / 2, 2, 4); // ディスクI/Oが主体のため控えめ

                    try
                    {
                        await Parallel.ForEachAsync(missing,
                            new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct },
                            (item, token) =>
                            {
                                var (group, path) = item;
                                ImageSource? source = null;

                                var swOne = System.Diagnostics.Stopwatch.StartNew();
                                byte[]? bgra = FastThumbnailExtractor.ExtractBgra(path, ThumbWidth, ThumbHeight);
                                if (bgra != null && bgra.Length >= ThumbWidth * ThumbHeight * 4)
                                {
                                    Interlocked.Increment(ref extractOk[0]);
                                    if (Interlocked.Exchange(ref firstLogged[0], 1) == 0)
                                        DashcamDebugLog.Log($"[Thumb] {driveLabel} 最初の新規サムネイル完成 バッチ開始から{swBatch.ElapsedMilliseconds}ms（1件の抽出{swOne.ElapsedMilliseconds}ms）");
                                    var bmp = BitmapSource.Create(ThumbWidth, ThumbHeight, 96, 96,
                                        PixelFormats.Bgr32, null, bgra, ThumbWidth * 4);
                                    bmp.Freeze();

                                    var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
                                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                                    using var ms = new MemoryStream();
                                    encoder.Save(ms);
                                    toSave.Enqueue((path, ms.ToArray()));
                                    source = bmp;
                                }

                                if (source != null)
                                {
                                    var s = source;
                                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                                        new Action(() => group.Thumbnail = s));
                                }
                                else
                                {
                                    Interlocked.Increment(ref extractFail[0]);
                                    DashcamDebugLog.Log($"[Thumb] {driveLabel} 高速抽出に失敗→プレイヤー経由へ: {Path.GetFileName(path)}（{swOne.ElapsedMilliseconds}ms）");
                                    fallback.Enqueue(item);
                                }
                                return ValueTask.CompletedTask;
                            });
                    }
                    catch (OperationCanceledException)
                    {
                        continue; // 再スキャンで中断された。次のループで新しいキューを処理する
                    }

                    if (missing.Count > 0)
                        DashcamDebugLog.Log($"[Thumb] {driveLabel} 新規抽出 成功{extractOk[0]} 失敗{extractFail[0]} 所要{swBatch.ElapsedMilliseconds}ms");

                    // 新規生成分のDB保存（1本のバックグラウンドタスクで逐次）
                    if (!toSave.IsEmpty)
                    {
                        var saves = toSave.ToArray();
                        _ = Task.Run(() =>
                        {
                            // 以前は例外が握りつぶされ、保存に失敗しても気付けなかった（毎回キャッシュ未命中になる原因候補）
                            var swSave = System.Diagnostics.Stopwatch.StartNew();
                            try
                            {
                                DashcamThumbnailCache.SaveBatch(saves.Select(s => (s.path, s.jpeg)));
                                DashcamDebugLog.Log($"[Thumb] {driveLabel} DB保存 {saves.Length}件 {swSave.ElapsedMilliseconds}ms");
                            }
                            catch (Exception ex)
                            {
                                DashcamPlayErrorLogger.Log($"[Thumb] {driveLabel} DB保存に失敗（{saves.Length}件）: {ex.GetType().Name}: {ex.Message}");
                            }
                        });
                    }

                    // FFmpeg直接抽出に失敗したファイルだけ、従来のプレイヤー経由(UIスレッド・逐次)で生成する
                    foreach (var (group, path) in fallback)
                    {
                        if (ct.IsCancellationRequested) break;
                        var swFb = System.Diagnostics.Stopwatch.StartNew();
                        byte[]? png = await GenerateThumbnailAsync(path, fast: false);
                        DashcamDebugLog.Log($"[Thumb] {driveLabel} プレイヤー経由 {Path.GetFileName(path)} {(png == null ? "失敗" : "成功")} {swFb.ElapsedMilliseconds}ms");
                        if (png == null) continue; // 壊れたファイル等はスキップ
                        await Task.Run(() =>
                        {
                            try { DashcamThumbnailCache.Save(path, png); }
                            catch (Exception ex) { DashcamPlayErrorLogger.Log($"[Thumb] {driveLabel} DB保存に失敗（プレイヤー経由分）: {ex.Message}"); }
                        });
                        var imageSource = BytesToImageSource(png);
                        Dispatcher.Invoke(() => group.Thumbnail = imageSource);
                    }
                }
            }
            finally
            {
                _thumbnailWorkerRunning = false;
            }
        }

        private static ImageSource BytesToImageSource(byte[] pngBytes)
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(pngBytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze(); // UIスレッド以外からでも安全に参照できるようにする
            return bmp;
        }

        private async Task<byte[]?> GenerateThumbnailAsync(string videoPath, bool fast = false)
        {
            // UI要素の操作およびキャプチャは確実にUIスレッドで行う（DispatcherOperationが
            // async delegateを返すため、結果取得には2段階のawaitが必要）。
            var op = Dispatcher.InvokeAsync(async () =>
            {
                var tcs = new TaskCompletionSource<bool>();
                RoutedEventHandler openedHandler = (s, e) => tcs.TrySetResult(true);
                EventHandler<FfmpegMediaFailedEventArgs> failedHandler = (s, e) => tcs.TrySetResult(false);

                ThumbCapturePlayer.MediaOpened += openedHandler;
                ThumbCapturePlayer.MediaFailed += failedHandler;
                try
                {
                    ThumbCapturePlayer.Stop();
                    ThumbCapturePlayer.Source = new Uri(videoPath);

                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                    if (completed != tcs.Task || !await tcs.Task)
                        return null; // オープン失敗 or タイムアウト

                    var duration = ThumbCapturePlayer.NaturalDuration.HasTimeSpan
                        ? ThumbCapturePlayer.NaturalDuration.TimeSpan
                        : TimeSpan.FromSeconds(4);

                    if (!fast)
                    {
                        // 簡易生成(fast)はシーク待ちの数百ms〜秒単位を丸ごと省略し、MediaOpened直後の
                        // 先頭フレームをそのまま使う（体感速度優先。正式なDB保存版は後で上書きされる）。
                        var target = TimeSpan.FromSeconds(Math.Min(2, duration.TotalSeconds * 0.3));
                        await ThumbCapturePlayer.StepToVideoOnlyAsync(target, timeoutMs: 3000);
                    }
                    // 画面外(Canvas.Left/Top=-2000)のままだとMeasure/Arrangeが確定しておらず、
                    // RenderTargetBitmap.Render(ThumbCapturePlayer)を直接呼んでも空(透明)の
                    // ビットマップになる不具合が実機で確認された。160x90でレイアウトを明示的に
                    // 確定させ、さらにVisualBrush経由でDrawingVisualへ転写してからキャプチャする
                    // ことで解消している。
                    ThumbCapturePlayer.Measure(new Size(160, 90));
                    ThumbCapturePlayer.Arrange(new Rect(-2000, -2000, 160, 90));
                    ThumbCapturePlayer.UpdateLayout();
                    await Task.Delay(fast ? 30 : 100);

                    var visualBrush = new VisualBrush(ThumbCapturePlayer) { Stretch = Stretch.Fill };
                    var drawingVisual = new DrawingVisual();
                    using (var dc = drawingVisual.RenderOpen())
                    {
                        dc.DrawRectangle(visualBrush, null, new Rect(0, 0, 160, 90));
                    }

                    var rtb = new RenderTargetBitmap(160, 90, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(drawingVisual);

                    // JPEG(品質80%)でバイト数を大幅カット（SQLiteへ大量保存するため）
                    var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
                    encoder.Frames.Add(BitmapFrame.Create(rtb));
                    using var ms = new MemoryStream();
                    encoder.Save(ms);
                    return ms.ToArray();
                }
                catch
                {
                    return null;
                }
                finally
                {
                    ThumbCapturePlayer.MediaOpened -= openedHandler;
                    ThumbCapturePlayer.MediaFailed -= failedHandler;
                    ThumbCapturePlayer.Stop();
                }
            });

            var innerTask = await op;
            return await innerTask;
        }

        // ---- リア時刻ベース同期 ----

        private static DateTime? ParseFileStamp(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string name = Path.GetFileName(path);
            if (name.Length >= 14 && DateTime.TryParseExact(name.AsSpan(0, 14), "yyyyMMddHHmmss",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
                return dt;
            return null;
        }

        /// <summary>録画フォルダ内の全リアファイルを開始時刻順の連続タイムラインとして構築する。
        /// ペアリング(近傍マッチ/DB)の結果には依存しない。</summary>
        private void BuildRearTimeline()
        {
            var clips = new Dictionary<string, RearClip>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in _groups)
            {
                var st = ParseFileStamp(g.RearVideoPath);
                if (st != null && g.RearVideoPath != null) clips[g.RearVideoPath] = new RearClip(st.Value, g.RearVideoPath);
            }

            var dirs = _groups
                .SelectMany(g => new[] { g.FrontVideoPath, g.RearVideoPath })
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => Path.GetDirectoryName(p!))
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var dir in dirs)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir!, "*_Rear.MP4"))
                    {
                        var st = ParseFileStamp(f);
                        if (st != null) clips[f] = new RearClip(st.Value, f);
                    }
                }
                catch (Exception ex)
                {
                    DashcamPlayErrorLogger.Log($"[RearTimeline] {dir} の列挙に失敗: {ex.Message}");
                }
            }

            _rearTimeline = clips.Values.OrderBy(c => c.Start).ThenBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>時刻tを含むリアclipを返す（無ければnull＝リアの録画が無い区間）。
        /// clipの終端は「次のclipの開始」または開始+2分のうち早い方とみなす。</summary>
        private RearClip? FindRearClipAt(DateTime t)
        {
            var tl = _rearTimeline;
            int lo = 0, hi = tl.Count - 1, idx = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (tl[mid].Start <= t) { idx = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (idx < 0) return null;

            var c = tl[idx];
            DateTime endEst = c.Start.AddSeconds(RearClipMaxSeconds);
            if (idx + 1 < tl.Count && tl[idx + 1].Start < endEst) endEst = tl[idx + 1].Start;
            if (t >= endEst) return null;
            if (string.Equals(c.FilePath, _rearExhaustedPath, StringComparison.OrdinalIgnoreCase)) return null;
            if (string.Equals(c.FilePath, _rearFailedPath, StringComparison.OrdinalIgnoreCase)) return null;
            return c;
        }

        private bool UseTimeAlignedRear => RearLinked && _frontStart != null && _rearTimeline.Count > 0;

        /// <summary>いまリア側を操作すべき状態か（Play/Pause/シーク/DNN等の対象にするか）。</summary>
        private bool HasActiveRear()
        {
            if (!RearLinked) return _currentRearGroup != null;
            if (UseTimeAlignedRear) return _currentRearClipPath != null;
            return _currentFrontGroup?.HasRear == true;
        }

        /// <summary>現在のFront時刻から読み込むべきリアclipを決め、違っていれば読み込み直す。
        /// Front切替後もリアの現clipが引き続きTを含むなら再読込せず、そのまま再生し続ける。</summary>
        private void ReconcileRear()
        {
            if (!UseTimeAlignedRear) return;

            var t = _frontStart!.Value + PlayerFront.Position + RearTimeOffsetSpan; // 【今回追加】手動時刻補正を加算
            var clip = FindRearClipAt(t);
            if (clip == null)
            {
                if (_currentRearClipPath != null) StopRearForGap();
                return;
            }

            if (string.Equals(clip.FilePath, _currentRearClipPath, StringComparison.OrdinalIgnoreCase)) return;
            LoadRearClip(clip, t);
        }

        private void LoadRearClip(RearClip clip, DateTime t)
        {
            DashcamDebugLog.Log($"[RearClip] T={t:HH:mm:ss.f} → {Path.GetFileName(clip.FilePath)} 位置={(t - clip.Start).TotalSeconds:F1}s " +
                $"(直前={Path.GetFileName(_currentRearClipPath) ?? "(なし)"})");

            _currentRearClipPath = clip.FilePath;
            _currentRearClipStart = clip.Start;
            _currentRearGroup = _rearGroups.FirstOrDefault(g => string.Equals(g.RearVideoPath, clip.FilePath, StringComparison.OrdinalIgnoreCase));
            _rearResyncWatch.Reset();
            _rearSettleSamplePending = false;

            _rearAvailable = false; // 新しいSourceが実際に開き終わるまでは「表示可能」とみなさない
            UpdateRearPipVisibility();
            PlayerRear.Stop();
            PlayerRear.Source = new Uri(clip.FilePath);

            SetListSelection(RearList, _currentRearGroup, scrollToTop: false);
            if (_currentRearGroup != null)
                ScrollSelectedToTop(RearList, _rearGroups.IndexOf(_currentRearGroup));
        }

        /// <summary>リアの録画が無い区間: リアを止めてPiPを黙って隠す（スイッチ自体は触らない）。</summary>
        private void StopRearForGap()
        {
            PlayerRear.Stop();
            _currentRearClipPath = null;
            _currentRearGroup = null;
            _rearAvailable = false;
            UpdateRearPipVisibility();
            SetListSelection(RearList, null, scrollToTop: false);
        }

        /// <summary>Front位置frontPosに対応するリア位置へ合わせる（シーク確定時）。</summary>
        private async Task SeekRearToFrontPositionAsync(TimeSpan frontPos)
        {
            _rearExhaustedPath = null; // シークで時刻が飛ぶため、終了扱い/失敗扱いは解除する
            _rearFailedPath = null;
            _rearResyncWatch.Reset();
            _rearSettleSamplePending = false;

            if (UseTimeAlignedRear)
            {
                var t = _frontStart!.Value + frontPos + RearTimeOffsetSpan; // 【今回追加】手動時刻補正を加算
                var clip = FindRearClipAt(t);
                if (clip == null)
                {
                    if (_currentRearClipPath != null) StopRearForGap();
                    return;
                }

                if (!string.Equals(clip.FilePath, _currentRearClipPath, StringComparison.OrdinalIgnoreCase))
                {
                    LoadRearClip(clip, t); // 開き終わり(MediaOpened)で位置を合わせる
                    return;
                }
                await PlayerRear.StepToVideoOnlyAsync(t - clip.Start, timeoutMs: 2000);
                return;
            }

            if (HasActiveRear())
                await PlayerRear.StepToVideoOnlyAsync(frontPos + RearTimeOffsetSpan, timeoutMs: 2000); // 【今回追加】非時刻整合モードにも補正を適用
        }

        // ---- リスト選択の同期補助 ----

        /// <summary>選択イベント(再生開始)を発火させずにリストの選択項目だけを設定する。</summary>
        private void SetListSelection(ListBox list, DashcamMediaGroup? group, bool scrollToTop)
        {
            bool prev = _suppressSelectionEvent;
            _suppressSelectionEvent = true;
            try { list.SelectedItem = group; }
            finally { _suppressSelectionEvent = prev; }

            if (group == null || !scrollToTop) return;

            var source = ReferenceEquals(list, FrontList) ? _frontGroups : _rearGroups;
            int idx = source.IndexOf(group);
            // ItemsSource差し替え直後はレイアウト前でオフセットが効かないため、Loaded優先度で遅延実行する
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ScrollSelectedToTop(list, idx)));
        }

        private void RebindCurrentGroupsAfterScan()
        {
            if (_currentFrontGroup != null)
            {
                string key = _currentFrontGroup.TimestampKey;
                var nf = _frontGroups.FirstOrDefault(g => g.TimestampKey == key);
                if (nf != null)
                {
                    _currentFrontGroup = nf;
                    SetListSelection(FrontList, nf, scrollToTop: true);
                }
            }

            if (_currentRearGroup != null)
            {
                string key = _currentRearGroup.TimestampKey;
                var nr = _rearGroups.FirstOrDefault(g => g.TimestampKey == key);
                if (nr != null)
                {
                    _currentRearGroup = nr;
                    SetListSelection(RearList, nr, scrollToTop: true);
                }
            }
        }

        // ---- リスト選択 → 再生 ----

        private void FrontList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FrontList.SelectedItem != null)
                FrontList.ScrollIntoView(FrontList.SelectedItem);

            if (_suppressSelectionEvent) return;
            if (FrontList.SelectedItem is not DashcamMediaGroup group) return;
            _mapInfoProvider.Reset(); // 手動でのファイル切替は明確な非連続点なので、通過中フラグ等をここでクリアする
            PlayFrontGroup(group);
        }

        private void RearList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RearList.SelectedItem != null)
                RearList.ScrollIntoView(RearList.SelectedItem);

            if (_suppressSelectionEvent) return;
            if (RearList.SelectedItem is not DashcamMediaGroup group) return;

            // リアリストは常に選択可能（従来はリア追従中は無効化していたため「手動でここから見たい」
            // ケースに一切対応できなかった）。手動で選ぶ＝追従を自動解除する意図とみなす。
            if (RearLinked)
                RearLinkedCheck.IsChecked = false;

            PlayRearGroup(group);
        }

        /// <summary>同期処理の所要時間を測り、150ms以上かかったらログに残す（UIフリーズ箇所の特定用）。</summary>
        private static void TimedStep(string name, Action action)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            action();
            if (sw.ElapsedMilliseconds >= 150)
                DashcamDebugLog.Log($"[Slow] {name} {sw.ElapsedMilliseconds}ms");
        }

        private void PlayFrontGroup(DashcamMediaGroup group)
        {
            StartUiStallProbe(); // 切替の前後約10秒間、UIスレッドの応答遅れを監視する
            _currentFrontGroup = group;
            _isStopped = false;
            // 直前のFrontの次(約2分後)に続くファイルへの切替（連続再生・次のシーン）では、リアの
            // 「終了済み/開けなかった」記録を維持する。リアはFrontと位相が違い、まだ終わったはずの
            // 直前のリアclipが新しいFrontの開始時刻にも含まれて見えることがあり、リセットすると
            // 同じclipを読み直してPiPが1〜2秒途切れていた。リストの選択などで別の場所へ飛ぶ場合だけリセットする。
            var newFrontStart = ParseFileStamp(group.FrontVideoPath) ?? group.Timestamp;
            bool sequentialSwitch = _frontStart != null && newFrontStart != null
                && Math.Abs((newFrontStart.Value - _frontStart.Value).TotalSeconds - NominalClipSeconds) <= 15;
            _frontStart = newFrontStart;
            if (!sequentialSwitch)
            {
                _rearExhaustedPath = null;
                _rearFailedPath = null;
            }
            _rearResyncWatch.Reset(); // 新しいファイルでは再同期の状態を初期化する
            _rearSettleSamplePending = false;
            CurrentFileChanged?.Invoke(Path.GetFileName(group.FrontVideoPath) ?? group.TimestampKey);
            NotifyPlayingFiles();
            _sensorFrames = new List<DashcamSensorFrame>(); // Frontの動画長が判明してからParseし直す（MediaOpened側）
            ClearEventMarkers();
            _wantsPlaying = true; // Front/RearどちらのMediaOpenedが先に来ても再生開始させる意図フラグ

            if (group.FrontVideoPath != null)
            {
                // 切替時に一度だけ起きたUIスレッドの長時間フリーズ（約3.5秒）の原因箇所を特定するため、
                // 各同期処理の所要時間を測り、150ms以上かかったものを[Slow]として記録する。
                string frontPath = group.FrontVideoPath;
                TimedStep("PlayerFront.Stop()", () => PlayerFront.Stop()); // 前回のOpen失敗等で内部状態が残っていても確実にリセットしてから開く
                TimedStep("PlayerFront.Source設定", () => PlayerFront.Source = new Uri(frontPath));
            }

            // リア連動の診断ログ（「次のシーンでフロントだけ切り替わりリアが付いて来ない」調査用）
            DashcamDebugLog.Log($"[Scene] Front={group.TimestampKey} 開始={_frontStart:HH:mm:ss} RearLinked={RearLinked} 時刻同期={UseTimeAlignedRear} HasRear={group.HasRear} " +
                $"RearPath={group.RearVideoPath ?? "(なし)"} 直前のリア={_currentRearGroup?.TimestampKey ?? "(null)"}");

            if (UseTimeAlignedRear)
            {
                TimedStep("ReconcileRear()", () => ReconcileRear()); // 絶対時刻でリアclipを選ぶ（現clipが引き続きTを含むなら再読込しない）
            }
            else if (RearLinked)
            {
                // 時刻が取れない場合のフォールバック（従来のグループ単位の追従）
                if (group.HasRear)
                {
                    PlayRearGroup(group, keepListSelectionOnly: true); // 内部でRearListの選択色も更新される
                    int rearIdx = _rearGroups.IndexOf(group);
                    ScrollSelectedToTop(RearList, rearIdx);
                }
                else
                {
                    SetListSelection(RearList, null, scrollToTop: false);
                    // フロントと同名のリアファイルが存在しないケース。スイッチ自体はユーザーの
                    // 操作結果のまま変更せず（勝手にOFFにしない）、表示できないので黙って隠すだけにする。
                    PlayerRear.Stop();
                    _currentRearGroup = null;
                    _rearAvailable = false;
                    UpdateRearPipVisibility();
                }
            }
        }

        private void PlayRearGroup(DashcamMediaGroup group, bool keepListSelectionOnly = false)
        {
            _currentRearGroup = group;
            _currentRearClipPath = null; // 独立選択/フォールバックでは時刻ベースの管理から外す
            _wantsPlaying = true; // 独立選択(リア追従OFF時)から呼ばれた場合もここで意図をセットする
            _rearAvailable = false; // 新しいSourceが実際に開き終わるまでは「表示可能」とみなさない
            UpdateRearPipVisibility();

            if (group.RearVideoPath != null)
            {
                PlayerRear.Stop(); // 前回Open失敗(エラー記録ファイル等)の内部状態を必ずリセットしてから開く
                PlayerRear.Source = new Uri(group.RearVideoPath);
            }

            // 追従(keepListSelectionOnly)時もリアリストの選択色は付ける（従来はスキップしており
            // フロント追従再生中にリア側だけ選択色が付かなかった）。スクロール位置だけは
            // 呼び出し側(ScrollSelectedToTop)に任せる。
            SetListSelection(RearList, group, scrollToTop: false);
            if (!keepListSelectionOnly)
                RearList.ScrollIntoView(group);
        }

        // ---- 再生制御 ----

        private async void PlayerFront_MediaOpened(object sender, RoutedEventArgs e)
        {
            _consecutiveFrontFailures = 0;
            var swOpened = System.Diagnostics.Stopwatch.StartNew();
            int openToken = ++_frontOpenToken; // 解析の待機中に別ファイルへ切り替わった場合、古い処理を捨てるための番号
            PlayerFront.ResetDnnEngineForNewFile();
            if (_gapPending)
                DashcamDebugLog.Log($"[Gap] MediaEnded → 次ファイルのOpen完了まで {GapMs(_gapMediaEndedTs)}ms");

            // ここから変更：動画情報(MediaInfo)の解析とNMEAの読み込み・解析を、UIスレッドではなく別スレッドで行う。
            // 以前はUIスレッドで同期処理していたため、SDカード(I:)からまだ読まれていないファイルへ飛んだときに
            // 約0.7秒、UIが止まっていた。再生開始(Play)の順序は従来どおり「解析が終わった後」のままなので、
            // 連続再生の切替時間は変わらない（解析は通常10ms台）。待機中はUIが固まらない。
            string? mediaInfoPath = PlayerFront.Source?.LocalPath;
            Task<MediaInfoNative?>? mediaInfoTask = mediaInfoPath != null
                ? Task.Run(() => ProbeMediaInfo(mediaInfoPath))
                : null;

            var duration = PlayerFront.NaturalDuration.HasTimeSpan
                ? PlayerFront.NaturalDuration.TimeSpan
                : TimeSpan.Zero;

            var sensorGroup = _currentFrontGroup;
            string? nmeaPath = sensorGroup?.FrontNmeaPath ?? sensorGroup?.RearNmeaPath;
            var sensorFrames = nmeaPath != null
                ? await Task.Run(() => NmeaSensorParser.Parse(nmeaPath, sensorGroup?.Timestamp, duration))
                : new List<DashcamSensorFrame>();
            if (openToken != _frontOpenToken)
            {
                DashcamDebugLog.Log($"[MediaInfo] 破棄(NMEA解析待ち中に別ファイルへ切替) token={openToken}/{_frontOpenToken}");
                DiscardMediaInfo(mediaInfoTask);
                return; // 待機中に別ファイルへ切り替わった
            }

            // ファイルの先頭/末尾が測位ロスト(トンネル等)なら、前後のファイルの測位速度を取り込んで
            // ロスト区間の推定速度を補間し直す（ファイルをまたぐ長いトンネルにも対応）
            if (nmeaPath != null && sensorGroup != null && sensorFrames.Count > 0 && NmeaSensorParser.MaxEstimateGapSeconds > 0
                && (!sensorFrames[0].HasGpsFix || !sensorFrames[^1].HasGpsFix))
            {
                bool needBefore = !sensorFrames[0].HasGpsFix;
                bool needAfter = !sensorFrames[^1].HasGpsFix;
                var gapContext = await Task.Run(() => BuildSpeedGapContext(sensorGroup, needBefore, needAfter));
                if (openToken != _frontOpenToken || !ReferenceEquals(_currentFrontGroup, sensorGroup))
                {
                    DiscardMediaInfo(mediaInfoTask);
                    return; // 待機中に別ファイルへ切り替わった
                }
                if (gapContext != null)
                    sensorFrames = await Task.Run(() => NmeaSensorParser.Parse(nmeaPath, sensorGroup.Timestamp, duration, gapContext));
                if (openToken != _frontOpenToken) { DiscardMediaInfo(mediaInfoTask); return; }
            }

            if (mediaInfoTask != null)
            {
                var mi = await mediaInfoTask;
                if (openToken != _frontOpenToken)
                {
                    DashcamDebugLog.Log($"[MediaInfo] 破棄(解析待ち中に別ファイルへ切替) token={openToken}/{_frontOpenToken}");
                    mi?.Dispose();
                    return;
                }
                ApplyMediaInfo(mi); // コーデック表示の更新はUIスレッドで行う
            }

            _sensorFrames = sensorFrames;
            if (_gapPending)
                DashcamDebugLog.Log($"[Gap] MediaOpened内: 動画情報・センサー(NMEA)の解析完了まで {swOpened.ElapsedMilliseconds}ms（{sensorFrames.Count}件）");
            // ここまで
            // グラフはファイル全体分をここで一度だけ計算して描画する（毎フレーム全点を再計算して
            // いた従来方式はスレッド負荷が無駄に高かったため）。再生中はSetPlayhead()で現在位置を
            // 反映するだけにする。チャート自体がシークUIも兼ねるため、Maximum等の設定は不要
            // （AccelChart内部でこのdurationを元に比率計算する）。
            AccelChart.SetFullTrack(_sensorFrames, duration);
            RebuildEventMarkersAsync(duration); // トンネル/SA-PA/Gセンサー急変点（バックグラウンドで1回だけ算出）

            // レジューム再生: 位置決めは必ずPlay()より先に行う。
            // StepToVideoOnlyAsyncはドラッグシーク確定時と同じ「指定フレームへ正確に着地させたら
            // 内部的にPause()する」設計のメソッドのため、これより先にPlay()してしまうと
            // 音声(hidden MediaElement側)だけ既に再生が進み、映像(AVEngine側)はシーク後に
            // Pause()されたまま……という「映像は止まったまま音声だけ流れる」不具合になる
            // （実機ログで確認: Play()→Seek()→catch-up→最後にPause()で終わっており、
            // その後Play()を呼び直していなかったのが原因）。
            //
            // また、レジューム直後のシークはSDカード等の低速ストレージからの読み出しが
            // まだ間に合っておらず本来遅くなりがちな処理のため、同じストレージへ同時に
            // アクセスするRefreshMapBufferAsync(前後最大21ファイル分のNMEA読み直し)や
            // SchedulePrefetchIfNeeded(次ファイルの先読み)は、あえてこのシークが完了した
            // "後"に回している（以前は先に走らせておりI/Oが競合して余計に遅くなっていた）。
            if (_pendingResumeSeconds is double resumeSec)
            {
                _pendingResumeSeconds = null;
                if (resumeSec > 0.5 && resumeSec < duration.TotalSeconds)
                    await PlayerFront.StepToVideoOnlyAsync(TimeSpan.FromSeconds(resumeSec), timeoutMs: 3000);
            }

            if (_wantsPlaying)
            {
                if (_gapPending)
                    DashcamDebugLog.Log($"[Gap] MediaOpened内: グラフ描画まで終え、Play()直前 {swOpened.ElapsedMilliseconds}ms（MediaEndedから{GapMs(_gapMediaEndedTs)}ms）");
                PlayerFront.Play();
                _isPlaying = true;
                SetPlayPauseIcon(true);
                if (_gapPending)
                    DashcamDebugLog.Log($"[Gap] MediaEnded → Play()呼び出し完了まで {GapMs(_gapMediaEndedTs)}ms");
            }

            if (PlayerFront.NaturalVideoWidth > 0 && PlayerFront.NaturalVideoHeight > 0)
            {
                RequestWindowFit?.Invoke(PlayerFront.NaturalVideoWidth * ZoomScale, PlayerFront.NaturalVideoHeight * ZoomScale);
                UpdateZoomAvailability();
            }

            if (_currentFrontGroup != null)
                _ = RefreshMapBufferAsync(_currentFrontGroup);

            SchedulePrefetchIfNeeded();
        }

        // =====================================================================
        // 設定メニュー（ポップオーバー）・ステータスバッジ・トースト通知
        // =====================================================================
        // ツールバーの設定項目は3つのポップオーバー(表示・解析 / キャプチャ / 配置・レイアウト)にまとめた。
        // 中のコントロールは従来のx:Name・イベントのまま移しただけなので、設定の保存/復元は従来と同じ。
        // ポップオーバーはStaysOpen=Trueにして、開閉（排他・外側クリック・Esc・ウィンドウ移動等）を
        // ここで制御する（ComboBoxのドロップダウンが外側クリック扱いで閉じてしまうのを避けるため）。

        private bool _menuWindowHooked;

        private Popup[] AllPopovers => new[] { DisplayPopover, CapturePopover, LayoutPopover };

        private ComboBox[] MenuCombos => new[]
        {
            ZoomCombo, RearZoomCombo, RearTimeOffsetCombo, TunnelEntryDetectionModeCombo,
            ScreenshotFormatCombo, ScreenshotComposeCombo, FrameStepAmountCombo,
            MapInfoCornerCombo, MapInfoScaleCombo,
        };

        private static string MenuLabel(string controlName) => controlName switch
        {
            nameof(ZoomCombo) => "フロント倍率",
            nameof(RearZoomCombo) => "リア倍率",
            nameof(RearTimeOffsetCombo) => "リア時間",
            nameof(TunnelEntryDetectionModeCombo) => "トンネル検出",
            nameof(ScreenshotFormatCombo) => "撮影形式",
            nameof(ScreenshotComposeCombo) => "撮影範囲",
            nameof(FrameStepAmountCombo) => "コマ送り量",
            nameof(MapInfoCornerCombo) => "地図位置",
            nameof(MapInfoScaleCombo) => "地図倍率",
            nameof(SpeedOsdCheck) => "車速OSD",
            nameof(DnnEnabledCheck) => "AI（超解像）",
            nameof(RearLinkedCheck) => "リア追従",
            _ => controlName,
        };

        private void InitializeMenuUi()
        {
            foreach (var cb in MenuCombos) cb.SelectionChanged += MenuCombo_SelectionChanged;
            foreach (var ck in new[] { SpeedOsdCheck, DnnEnabledCheck, RearLinkedCheck })
            {
                ck.Checked += MenuCheck_Changed;
                ck.Unchecked += MenuCheck_Changed;
            }

            // 外側クリックで閉じる（Previewなのでコントロールが処理済みのクリックも拾う）。Escでも閉じる。
            AddHandler(PreviewMouseDownEvent, new System.Windows.Input.MouseButtonEventHandler(Menu_PreviewMouseDown), true);
            AddHandler(PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler(Menu_PreviewKeyDown), true);
            Unloaded += (_, _) => CloseAllPopovers();

            UpdateStatusBadges();
        }

        // ウィンドウの非アクティブ化・移動・サイズ変更でもポップオーバーを閉じる（Popupは自動では追従しないため）
        private void HookMenuWindowEvents()
        {
            if (_menuWindowHooked) return;
            if (Window.GetWindow(this) is not Window w) return;
            w.Deactivated += (_, _) => CloseAllPopovers();
            w.LocationChanged += (_, _) => CloseAllPopovers();
            w.SizeChanged += (_, _) => CloseAllPopovers();
            w.StateChanged += (_, _) => CloseAllPopovers();
            _menuWindowHooked = true;
        }

        private void PopoverButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string name || FindName(name) is not Popup popup) return;
            bool wasOpen = popup.IsOpen;
            CloseAllPopovers(); // 排他: 他のポップオーバーが開いていれば閉じてから切り替える
            if (!wasOpen)
            {
                popup.IsOpen = true;
                UpdatePopoverButtonStates();
            }
        }

        private void CloseAllPopovers()
        {
            foreach (var p in AllPopovers) p.IsOpen = false;
            UpdatePopoverButtonStates();
        }

        // 開いているポップオーバーのボタンを強調表示する
        private void UpdatePopoverButtonStates()
        {
            SetMenuButtonActive(DisplayMenuButton, DisplayPopover.IsOpen);
            SetMenuButtonActive(CaptureMenuButton, CapturePopover.IsOpen);
            SetMenuButtonActive(LayoutMenuButton, LayoutPopover.IsOpen);
        }

        private void SetMenuButtonActive(Button button, bool active)
        {
            if (active) button.Foreground = (Brush)FindResource("TextAccentCyan");
            else button.ClearValue(Control.ForegroundProperty);
        }

        private void Menu_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!AllPopovers.Any(p => p.IsOpen)) return;
            if (e.OriginalSource is not DependencyObject src) { CloseAllPopovers(); return; }

            // メニューボタン自体のクリックは、各ボタンのClick(PopoverButton_Click)で開閉する
            if (IsInside(src, DisplayMenuButton) || IsInside(src, CaptureMenuButton) || IsInside(src, LayoutMenuButton)) return;
            // ポップオーバーの中（ComboBoxのドロップダウン含む）のクリックでは閉じない
            foreach (var p in AllPopovers)
                if (p.IsOpen && IsInside(src, p)) return;

            CloseAllPopovers();
        }

        private void Menu_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            if (!AllPopovers.Any(p => p.IsOpen)) return;
            // ComboBoxのドロップダウンが開いている間のEscは、まずドロップダウンを閉じる（ComboBox側に任せる）
            if (MenuCombos.Any(cb => cb.IsDropDownOpen)) return;
            CloseAllPopovers();
            e.Handled = true;
        }

        // nodeがcontainer（Popupの場合はその子コンテンツ）の内側にあるか。Popup/ComboBoxのドロップダウンは
        // 別のビジュアルツリーなので、ビジュアル親が途切れたら論理親(Popup)へ乗り換えながら辿る。
        private static bool IsInside(DependencyObject? node, FrameworkElement container)
        {
            var popup = container as Popup;
            for (int guard = 0; node != null && guard < 400; guard++)
            {
                if (ReferenceEquals(node, container) || (popup != null && ReferenceEquals(node, popup.Child))) return true;
                if (node is FrameworkElement fe && fe.Parent is Popup owner) { node = owner; continue; }
                DependencyObject? next = null;
                if (node is Visual || node is System.Windows.Media.Media3D.Visual3D) next = VisualTreeHelper.GetParent(node);
                next ??= LogicalTreeHelper.GetParent(node);
                node = next;
            }
            return false;
        }

        // ---- 設定変更の検知（ステータスバッジの即時更新 + トースト通知） ----

        private void MenuCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateStatusBadges();
            // 初期選択やRestoreSettings等のコード側の変更では通知しない。ユーザーがドロップダウン/キーボードで
            // 変更したとき（ドロップダウンが開いている、またはフォーカスがあるとき）だけ通知する。
            if (e.RemovedItems.Count == 0) return;
            if (sender is ComboBox cb && (cb.IsDropDownOpen || cb.IsKeyboardFocusWithin) && cb.SelectedItem is ComboBoxItem item)
                ShowToast($"{MenuLabel(cb.Name)}を{item.Content}に変更しました");
        }

        private void MenuCheck_Changed(object sender, RoutedEventArgs e)
        {
            UpdateStatusBadges();
            if (sender is CheckBox ck && (ck.IsMouseOver || ck.IsKeyboardFocusWithin))
                ShowToast($"{MenuLabel(ck.Name)}を{(ck.IsChecked == true ? "ON" : "OFF")}に変更しました");
        }

        private static string SelectedLabel(ComboBox cb) =>
            (cb.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;

        // メインツールバー右端のバッジ（現在の主要設定）を更新する
        private void UpdateStatusBadges()
        {
            if (BadgeAiText == null || BadgeZoomText == null || BadgeCaptureText == null || BadgeRearText == null) return;

            bool ai = DnnEnabledCheck.IsChecked == true;
            BadgeAiText.Text = ai ? "AI: ON" : "AI: OFF";
            BadgeAiText.Foreground = ai ? (Brush)FindResource("TextAccentCyan") : (Brush)FindResource("TextMuted");

            BadgeZoomText.Text = $"倍率: {SelectedLabel(ZoomCombo)}";

            string scope = _screenshotScope switch
            {
                ScreenshotScope.Composite => "全合成",
                ScreenshotScope.RearOnly => "リア",
                _ => "フロント",
            };
            BadgeCaptureText.Text = $"{SelectedLabel(ScreenshotFormatCombo)}/{scope}";

            BadgeRearText.Text = RearLinkedCheck.IsChecked == true ? "リア追従" : "追従OFF";
        }

        private int _toastToken;

        /// <summary>画面下部に簡易通知を約2秒（フェードイン0.2秒・表示1.6秒・フェードアウト0.4秒）表示する。</summary>
        private void ShowToast(string message)
        {
            ToastText.Text = message;
            ToastHost.Visibility = Visibility.Visible;
            int token = ++_toastToken;

            var anim = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, TimeSpan.Zero));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(200)));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(1800)));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(2200)));
            anim.Completed += (_, _) =>
            {
                // 連続して通知した場合、古いアニメーションの完了で新しい通知を消さないようにする
                if (token == _toastToken) ToastHost.Visibility = Visibility.Collapsed;
            };
            ToastHost.BeginAnimation(OpacityProperty, anim);
        }

        private int _frontOpenToken; // PlayerFront_MediaOpenedの世代番号（解析待機中の切替を検出する）

        // 別スレッドで動画情報を解析する（UI要素には触れない）。失敗時はnull。
        private static MediaInfoNative? ProbeMediaInfo(string path)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var mi = new MediaInfoNative(path);
                if (mi.Success)
                {
                    DashcamDebugLog.Log($"[MediaInfo] 解析OK {sw.ElapsedMilliseconds}ms video={mi.VideoCodec ?? "(null)"} audio={mi.AudioCodec ?? "(null)"} ch={mi.AudioChannelCount} {path}");
                    return mi;
                }
                DashcamPlayErrorLogger.Log($"[MediaInfo] failed for {path}");
                DashcamDebugLog.Log($"[MediaInfo] 解析失敗(Success=false) {sw.ElapsedMilliseconds}ms {path}");
                mi.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                DashcamPlayErrorLogger.Log($"[MediaInfo] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                DashcamDebugLog.Log($"[MediaInfo] 例外 {sw.ElapsedMilliseconds}ms {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // 解析結果を反映する（UIスレッドで呼ぶ）。
        private void ApplyMediaInfo(MediaInfoNative? mi)
        {
            _mediaInfo?.Dispose();
            _mediaInfo = mi;
            DashcamDebugLog.Log($"[MediaInfo] 表示へ反映 mi={(mi == null ? "null" : "あり")}");
            UpdateCodecStatusBar();
            LogCodecLabelLayout();
            FrontVideoInfoChanged?.Invoke(); // メインウィンドウの「情報」タブ（動画情報）も更新させる
        }

        /// <summary>メインウィンドウの「情報」タブ（動画情報）へ渡す、再生中のFront動画の情報。</summary>
        internal sealed record FrontVideoInfoSnapshot(string? Path, int Width, int Height, TimeSpan? Duration, MediaInfoNative? MediaInfo);

        /// <summary>Front動画の情報が変わった（MediaInfoの解析完了・停止）。メインウィンドウの「情報」タブの更新用。
        /// UIスレッドで発火する。</summary>
        public event Action? FrontVideoInfoChanged;

        /// <summary>現在のFront動画の情報を返す。再生中のシーンが無ければPathがnull。</summary>
        internal FrontVideoInfoSnapshot GetFrontVideoInfo() => new(
            _currentFrontGroup?.FrontVideoPath,
            PlayerFront.NaturalVideoWidth,
            PlayerFront.NaturalVideoHeight,
            PlayerFront.NaturalDuration.HasTimeSpan ? PlayerFront.NaturalDuration.TimeSpan : null,
            _mediaInfo);

        /// <summary>コーデック表示が実際に見えているかの診断。レイアウト確定後に、ラベルの表示状態・左端位置・幅を
        /// 画面幅と並べてdebug.logへ出す（位置+幅が画面幅を超えていれば、バーの右側が見切れている）。</summary>
        private void LogCodecLabelLayout()
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                try
                {
                    var origin = new System.Windows.Point(0, 0);
                    var v = VideoCodecLabel.TranslatePoint(origin, this);
                    var d = DecodeModeText.TranslatePoint(origin, this);
                    var f = FpsText.TranslatePoint(origin, this);
                    DashcamDebugLog.Log(
                        $"[MediaInfo] 表示レイアウト 表示文字=\"{VideoCodecLabel.Text} / {AudioCodecLabel.Text} / {AudioChannelLabel.Text}\" " +
                        $"映像コーデック: 可視={VideoCodecLabel.IsVisible} 左端x={v.X:F0} 幅={VideoCodecLabel.ActualWidth:F0} / " +
                        $"デコード表示: x={d.X:F0} 幅={DecodeModeText.ActualWidth:F0} / fps表示: x={f.X:F0} / 画面幅={ActualWidth:F0}");
                }
                catch (Exception ex)
                {
                    DashcamDebugLog.Log($"[MediaInfo] 表示レイアウトの取得に失敗: {ex.Message}");
                }
            }));
        }

        // 解析の完了前に別ファイルへ切り替わった場合、その結果を破棄する。
        private static void DiscardMediaInfo(Task<MediaInfoNative?>? task)
        {
            if (task == null) return;
            _ = task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); }, TaskScheduler.Default);
        }

        // コントロールバーにMainWindow本体と同じコーデック略称を表示する。
        // AnalyzeAndShowMediaInfoの解析が終わるたびに呼ばれる。
        private void UpdateCodecStatusBar()
        {
            if (_mediaInfo == null || !_mediaInfo.Success)
            {
                VideoCodecLabel.Text = "";
                AudioCodecLabel.Text = "";
                AudioChannelLabel.Text = "";
                return;
            }

            VideoCodecLabel.Text = _mediaInfo.VideoCodec ?? "";
            AudioCodecLabel.Text = _mediaInfo.AudioCodec ?? "";
            int ch = _mediaInfo.AudioChannelCount;
            AudioChannelLabel.Text = ch switch
            {
                1 => "1.0",
                2 => "2.0",
                6 => "5.1",
                8 => "7.1",
                _ => ch > 0 ? $"{ch}ch" : ""
            };
        }

        private void PlayerRear_MediaOpened(object sender, RoutedEventArgs e)
        {
            DashcamDebugLog.Log($"[RearOpened] Rear={_currentRearGroup?.TimestampKey ?? "(null)"} wantsPlaying={_wantsPlaying} " +
                $"Front位置={PlayerFront.Position.TotalSeconds:F2}s Rear映像={PlayerRear.NaturalVideoWidth}x{PlayerRear.NaturalVideoHeight}");
            PlayerRear.ResetDnnEngineForNewFile();

            // 時刻ベース同期: リアclipがFrontより前から始まっている/Front再生が進んでいる場合は、
            // 現在のFront時刻に対応する位置から始める（開き終わりまでにFrontが進んだ分も含む）
            if (RearLinked && _frontStart != null && _currentRearClipPath != null)
            {
                var desired = (_frontStart.Value + PlayerFront.Position + RearTimeOffsetSpan) - _currentRearClipStart; // 【今回追加】手動時刻補正を加算
                if (desired > TimeSpan.FromMilliseconds(300))
                {
                    var target = desired + _rearSeekLead;
                    if (PlayerRear.NaturalDuration.HasTimeSpan && target > PlayerRear.NaturalDuration.TimeSpan)
                        target = PlayerRear.NaturalDuration.TimeSpan;
                    PlayerRear.Position = target;
                }
            }

            _rearAvailable = true;
            ApplyRearPipLayout(); // 「オリジナル」倍率はリア映像の実サイズで再計算する
            UpdateRearPipVisibility();
            if (_wantsPlaying)
                PlayerRear.Play();
        }

        private void PlayerFront_MediaFailed(object sender, FfmpegMediaFailedEventArgs e)
        {
            _consecutiveFrontFailures++;
            DashcamPlayErrorLogger.Log($"[MediaFailed] Front={_currentFrontGroup?.TimestampKey ?? "(null)"} 連続失敗={_consecutiveFrontFailures}回");
            if (_consecutiveFrontFailures >= 3)
            {
                _consecutiveFrontFailures = 0;
                AppMessageBox.Show(Window.GetWindow(this),
                    "複数のFront動画が連続して再生できませんでした。ファイルの状態を確認してください。",
                    "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Warning, isDarkMode: true);
                return;
            }
            AdvanceToNextFrontScene();
        }

        private void PlayerRear_MediaFailed(object sender, FfmpegMediaFailedEventArgs e)
        {
            // 「エラー記録」ファイル等で開けない場合。スイッチには一切触らず（ユーザーが自分で
            // 操作したものを勝手にOFFにするのは体験として最悪、というご指摘のため）、
            // 単に「今は表示できるものが無い」状態にしてPiPを黙って隠すだけにする。
            DashcamPlayErrorLogger.Log($"[MediaFailed] Rear={_currentRearGroup?.TimestampKey ?? "(null)"}");
            PlayerRear.Stop();
            _rearFailedPath = _currentRearClipPath; // 時刻ベース同期で同じclipを読み直し続けないよう記録
            _currentRearClipPath = null;
            _currentRearGroup = null;
            _rearAvailable = false;
            UpdateRearPipVisibility();
        }

        // ファイル切替の継ぎ目の長さを測る（MediaEnded → 次ファイルのOpen完了 → 最初のフレーム表示）
        private long _gapMediaEndedTs;
        private bool _gapPending;


        // 切替後の約10秒間、UIスレッドが固まっていないかを監視して[UIStall]として記録する
        private System.Windows.Threading.DispatcherTimer? _uiStallTimer;
        private long _uiStallLastTick;
        private long _uiStallUntilTs;

        private void StartUiStallProbe()
        {
            _uiStallLastTick = System.Diagnostics.Stopwatch.GetTimestamp();
            _uiStallUntilTs = _uiStallLastTick + 10 * System.Diagnostics.Stopwatch.Frequency;
            if (_uiStallTimer == null)
            {
                _uiStallTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal)
                {
                    Interval = TimeSpan.FromMilliseconds(50)
                };
                _uiStallTimer.Tick += (_, _) =>
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    long lateMs = (long)((now - _uiStallLastTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                    _uiStallLastTick = now;
                    if (lateMs > 300)
                        DashcamDebugLog.Log($"[UIStall] UIスレッドが約{lateMs}ms応答しませんでした");
                    if (now > _uiStallUntilTs) _uiStallTimer!.Stop();
                };
            }
            _uiStallTimer.Start();
        }

        private static long GapMs(long fromTs) => (long)((System.Diagnostics.Stopwatch.GetTimestamp() - fromTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

        private void PlayerFront_MediaEnded(object sender, RoutedEventArgs e)
        {
            DashcamDebugLog.Log($"[MediaEnded] Front={_currentFrontGroup?.TimestampKey ?? "(null)"}");
            _gapMediaEndedTs = System.Diagnostics.Stopwatch.GetTimestamp();
            _gapPending = true;
            StartUiStallProbe();
            AdvanceToNextFrontScene();
        }

        private void NextSceneButton_Click(object sender, RoutedEventArgs e)
        {
            _mapInfoProvider.Reset(); // 手動でのシーン切替は明確な非連続点なので、通過中フラグ等をここでクリアする
            AdvanceToNextFrontScene();
        }

        private void PrevSceneButton_Click(object sender, RoutedEventArgs e)
        {
            _mapInfoProvider.Reset();
            GoToPreviousFrontScene();
        }

        /// <summary>Frontリストの1つ前のグループを再生する（次のシーンの逆方向）。</summary>
        private void GoToPreviousFrontScene()
        {
            if (_currentFrontGroup is null)
            {
                DashcamPlayErrorLogger.Log("[Previous] _currentFrontGroupがnullのため中止");
                return;
            }

            int idx = _frontGroups.IndexOf(_currentFrontGroup);
            if (idx < 0)
            {
                string curKey = _currentFrontGroup.TimestampKey;
                idx = _frontGroups.FindIndex(g => g.TimestampKey == curKey); // 再スキャンで実体が入れ替わっていてもキーで探す
            }
            if (idx < 0)
            {
                DashcamPlayErrorLogger.Log($"[Previous] {_currentFrontGroup.TimestampKey}が_frontGroupsに見つからず中止");
                return;
            }
            if (idx <= 0)
            {
                DashcamDebugLog.Log($"[Previous] {_currentFrontGroup.TimestampKey}は先頭ファイルのため中止");
                return;
            }

            var prev = _frontGroups[idx - 1];
            SetListSelection(FrontList, prev, scrollToTop: false);
            ScrollSelectedToTop(FrontList, idx - 1);
            PlayFrontGroup(prev);
        }

        private void AdvanceToNextFrontScene()
        {
            if (_currentFrontGroup is null)
            {
                DashcamPlayErrorLogger.Log("[Advance] _currentFrontGroupがnullのため中止");
                return;
            }

            int idx = _frontGroups.IndexOf(_currentFrontGroup);
            if (idx < 0)
            {
                string curKey = _currentFrontGroup.TimestampKey;
                idx = _frontGroups.FindIndex(g => g.TimestampKey == curKey); // 再スキャンで実体が入れ替わっていてもキーで探す
            }
            if (idx < 0)
            {
                DashcamPlayErrorLogger.Log($"[Advance] {_currentFrontGroup.TimestampKey}が_frontGroupsに見つからず中止（再スキャンで参照が失われた可能性）");
                return;
            }
            if (idx + 1 >= _frontGroups.Count)
            {
                DashcamDebugLog.Log($"[Advance] {_currentFrontGroup.TimestampKey}は最終ファイルのため中止");
                return;
            }

            var next = _frontGroups[idx + 1];
            SetListSelection(FrontList, next, scrollToTop: false);

            // 連続再生の見た目: 再生中のファイルが常にリストの一番上に来て、後続が下から
            // 順々にせり上がって見えるようにする（単なるScrollIntoViewだと最小限しか動かない）。
            ScrollSelectedToTop(FrontList, idx + 1);

            PlayFrontGroup(next);
        }

        private void PlayerRear_MediaEnded(object sender, RoutedEventArgs e)
        {
            DashcamDebugLog.Log($"[RearEnded] Rear={_currentRearGroup?.TimestampKey ?? "(null)"} RearLinked={RearLinked} Front位置={PlayerFront.Position.TotalSeconds:F2}s");
            if (RearLinked)
            {
                // 追従中: 次のclipへの切替は時刻ベース(ReconcileRear)が行う。終了したclipは再読込しない
                _rearExhaustedPath = _currentRearClipPath;
                return;
            }
            if (_currentRearGroup is null) return;
            int idx = _rearGroups.IndexOf(_currentRearGroup);
            if (idx < 0)
            {
                string curKey = _currentRearGroup.TimestampKey;
                idx = _rearGroups.FindIndex(g => g.TimestampKey == curKey);
            }
            if (idx < 0 || idx + 1 >= _rearGroups.Count) return;

            PlayRearGroup(_rearGroups[idx + 1]);
            PlayerRear.Play();
        }

        /// <summary>選択中の項目をリストの一番上（ビューポート先頭）へスクロールする。</summary>
        private static void ScrollSelectedToTop(ListBox listBox, int index)
        {
            if (index < 0) return;
            if (FindScrollViewer(listBox) is ScrollViewer sv)
                sv.ScrollToVerticalOffset(index); // ModernListでVirtualizingPanel.ScrollUnit="Item"を設定済み
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject d)
        {
            int count = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(d, i);
                if (child is ScrollViewer sv) return sv;
                var found = FindScrollViewer(child);
                if (found != null) return found;
            }
            return null;
        }

        // Segoe Fluent Icons / Segoe MDL2 Assets のグリフ（再生 / 一時停止）
        private const string PlayGlyph = "\uE768";
        private const string PauseGlyph = "\uE769";

        /// <summary>再生/一時停止ボタンのアイコンとTipsを状態に合わせる（playing=trueなら「一時停止」を表示）。</summary>
        private void SetPlayPauseIcon(bool playing)
        {
            PlayPauseButton.Content = playing ? PauseGlyph : PlayGlyph;
            PlayPauseButton.ToolTip = playing ? "一時停止" : (_isStopped ? "再生（先頭から）" : "再生");
        }

        // 停止ボタンで止めた状態（映像を消している）。この間は再生ボタンで現在のシーンを先頭から開き直す。
        private bool _isStopped;

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isStopped)
            {
                if (_currentFrontGroup != null)
                    PlayFrontGroup(_currentFrontGroup); // 内部で_isStopped解除・アイコン更新
                return;
            }

            if (_isPlaying)
            {
                PlayerFront.Pause();
                PlayerRear.Pause();
                SetPlayPauseIcon(false);
                _wantsPlaying = false;
            }
            else
            {
                PlayerFront.Play();
                if (HasActiveRear()) PlayerRear.Play();
                SetPlayPauseIcon(true);
                _wantsPlaying = true;
            }
            _isPlaying = !_isPlaying;
        }

        // ここから追加
        // ---- コマ送り / コマ戻し ----
        // 1フレームの長さ。動画情報(MediaInfoNative)から取れた実fpsを使い、取れない場合は30fpsとみなす。
        private const double FallbackFrameStepSeconds = 1.0 / 30.0;
        private double FrameStepSeconds
        {
            get
            {
                double fps = _mediaInfo is { Success: true } ? _mediaInfo.VideoFrameRate : 0;
                return fps is > 1 and < 240 ? 1.0 / fps : FallbackFrameStepSeconds;
            }
        }
        private bool _frameStepBusy;
        private long _lastStepEndTs; // 直近のコマ送りが終わった時刻(Stopwatch)。連続操作が途切れたかの判定に使う
        private TimeSpan _frameStepPos; // 連続コマ送り時に位置がぶれないよう、送り基準位置を自前で保持する
        private bool _frameStepPosValid;

        // 1回に進めるコマ数（30fps換算）。1コマ(約0.03秒)では動きが小さく何度も押す必要があったため選べるようにした。
        private int _frameStepFrames = 3;
        private DispatcherTimer? _frameStepRepeatTimer;
        private int _frameStepRepeatDir;

        /// <summary>コマ送り1回あたりのコマ数（1/3/5/8/10/15/30）。永続化対象。AppSettings側でintとして保持する想定。</summary>
        public int FrameStepFramesSetting
        {
            get => _frameStepFrames;
            set
            {
                _frameStepFrames = value is 1 or 3 or 5 or 8 or 10 or 15 or 30 ? value : 3;
                foreach (ComboBoxItem item in FrameStepAmountCombo.Items)
                {
                    if ((string)item.Tag == _frameStepFrames.ToString()) { FrameStepAmountCombo.SelectedItem = item; break; }
                }
            }
        }

        private void FrameStepAmountCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (FrameStepAmountCombo.SelectedItem is ComboBoxItem item && int.TryParse((string)item.Tag, out var n))
                _frameStepFrames = n;
        }

        // ボタンを押した瞬間に1回進め、押し続けている間は一定間隔で繰り返す（0.4秒後から約0.09秒ごと）。
        // 前の送りが終わっていないときはStepFrameAsync側(_frameStepBusy)で読み飛ばすため、デコードが追いつかなくても溜まらない。
        private void FrameStepButton_Down(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not Button b || !int.TryParse((string)b.Tag, out int dir)) return;
            _frameStepRepeatDir = dir;
            _ = StepFrameAsync(dir);

            if (_frameStepRepeatTimer == null)
            {
                _frameStepRepeatTimer = new DispatcherTimer(DispatcherPriority.Input);
                _frameStepRepeatTimer.Tick += FrameStepRepeatTimer_Tick;
            }
            _frameStepRepeatTimer.Interval = TimeSpan.FromMilliseconds(400);
            _frameStepRepeatTimer.Start();
        }

        private void FrameStepRepeatTimer_Tick(object? sender, EventArgs e)
        {
            if (_frameStepRepeatTimer != null) _frameStepRepeatTimer.Interval = TimeSpan.FromMilliseconds(90);
            _ = StepFrameAsync(_frameStepRepeatDir);
        }

        private void FrameStepButton_Up(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _frameStepRepeatTimer?.Stop();
        }

        private async Task StepFrameAsync(int direction)
        {
            if (_isStopped || _frameStepBusy || _isDragging) return;
            if (PlayerFront.Source == null || !PlayerFront.NaturalDuration.HasTimeSpan) return;

            _frameStepBusy = true;
            try
            {
                if (_isPlaying)
                {
                    PlayerFront.Pause();
                    PlayerRear.Pause();
                    _isPlaying = false;
                    _wantsPlaying = false;
                    SetPlayPauseIcon(false);
                }

                var oneFrame = TimeSpan.FromSeconds(FrameStepSeconds);
                var step = TimeSpan.FromTicks(oneFrame.Ticks * _frameStepFrames);
                var dur = PlayerFront.NaturalDuration.TimeSpan;

                // コマ送りの基準は、このコマ送り自身が保持している「論理位置」(_frameStepPos)。
                // 画面に出ているフレームの時刻(表示pts)を毎回の基準にしてはいけない：シーク後に表示される
                // フレームは直近のキーフレームに丸められ、目標より最大0.4秒ほど先の時刻になる（trace.logで確認）。
                // それを基準に取り直すと、コマ戻しの途中で位置が前方へ跳ね返り、「戻しても元に戻る」動きになっていた。
                // 論理位置を取り直すのは、コマ送りの連続操作が途切れたとき（再生・シーク・ファイル切替の後）だけ。
                bool rebased = false;
                var prevStepPos = _frameStepPos;
                if (!_frameStepPosValid)
                {
                    // 新しいコマ送りの開始。いま画面に出ているフレームの時刻を起点にする
                    // （再生中に止めた直後は時計(Position)が表示中のフレームとずれることがあるため）
                    var basePos = PlayerFront.Position;
                    if (_lastDisplayedPts >= 0)
                    {
                        var shown = TimeSpan.FromSeconds(_lastDisplayedPts);
                        if ((shown - basePos).Duration() < TimeSpan.FromSeconds(2)) basePos = shown; // 前のファイルの古い値は使わない
                    }
                    _frameStepPos = basePos;
                    rebased = true;
                }

                var next = _frameStepPos + TimeSpan.FromTicks(step.Ticks * direction);
                var last = dur - oneFrame;
                if (last < TimeSpan.Zero) last = TimeSpan.Zero;
                if (next < TimeSpan.Zero) next = TimeSpan.Zero;
                if (next > last) next = last;

                _frameStepPos = next;
                _frameStepPosValid = true;

                // 半フレーム先へ着地させ、丸め誤差で前のフレームに落ちるのを防ぐ
                var seekTarget = next + TimeSpan.FromTicks(oneFrame.Ticks / 2);
                // ここから追加：逆行現象の原因調査用ログ（debug.logに[Step]として出力、Debugビルドのみ）
                var swStep = System.Diagnostics.Stopwatch.StartNew();
                double shownBefore = _lastDisplayedPts;
                // ここまで
                await PlayerFront.StepToVideoOnlyAsync(seekTarget, timeoutMs: 1000);
                // ここから追加
                DashcamDebugLog.Log(
                    $"[Step] dir={direction:+0;-0} {_frameStepFrames}コマ(1コマ={oneFrame.TotalMilliseconds:F1}ms) " +
                    $"Position={PlayerFront.Position.TotalSeconds:F3} 表示pts: {shownBefore:F3}→{_lastDisplayedPts:F3} " +
                    $"前回の論理位置={(rebased ? double.NaN : prevStepPos.TotalSeconds):F3} 起点の取り直し={(rebased ? "あり(新規開始)" : "なし(継続)")} " +
                    $"目標={next.TotalSeconds:F3} 所要{swStep.ElapsedMilliseconds}ms");
                // ここまで
                await SeekRearToFrontPositionAsync(next);
                AccelChart.SetPlayhead(next);
            }
            finally
            {
                _lastStepEndTs = System.Diagnostics.Stopwatch.GetTimestamp();
                _frameStepBusy = false;
            }
        }
        // ここまで

        private void StopButton_Click(object sender, RoutedEventArgs e) => StopPlayback(keepCurrent: true);

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            PlayerFront.Volume = e.NewValue; // Rearは常時Volume=0（音声はFrontのみ）
        }

        /// <summary>Front映像の音量（0..1）。永続化対象。ノーマルモードのVolumeSlider/Player.Volumeと
        /// AppSettings.Volumeを共用する（アプリ全体で音量はひとつという想定）。MainWindow側の
        /// EnterDashcamMode/ExitDashcamMode/RestoreSettings/Save処理から読み書きされる。</summary>
        public double FrontVolume
        {
            get => PlayerFront.Volume;
            set
            {
                PlayerFront.Volume = value;
                VolumeSlider.Value = value; // スライダー表示も同期
            }
        }

        private ScreenshotFormat _screenshotFormat = ScreenshotFormat.Jpg;
        private enum ScreenshotScope { FrontOnly, Composite, RearOnly }
        private ScreenshotScope _screenshotScope = ScreenshotScope.FrontOnly;

        /// <summary>スクリーンショットの撮影範囲。"FrontOnly"（メイン映像のみ）／"Composite"（映像エリアの全表示を合成）／
        /// "RearOnly"（リア映像のみ）。永続化対象。AppSettings側では文字列で保持する。</summary>
        public string ScreenshotComposeSetting
        {
            get => _screenshotScope.ToString();
            set
            {
                if (!Enum.TryParse<ScreenshotScope>(value, true, out var scope)) scope = ScreenshotScope.FrontOnly;
                _screenshotScope = scope;
                foreach (ComboBoxItem item in ScreenshotComposeCombo.Items)
                {
                    if ((string)item.Tag == scope.ToString()) { ScreenshotComposeCombo.SelectedItem = item; break; }
                }
            }
        }

        private void ScreenshotComposeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ScreenshotComposeCombo.SelectedItem is ComboBoxItem item
                && Enum.TryParse<ScreenshotScope>((string)item.Tag, out var scope))
                _screenshotScope = scope;
        }

        /// <summary>スクリーンショットの保存形式。永続化対象。AppSettings側では文字列(enum名)で保持する想定。</summary>
        public string ScreenshotFormatSetting
        {
            get => _screenshotFormat.ToString();
            set
            {
                if (!Enum.TryParse<ScreenshotFormat>(value, out var f)) f = ScreenshotFormat.Jpg;
                _screenshotFormat = f;
                foreach (ComboBoxItem item in ScreenshotFormatCombo.Items)
                {
                    if ((string)item.Tag == f.ToString()) { ScreenshotFormatCombo.SelectedItem = item; break; }
                }
            }
        }

        private void ScreenshotFormatCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ScreenshotFormatCombo.SelectedItem is ComboBoxItem item
                && Enum.TryParse<ScreenshotFormat>((string)item.Tag, out var f))
                _screenshotFormat = f;
        }

        private async void ScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFrontGroup?.FrontVideoPath == null || _isStopped)
            {
                AppMessageBox.Show(Window.GetWindow(this), "Front動画を再生してから撮影してください。",
                    "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Information, isDarkMode: true);
                return;
            }

            ScreenshotButton.IsEnabled = false; // AVIFは数秒かかることがあるため、保存中の二重押しを防ぐ
            try
            {
                // 「全て合成」は映像エリア(VideoArea)ごと描画する。フロント映像の上に重なっているリア(PiP)・
                // 車速OSD・地図情報通知がそのまま入る。「フロントのみ」は従来どおりメイン映像だけ。
                // 「リアのみ」はリア映像だけ（リア表示がONで映っているときのみ。現在の表示サイズで保存）。
                bool composite = _screenshotScope == ScreenshotScope.Composite;
                FrameworkElement target;
                switch (_screenshotScope)
                {
                    case ScreenshotScope.Composite:
                        target = VideoArea;
                        break;
                    case ScreenshotScope.RearOnly:
                        if (RearPipBorder.Visibility != Visibility.Visible)
                        {
                            AppMessageBox.Show(Window.GetWindow(this), "リア映像が表示されていません。リア表示をONにして、リアが映っているときに撮影してください。",
                                "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Information, isDarkMode: true);
                            return;
                        }
                        target = PlayerRear;
                        break;
                    default:
                        target = PlayerFront;
                        break;
                }
                int w = (int)Math.Max(1, target.ActualWidth);
                int h = (int)Math.Max(1, target.ActualHeight);
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);

                // リアPiP右下のリサイズ用グリップは操作用の部品なので、写り込まないよう一時的に隠す
                var gripVisibility = RearPipResizeGrip.Visibility;
                if (composite)
                {
                    RearPipResizeGrip.Visibility = Visibility.Hidden;
                    VideoArea.UpdateLayout();
                }
                try { rtb.Render(target); }
                finally { RearPipResizeGrip.Visibility = gripVisibility; }
                rtb.Freeze(); // 別スレッドでエンコードするため

                string dir = Path.Combine(AppContext.BaseDirectory, "Screenshots");
                // ファイル名は「撮影した現在日時_VPSC」（同じ秒に複数撮った場合は _2, _3 を付ける）
                string baseName = $"{DateTime.Now:yyyyMMdd_HHmmss}_VPSC";
                var format = _screenshotFormat;

                string? note = null;
                await Task.Run(() => ScreenshotEncoder.Save(rtb, dir, baseName, format, out note));

                if (note != null)
                {
                    AppMessageBox.Show(Window.GetWindow(this), note,
                        "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Information, isDarkMode: true);
                }
            }
            catch (Exception ex)
            {
                AppMessageBox.Show(Window.GetWindow(this), $"スクリーンショットの保存に失敗しました。\n{ex.Message}",
                    "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Warning, isDarkMode: true);
            }
            finally
            {
                ScreenshotButton.IsEnabled = true;
            }
        }

        /// <summary>再生を停止して映像を消す。keepCurrent=trueは停止ボタン用: 現在のシーン(_currentFrontGroup)を
        /// 覚えておき、再生ボタンで先頭から再生し直せるようにする。既定(false)はドライブ切替・モード終了用で、
        /// 現在のシーンも破棄する（別ドライブの古いシーンを再生し直してしまわないため）。</summary>
        public void StopPlayback(bool keepCurrent = false)
        {
            PlayerFront.Stop();
            PlayerRear.Stop();
            // Stop()だけでは最後のフレームが画面に残り、一時停止と見分けがつかない（＝停止したつもりが
            // 一時停止に見え、再生ボタンも効かない状態になっていた）。映像領域を空にする。
            PlayerFront.ClearDisplay();
            PlayerRear.ClearDisplay();
            _rearAvailable = false;
            UpdateRearPipVisibility();
            _isStopped = keepCurrent && _currentFrontGroup != null;
            if (!keepCurrent)
            {
                _currentFrontGroup = null;
                _currentRearGroup = null;
            }
            AccelChart.SetPlayhead(TimeSpan.Zero);
            ClearEventMarkers();
            Hud.UpdateFrame(null);
            _mapInfoProvider.Reset(); // 明確な非連続点なのでトンネル通過中フラグ等もここでクリアする
            MapInfo.Apply(_mapInfoProvider.State, _mapInfoProvider.ShouldShowOverlay);
            _currentRearClipPath = null;
            _frontStart = null;          // 次に再生を始めるときは連続再生扱いにしない
            _rearExhaustedPath = null;
            _rearFailedPath = null;
            _isPlaying = false;
            _wantsPlaying = false;
            SetPlayPauseIcon(false);
            UpdateSpeedOsd(null);
            CurrentFileChanged?.Invoke(null);
            NotifyPlayingFiles();
            FrontVideoInfoChanged?.Invoke(); // 停止: 動画情報を「未読み込み」へ戻す
        }

        private void RearVisibleCheck_Changed(object sender, RoutedEventArgs e) => UpdateRearPipVisibility();

        /// <summary>
        /// 「リア表示」スイッチはユーザーが完全に手動で制御するものとして扱う（コード側から
        /// 勝手にON/OFFを書き換えることは一切しない）。実際にPiPを見せるかどうかは
        /// 「スイッチがONか」と「今リアが実際に再生可能か(_rearAvailable)」の掛け算で決まる。
        /// 表示できない間はスイッチがONのままでも黙って隠すだけにし、後で表示可能になれば
        /// （このメソッドを呼び直すことで）ユーザーは何も操作し直さなくても自動的に映るようになる。
        /// </summary>
        private void UpdateRearPipVisibility()
        {
            bool show = RearVisibleCheck.IsChecked == true && _rearAvailable;
            RearPipBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            // リア倍率の設定UIは「リア表示スイッチがON」の間だけ出す（リアが今再生可能かは問わない）
            if (RearZoomPanel != null)
                RearZoomPanel.Visibility = RearVisibleCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            // 【今回追加】リア時間（Front/Rearの時刻ズレ手動補正）もリア倍率と同じ条件で表示切替
            if (RearTimeOffsetPanel != null)
                RearTimeOffsetPanel.Visibility = RearVisibleCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            NotifyPlayingFiles(); // リアの切替/消失/スイッチ変更がタイトルバー表示へ反映される

            if (show && _wantsPlaying)
                PlayerRear.Play();
        }
        private void RearPipCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyRearPipLayout();

        /// <summary>リア倍率(リア映像の等倍を1.0とした縮小倍率)に応じたPiPサイズを返す。映像エリアより大きくなる場合は
        /// 縦横比を保って収まるよう縮小する（「オリジナル」も映像エリアに入らなければ収まる最大サイズになる）。</summary>
        private Size CalcRearPipSize(double scale)
        {
            double nw = PlayerRear.NaturalVideoWidth;
            double nh = PlayerRear.NaturalVideoHeight;
            if (nw <= 0 || nh <= 0) { nw = 1920; nh = 1080; } // リア未オープン時の暫定値（オープン後に再計算される）
            if (scale <= 0.0) scale = DefaultRearZoomScale;

            double w = nw * scale;
            double h = nh * scale;

            double cw = RearPipCanvas.ActualWidth, ch = RearPipCanvas.ActualHeight;
            if (cw > 0 && ch > 0)
            {
                double f = Math.Min(1.0, Math.Min(cw / w, ch / h));
                w *= f;
                h *= f;
            }
            return new Size(w, h);
        }

        /// <summary>サイズと位置を保存済みの倍率・比率から組み立て直す（Canvasのサイズ確定時、リアオープン時、復元時）。</summary>
        private void ApplyRearPipLayout()
        {
            if (RearPipBorder == null || RearPipCanvas == null || PlayerRear == null) return;
            double cw = RearPipCanvas.ActualWidth, ch = RearPipCanvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return; // まだレイアウト前。SizeChangedで再度呼ばれる

            var sz = CalcRearPipSize(RearZoomScale);
            RearPipBorder.Width = sz.Width;
            RearPipBorder.Height = sz.Height;

            double freeW = Math.Max(0, cw - sz.Width);
            double freeH = Math.Max(0, ch - sz.Height);
            if (_pipUserPositioned && _pipRatioX >= 0 && _pipRatioY >= 0)
            {
                Canvas.SetLeft(RearPipBorder, _pipRatioX * freeW);
                Canvas.SetTop(RearPipBorder, _pipRatioY * freeH);
            }
            else
            {
                Canvas.SetLeft(RearPipBorder, Math.Min(12, freeW));
                Canvas.SetTop(RearPipBorder, Math.Max(0, ch - sz.Height - 12));
            }
        }

        /// <summary>リア倍率コンボ変更時: サイズだけ差し替える（位置は左上を維持しつつ映像エリア内へ収める）。</summary>
        private void ApplyRearPipSize()
        {
            if (RearPipBorder == null || RearPipCanvas == null || PlayerRear == null) return;
            double cw = RearPipCanvas.ActualWidth, ch = RearPipCanvas.ActualHeight;
            if (cw <= 0 || ch <= 0)
            {
                var early = CalcRearPipSize(RearZoomScale);
                RearPipBorder.Width = early.Width;
                RearPipBorder.Height = early.Height;
                return;
            }

            double curLeft = Canvas.GetLeft(RearPipBorder);
            double curTop = Canvas.GetTop(RearPipBorder);
            if (!_pipUserPositioned || double.IsNaN(curLeft) || double.IsNaN(curTop))
            {
                ApplyRearPipLayout();
                return;
            }

            var sz = CalcRearPipSize(RearZoomScale);
            RearPipBorder.Width = sz.Width;
            RearPipBorder.Height = sz.Height;
            Canvas.SetLeft(RearPipBorder, Math.Clamp(curLeft, 0, Math.Max(0, cw - sz.Width)));
            Canvas.SetTop(RearPipBorder, Math.Clamp(curTop, 0, Math.Max(0, ch - sz.Height)));
            UpdateRearPipRatiosFromCurrent();
        }

        /// <summary>現在のPiP位置を空き領域に対する比率として記録する（永続化用）。</summary>
        private void UpdateRearPipRatiosFromCurrent()
        {
            double left = Canvas.GetLeft(RearPipBorder);
            double top = Canvas.GetTop(RearPipBorder);
            if (double.IsNaN(left) || double.IsNaN(top)) return;

            double freeW = RearPipCanvas.ActualWidth - RearPipBorder.Width;
            double freeH = RearPipCanvas.ActualHeight - RearPipBorder.Height;
            _pipRatioX = freeW > 1 ? Math.Clamp(left / freeW, 0, 1) : 0;
            _pipRatioY = freeH > 1 ? Math.Clamp(top / freeH, 0, 1) : 0;
        }

        private void RearZoomCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyRearPipSize();

        /// <summary> リア時間オフセット変更時: 現在のFront位置に対してリアを即座に再同期する。</summary>
        private async void RearTimeOffsetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlayerFront == null) return;
            try
            {
                await SeekRearToFrontPositionAsync(PlayerFront.Position);

                // 同一clip内の再同期はStepToVideoOnlyAsync（フレーム合わせのみ）で終わり、リアが停止したまま
                // になっていた（リア表示をOFF→ONするとUpdateRearPipVisibilityがPlayして動き出すのはこのため）。
                // Front再生中でリアが表示可能なら、ここで再開する。別clipへの切替時はLoadRearClipが
                // _rearAvailable=falseにするのでここでは再生せず、MediaOpened側が_wantsPlayingを見て再生する。
                // 再開後の細かなズレは、毎フレームのリア再同期（RearResync）が補正する。
                if (_wantsPlaying && RearVisibleCheck.IsChecked == true && _rearAvailable)
                    PlayerRear.Play();
            }
            catch (Exception ex)
            {
                DashcamPlayErrorLogger.Log($"[RearTimeOffset] 再同期に失敗: {ex.Message}");
            }
        }

        private void RearPipBorder_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _pipUserPositioned = true;
            _pipDragStart = e.GetPosition(RearPipCanvas);
            _pipDragStartPos = new Point(Canvas.GetLeft(RearPipBorder), Canvas.GetTop(RearPipBorder));
            RearPipBorder.CaptureMouse();
            e.Handled = true;
        }

        private void RearPipBorder_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_pipDragStart is not Point start || _pipDragStartPos is not Point startPos) return;
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;

            var cur = e.GetPosition(RearPipCanvas);
            double newLeft = startPos.X + (cur.X - start.X);
            double newTop = startPos.Y + (cur.Y - start.Y);

            // 映像エリアの外へ出て行方不明にならないよう範囲内にクランプする
            newLeft = Math.Clamp(newLeft, 0, Math.Max(0, RearPipCanvas.ActualWidth - RearPipBorder.Width));
            newTop = Math.Clamp(newTop, 0, Math.Max(0, RearPipCanvas.ActualHeight - RearPipBorder.Height));

            Canvas.SetLeft(RearPipBorder, newLeft);
            Canvas.SetTop(RearPipBorder, newTop);
        }

        private void RearPipBorder_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_pipDragStart != null) UpdateRearPipRatiosFromCurrent();
            _pipDragStart = null;
            _pipDragStartPos = null;
            RearPipBorder.ReleaseMouseCapture();
        }

        private void RearPipResizeGrip_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _pipResizeStart = e.GetPosition(RearPipCanvas);
            _pipResizeStartSize = new Size(RearPipBorder.Width, RearPipBorder.Height);
            RearPipResizeGrip.CaptureMouse();
            e.Handled = true; // Border側のドラッグ判定へ伝播させない
        }

        private void RearPipResizeGrip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_pipResizeStart is not Point start || _pipResizeStartSize is not Size startSize) return;
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;

            var cur = e.GetPosition(RearPipCanvas);
            double newW = Math.Max(120, startSize.Width + (cur.X - start.X));
            double newH = newW * 9.0 / 16.0; // 16:9に近い比率を維持して映像が歪まないようにする

            RearPipBorder.Width = newW;
            RearPipBorder.Height = newH;
        }

        private void RearPipResizeGrip_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _pipResizeStart = null;
            _pipResizeStartSize = null;
            RearPipResizeGrip.ReleaseMouseCapture();

            // グリップでの自由リサイズは、離した時点で最寄りのリア倍率プリセットへ揃える
            // （倍率と永続化の値を常にプリセット1つに保つため）
            ComboBoxItem? best = null;
            double bestDiff = double.MaxValue;
            foreach (var item in RearZoomCombo.Items.OfType<ComboBoxItem>())
            {
                if (!double.TryParse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture, out var s)) continue;
                double diff = Math.Abs(CalcRearPipSize(s).Width - RearPipBorder.Width);
                if (diff < bestDiff) { bestDiff = diff; best = item; }
            }
            if (best == null) return;
            if (ReferenceEquals(RearZoomCombo.SelectedItem, best))
                ApplyRearPipSize(); // 同じ倍率のままでもサイズを揃え直す
            else
                RearZoomCombo.SelectedItem = best; // SelectionChanged経由でApplyRearPipSize
        }

        private void RearLinkedCheck_Changed(object sender, RoutedEventArgs e)
        {
            // RearLinkedCheckはXAMLでIsChecked="True"指定のため、InitializeComponent実行中
            // （まだRearList等が未接続の段階）にもこのイベントが発火する。ガード必須。
            if (RearList == null) return;

            // リアリストは追従ON/OFFに関わらず常に選択可能にする（手動選択のしやすさのため）。

            if (!RearLinked) return;

            if (UseTimeAlignedRear)
            {
                _rearExhaustedPath = null;
                _rearFailedPath = null;
                ReconcileRear();
            }
            else if (_currentFrontGroup?.HasRear == true)
            {
                PlayRearGroup(_currentFrontGroup);
            }
        }

        private void ZoomCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlayerFront == null) return;

            if (PlayerFront.NaturalVideoWidth > 0 && PlayerFront.NaturalVideoHeight > 0)
                RequestWindowFit?.Invoke(PlayerFront.NaturalVideoWidth * ZoomScale, PlayerFront.NaturalVideoHeight * ZoomScale);
        }

        /// <summary>
        /// 動画のネイティブ解像度×各倍率が、いま使っている画面の作業領域に収まるかどうかで
        /// ズーム選択肢のIsEnabledを更新する（「4xは2K/4K環境では選択不能に」というご要望に対応。
        /// 画面の解像度そのものより「動画側の解像度が大きい場合に大倍率が意味をなさなくなる」方が
        /// 本質なため、固定の解像度名で判定せず、実際にウィンドウが収まるかどうかで動的に判定する）。
        /// 選択中の項目が入らなくなった場合は、収まる範囲で一番大きい倍率へ自動的に切り替える。
        /// 右サイドバー幅は地図の向きで動的に変わるため、固定値ではなく実際のActualWidthを使う。
        /// </summary>
        private void UpdateZoomAvailability()
        {
            if (PlayerFront.NaturalVideoWidth <= 0 || PlayerFront.NaturalVideoHeight <= 0) return;

            double screenW = SystemParameters.WorkArea.Width;
            double screenH = SystemParameters.WorkArea.Height;
            const double leftSidebarWidth = 16;
            double rightSidebarWidth = RightSidebarColumnDef.ActualWidth > 0 ? RightSidebarColumnDef.ActualWidth : 260;
            const double titleBarHeight = 48;
            const double controlBarHeight = 56;

            foreach (var item in ZoomCombo.Items.OfType<ComboBoxItem>())
            {
                if (!double.TryParse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture, out var scale))
                    continue;

                double neededW = leftSidebarWidth + rightSidebarWidth + PlayerFront.NaturalVideoWidth * scale;
                double neededH = titleBarHeight + controlBarHeight + PlayerFront.NaturalVideoHeight * scale;
                item.IsEnabled = neededW <= screenW && neededH <= screenH;
            }

            if (ZoomCombo.SelectedItem is ComboBoxItem selected && !selected.IsEnabled)
            {
                var fallback = ZoomCombo.Items.OfType<ComboBoxItem>()
                    .Where(i => i.IsEnabled)
                    .OrderByDescending(i => double.Parse((string)i.Tag, System.Globalization.CultureInfo.InvariantCulture))
                    .FirstOrDefault();
                if (fallback != null) ZoomCombo.SelectedItem = fallback;
            }
        }

        private async void DnnEnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            bool on = DnnEnabledCheck.IsChecked == true;

            if (!on)
            {
                PlayerFront.DnnSuperResolutionEnabled = false;
                PlayerRear.DnnSuperResolutionEnabled = false;
                return;
            }

            await EnableDnnAsync(PlayerFront);
            if (HasActiveRear())
                await EnableDnnAsync(PlayerRear);
        }

        private async Task EnableDnnAsync(FfmpegMediaElement player)
        {
            if (player.NaturalVideoWidth <= 0 || player.NaturalVideoHeight <= 0)
            {
                player.DnnSuperResolutionEnabled = true;
                return;
            }

            if (player.IsDnnReadyForCurrentResolution)
            {
                player.DnnSuperResolutionEnabled = true;
                return;
            }

            bool wasPlaying = _isPlaying;
            if (wasPlaying) player.Pause();

            bool ok = await player.PrebuildDnnSuperResolutionAsync();
            player.DnnSuperResolutionEnabled = ok;

            if (wasPlaying) player.Play();
        }

        // ---- シーク（AccelChart最上段の独立した帯から通知される。旧SeekSliderと同じ
        //      ドラッグ間引きプレビュー・クリック即シーク・ホバー時刻プレビューを維持） ----

        private void AccelChart_SeekDragStarted()
        {
            _isDragging = true;
            _wasPlayingBeforeSeekDrag = _isPlaying;
            if (_isPlaying)
            {
                PlayerFront.Pause();
                PlayerRear.Pause();
                _isPlaying = false;
                SetPlayPauseIcon(false);
            }
        }

        private async void AccelChart_SeekPreview(TimeSpan pos)
        {
            if (_dragCompleting || !PlayerFront.NaturalDuration.HasTimeSpan) return;
            await RequestLiveSeekAsync(pos.TotalSeconds);
        }

        private async Task RequestLiveSeekAsync(double seconds)
        {
            _seekLivePendingSeconds = seconds;
            if (_seekLiveBusy) return;
            _seekLiveBusy = true;
            try
            {
                while (_seekLivePendingSeconds is double target)
                {
                    _seekLivePendingSeconds = null;
                    await PlayerFront.FastSeekPreviewAsync(TimeSpan.FromSeconds(target));
                }
            }
            finally
            {
                _seekLiveBusy = false;
            }
        }

        private async void AccelChart_SeekDragCompleted(TimeSpan target)
        {
            _dragCompleting = true;
            while (_seekLiveBusy)
                await Task.Delay(15);

            // グラフはファイル全体を静的に表示する方式のため、シークしても消さない
            // （消すとまた全体を再計算することになり本末転倒）。プレイヘッド／シークバーの
            // 位置だけ動かす。
            AccelChart.SetPlayhead(target);
            await PlayerFront.StepToVideoOnlyAsync(target, timeoutMs: 2000);
            await SeekRearToFrontPositionAsync(target);

            _isDragging = false;
            _dragCompleting = false;

            if (_wasPlayingBeforeSeekDrag)
            {
                PlayerFront.Play();
                if (HasActiveRear()) PlayerRear.Play();
                _isPlaying = true;
                SetPlayPauseIcon(true);
            }
        }

        // ホバー中の位置に対応する時刻をポップアップでプレビュー表示する（MainWindow本体と同等）
        private void AccelChart_HoverTimeChanged(TimeSpan? time, double x)
        {
            if (time == null || !PlayerFront.NaturalDuration.HasTimeSpan)
            {
                SeekPreviewPopup.IsOpen = false;
                return;
            }

            string? markerLabel = AccelChart.FindMarkerLabelAt(x);
            SeekPreviewText.Text = time.Value.ToString(@"hh\:mm\:ss") + (markerLabel != null ? "  " + markerLabel : "");
            SeekPreviewPopup.IsOpen = true;
            var popupChild = (FrameworkElement)SeekPreviewPopup.Child;
            popupChild.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double halfW = popupChild.DesiredSize.Width / 2;
            double width = AccelChart.ActualWidth;
            SeekPreviewPopup.HorizontalOffset = Math.Clamp(x - halfW, 0, Math.Max(0, width - halfW * 2));
        }

        // ---- シークバーのイベントマーカー（トンネル・SA/PA・Gセンサー急変点） ----
        //  ファイルを開いた時(MediaOpened)と、地図情報の更新時(MapInfoProvider.ProjectionUpdated)に
        //  バックグラウンドスレッドで一括算出し、再生中のフレームごとの計算は行わない。
        //  EventMarkersは一覧パネル等へそのままデータバインドできる（UIスレッドでのみ更新される）。

        private int _eventMarkerGen;
        private double _gSensorEventThresholdG = 0.35;

        /// <summary>現在のファイルのイベント一覧（再生位置順）。UIスレッドでのみ更新される。</summary>
        public ObservableCollection<DashcamEventMarker> EventMarkers { get; } = new();

        /// <summary>Gセンサー急変点とみなす閾値(G、既定0.35)。永続化対象（AppSettings.DashcamGSensorEventThresholdG）。
        /// 変更すると開いているファイルのマーカーを作り直す。</summary>
        public double GSensorEventThresholdG
        {
            get => _gSensorEventThresholdG;
            set
            {
                double v = double.IsNaN(value) ? 0.35 : Math.Clamp(value, 0.05, 3.0);
                if (Math.Abs(v - _gSensorEventThresholdG) < 1e-9) return;
                _gSensorEventThresholdG = v;
                SyncGSensorThresholdCombo();
                EventList.Clear(); // 一覧の中の旧閾値で出した結果を捨て、新しい閾値で作り直す
                if (_eventIndexWanted) { _eventIndexStarted = false; StartEventIndex(); }
                if (_sensorFrames.Count > 0) RebuildEventMarkersAsync();
            }
        }

        /// <summary>プルダウンの選択を現在の閾値に合わせる（選択肢に無い値[設定ファイルの手書き等]なら未選択にする）。</summary>
        private void SyncGSensorThresholdCombo()
        {
            ComboBoxItem? match = null;
            foreach (ComboBoxItem item in GSensorThresholdCombo.Items)
            {
                if (double.TryParse((string)item.Tag, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var g)
                    && Math.Abs(g - _gSensorEventThresholdG) < 1e-6) { match = item; break; }
            }
            GSensorThresholdCombo.SelectedItem = match;
        }

        private void GSensorThresholdCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (GSensorThresholdCombo.SelectedItem is not ComboBoxItem item
                || !double.TryParse((string)item.Tag, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var g)) return;
            GSensorEventThresholdG = g;
        }

        private void ClearEventMarkers()
        {
            _eventMarkerGen++; // 算出中の古い結果を捨てる
            EventMarkers.Clear();
            AccelChart.SetEventMarkers(null);
        }

        private async void RebuildEventMarkersAsync(TimeSpan? durationOverride = null)
        {
            var frames = _sensorFrames;
            var group = _currentFrontGroup;
            TimeSpan duration = durationOverride
                ?? (PlayerFront.NaturalDuration.HasTimeSpan ? PlayerFront.NaturalDuration.TimeSpan : TimeSpan.Zero);
            if (frames.Count == 0 || duration <= TimeSpan.Zero) return;

            int gen = ++_eventMarkerGen;
            double threshold = _gSensorEventThresholdG;
            try
            {
                var list = await Task.Run(() =>
                {
                    var all = new List<DashcamEventMarker>();
                    all.AddRange(DashcamEventAnalyzer.AnalyzeGSensor(frames, threshold));
                    var map = _mapInfoProvider.BuildMapEventMarkers(frames, duration);
                    all.AddRange(map);
                    all.AddRange(DashcamEventAnalyzer.AnalyzeGpsLoss(frames, duration, map));
                    all.Sort((a, b) => a.VideoOffsetSeconds.CompareTo(b.VideoOffsetSeconds));
                    return all;
                });
                if (gen != _eventMarkerGen) return; // 待機中に別ファイルへ切り替わった／再算出された

                EventMarkers.Clear();
                foreach (var m in list) EventMarkers.Add(m);
                AccelChart.SetEventMarkers(list);

                // 情報一覧のイベントタブへも反映する。動画長・地図情報込みの精密な結果なので、
                // NMEAだけから出した概算（このファイル分）を置き換える。
                string? path = group?.FrontVideoPath ?? group?.RearVideoPath;
                if (group != null && path != null)
                {
                    EventList.UpdateFile(group.TimestampKey,
                        DashcamEventIndexer.FromMarkers(group.TimestampKey, path, group.Timestamp, list, duration.TotalSeconds, precise: true),
                        precise: true);
                }
            }
            catch (Exception ex)
            {
                DashcamDebugLog.Log($"[EventMarker] 生成失敗: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---- 情報一覧「イベント」タブ（走行ファイル全体のイベント一覧・ジャンプ） ----
        //  ViewModel(EventList)をタブのDataContextに渡す。索引は、タブを開いた時にEnsureEventIndex()で
        //  1回だけ始める（ファイルを開かずNMEAのみ読む軽い処理。再生中のI/Oへ極力影響させない）。

        /// <summary>イベントタブのViewModel（UIスレッド専用）。</summary>
        public DashcamEventListViewModel EventList { get; } = new();

        private readonly DashcamEventIndexer _eventIndexer = new();
        private System.Threading.CancellationTokenSource? _eventIndexCts;
        private bool _eventIndexWanted;   // イベントタブが一度でも開かれた（以後、ドライブ切替時も自動で再索引する）
        private bool _eventIndexStarted;
        private string? _eventScanKey;

        /// <summary>イベントタブを開いた時に呼ぶ。現在のドライブ/フォルダ全体の索引を（未実行なら）始める。</summary>
        public void EnsureEventIndex()
        {
            _eventIndexWanted = true;
            if (!_eventIndexStarted) StartEventIndex();
        }

        private void CancelEventIndex()
        {
            _eventIndexCts?.Cancel();
            _eventIndexCts = null;
        }

        private void StartEventIndex()
        {
            CancelEventIndex();
            if (_frontGroups.Count == 0) return; // まだスキャン前。スキャン完了時(ScanDrive)に改めて開始される

            var cts = new System.Threading.CancellationTokenSource();
            _eventIndexCts = cts;
            _eventIndexStarted = true;

            var inputs = _frontGroups
                .Select(g => new DashcamEventIndexer.GroupInput(g.TimestampKey, g.FrontVideoPath, g.RearVideoPath,
                    g.FrontNmeaPath ?? g.RearNmeaPath, g.Timestamp))
                .ToList();
            EventList.SetIndexProgress(0, inputs.Count);

            _ = _eventIndexer.IndexAsync(inputs, _gSensorEventThresholdG,
                files => Dispatcher.BeginInvoke(new Action(() => { if (!cts.IsCancellationRequested) EventList.UpdateFiles(files); })),
                (done, total) => Dispatcher.BeginInvoke(new Action(() => { if (!cts.IsCancellationRequested) EventList.SetIndexProgress(done, total); })),
                cts.Token)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        DashcamDebugLog.Log($"[EventIndex] 索引に失敗: {t.Exception?.GetBaseException().Message}");
                }, TaskScheduler.Default);
        }

        /// <summary>
        /// イベント一覧の行から、該当ファイルの「発生位置 − プレロール秒」へシークして再生を始める。
        /// 再生中のファイルと同じなら、その場でシーク。別ファイルなら、レジューム再生と同じ経路
        /// （読み込み完了時に目標位置へシークしてから再生）で切り替える。
        /// 見つからない/読めないファイルはfalse（一覧側が状態表示へ出す）。
        /// </summary>
        private async Task<bool> JumpToEventAsync(EventItemViewModel item, double prerollSeconds)
        {
            var group = _frontGroups.FirstOrDefault(g => g.TimestampKey == item.GroupKey);
            if (group == null || group.FrontVideoPath == null || !File.Exists(group.FrontVideoPath))
            {
                DashcamPlayErrorLogger.Log($"[EventJump] ファイルが見つからない: {item.GroupKey} {item.FilePath}");
                return false;
            }

            double target = Math.Max(0.0, item.OffsetSeconds - Math.Max(0.0, prerollSeconds));

            if (ReferenceEquals(group, _currentFrontGroup) && PlayerFront.NaturalDuration.HasTimeSpan)
            {
                // 同じファイル内のシーク。範囲外（ファイル末尾を超える等）は末尾の手前へ丸める。
                var dur = PlayerFront.NaturalDuration.TimeSpan;
                var t = TimeSpan.FromSeconds(Math.Min(target, Math.Max(0.0, dur.TotalSeconds - 1.0)));
                _wantsPlaying = true;
                AccelChart.SetPlayhead(t);
                await PlayerFront.StepToVideoOnlyAsync(t, timeoutMs: 3000);
                await SeekRearToFrontPositionAsync(t);
                PlayerFront.Play();
                if (HasActiveRear()) PlayerRear.Play();
                _isPlaying = true;
                SetPlayPauseIcon(true);
                return true;
            }

            // 別ファイル: MediaOpenedで_pendingResumeSecondsの位置へシークしてから再生が始まる。
            // SetListSelectionは選択イベントを抑止するので、リスト側が二重に再生を始めることはない。
            _pendingResumeSeconds = target;
            SetListSelection(FrontList, group, scrollToTop: true);
            PlayFrontGroup(group);
            return true;
        }

        // ---- フルスクリーン（MainWindowから切替。プレイヤーは同一インスタンスのまま表示だけ切り替える）----
        //  ・ツールバー/左サイドバー(リスト)を隠し、映像を全面に広げる。
        //  ・右サイドバー(日時・緯度経度＋走行軌跡マップ)は、車速OSDの下・チャートの上の右端へ
        //    半透明のオーバーレイとして重ねる（Mキー[MainWindow側]で表示/非表示を切替）。
        //  ・加速度チャート(＋速度/加速度HUD)は映像下部へ半透明で重ねる（常時表示）。
        //  ・再生コントロールバーはマウスを動かすと現れ、再生中に無操作が続くと隠れる（一時停止中は出しっぱなし）。

        private bool _isFullScreen;
        private DispatcherTimer? _fsControlsTimer;
        private Point _fsLastMousePos;
        private long _fsLastActivityTick;
        private double _fsControlsHideDelaySec = 2.5;
        private Brush? _fsSavedControlBarBackground;
        private bool _fsMapOverlayVisible = true;

        public bool IsFullScreen => _isFullScreen;

        /// <summary>再生/一時停止のトグル（フルスクリーン中のSpaceキー等、外部から呼ぶ用）。</summary>
        public void TogglePlayPause()
        {
            if (PlayerFront.Source == null) return;
            PlayPauseButton_Click(this, new RoutedEventArgs());
        }

        public void SetFullScreen(bool on, double controlsHideDelaySec = 2.5)
        {
            if (_isFullScreen == on) return;
            _isFullScreen = on;

            if (on)
            {
                _fsControlsHideDelaySec = controlsHideDelaySec > 0 ? controlsHideDelaySec : 2.5;

                CloseAllPopovers(); // ツールバーごと隠れるので、開いているポップオーバーも閉じる
                ToolbarBar.Visibility = Visibility.Collapsed;
                LeftSidebarHost.Visibility = Visibility.Collapsed;
                LeftSidebarColumn.Width = new GridLength(0);
                // 右サイドバー列は0幅にし、サイドバー本体は映像列(1)の右上へオーバーレイとして移す
                RightSidebarColumnDef.Width = new GridLength(0);
                Grid.SetColumn(RightSidebarHost, 1);
                Panel.SetZIndex(RightSidebarHost, 60);
                RightSidebarHost.HorizontalAlignment = HorizontalAlignment.Right;
                RightSidebarHost.VerticalAlignment = VerticalAlignment.Top;
                RightSidebarHost.Opacity = 0.8;

                // 映像を全行にまたがらせ、下段(コントロールバー/チャート)を映像の上へ重ねる
                Grid.SetRow(MainRowGrid, 0);
                Grid.SetRowSpan(MainRowGrid, 4);

                _fsSavedControlBarBackground = ControlBar.Background;
                ControlBar.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xB0, 0x0F, 0x16, 0x23));
                ChartStrip.Opacity = 0.75;

                _fsLastMousePos = System.Windows.Input.Mouse.GetPosition(this);
                _fsLastActivityTick = Environment.TickCount64;
                ControlBar.Visibility = Visibility.Visible;

                UpdateFullScreenMapOverlay(); // 内部でMapInfoの下部インセットも合わせて更新する

                PreviewMouseMove += FullScreen_PreviewMouseMove;
                _fsControlsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _fsControlsTimer.Tick += FullScreenControlsTimer_Tick;
                _fsControlsTimer.Start();
            }
            else
            {
                PreviewMouseMove -= FullScreen_PreviewMouseMove;
                _fsControlsTimer?.Stop();
                _fsControlsTimer = null;

                ToolbarBar.Visibility = Visibility.Visible;
                LeftSidebarHost.Visibility = Visibility.Visible;
                LeftSidebarColumn.Width = new GridLength(16);
                LeftSidebarHost_MouseLeave(this, null!); // 折りたたみ状態(幅16px)へ戻す
                // オーバーレイ化していた右サイドバーを通常の右列へ戻す
                Grid.SetColumn(RightSidebarHost, 2);
                Panel.SetZIndex(RightSidebarHost, 0);
                RightSidebarHost.ClearValue(FrameworkElement.WidthProperty);
                RightSidebarHost.ClearValue(FrameworkElement.HeightProperty);
                RightSidebarHost.ClearValue(FrameworkElement.MarginProperty);
                RightSidebarHost.HorizontalAlignment = HorizontalAlignment.Stretch;
                RightSidebarHost.VerticalAlignment = VerticalAlignment.Stretch;
                RightSidebarHost.Opacity = 1.0;
                RightSidebarHost.Visibility = Visibility.Visible;
                RightSidebarColumnDef.Width = new GridLength(_mapHorizontal ? 420 : 260);

                Grid.SetRow(MainRowGrid, 1);
                Grid.SetRowSpan(MainRowGrid, 1);

                ControlBar.Visibility = Visibility.Visible;
                if (_fsSavedControlBarBackground != null)
                    ControlBar.Background = _fsSavedControlBarBackground;
                ChartStrip.Opacity = 1.0;
                MapInfo.SetFullScreenBottomInset(0); // 通常表示に戻るので下部インセットは不要
            }
        }

        /// <summary>全画面中の地図・日時オーバーレイの表示/非表示を切り替える。</summary>
        public void ToggleFullScreenMapOverlay()
        {
            _fsMapOverlayVisible = !_fsMapOverlayVisible;
            UpdateFullScreenMapOverlay();
        }

        /// <summary>全画面中の右サイドバー(日時・緯度経度＋地図)オーバーレイの位置とサイズを更新する。
        /// 上端は車速OSDの下、下端は再生コントロールバーとチャートの上に収める。</summary>
        private void UpdateFullScreenMapOverlay()
        {
            if (!_isFullScreen) return;

            // 地図情報通知(MapInfo)は「地図・日時オーバーレイ(Mキー)」の表示/非表示とは独立して
            // 常時出しうるため、この計算はRightSidebarHostの表示状態に関わらず毎回行う。
            double bottomReserve = ChartStrip.ActualHeight + 56 + 16; // チャート＋コントロールバー＋余白
            MapInfo.SetFullScreenBottomInset(bottomReserve);

            if (!_fsMapOverlayVisible)
            {
                RightSidebarHost.Visibility = Visibility.Collapsed;
                return;
            }

            double width = _mapHorizontal ? 420 : 260;
            double top = SpeedOsdEnabled && SpeedOsd != null
                ? 10 + SpeedOsd.Height + 8 // OSD(数字の高さ＋余白)の下
                : 12;
            double available = MainRowGrid.ActualHeight - top - bottomReserve;
            double height = Math.Clamp(width * 1.25, 200, Math.Max(200, available));

            RightSidebarHost.Width = width;
            RightSidebarHost.Height = height;
            RightSidebarHost.Margin = new Thickness(0, top, 12, 0);
            RightSidebarHost.Visibility = Visibility.Visible;
        }

        private void FullScreen_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // カーソルを隠した/レイアウト変化で発生する「位置が変わらない合成MouseMove」は活動とみなさない
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _fsLastMousePos.X) < 2 && Math.Abs(p.Y - _fsLastMousePos.Y) < 2) return;
            _fsLastMousePos = p;
            _fsLastActivityTick = Environment.TickCount64;
            ControlBar.Visibility = Visibility.Visible;
        }

        private void FullScreenControlsTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isFullScreen) return;

            // 一時停止中・操作中(マウスがバー上/ドラッグ中)は出しっぱなし
            if (!_isPlaying || ControlBar.IsMouseOver || System.Windows.Input.Mouse.Captured != null)
            {
                ControlBar.Visibility = Visibility.Visible;
                return;
            }

            if (Environment.TickCount64 - _fsLastActivityTick >= (long)(_fsControlsHideDelaySec * 1000))
                ControlBar.Visibility = Visibility.Collapsed;
        }

        // ---- 測位ロスト区間の速度推定: 前後ファイルからの文脈取得 ----
        private const double NominalClipSeconds = 120; // 1ファイルの公称長(2分)

        private static bool AreConsecutiveClips(DashcamMediaGroup earlier, DashcamMediaGroup later)
        {
            var a = ParseFileStamp(earlier.FrontVideoPath) ?? earlier.Timestamp;
            var b = ParseFileStamp(later.FrontVideoPath) ?? later.Timestamp;
            if (a == null || b == null) return false;
            // 録画は2分ごとに連続する。大きく空いていれば別の走行（トンネルの連続とみなさない）
            return Math.Abs((b.Value - a.Value).TotalSeconds - NominalClipSeconds) <= 15;
        }

        private static List<DashcamSensorFrame> ParseNeighborFrames(DashcamMediaGroup g)
        {
            string? path = g.FrontNmeaPath ?? g.RearNmeaPath;
            return path == null
                ? new List<DashcamSensorFrame>()
                : NmeaSensorParser.Parse(path, g.Timestamp, TimeSpan.FromSeconds(NominalClipSeconds));
        }

        /// <summary>現在ファイルの先頭/末尾の測位ロスト区間が前後のファイルへ続いている場合に、その区間の直前/直後の
        /// 測位速度と、ファイル外で経過している秒数を求める（上限はNmeaSensorParser.MaxEstimateGapSeconds）。
        /// バックグラウンドスレッドから呼ぶ想定（UI要素には触れない）。</summary>
        private NmeaSensorParser.SpeedGapContext? BuildSpeedGapContext(DashcamMediaGroup group, bool needBefore, bool needAfter)
        {
            var groups = _frontGroups;
            int idx = groups.IndexOf(group);
            if (idx < 0)
            {
                string key = group.TimestampKey;
                idx = groups.FindIndex(g => g.TimestampKey == key);
            }
            if (idx < 0) return null;

            double maxGap = NmeaSensorParser.MaxEstimateGapSeconds;
            double? speedBefore = null, speedAfter = null;
            double gapBefore = 0, gapAfter = 0;

            if (needBefore)
            {
                double acc = 0;
                for (int j = idx - 1; j >= 0 && acc <= maxGap; j--)
                {
                    if (!AreConsecutiveClips(groups[j], groups[j + 1])) break;
                    var frames = ParseNeighborFrames(groups[j]);
                    int lastFix = frames.FindLastIndex(f => f.HasGpsFix);
                    if (lastFix >= 0)
                    {
                        speedBefore = frames[lastFix].SpeedKmh;
                        gapBefore = acc + (NominalClipSeconds - frames[lastFix].VideoOffset.TotalSeconds);
                        break;
                    }
                    acc += NominalClipSeconds; // このファイルは全体が測位ロスト
                }
            }

            if (needAfter)
            {
                double acc = 0;
                for (int j = idx + 1; j < groups.Count && acc <= maxGap; j++)
                {
                    if (!AreConsecutiveClips(groups[j - 1], groups[j])) break;
                    var frames = ParseNeighborFrames(groups[j]);
                    int firstFix = frames.FindIndex(f => f.HasGpsFix);
                    if (firstFix >= 0)
                    {
                        speedAfter = frames[firstFix].SpeedKmh;
                        gapAfter = acc + frames[firstFix].VideoOffset.TotalSeconds;
                        break;
                    }
                    acc += NominalClipSeconds;
                }
            }

            if (speedBefore == null && speedAfter == null) return null;
            return new NmeaSensorParser.SpeedGapContext(speedBefore, gapBefore, speedAfter, gapAfter);
        }

        // ---- 車速OSD ----

        // ── 地図情報通知（地名・SA/PA・トンネル） ──
        private readonly MapInfoProvider _mapInfoProvider = new();
        private CancellationTokenSource? _mapInfoLoadCts;

        private DashcamSensorFrame? _lastOsdFrame;

        private void SpeedOsdCheck_Changed(object sender, RoutedEventArgs e) => UpdateSpeedOsd(_lastOsdFrame);

        private void VideoArea_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSpeedOsdSize();
            UpdateFullScreenMapOverlay(); // 全画面中のみ動作（OSDの高さ確定後に配置を再計算）
        }

        /// <summary>OSDの高さ(＝7セグ数字の高さ)を映像エリアの高さの約8%に追従させる。
        /// 幅は縦横比を保ってViewboxが決めるため、ここでは高さだけ与える。</summary>
        private void UpdateSpeedOsdSize()
        {
            if (SpeedOsd == null || VideoArea == null) return;
            double h = VideoArea.ActualHeight;
            if (h <= 0) return;
            double size = Math.Max(30, h * 0.08);
            if (Math.Abs(SpeedOsd.Height - size) > 0.5)
                SpeedOsd.Height = size;
        }

        /// <summary>現在フレームの走行速度をOSDへ反映する。未測位(トンネル内等)で推定値も無い場合は全桁消灯、
        /// センサーデータが無い/OSDがOFFの間は非表示。</summary>
        private void UpdateSpeedOsd(DashcamSensorFrame? frame)
        {
            _lastOsdFrame = frame;
            if (SpeedOsd == null) return; // InitializeComponent中のCheckedイベント対策

            if (!SpeedOsdEnabled || frame == null)
            {
                SpeedOsd.Visibility = Visibility.Collapsed;
                return;
            }

            // 測位ロスト中は前後の測位速度から補間した推定値を「≈」付き・琥珀色で表示（実測の緑と区別）
            bool estimated = !frame.HasGpsFix && frame.SpeedEstimated;
            int? speed = (frame.HasGpsFix || estimated) ? (int)Math.Round(frame.SpeedKmh) : (int?)null;
            SpeedOsd.IsEstimated = estimated;
            SpeedOsd.Speed = speed; // 値が変わった桁だけ再描画される
            if (SpeedOsd.Visibility != Visibility.Visible)
            {
                UpdateSpeedOsdSize();
                SpeedOsd.Visibility = Visibility.Visible;
            }
        }

        // ---- HUD更新（Frontの実フレーム表示に同期。FrameDisplayedはAction<double>で秒単位pts） ----

        private double _lastDisplayedPts = -1; // 直近に実際に画面へ出たフレームの時刻(秒)。コマ送りの基準位置に使う

        private void OnFrontFrameDisplayed(double ptsSeconds)
        {
            _fpsFrameCount++;
            _lastDisplayedPts = ptsSeconds;
            // コマ送り以外の要因で位置が動いたら、コマ送りの論理位置を無効にして、次回は表示中のフレームから始め直す。
            // ・再生中のフレームが流れている　・コマ送りが終わって0.7秒以上たってから、位置が1.5秒以上ずれた（シーク/切替）
            if (_frameStepPosValid && !_frameStepBusy)
            {
                bool stepsEnded = GapMs(_lastStepEndTs) > 700;
                if (_isPlaying || (stepsEnded && Math.Abs(ptsSeconds - _frameStepPos.TotalSeconds) > 1.5))
                    _frameStepPosValid = false;
            }
            if (_gapPending)
            {
                _gapPending = false;
                DashcamDebugLog.Log($"[Gap] MediaEnded → 次ファイルの最初のフレーム表示まで {GapMs(_gapMediaEndedTs)}ms");
            }

            var pos = TimeSpan.FromSeconds(ptsSeconds);
            var frame = DashcamSensorLookup.FindNearest(_sensorFrames, pos);
            Hud.UpdateFrame(frame); // Hud側は速度・加速度3軸のみ表示する想定（日時/緯度経度は下記の地図上パネルへ分離）
            UpdateSpeedOsd(frame);
            _mapInfoProvider.UpdateFrame(frame); // 通信なし・ローカル計算のみ（Overpass取得はLoadMapInfoAsync側で1回だけ）
            MapInfo.Apply(_mapInfoProvider.State, _mapInfoProvider.ShouldShowOverlay);
            if (frame != null)
            {
                GeoDateTimeText.Text = frame.Timestamp.ToString("yyyy/MM/dd HH:mm:ss");
                GeoLatText.Text = frame.HasGpsFix ? frame.Latitude.ToString("F6") : "---";
                GeoLngText.Text = frame.HasGpsFix ? frame.Longitude.ToString("F6") : "---";
                MapView.SetCarPosition(frame.Latitude, frame.Longitude, frame.HasGpsFix);
            }
            // チャート本体はファイルを開いた時点で一度だけ計算済み（SetFullTrack）。
            // 毎フレームはプレイヘッド／シークバーの位置を動かすだけの軽量な処理にとどめる。
            // ドラッグ中はAccelChart側で既にマウス位置に応じた更新を行っているため、
            // ここからの上書きは行わない（せめぎ合い防止）。
            if (!_isDragging)
                AccelChart.SetPlayhead(pos);

            var duration = PlayerFront.NaturalDuration.HasTimeSpan ? PlayerFront.NaturalDuration.TimeSpan : TimeSpan.Zero;
            PositionText.Text = $"{pos:hh\\:mm\\:ss} / {duration:hh\\:mm\\:ss}";
        }

        // ---- Rearのドリフト追従・実測fps集計（1秒ごと） ----

        private void SyncTimer_Tick(object? sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            double elapsed = (now - _fpsWindowStart).TotalSeconds;
            if (elapsed >= 1.0)
            {
                double fps = _fpsFrameCount / elapsed;
                FpsText.Text = $"{fps:F0}fps";
                _fpsFrameCount = 0;
                _fpsWindowStart = now;
            }

            if (RearLinked && !_isDragging)
                ReconcileRear(); // Frontの現在時刻に対してリアclipを切り替える/外す

            // 「情報一覧」ウィンドウの通知情報タブは、開いている間だけこの周期(500ms)で更新する。
            // 毎フレーム(OnFrontFrameDisplayed)から呼ぶとDataGridの再バインドが重くなるため。
            _pairListWindow?.UpdateDebugSnapshot(_mapInfoProvider.GetDebugSnapshot(), _mapInfoProvider.LastFetchErrorSummary);

            bool timeAligned = UseTimeAlignedRear && _currentRearClipPath != null;
            bool rearReady = timeAligned ? _rearAvailable : HasActiveRear();
            if (!RearLinked || !rearReady || _isDragging)
            {
                _rearSettleSamplePending = false;
                return;
            }

            // 時刻ベース: リアの目標位置 = (Front開始時刻+Front位置+手動補正) − リアclip開始時刻。
            // フォールバック時は従来どおり「同名ペア＝同時開始」としてFront位置(+手動補正)に合わせる。
            // 【今回追加】RearTimeOffsetSpanの加算はFront/Rearの時刻ズレ手動補正機能のため。
            TimeSpan desiredRearPos = timeAligned
                ? (_frontStart!.Value + PlayerFront.Position + RearTimeOffsetSpan) - _currentRearClipStart
                : PlayerFront.Position + RearTimeOffsetSpan;
            var diff = desiredRearPos - PlayerRear.Position; // 正=リアが遅れている

            // 再同期の約1.5秒後に残っている遅れを、シーク所要時間ぶんの遅れとして学習し
            // 次回のシーク先へ先乗せする（一時停止中や不安定な状態は学習しない）。
            if (_rearSettleSamplePending && _rearResyncWatch.ElapsedMilliseconds >= 1500)
            {
                _rearSettleSamplePending = false;
                if (_isPlaying)
                {
                    var lead = _rearSeekLead + diff;
                    _rearSeekLead = lead < TimeSpan.Zero ? TimeSpan.Zero : (lead > MaxRearLead ? MaxRearLead : lead);
                }
            }

            bool cooledDown = !_rearResyncWatch.IsRunning || _rearResyncWatch.Elapsed >= ResyncCooldown;
            if (RearLinked && cooledDown && diff.Duration() > ResyncThreshold)
            {
                var target = desiredRearPos + _rearSeekLead;
                if (PlayerRear.NaturalDuration.HasTimeSpan && target > PlayerRear.NaturalDuration.TimeSpan)
                    target = PlayerRear.NaturalDuration.TimeSpan;
                DashcamDebugLog.Log($"[RearResync] diff={diff.TotalSeconds:F2}s → Rear位置={target.TotalSeconds:F2}s");
                PlayerRear.Position = target;
                _rearResyncWatch.Restart();
                _rearSettleSamplePending = true;
            }
        }

        // ---- 走行軌跡マップ: 前後数ファイル分をつないだ連続した経路として表示する ----
        // NMEAは1ファイル数十KB程度と極めて軽量なため、動画本体(1ファイル150MB級)とは違い
        // 大量にまとめて読み直しても実質コストが無い。前後の範囲は「10ファイルや20ファイルは
        // 楽勝か」というご質問への回答を兼ねて、前後10ファイルずつ（現在込みで最大21ファイル分）
        // まで広げている。必要であればもっと増やしても問題ない。
        private const int MapBufferPrevFiles = 10;
        private const int MapBufferNextFiles = 10;

        private async Task RefreshMapBufferAsync(DashcamMediaGroup currentGroup)
        {
            int idx = _frontGroups.IndexOf(currentGroup);
            if (idx < 0)
            {
                MapView.SetRoute(new List<MapSegment>());
                return;
            }

            int prevCount = Math.Min(MapBufferPrevFiles, idx);
            int nextCount = Math.Min(MapBufferNextFiles, _frontGroups.Count - 1 - idx);
            int startIdx = idx - prevCount;
            int endIdx = idx + nextCount;

            var targetGroup = currentGroup;
            var merged = new List<DashcamSensorFrame>();
            TimeSpan cursor = TimeSpan.Zero;

            for (int i = startIdx; i <= endIdx; i++)
            {
                var g = _frontGroups[i];
                string? nmea = g.FrontNmeaPath ?? g.RearNmeaPath;
                if (nmea == null) continue;

                TimeSpan? durationHint = (i == idx && PlayerFront.NaturalDuration.HasTimeSpan)
                    ? PlayerFront.NaturalDuration.TimeSpan
                    : null;

                var frames = await Task.Run(() => NmeaSensorParser.Parse(nmea, g.Timestamp, durationHint));
                foreach (var f in frames)
                    merged.Add(f with { VideoOffset = cursor + f.VideoOffset });

                if (frames.Count > 0)
                    cursor += frames[^1].VideoOffset + TimeSpan.FromSeconds(1);
            }

            if (!ReferenceEquals(_currentFrontGroup, targetGroup))
                return;

            // 進行方位から縦長/横長を自動判定する（南北方向の移動量 dLat と、経度差を緯度で
            // 補正した東西方向相当の移動量 dLng を比較。dLngの方が大きければ横長を推奨）。
            // 有効なGPS測位点が2点未満の場合は前回の判定を維持する（無理に判定しない）。
            var validPoints = merged.Where(f => f.HasGpsFix).ToList();
            if (validPoints.Count >= 2)
            {
                double minLat = validPoints.Min(p => p.Latitude), maxLat = validPoints.Max(p => p.Latitude);
                double minLng = validPoints.Min(p => p.Longitude), maxLng = validPoints.Max(p => p.Longitude);
                double dLat = maxLat - minLat;
                double lat0 = (minLat + maxLat) / 2.0;
                double dLng = (maxLng - minLng) * Math.Cos(lat0 * Math.PI / 180.0);
                _lastAutoHorizontal = dLng > dLat;
                UpdateEffectiveMapOrientation();
            }

            MapView.SetRoute(DashcamMapPointBuilder.BuildSegments(merged));

            // 地図情報通知（地名・SA/PA・トンネル）用にOverpassへ1回だけ問い合わせる。
            // 再生自体をブロックしないようfire-and-forgetにし、切替が連続した場合は前回分をキャンセルする。
            _mapInfoLoadCts?.Cancel();
            var mapInfoCts = new CancellationTokenSource();
            _mapInfoLoadCts = mapInfoCts;
            _ = LoadMapInfoAsync(merged, mapInfoCts);
        }

        /// <summary>RefreshMapBufferAsyncが確定させたルートに対し、Overpass APIへ1回だけ問い合わせる。
        /// ファイル切替や連続シークでキャンセルされた場合はOperationCanceledExceptionを静かに無視する
        /// （MapInfoProvider.LoadRouteAsync自体はネットワークエラー時も例外を投げない設計だが、
        /// キャンセルはCancellationTokenの仕組み上例外として届くため、ここでだけ吸収する）。</summary>
        private async Task LoadMapInfoAsync(List<DashcamSensorFrame> merged, CancellationTokenSource cts)
        {
            try
            {
                await _mapInfoProvider.LoadRouteAsync(merged, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // ---- 地図の縦長/横長切替（自動判定＋手動上書き。Hudの位置には一切影響しない） ----

        /// <summary>
        /// 地図の縦長/横長を、ユーザー選択(MapView.OrientationMode)とルート方位の自動判定結果
        /// (_lastAutoHorizontal)を総合して適用する。手動でVertical/Horizontalを選んでいる間は
        /// 自動判定結果を無視し固定表示にする。
        /// </summary>
        private void UpdateEffectiveMapOrientation()
        {
            bool horizontal = MapView.OrientationMode switch
            {
                DashcamMapOrientation.Horizontal => true,
                DashcamMapOrientation.Vertical => false,
                _ => _lastAutoHorizontal
            };
            ApplyMapOrientation(horizontal);
        }

        /// <summary>
        /// 実際のレイアウト切替本体。センサー情報(Hud)は常にAccelChartの右隣に固定表示のため
        /// ここでは一切動かさない。地図が占める右サイドバーの列幅だけを調整する
        /// （縦長ルート想定=狭め／横長ルート想定=広め）。
        /// </summary>
        private void ApplyMapOrientation(bool horizontal)
        {
            if (_mapHorizontal == horizontal) return;
            _mapHorizontal = horizontal;
            if (_isFullScreen) { UpdateFullScreenMapOverlay(); return; } // 全画面中は列幅を触らずオーバーレイの幅だけ更新。復帰時に_mapHorizontalから戻す
            RightSidebarColumnDef.Width = new GridLength(horizontal ? 420 : 260);

            // 【今回追加】この列幅変更はNonVideoWidthを増減させるが、以前はここでウィンドウの
            // 再フィットが一切トリガーされていなかった。RequestWindowFit（延いては動画の縦横比を
            // 保った黒帯なし表示）はMediaOpened時にしか発火しないため、再生中に地図の縦長/横長を
            // 切り替えると、ウィンドウサイズは変わらないまま動画エリアだけが160px分縮む/広がる形に
            // なり、縦横比が崩れて黒帯が出ていた（次ファイルに切り替わるとMediaOpened経由で
            // 再フィットがかかり直るため、その時だけ直って見えていた）。
            // UpdateLayout()でColumnDefinitionの変更を即座に反映させてから
            // （そうしないとRequestWindowFitが変更前の古いNonVideoWidthを使ってしまう）、
            // 明示的に再フィットをトリガーする。
            this.UpdateLayout();
            if (PlayerFront.NaturalVideoWidth > 0 && PlayerFront.NaturalVideoHeight > 0)
                RequestWindowFit?.Invoke(PlayerFront.NaturalVideoWidth * ZoomScale, PlayerFront.NaturalVideoHeight * ZoomScale);
        }

        // ---- 次ファイルの先読み（OSファイルキャッシュ温め） ----
        private void SchedulePrefetchIfNeeded()
        {
            if (_currentFrontGroup == null) return;
            int idx = _frontGroups.IndexOf(_currentFrontGroup);
            if (idx < 0) return;

            var toPrefetch = new List<string>();
            for (int off = 1; off <= 2 && idx + off < _frontGroups.Count; off++)
            {
                var g = _frontGroups[idx + off];
                if (g.FrontVideoPath != null) toPrefetch.Add(g.FrontVideoPath);
                if (RearLinked && g.RearVideoPath != null) toPrefetch.Add(g.RearVideoPath);
            }

            foreach (var path in toPrefetch)
            {
                if (_prefetchedPaths.Contains(path)) continue;
                _prefetchedPaths.Add(path);
                string p = path;
                _ = Task.Run(() => WarmFileCache(p));
            }

            if (_prefetchedPaths.Count > 12) _prefetchedPaths.Clear();
        }

        private static void WarmFileCache(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
                var buffer = new byte[1 << 20];
                while (fs.Read(buffer, 0, buffer.Length) > 0) { }
            }
            catch { }
        }

        // ---- レジューム再生 ----
        public async Task<bool> TryResumeAsync(string? drivePath, string? groupKey, double positionSeconds, string? eventFolderName = null)
        {
            if (string.IsNullOrEmpty(drivePath) || string.IsNullOrEmpty(groupKey))
                return false;

            _isResuming = true;
            try
            {
                if (!string.IsNullOrEmpty(eventFolderName) && Enum.TryParse<DashcamEventFolder>(eventFolderName, out var folder))
                {
                    foreach (var obj in EventFolderCombo.Items)
                    {
                        if (obj is ComboBoxItem ci && (string)ci.Tag == folder.ToString())
                        {
                            EventFolderCombo.SelectedItem = ci;
                            break;
                        }
                    }
                }

                RefreshDriveList();
                var options = DriveCombo.ItemsSource as IEnumerable<DriveOrFolderOption>;
                var drive = options?.FirstOrDefault(o => !o.IsBrowseOption && string.Equals(o.RootPath, drivePath, StringComparison.OrdinalIgnoreCase));

                if (drive == null && Directory.Exists(drivePath))
                {
                    // ドライブレターとしては見つからない（例: フォルダコピー運用）が、パス自体は存在する場合
                    drive = new DriveOrFolderOption { DisplayName = drivePath, RootPath = drivePath };
                    var list = new List<DriveOrFolderOption>(options ?? Enumerable.Empty<DriveOrFolderOption>());
                    list.Insert(Math.Max(0, list.Count - 1), drive);
                    DriveCombo.ItemsSource = list;
                }

                if (drive == null)
                    return false;

                DriveCombo.SelectedItem = drive;
                ScanDrive(drive.RootPath!, CurrentEventFolder);

                var group = _frontGroups.FirstOrDefault(g => g.TimestampKey == groupKey);
                if (group == null)
                    return false;

                _pendingResumeSeconds = positionSeconds;
                SetListSelection(FrontList, group, scrollToTop: true);
                PlayFrontGroup(group);

                await Task.CompletedTask;
                return true;
            }
            finally
            {
                _isResuming = false;
            }
        }
        /// <summary>
        /// 起動時にコマンドライン引数でフォルダが渡された場合の直接読み込み。
        /// レジューム(ドライブ/ファイル選択/再生位置の復元)は一切行わず、指定フォルダを
        /// ドライブ一覧に追加してスキャンするだけに留める（ファイル選択・再生開始はユーザーに委ねる）。
        /// </summary>
        public void LoadFolderDirect(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return;

            RefreshDriveList();
            var options = DriveCombo.ItemsSource as IEnumerable<DriveOrFolderOption>;
            var existing = options?.FirstOrDefault(o => !o.IsBrowseOption && string.Equals(o.RootPath, folderPath, StringComparison.OrdinalIgnoreCase));

            DriveOrFolderOption target;
            if (existing != null)
            {
                target = existing;
            }
            else
            {
                target = new DriveOrFolderOption { DisplayName = folderPath, RootPath = folderPath };
                var list = new List<DriveOrFolderOption>(options ?? Enumerable.Empty<DriveOrFolderOption>());
                list.Insert(Math.Max(0, list.Count - 1), target);
                DriveCombo.ItemsSource = list;
            }

            // DriveCombo_SelectionChanged経由でRescanCurrentSelection→ScanDriveが走り、
            // フロント/リアリストが表示される（ファイルの自動選択・再生は行わない）。
            DriveCombo.SelectedItem = target;
        }

    }
}
