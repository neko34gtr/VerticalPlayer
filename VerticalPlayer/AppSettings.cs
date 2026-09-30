using System.Collections.Generic;

namespace VerticalPlayer
{
    // ─────────────────────────────────────────────────────────────────────────
    // データモデル
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>エフェクトプリセット 1 件分</summary>
    public class PresetSettings
    {
        public string Name { get; set; } = "Preset";
        public double Contrast { get; set; }
        public double Saturation { get; set; }
        public double Gamma { get; set; }
    }

    /// <summary>アプリ全設定（起動・終了時に JSON 保存）</summary>
    public class AppSettings
    {
        // ── ウィンドウ ──
        public double WindowLeft { get; set; }
        public double WindowTop { get; set; }
        public double WindowWidth { get; set; } = 460;
        public double WindowHeight { get; set; } = 860;
        public bool AlwaysOnTop { get; set; }
        public bool AutoPlayNext { get; set; } = true;
        public bool FitWindowToVideo { get; set; } = true;
        public bool DnnFastBuild { get; set; } = false;

        // ── ドラレコモード ──
        public bool DashcamRearLinked { get; set; } = true;
        public bool DashcamRearVisible { get; set; } = true;
        public double DashcamZoomScale { get; set; } = 2.0;
        /// <summary>リア(PiP)の表示倍率。リア映像の等倍(オリジナル)を1.0とした縮小倍率。既定は最小(従来のPiP既定サイズ)。</summary>
        public double DashcamRearZoomScale { get; set; } = 0.115;
        /// <summary>リア(PiP)の位置。映像エリアの空き領域に対する比率0..1、-1=未設定(既定の左下)。</summary>
        public double DashcamRearPipX { get; set; } = -1;
        public double DashcamRearPipY { get; set; } = -1;

        // ── 再生まわりの追加機能 ──
        /// <summary>ドラレコモードの車速OSD（映像右上の走行速度表示）のON/OFF。</summary>
        public bool EnableOSD { get; set; } = true;
        public string MapInfoCorner { get; set; } = "BottomLeft"; // Dashcam.MapInfoCornerのenum名をそのまま保存
        /// <summary>スクリーンショットの保存形式（Media.ScreenshotFormatのenum名: Png/Jpg/WebP/Avif）。</summary>
        public string ScreenshotFormat { get; set; } = "Jpg";
        /// <summary>ドラレコのコマ送り1回あたりのコマ数（1/3/5/10/30）。</summary>
        public int DashcamFrameStepFrames { get; set; } = 3;
        public double MapInfoScale { get; set; } = 1.0;
        /// <summary>再生中、無操作でマウスカーソルを隠すまでの時間(秒)。0以下で無効。</summary>
        public double CursorHideDelaySec { get; set; } = 2.5;
        /// <summary>再生中のスリープ(画面オフ/システムスリープ)防止。</summary>
        public bool EnableSleepPrevention { get; set; } = true;
        /// <summary>測位ロスト区間(トンネル等)の速度を推定補間する最大の長さ(秒)。0以下で無効。
        /// 既定900秒＝日本最長の道路トンネル(山手トンネル約18.2km)を80km/hで走る約14分に余裕を持たせた値。</summary>
        public double SpeedEstimateMaxGapSec { get; set; } = 900;
        /// <summary>ログ(trace.log / play_error.txt)の出力先フォルダ。null/空＝自動（Xドライブ[RAMディスク]があれば
        /// X:\temp\VerticalPlayer、無ければ実行ファイル直下）。通常/ドラレコ両モード共通。</summary>
        public string? LogDirectory { get; set; }
        public double DashcamWindowWidth { get; set; }
        public double DashcamWindowHeight { get; set; }
        public bool DashcamWasActive { get; set; }
        /// <summary>trueの場合、起動時のレジューム自動再生（通常モードの前回ファイル復元、
        /// ドラレコモードのTryResumeAsync）を両方ともスキップする。ログ採取時など、
        /// 起動のたびに前回状態が勝手に再生されると検証の邪魔になるための逃げ道。
        /// 起動引数でファイル/フォルダが明示的に渡された場合はこの設定に関係なく従来通り開く。</summary>
        public bool DisableAutoResume { get; set; }
        public string? DashcamLastDrivePath { get; set; }
        public string? DashcamLastGroupKey { get; set; }
        public double DashcamLastPosition { get; set; }
        public string? DashcamLastEventFolder { get; set; }
        public string? MapInfoHighwayName { get; set; }
        public bool MapInfoIsOnExpressway { get; set; }
        public string? MapInfoCurrentLocationName { get; set; }
        /// <summary>ドラレコの地図情報通知：トンネル進入検出方式
        /// (VerticalPlayer.Dashcam.MapInfoProvider.TunnelEntryDetectionMode)のenum名をそのまま保存。
        /// 既定Legacy（従来方式）。地図情報通知はドラレコモード専用の機能のため、この設定項目もドラレコ専用。</summary>
        public string DashcamTunnelEntryDetectionMode { get; set; } = "Legacy";

        // ── 再生 ──
        public double Volume { get; set; } = 0.7;
        public bool IsMuted { get; set; }
        public double PlaybackSpeed { get; set; } = 1.0;
        public bool Loop { get; set; }
        public string? LastFilePath { get; set; }
        public double LastPosition { get; set; }   // 秒

        // ── 表示 ──
        public bool IsForceVertical { get; set; }
        public double Rotation { get; set; }
        public bool HwAccel { get; set; } = true;
        public string AudioBackend { get; set; } = "XAudio2"; // AudioBackendKindのenum名をそのまま保存
        // ❗【今回削除】PacketPrefetchは常時ON固定になったため設定から排除した。
        public bool ShowFpsCounter { get; set; } = true;

        /// <summary>TensorRTキャッシュ(trtcache)の永続バックアップ先。未指定(null/空)の場合は
        /// AVEngine.GetDefaultTrtCacheBackupDir()の既定パス（%LOCALAPPDATA%配下、ビルド構成に
        /// 依存しない固定パス）を使う。</summary>
        public string? TrtCacheBackupDir { get; set; }

        // ── エフェクト ──
        public double Contrast { get; set; }
        public double Saturation { get; set; }
        public double Gamma { get; set; }
        public double ZoomScaleX { get; set; } = 1.0;
        public double ZoomScaleY { get; set; } = 1.0;
        public bool Denoise { get; set; } = true;
        public bool DynamicContrast { get; set; } = true;
        public int CompareViewMode { get; set; }
        public float SuperResolutionScale { get; set; } = 1f;
        public bool DnnSuperResolution { get; set; }
        public bool DnnWaitForBuild { get; set; } = true;
        public string? DnnModelFileName { get; set; }
        public double SharpAmount { get; set; } = 0.5;
        public bool ColorMatrix601To709 { get; set; }

        // ── プリセット（複数） ──
        public List<PresetSettings> Presets { get; set; } = new();
    }

}
