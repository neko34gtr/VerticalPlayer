using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VerticalPlayer; // IAudioOutput / XAudio2AudioOutput

namespace VerticalPlayer.Media
{
    /// <summary>
    /// FFmpeg.AutoGen (v8.1.0 / shared dll) による「映像デコード専用」エンジン。
    /// 音声・シーク・速度制御・マスタークロックは上位の FfmpegMediaElement 側の
    /// 非表示 MediaElement に一本化されている（本クラスは映像デコード＋描画のみ担当）。
    ///
    /// 【今回追加】
    /// - ハードウェアデコード（D3D11VA）対応。要求時のみ有効化を試み、非対応環境や
    ///   失敗時は自動的にソフトウェアデコードへフォールバックする（例外にしない）。
    /// - 実際にどちらのモードで復号しているかを DecodeModeChanged イベントで通知。
    /// - コントラスト／彩度／ガンマをデコード後のBGRAバッファへCPUで直接適用
    ///   （WPFのEffectはMediaElement/Image双方で信頼性が低いため、生ピクセルに対して処理）。
    /// </summary>
    public sealed unsafe class AVEngine : IDisposable
    {
        public event Action<int, int, TimeSpan>? Opened;
        public event Action<Exception>? Failed;
        /// <summary>実際に使われたデコードモードを通知（例: "HW (D3D11VA)" / "SW"）。UIスレッドで発火。</summary>
        public event Action<string>? DecodeModeChanged;
        /// <summary>ファイルのチャプター開始秒（先頭0秒は除く）をUIスレッドで通知。</summary>
        public event Action<List<double>>? ChaptersLoaded;
        /// <summary>1フレームが実際にWritePixelsされた直後、そのフレームの再生時刻(秒)を伴って発火。UIスレッドで発火。</summary>
        public event Action<double>? FrameDisplayed;
        /// <summary>ファイル終端まで再生し終えたことを通知（UIスレッドで発火）。従来 _audio.MediaEnded
        /// が担っていた役割を、音声も含めて自前でデコードするようになった本クラス側で肩代わりする。</summary>
        public event Action? EndOfStream;

        private readonly Dispatcher _ui;
        private int _generation;

        // ── 音声出力（IAudioOutput経由）。従来は上位のFfmpegMediaElementが同じファイルを
        // WPF MediaElementで別途開いて音声再生していたが、SDカード等でのI/O競合が起動直後の
        // 無音・カクつきの根本原因だったため、AVEngine自身が音声もデコードしてここへ直接出力する。
        // 現在はXAudio2AudioOutput固定だが、将来WASAPI実装に差し替えられるようIAudioOutput
        // 経由でのみ扱う。出力フォーマットは常に48kHz/stereo/Float32に固定し、swresampleで
        // 変換する（デバイス側の対応フォーマットを気にしなくてよいようにするため）。
        private const int AudioOutSampleRate = 48000;
        private const int AudioOutChannels = 2;

        private Thread? _decodeThread;
        private volatile bool _paused = true;
        private volatile bool _audioDesired = true; // false時はPlay()中でも音声を出さない（コマ送り/プレビュー用）
        private double _volume = 1.0;
        private double _speedRatio = 1.0;
        /// <summary>音声ストリームの最初のフレームの実pts（秒）。AAC等はエンコーダ遅延で
        /// 最初のフレームのptsが0でないことがあり、これを無視して「最初に送ったサンプル=
        /// コンテンツ時刻0」として扱うと、音声だけ最初からその分オフセットしたまま
        /// 1倍速で進み続ける（内部クロックの整合性チェックには出ない、コンテンツそのものの
        /// ズレ）。AudioDecodeLoopが(再)開始時に最初のフレームのptsをここへ書き込み、
        /// メインループの再アンカー時にaudioOutput.GetPositionSeconds()へ加算する。</summary>
        private double _audioContentOffsetSeconds;

        /// <summary>音声ストリームを実際に開けているか（Open時にfalseへ戻し、音声デコードスレッド起動時にtrue）。</summary>
        private volatile bool _hasAudioStream;

        /// <summary>直近にデコードした（表示対象の）映像フレームのpts秒。映像のみ再生(Play(false))から
        /// 音声付き再生(Play(true))へ切り替える際、映像位置へ音声を揃え直すための再シーク先に使う。</summary>
        private double _lastShownPtsSeconds = -1;

        /// <summary>次にOpen()する際にハードウェアデコードを試みるかどうか。</summary>
        public bool HardwareAccelRequested { get; set; }

        // ── Stage1: パケット先読み（Demux/Decode分離パイプライン） ──
        // HDD等の低速ストレージでの av_read_frame の I/O 遅延（ディスクI/Oスパイク）が
        // そのままデコード全体・キャッチアップドロップに直結していた問題への対策。
        // 有効時は専用スレッドが av_read_frame をバックグラウンドで回してChannelへ溜め、
        // デコード側（既存のDecode/DNN/Presentロジックはそのまま）はChannelから取り出す
        // だけになる。HardwareAccelRequestedと同じ設計方針で、値は次にOpenする際にのみ
        // 反映される（再生中の動的切替はしない）。
        public bool PrefetchEnabled { get; set; }

        private const int PrefetchChannelCapacity = 80; // 60〜100パケット目安
        private Thread? _demuxThread;
        private Channel<DemuxedPacket>? _pktChannel;

        /// <summary>Seek要求中はtrue(非0)。pin留めしたintをAVIOInterruptCB.opaqueとして
        /// ネイティブ側へ渡し、av_read_frameが内部のI/O待ちで定期的にこれをポーリングする
        /// ことで、遅いディスクI/Oの最中でもSeekを即座に中断・脱出できるようにする。</summary>
        private volatile int _demuxInterruptFlag;
        /// <summary>Demuxスレッドが_demuxInterruptFlagを検知し、av_read_frame/av_seek_frame
        /// を同時に叩かない安全な待機状態に入ったことをメインスレッドへ知らせる確認応答。</summary>
        private volatile int _demuxAcked;

        /// <summary>Seek時、AudioDecodeLoopがactx（AVCodecContext*）へ同時アクセスしないよう
        /// 一時停止させるためのハンドシェイク。仕組みは_demuxInterruptFlag/_demuxAckedと同じ。</summary>
        private volatile int _audioSeekInterruptFlag;
        private volatile int _audioSeekAcked;

        /// <summary>Demuxスレッドが積んだ1パケット分のラッパー。AVPacket*はジェネリック型
        /// 引数に使えないためIntPtrで保持する。</summary>
        private readonly struct DemuxedPacket
        {
            public readonly IntPtr PktPtr;
            public DemuxedPacket(IntPtr pktPtr) => PktPtr = pktPtr;
        }

        // AVIOInterruptCB.callback に渡すネイティブ呼び出し可能コールバック。
        // 実機で入手した定義により確定: AVIOInterruptCB_callback_func は
        // 「AVIOInterruptCB_callback」という通常のdelegate型からの暗黙変換演算子
        // （Marshal.GetFunctionPointerForDelegateを内部で呼ぶ）を持つラッパー構造体。
        // そのため関数ポインタ(delegate* unmanaged)ではなく、AVCodecContext.get_format
        // と同じ「通常のdelegateインスタンスを保持し続ける」方式が正解だった。
        // NOTE: このdelegateインスタンス（_interruptCallback）はGCに回収されると
        // ネイティブ側が呼び出し不能なポインタを踏んでクラッシュするため、必ず
        // staticフィールドとして参照を保持し続けること（ローカル変数化は絶対に不可）。
        private static readonly AVIOInterruptCB_callback _interruptCallback = InterruptCheck;

        private static unsafe int InterruptCheck(void* opaque)
        {
            if (opaque == null) return 0;
            return *(int*)opaque != 0 ? 1 : 0;
        }

        // AVERROR_EXIT = -MKTAG('E','X','I','T')。ffmpeg.AVERROR_EXITの有無/名前が
        // AutoGenのバージョンに依存するため、確実にビルドできるようここで直接計算する。
        private const int AvErrorExit = -(('E') | ('X' << 8) | ('I' << 16) | ('T' << 24));

        // ── エフェクト（-1〜1想定。0が無効） ──
        private volatile bool _effectsActive;
        private double _contrast, _saturation, _gamma;
        private volatile bool _deinterlaceEnabled;

        /// <summary>ノイズリダクション（FFmpeg hqdn3dフィルタ、avfilter経由）の要求状態。
        /// 「高画質化エンジン設計提案」段階3。HardwareAccelRequestedと同じ設計方針：
        /// 値は次にOpenする際（＝ファイル再オープン時）にのみ反映され、再生中のスレッドの
        /// 途中で動的には切り替えない。動的トグルは、グラフ構築の一時失敗時に毎フレーム
        /// 再構築を試み続けて重くなる、OFFにした瞬間に描画が止まる、といった不安定さの
        /// 原因になっていたため、HW/SW切替と同じ「再オープンして反映」方式へ統一した。</summary>
        public bool DenoiseRequested { get; set; }

        /// <summary>ダイナミックコントラスト（段階4）の強さ。0〜1、0で無効。
        /// GPU側(Compute Shader)でのみ有効な機能で、値はGpuPresenterへそのまま転送するだけ。
        /// AVEngine自身はCPU側の対応する処理を持たない（設計提案書どおりGPU完結の機能）。</summary>
        public void SetDynamicContrast(float strength) => GpuPresenter?.SetDynamicContrast(strength);

        /// <summary>超解像（段階5）の拡大倍率。1.0以下で無効。GPU側のみで完結する機能。</summary>
        public void SetSuperResolution(float scale) => GpuPresenter?.SetSuperResolution(scale);

        // ── DNN超解像（段階6、TensorRT） ──
        private DnnSuperResolutionEngine? _dnnSr;
        private volatile bool _dnnSrEnabled;
        private Task? _dnnBuildTask;
        private int _dnnBuildW, _dnnBuildH;
        // DNN推論用のフレームコピー先バッファ（使い回し）。_dnnInferenceBusyにより、次回ここへ
        // 書き込むのは前回のTask.Runが完了（_dnnInferenceBusy=falseへ復帰）した後のみと保証
        // されているため、毎フレームClone()で新規配列を確保する必要がない（GC負荷削減）。
        private byte[]? _dnnFrameBuf;
        private volatile bool _dnnInferenceBusy; // 前フレームの推論(Task)がまだ完了していない

        /// <summary>DNN超解像エンジンのバックグラウンドビルド中/完了が変化した時に発火
        /// （UIスレッドにディスパッチ済み）。呼び出し側で進捗表示等に利用できる。</summary>
        public event Action<bool>? DnnBuildStateChanged;

        private static readonly string DnnModelsDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "models");
        private const string DnnTrtCacheDir = @"X:\Temp\VerticalPlayer\trtcache";

        /// <summary>trtcache（RAMディスク運用想定）の永続バックアップ先。
        /// 既定値は「%LOCALAPPDATA%\VerticalPlayer\trtcache_backup」というOS標準の
        /// 固定パスとした。以前はexe直下（AppDomain.CurrentDomain.BaseDirectory基準）
        /// だったため、Debug/Release等ビルド構成違いで出力フォルダが変わるたびに
        /// バックアップ先も分裂し、同じRAMディスクキャッシュに対して複数のバックアップが
        /// 無駄にストレージを消費する問題があった。設定パネルから上書きできるよう、
        /// 通常のプロパティ（setter付き）として公開する。</summary>
        public string TrtCacheBackupDir { get; set; } = GetDefaultTrtCacheBackupDir();

        /// <summary>TrtCacheBackupDirの既定値を返す（設定パネルの「既定に戻す」用に公開）。</summary>
        public static string GetDefaultTrtCacheBackupDir() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VerticalPlayer", "trtcache_backup");

        /// <summary>TensorRTタイミングキャッシュ（DnnSuperResolutionEngine.TimingCacheDir
        /// 参照）の保存先。GPU/ドライバに依存する小さな共有キャッシュのため、trtcache_backup
        /// と同様に永続領域(LocalApplicationData)に置き、再起動をまたいで使い回す。
        /// trtcache_backupと違いユーザーが変更する必要は薄いため、設定パネルには出さず
        /// 固定パスとする。</summary>
        public static string TrtTimingCacheDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VerticalPlayer", "trt_timing_cache");

        /// <summary>DnnSuperResolutionEngine.FastBuild参照。設定パネルのトグルから
        /// 設定される。既定false（従来の実行時性能優先ビルド）。</summary>
        public bool DnnFastBuild { get; set; }

        private string _dnnModelFileName = "4x-UltraSharpV2_Lite_fp16_op17.onnx"; // 既定値（後方互換）
        private int _dnnScale = 4;

        /// <summary>modelsフォルダ内の.onnxファイル名一覧（今後の軽量モデル差し替え用）。</summary>
        public static IEnumerable<string> ListAvailableDnnModels()
        {
            if (!Directory.Exists(DnnModelsDir)) return Array.Empty<string>();
            return Directory.GetFiles(DnnModelsDir, "*.onnx")
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Select(n => n!);
        }

        /// <summary>使用するDNNモデルのファイル名（modelsフォルダ内、拡張子込み）。
        /// 変更すると倍率をファイル名から再解析し、既存セッションは破棄する。DNN有効中の
        /// 場合は新モデル用のインスタンスをその場で作り直す（そうしないと以降デコード
        /// ループの_dnnSr!=null判定が常にfalseになりDNN分岐自体が丸ごと無効化されたままに
        /// なってしまう＝停止状態で切り替えても再起動するまで反映されない不具合の原因だった）。
        /// 実際のTensorRTビルドはEnsureEngine呼び出し元（デコードループの背景Task等）の
        /// タイミングに従うため、ここでは即座に重い処理は行わない。</summary>
        public string DnnModelFileName
        {
            get => _dnnModelFileName;
            set
            {
                if (_dnnModelFileName == value) return;
                _dnnModelFileName = value;
                _dnnScale = ParseScaleFromFileName(value);
                DiscardDnnEngineAndRebuildIfEnabled();
            }
        }

        /// <summary>現在のDnnSuperResolutionEngineインスタンスを手放し、DNN有効中なら
        /// 新しいインスタンスを作り直す。旧インスタンスのビルドが実行中の場合でも、
        /// TensorRTのネイティブビルド呼び出しは中断できないため、Dispose自体は
        /// 別スレッドへ逃がして完了を待たせる（呼び出し元のUI/デコードスレッドを
        /// フリーズさせないため）。新しいインスタンスは別ロックを持つため、旧ビルドの
        /// 完了を待たされずに独立してビルドを開始できる。
        /// モデル変更時、および新しいファイルを開いた時（旧ファイルのビルドが新ファイルの
        /// ビルドを数分間ブロックしてしまう不具合の対策）の両方から呼ぶ。</summary>
        private void DiscardDnnEngineAndRebuildIfEnabled()
        {
            var oldSr = _dnnSr;
            _dnnSr = null;
            if (oldSr != null)
                Task.Run(() => oldSr.Dispose());

            // デコードループの「この解像度は既にビルド開始済みか」判定(_dnnBuildW/H)は
            // 解像度のみを見ておりモデル/ファイルの違いを考慮しないため、ここで無効化
            // （-1にして必ず不一致にする）しないと新しいエンジンのビルドがトリガーされない。
            _dnnBuildW = -1;
            _dnnBuildH = -1;
            _dnnBuildTask = null;

            if (_dnnSrEnabled)
            {
                // 全解像度分をまとめて復元しておく（存在するものは上書きしない）。
                // 個別解像度単位の復元はBuildSessionOnce内でも安全網として行われる。
                DnnSuperResolutionEngine.RestoreAllCachesIfNeeded(DnnTrtCacheDir, TrtCacheBackupDir);
                _dnnSr = new DnnSuperResolutionEngine(Path.Combine(DnnModelsDir, _dnnModelFileName), DnnTrtCacheDir)
                {
                    BackupDir = TrtCacheBackupDir,
                    TimingCacheDir = TrtTimingCacheDir,
                    FastBuild = DnnFastBuild
                };
            }
        }

        /// <summary>新しいファイルを開いた時に呼ぶこと。旧ファイル用にビルド中/ビルド済み
        /// だったインスタンスを手放し、新ファイル用のインスタンスを用意する（旧ファイルの
        /// ビルドが新ファイルのビルドを数分間ブロックしてしまう不具合の対策）。</summary>
        public void ResetDnnEngineForNewFile() => DiscardDnnEngineAndRebuildIfEnabled();

        /// <summary>現在のDNNモデルの拡大倍率（ファイル名先頭の"Nx"から自動解析、
        /// 解析できない場合は4を既定とする）。</summary>
        public int DnnScale => _dnnScale;

        private static int ParseScaleFromFileName(string fileName)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                fileName, @"^(\d+)x", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return (m.Success && int.TryParse(m.Groups[1].Value, out int s) && s > 0) ? s : 4;
        }

        /// <summary>DNN超解像(TensorRT)の有効/無効。trueの間はGPU側のLanczos超解像
        /// (SetSuperResolution)を無効化し、代わりにデコードスレッド内でCPU経由の
        /// DNN推論によるアップスケールを行う。GpuPresenterが未設定（WriteableBitmap
        /// フォールバック時）は効果なし。</summary>
        public void SetDnnSuperResolution(bool enabled)
        {
            _dnnSrEnabled = enabled;
            if (enabled)
            {
                // trtcacheはRAMディスク等の揮発ストレージ運用のため、全解像度分を
                // まとめて復元しておく（存在するものは上書きしない）。
                DnnSuperResolutionEngine.RestoreAllCachesIfNeeded(DnnTrtCacheDir, TrtCacheBackupDir);
                _dnnSr ??= new DnnSuperResolutionEngine(
                    Path.Combine(DnnModelsDir, _dnnModelFileName), DnnTrtCacheDir);
                _dnnSr.BackupDir = TrtCacheBackupDir;
                _dnnSr.TimingCacheDir = TrtTimingCacheDir;
                _dnnSr.FastBuild = DnnFastBuild;
                // DNN側で拡大するため、GPU側のLanczos超解像は二重適用を避けるため無効化する
                GpuPresenter?.SetSuperResolution(1f);
            }
        }

        /// <summary>指定解像度で即座に推論可能か（ビルド済みか）。呼び出し元がライブ切替前に
        /// 「既にキャッシュ済みだから待たずに切り替えてよいか」を判定するのに使う。</summary>
        public bool IsDnnReadyFor(int width, int height) => _dnnSr?.IsReadyFor(width, height) ?? false;

        /// <summary>指定解像度用のDNNエンジンを同期的に（呼び出し元スレッドをブロックして）
        /// ビルドする。ライブ切替時に「一時停止→ビルド完了待ち→再生再開」のUIフローで使うことを
        /// 想定し、呼び出し元でTask.Run等により別スレッドから呼ぶこと（デコードスレッド上や
        /// UIスレッド上で直接呼ばないこと）。</summary>
        public bool PrebuildDnnEngine(int width, int height)
        {
            // 解像度専用サブフォルダ単位の復元はBuildSessionOnce（EnsureEngine経由）内で
            // 自動的に行われるため、ここではBackupDirを設定するだけでよい。
            _dnnSr ??= new DnnSuperResolutionEngine(Path.Combine(DnnModelsDir, _dnnModelFileName), DnnTrtCacheDir);
            _dnnSr.BackupDir = TrtCacheBackupDir;
            _dnnSr.TimingCacheDir = TrtTimingCacheDir;
            _dnnSr.FastBuild = DnnFastBuild;
            GpuPresenter?.SetSuperResolution(1f);
            return _dnnSr.EnsureEngine(width, height);
        }

        /// <summary>trtcache（RAMディスク等の揮発ストレージ）が使われていれば、
        /// exe直下の永続バックアップへ丸ごとコピーする。アプリ終了時に呼ぶこと。</summary>
        public void BackupDnnTrtCacheIfUsed() =>
            DnnSuperResolutionEngine.BackupCache(DnnTrtCacheDir, TrtCacheBackupDir);

        private void RaiseDnnBuildState(bool building) =>
            _ui.BeginInvoke(DispatcherPriority.Normal, new Action(() => DnnBuildStateChanged?.Invoke(building)));

        /// <summary>比較ビュー（段階5拡張）のモード。GPU側のみで完結する機能。</summary>
        public void SetCompareMode(int mode) => GpuPresenter?.SetCompareMode(mode);

        /// <summary>簡易デインターレース（隣接ラインのブレンド方式）の有効/無効。</summary>
        public bool DeinterlaceEnabled
        {
            get => _deinterlaceEnabled;
            set => _deinterlaceEnabled = value;
        }

        // ── シークバードラッグ中の軽量プレビュー用 ──
        private volatile bool _fastSeekPreview;

        /// <summary>trueの間は、シーク直後の「目標フレームまで追いつきデコード」を行わず、
        /// 直近のキーフレーム（AVSEEK_FLAG_BACKWARDで着地した最初の1枚）をそのまま即表示する。
        /// シークバードラッグ中の低遅延プレビュー専用（精度より速さ優先）。
        /// ドラッグ終了時の最終着地はfalseのまま通常の追いつきロジックで正確な1枚に合わせる。</summary>
        public bool FastSeekPreview
        {
            get => _fastSeekPreview;
            set => _fastSeekPreview = value;
        }

        // ── 外部マスタークロック（FfmpegMediaElement内の非表示MediaElementのPositionを反映） ──
        private readonly Stopwatch _extClock = new();
        private double _extBaseSeconds;
        private volatile bool _extPlaying;

        private readonly object _seekLock = new();
        private double _pendingSeekSeconds = -1;

        // ── シーク直後の「クロックが逃げる」対策 ──
        // シーク実行〜最初のフレーム表示までの間は外部クロックを凍結し、
        // 実時間で進み続ける再生位置にキャッチアップが追いつけず映像だけ
        // 止まり続ける（音声だけ流れる）症状を防ぐ。
        private volatile bool _catchingUpAfterSeek;
        private volatile bool _desiredPlaying;

        public WriteableBitmap? Bitmap { get; private set; }

        /// <summary>非nullの場合、WritePixels(WriteableBitmap)の代わりにこちらへ毎フレーム渡す。
        /// 「高画質化エンジン設計提案」段階1（D3DImage土台）用。既定はnull＝従来通りWriteableBitmap表示。</summary>
        public IFramePresenter? GpuPresenter { get; set; }
        public int VideoWidth { get; private set; }
        public int VideoHeight { get; private set; }
        public TimeSpan Duration { get; private set; }

        public AVEngine(Dispatcher uiDispatcher)
        {
            _ui = uiDispatcher;
            EnsureFfmpegBinaries();
        }

        private static bool _binariesReady;
        private static void EnsureFfmpegBinaries()
        {
            if (_binariesReady) return;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string candidate = Path.Combine(baseDir, "ffmpeg");
                ffmpeg.RootPath = Directory.Exists(candidate) ? candidate : baseDir;
                _binariesReady = true;
            }
            catch (Exception ex)
            {
                Trace($"EnsureFfmpegBinaries failed: {ex.Message}");
            }
        }

        public void SetSpeedRatio(double ratio) => _speedRatio = Math.Clamp(ratio, 0.1, 4.0);

        /// <summary>音量 0.0〜1.0。実際の反映はデコードスレッド側で毎ループIAudioOutputへ適用する
        /// （IAudioOutputインスタンスはデコードスレッドのローカル変数のため、ここでは値の保持のみ）。</summary>
        public void SetVolume(double volume) => _volume = Math.Clamp(volume, 0.0, 1.0);

        /// <summary>現在のマスタークロック秒数を外部（FfmpegMediaElement）へ公開する。
        /// シークバー表示等、UIスレッドからの参照用。</summary>
        public double GetCurrentPositionSeconds() => GetMasterClockSec();

        public void SetEffects(double contrast, double saturation, double gamma)
        {
            _contrast = Math.Clamp(contrast, -1, 1);
            _saturation = Math.Clamp(saturation, -1, 1);
            _gamma = Math.Clamp(gamma, -1, 1);
            _effectsActive = _contrast != 0 || _saturation != 0 || _gamma != 0;
            // GPU側が有効な場合はCompute Shaderで同じ処理を行うため、値だけ転送する。
            GpuPresenter?.SetEffects(_contrast, _saturation, _gamma);
        }

        public void SetExternalClock(double seconds, bool isPlaying)
        {
            _desiredPlaying = isPlaying;

            if (_catchingUpAfterSeek)
            {
                // キャッチアップ中はアンカーに一切触れない（完全凍結）。
                // 以前は「秒数だけ更新」していたが、_clockTimer が50ms毎に実際の
                // 音声再生位置（シーク後も音声はリアルタイムで進み続ける）で
                // 上書きしてしまい、結局アンカーが実時間でズルズル前進 → デコードが
                // 追いつけない、という同じ「目標が逃げる」問題を再発させていた。
                // [原因究明用ログ] シーク直後のキャッチアップ中のみ発生（通常再生時は到達しない）。
                // クロック凍結が正しく機能しているかの確認用。
                Trace($"SetExternalClock ignored (catching up) seconds={seconds:F3}");
                return;
            }

            _extBaseSeconds = seconds;
            _extClock.Restart();
            _extPlaying = isPlaying;
        }

        private double GetMasterClockSec()
        {
            if (!_extPlaying) return _extBaseSeconds;
            return _extBaseSeconds + _extClock.Elapsed.TotalSeconds * _speedRatio;
        }

        // ─────────────────────────────────────────────────────────────
        // Open / Stop
        // ─────────────────────────────────────────────────────────────
        /// <summary>wantAudio=falseの場合、音声ストリームを一切開かず、XAudio2エンジンも
        /// 作成しない（サムネイル生成用の専用プレイヤー等、音声が最初から不要な用途向け。
        /// Source設定のたびに毎回フルのXAudio2エンジンを作って捨てるのは無駄が大きく、
        /// 短時間に大量オープンする用途では負荷の原因にもなるため）。</summary>
        public void Open(Uri source, bool wantAudio = true)
        {
            Stop();

            int myGen = Interlocked.Increment(ref _generation);
            _paused = true;
            _audioDesired = true;
            _extBaseSeconds = 0;
            _extClock.Restart();
            _extPlaying = false;
            _catchingUpAfterSeek = false;
            _desiredPlaying = false;
            _hasAudioStream = false;
            _lastShownPtsSeconds = -1;

            bool wantHw = HardwareAccelRequested;
            bool wantDenoise = DenoiseRequested;
            bool wantPrefetch = PrefetchEnabled;
            var t = new Thread(() => OpenAndRun(source.LocalPath, myGen, wantHw, wantDenoise, wantPrefetch, wantAudio))
            {
                IsBackground = true,
                Name = "AVEngine-VideoDecode"
            };
            _decodeThread = t;
            t.Start();
        }

        public void Stop()
        {
            Interlocked.Increment(ref _generation);
            _paused = true;

            // 旧スレッドの終了をUIスレッドで同期待ちしない。ネイティブリソースは
            // 各デコードスレッド自身のローカル変数として保持・解放されるため、
            // 旧スレッドが多少長く生き残っても他スレッドと衝突しない設計になっている。
            // ここでJoin()してUIスレッドを止めると、旧スレッドが何らかの理由で
            // 応答が遅い場合に無関係な操作までブロックしてしまう。
            var t = _decodeThread;
            _decodeThread = null;
            if (t != null)
            {
                Task.Run(() =>
                {
                    if (!t.Join(10000))
                        Trace($"Stop(): decode thread did not exit within 10000ms ({t.ManagedThreadId}) - リーク疑いあり");
                });
            }
        }

        /// <summary>withAudio=falseの場合、映像デコードは再開するが音声は一切出さない
        /// （コマ送り/シークバードラッグ中のプレビュー専用。FfmpegMediaElement.StepToVideoOnlyAsync/
        /// FastSeekPreviewAsyncから使われる）。</summary>
        public void Play(bool withAudio = true)
        {
            Trace($"AVEngine.Play() paused=false withAudio={withAudio}");
            bool wasVideoOnly = !_audioDesired;
            _audioDesired = withAudio;

            // 映像のみ(Play(false))の間、音声デコードスレッドは音声パケットを読み捨てたまま
            // Demux先読み分（約3秒）だけ映像より先へ進んでしまう。そのまま音声を有効に戻すと
            // 音声が映像より約3秒先から鳴り始め、映像が約50フレームを捨てて追いつくまで
            // カクつく（レジューム直後・ドラッグシーク直後の症状）。音声ありへ戻る瞬間に
            // 直近の映像位置へ再シークして、映像・音声を同じ位置から始め直す。
            // 音声ストリームが無いエンジン、および別のシークが未消化の場合は何もしない。
            if (withAudio && wasVideoOnly && _hasAudioStream)
            {
                double pos = _lastShownPtsSeconds;
                lock (_seekLock)
                {
                    if (pos >= 0 && _pendingSeekSeconds < 0)
                    {
                        _pendingSeekSeconds = pos;
                        Trace($"AVEngine.Play(): 映像のみ→音声ありへの切替のため映像位置{pos:F3}sへ再シークして音声を揃える");
                    }
                }
            }
            _paused = false;
        }

        public void Pause()
        {
            Trace($"AVEngine.Pause() paused=true catchingUp={_catchingUpAfterSeek}");
            _paused = true;
        }

        public void Seek(TimeSpan pos)
        {
            lock (_seekLock)
            {
                _pendingSeekSeconds = Math.Max(0, pos.TotalSeconds);
                Trace($"AVEngine.Seek() requested pendingSeekSeconds={_pendingSeekSeconds:F3}");
            }
        }

        public void Dispose() => Stop();

        // ─────────────────────────────────────────────────────────────
        // ハードウェアデコード用: get_format コールバック
        // ─────────────────────────────────────────────────────────────
        [ThreadStatic] private static AVPixelFormat _negotiatedHwPixFmt;
        private static readonly AVCodecContext_get_format _getHwFormatDelegate = GetHwFormat;

        private static AVPixelFormat GetHwFormat(AVCodecContext* ctx, AVPixelFormat* pixFmts)
        {
            for (var p = pixFmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == _negotiatedHwPixFmt)
                {
                    Trace($"get_format: HW形式({*p})を採用");
                    return *p;
                }
            }
            Trace($"get_format: HW形式({_negotiatedHwPixFmt})が候補に無く、先頭の{*pixFmts}にフォールバック（＝実質SW動作）");
            return *pixFmts;
        }

        // ─────────────────────────────────────────────────────────────
        // デコードスレッド本体（映像のみ）
        // ─────────────────────────────────────────────────────────────
        private void OpenAndRun(string path, int myGen, bool wantHw, bool wantDenoise, bool wantPrefetch, bool wantAudio = true)
        {
            AVFormatContext* fmt = null;
            AVCodecContext* vctx = null;
            SwsContext* sws = null;
            int videoIdx = -1;
            bool hwActive = false;
            AVBufferRef* hwDeviceCtx = null;

            AVPacket* pkt = null;
            AVFrame* frame = null;
            AVFrame* swFrame = null;
            AVFrame* rgbFrame = null;
            byte* rgbBuffer = null;

            // ── 音声デコード（今回追加）──
            // 音声トラックが無い/オープンに失敗した場合でも致命的エラーにはせず、
            // 音声無しの映像として通常通り再生を続ける（actx/swr/audioOutputがnullのまま）。
            // 【今回修正】当初は映像デコードと同じループ内で音声パケットも処理していたが、
            // 映像側の待機（Thread.Sleep、最大200ms/フレーム）と同じスレッドを共有するため、
            // 映像が待っている間は音声パケットが一切処理されずXAudio2側のバッファが枯渇し、
            // 「無音→映像1コマ進む→音がまとめて鳴る」を繰り返す不具合になっていた。
            // 音声デコード＋swresample＋IAudioOutput送出は専用スレッド（AudioDecodeLoop）に
            // 分離し、映像の待機処理と完全に非同期で回す。
            AVCodecContext* actx = null;
            SwrContext* swr = null;
            int audioIdx = -1;
            AVFrame* audioFrame = null;
            IAudioOutput? audioOutput = null;
            bool audioRunning = false; // Start()/Pause()の二重呼び出し防止用ローカルフラグ
            double lastAnchoredAudioPos = double.NaN; // 音声位置が変化した時だけ再アンカーするための直近値
            Channel<DemuxedPacket>? audioPktChannel = null;
            Thread? audioDecodeThread = null;
            bool audioDecodeJoinedCleanly = true;

            // ── Stage1: パケット先読み（wantPrefetch時のみ使用） ──
            int[] interruptFlagArr = new int[1]; // pin留めしてネイティブへポインタを渡す
            GCHandle interruptFlagHandle = default;
            bool interruptFlagPinned = false;
            Channel<DemuxedPacket>? pktChannel = null;
            Thread? demuxThread = null;
            bool demuxJoinedCleanly = true; // finallyでfmt/vctxを解放してよいか（デッドロック疑い時は解放しない）

            // ── ノイズリダクション用フィルタグラフ（段階3）──
            // 他のネイティブハンドルと同じく、このデコードスレッドのローカル変数として
            // 保持・解放する（use-after-free回避の既存方針を踏襲）。
            AVFilterGraph* filterGraph = null;
            AVFilterContext* bufferSrcCtx = null;
            AVFilterContext* bufferSinkCtx = null;
            int filterW = -1, filterH = -1;
            AVPixelFormat filterFmt = AVPixelFormat.AV_PIX_FMT_NONE;

            void PauseAudioDecodeForSeek()
            {
                if (actx == null || audioDecodeThread == null) return;
                _audioSeekInterruptFlag = 1;
                var ackSw = Stopwatch.StartNew();
                while (_audioSeekAcked == 0 && audioDecodeThread.IsAlive && ackSw.ElapsedMilliseconds < 2000)
                    Thread.Sleep(1);
                if (_audioSeekAcked == 0)
                    Trace($"Seek: AudioDecodeThreadが応答しないため音声flushを断念（映像優先で続行）");
            }

            void ResumeAudioDecodeAfterSeek()
            {
                if (audioPktChannel != null)
                {
                    while (audioPktChannel.Reader.TryRead(out var staleA))
                    {
                        var sap = (AVPacket*)staleA.PktPtr;
                        if (sap != null) ffmpeg.av_packet_free(&sap);
                    }
                }
                _audioSeekInterruptFlag = 0;
            }

            void FreeDenoiseFilter()
            {
                if (filterGraph != null)
                {
                    var g = filterGraph;
                    ffmpeg.avfilter_graph_free(&g);
                }
                filterGraph = null;
                bufferSrcCtx = null;
                bufferSinkCtx = null;
                filterW = -1; filterH = -1; filterFmt = AVPixelFormat.AV_PIX_FMT_NONE;
            }

            void EnsureDenoiseFilter(AVFrame* f, AVFormatContext* fmtCtx, int vIdx)
            {
                if (filterGraph != null && filterW == f->width && filterH == f->height && filterFmt == (AVPixelFormat)f->format)
                    return;

                FreeDenoiseFilter();

                filterGraph = ffmpeg.avfilter_graph_alloc();
                if (filterGraph == null) return;

                var bufferSrc = ffmpeg.avfilter_get_by_name("buffer");
                var bufferSink = ffmpeg.avfilter_get_by_name("buffersink");
                var timeBase = fmtCtx->streams[vIdx]->time_base;
                var sar = f->sample_aspect_ratio.num != 0 ? f->sample_aspect_ratio : new AVRational { num = 1, den = 1 };
                string args = $"video_size={f->width}x{f->height}:pix_fmt={(int)f->format}:time_base={timeBase.num}/{timeBase.den}:pixel_aspect={sar.num}/{sar.den}";

                AVFilterContext* srcCtx = null, sinkCtx = null;
                if (ffmpeg.avfilter_graph_create_filter(&srcCtx, bufferSrc, "in", args, null, filterGraph) < 0 ||
                    ffmpeg.avfilter_graph_create_filter(&sinkCtx, bufferSink, "out", null, null, filterGraph) < 0)
                {
                    Trace("DenoiseFilter: create buffer/buffersink failed");
                    FreeDenoiseFilter();
                    return;
                }

                var outputs = ffmpeg.avfilter_inout_alloc();
                var inputs = ffmpeg.avfilter_inout_alloc();
                outputs->name = ffmpeg.av_strdup("in");
                outputs->filter_ctx = srcCtx;
                outputs->pad_idx = 0;
                outputs->next = null;
                inputs->name = ffmpeg.av_strdup("out");
                inputs->filter_ctx = sinkCtx;
                inputs->pad_idx = 0;
                inputs->next = null;

                // hqdn3d=luma_spatial:chroma_spatial:luma_tmp:chroma_tmp（既定よりやや強め）
                const string filterDesc = "hqdn3d=4:3:6:4.5";
                int parseRet = ffmpeg.avfilter_graph_parse_ptr(filterGraph, filterDesc, &inputs, &outputs, null);
                if (inputs != null) ffmpeg.avfilter_inout_free(&inputs);
                if (outputs != null) ffmpeg.avfilter_inout_free(&outputs);
                if (parseRet < 0)
                {
                    Trace($"DenoiseFilter: parse failed ({parseRet})");
                    FreeDenoiseFilter();
                    return;
                }

                if (ffmpeg.avfilter_graph_config(filterGraph, null) < 0)
                {
                    Trace("DenoiseFilter: graph_config failed");
                    FreeDenoiseFilter();
                    return;
                }

                bufferSrcCtx = srcCtx;
                bufferSinkCtx = sinkCtx;
                filterW = f->width; filterH = f->height; filterFmt = (AVPixelFormat)f->format;
                Trace($"DenoiseFilter graph built ({filterW}x{filterH} fmt={filterFmt})");
            }

            try
            {
                IntPtr interruptFlagPtr = IntPtr.Zero;
                if (wantPrefetch)
                {
                    interruptFlagHandle = GCHandle.Alloc(interruptFlagArr, GCHandleType.Pinned);
                    interruptFlagPinned = true;
                    interruptFlagPtr = interruptFlagHandle.AddrOfPinnedObject();
                }

                OpenStreamsWithHw(path, wantHw, interruptFlagPtr, out fmt, out vctx, out videoIdx, out hwActive, out hwDeviceCtx);

                int w = vctx->width, h = vctx->height;
                double durSec = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
                var duration = TimeSpan.FromSeconds(durSec);
                string modeLabel = hwActive ? "HW (D3D11VA)" : "SW";

                var chapters = new List<double>();
                for (uint ci = 0; ci < fmt->nb_chapters; ci++)
                {
                    var ch = fmt->chapters[ci];
                    double startSec = ch->start * ffmpeg.av_q2d(ch->time_base);
                    if (startSec > 0.01) chapters.Add(startSec); // 先頭0秒は目印として不要
                }

                if (myGen != _generation) return;

                _ui.BeginInvoke(new Action(() =>
                {
                    if (myGen != _generation) return;
                    Bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                    GpuPresenter?.EnsureSize(w, h);
                    VideoWidth = w; VideoHeight = h; Duration = duration;
                    Opened?.Invoke(w, h, duration);
                    DecodeModeChanged?.Invoke(modeLabel);
                    ChaptersLoaded?.Invoke(chapters);
                }));

                Trace($"Opened(gen={myGen}): {path} {w}x{h} dur={duration} mode={modeLabel}");

                // ── 音声ストリームのオープン（今回追加）──
                // 映像と同じfmtから音声の最適ストリームを探す。失敗しても致命的にはしない。
                // wantAudio=falseの場合はこのブロック自体を丸ごとスキップする
                // （サムネイル生成用プレイヤー等、音声が最初から不要な用途でXAudio2エンジンを
                // 作らずに済ませるため。audioIdxは-1のままとなり、Demux側もvideoIdxのみ拾う）。
                if (wantAudio)
                {
                    try
                    {
                        AVCodec* acodec = null;
                        audioIdx = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, videoIdx, &acodec, 0);
                        if (audioIdx >= 0 && acodec != null)
                        {
                            actx = ffmpeg.avcodec_alloc_context3(acodec);
                            ffmpeg.avcodec_parameters_to_context(actx, fmt->streams[audioIdx]->codecpar);
                            if (ffmpeg.avcodec_open2(actx, acodec, null) == 0)
                            {
                                swr = ffmpeg.swr_alloc();
                                AVChannelLayout inLayout = actx->ch_layout;
                                AVChannelLayout outLayout;
                                ffmpeg.av_channel_layout_default(&outLayout, AudioOutChannels);
                                var swrLocal = swr;
                                int swrRet = ffmpeg.swr_alloc_set_opts2(&swrLocal, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT,
                                    AudioOutSampleRate, &inLayout, actx->sample_fmt, actx->sample_rate, 0, null);
                                swr = swrLocal;
                                if (swrRet == 0 && swr != null && ffmpeg.swr_init(swr) == 0)
                                {
                                    audioFrame = ffmpeg.av_frame_alloc();
                                    audioOutput = new XAudio2AudioOutput();
                                    audioOutput.Open(AudioOutSampleRate, AudioOutChannels);
                                    Trace($"Audio(gen={myGen}): opened idx={audioIdx} srcRate={actx->sample_rate} -> {AudioOutSampleRate}Hz/{AudioOutChannels}ch");

                                    // 音声パケットは専用チャンネル経由で専用スレッドへ渡す（映像の
                                    // 待機処理と非同期化するため）。書き込みはこのOpenAndRunスレッド
                                    // のみ（prefetch有無どちらの経路でも、パケット取り出し自体は
                                    // このスレッドで行うため SingleWriter=true でよい）。
                                    audioPktChannel = Channel.CreateBounded<DemuxedPacket>(new BoundedChannelOptions(256)
                                    {
                                        FullMode = BoundedChannelFullMode.Wait,
                                        SingleReader = true,
                                        SingleWriter = true,
                                    });
                                    var adActx = actx;
                                    var adSwr = swr;
                                    var adFrame = audioFrame;
                                    var adOutput = audioOutput;
                                    var adChannel = audioPktChannel;
                                    double adTimeBase = ffmpeg.av_q2d(fmt->streams[audioIdx]->time_base);
                                    _audioContentOffsetSeconds = 0;
                                    _audioSeekInterruptFlag = 0;
                                    _audioSeekAcked = 0;
                                    audioDecodeThread = new Thread(() => AudioDecodeLoop(adActx, adSwr, adFrame, adOutput, adChannel, myGen, adTimeBase))
                                    {
                                        IsBackground = true,
                                        Name = "AVEngine-AudioDecode"
                                    };
                                    audioDecodeThread.Start();
                                    _hasAudioStream = true;
                                }
                                else
                                {
                                    Trace($"Audio(gen={myGen}): swr_alloc_set_opts2/swr_init failed ({swrRet}) - 音声無しで続行");
                                    if (swr != null) { var s2 = swr; ffmpeg.swr_free(&s2); swr = null; }
                                }
                            }
                            else
                            {
                                Trace($"Audio(gen={myGen}): avcodec_open2 failed - 音声無しで続行");
                                if (actx != null) { var a2 = actx; ffmpeg.avcodec_free_context(&a2); actx = null; }
                            }
                        }
                        else
                        {
                            Trace($"Audio(gen={myGen}): 音声ストリーム無し");
                        }
                    }
                    catch (Exception exAudio)
                    {
                        Trace($"Audio(gen={myGen}): open exception - 音声無しで続行: {exAudio.Message}");
                    }
                }
                else
                {
                    Trace($"Audio(gen={myGen}): wantAudio=falseのためスキップ（音声無しで続行）");
                }


                pkt = ffmpeg.av_packet_alloc();
                frame = ffmpeg.av_frame_alloc();
                swFrame = ffmpeg.av_frame_alloc();
                rgbFrame = ffmpeg.av_frame_alloc();

                int bufSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, w, h, 1);
                rgbBuffer = (byte*)ffmpeg.av_malloc((ulong)bufSize);
                byte_ptrArray4 dstData = new byte_ptrArray4();
                int_array4 dstLinesize = new int_array4();
                ffmpeg.av_image_fill_arrays(ref dstData, ref dstLinesize, rgbBuffer,
                    AVPixelFormat.AV_PIX_FMT_BGRA, w, h, 1);
                for (uint i = 0; i < 4; i++)
                {
                    rgbFrame->data[i] = dstData[i];
                    rgbFrame->linesize[i] = dstLinesize[i];
                }

                byte[] managedBuf = new byte[bufSize];

                // ── 起動時「無音/カクつき」切り分け用の診断トレース ──
                // このOpen世代で最初の1フレームだけ、デコード完了時刻・実描画時刻をtrace.logへ記録する。
                // 原因切り分けが済んだら削除してよい。
                bool diagFirstDecodeLogged = false;
                bool diagFirstDisplayLogged = false;

                // ── クロックペース切り分け用の周期診断（今回追加）──
                // audioOutputの実再生位置(=マスタークロック)が壁時計と同じ速さで進んでいるか、
                // 大量ドロップが起きていないかを1秒おきにtrace.logへ出す。原因切り分けが済んだら削除可。
                var diagWallSw = Stopwatch.StartNew();
                double diagLastLoggedWall = 0;
                int diagDropCount = 0;
                int diagShowCount = 0;
                int diagAudioForwardBlockMs = 0; // audioPktChannel満杯によるメインループ側の足止め(ms相当)
                // ── Seek直後の低fps原因切り分け用（今回追加）──
                // HW転送・sws_scale・Marshal.Copyそれぞれに実際どれだけ時間がかかっているかを
                // 1秒ごとに積算し、平均msで出す。「クロックは正しいのにshowsが低い」原因が
                // どの段階にあるかをここで特定する。
                long diagHwTransferTicks = 0;
                long diagScaleTicks = 0;
                long diagCopyTicks = 0;
                int diagFrameProcCount = 0;
                double diagLastLoggedFrameStats = 0;
                int diagWaitMsSum = 0; // 映像ペーシング待機(diff>0時のSleep)の累積ms

                // ── Stage1: パケット先読みスレッド起動（wantPrefetch時のみ） ──
                if (wantPrefetch)
                {
                    pktChannel = Channel.CreateBounded<DemuxedPacket>(new BoundedChannelOptions(PrefetchChannelCapacity)
                    {
                        FullMode = BoundedChannelFullMode.Wait, // 満杯ならDemux側が自然に待つ（TryWriteループでポーリング）
                        SingleReader = true,
                        SingleWriter = true,
                    });
                    _pktChannel = pktChannel;
                    _demuxInterruptFlag = 0;
                    _demuxAcked = 0;

                    var demuxFmt = fmt;
                    var demuxChannel = pktChannel;
                    int demuxVideoIdx = videoIdx;
                    int demuxAudioIdx = audioIdx;
                    var demuxAudioChannel = audioPktChannel; // 今回修正: 音声パケットはDemuxスレッドから
                                                             // 直接audioPktChannelへ渡す。従来はメインループ（映像デコードループ）経由で
                                                             // 中継していたが、映像側のペーシングSleep（最大200ms/フレーム）で同じスレッドが
                                                             // 頻繁に止まるため、その間ずっと音声パケットも運ばれず、結果的に「3秒おきに
                                                             // まとめて音声が届く→XAudio2が残り時間ずっと無音で干上がる→マスタークロックが
                                                             // 進まない→映像がさらに待つ」という自己増殖ループになっていた。Demuxスレッドは
                                                             // 映像の待機とは無関係に走り続けるため、ここで直接振り分ければ解消する。
                    demuxThread = new Thread(() => DemuxPrefetchLoop(demuxFmt, demuxVideoIdx, demuxAudioIdx, myGen, demuxChannel, demuxAudioChannel))
                    {
                        IsBackground = true,
                        Name = "AVEngine-Demux"
                    };
                    _demuxThread = demuxThread;
                    demuxThread.Start();
                    Trace($"DemuxPrefetch(gen={myGen}): started (capacity={PrefetchChannelCapacity})");
                }

                while (myGen == _generation)
                {
                    lock (_seekLock)
                    {
                        if (_pendingSeekSeconds >= 0)
                        {
                            double target = _pendingSeekSeconds;
                            _pendingSeekSeconds = -1;

                            if (wantPrefetch && demuxThread != null)
                            {
                                // Demuxスレッドがfmtに対してav_read_frameを呼んでいる最中に
                                // こちらがav_seek_frameを呼ぶとFFmpeg内部状態が競合するため、
                                // Demuxスレッドを安全な待機状態に入らせてから操作する。
                                // interrupt_callback経由で、たとえ低速HDDのI/O待ちの最中でも
                                // av_read_frameを即座に脱出させられる（これがStage1の主目的）。
                                _demuxInterruptFlag = 1;
                                var ackSw = Stopwatch.StartNew();
                                while (_demuxAcked == 0 &&
                                       demuxThread.IsAlive && ackSw.ElapsedMilliseconds < 5000)
                                {
                                    Thread.Sleep(1);
                                }
                                if (_demuxAcked == 0)
                                {
                                    // 5秒待っても応答が無い＝Demuxスレッドが何らかの理由で
                                    // 応答不能になっている疑い。fmtへの同時アクセスによる
                                    // クラッシュを避けるため、このSeekはあきらめて次ループへ
                                    // 回す（pendingSeekSecondsを戻さないため、この一度の
                                    // Seekは失われるが、無応答状態が続く限りは何度Seekしても
                                    // 同じなのでこれ以上粘っても実害しか無い）。
                                    Trace($"Seek(gen={myGen}): Demuxスレッドが応答しないためSeekを断念");
                                }
                                else
                                {
                                    // 残留パケットを全て解放（Seek前の古いデータのため不要）
                                    if (pktChannel != null)
                                    {
                                        while (pktChannel.Reader.TryRead(out var stale))
                                        {
                                            var sp = (AVPacket*)stale.PktPtr;
                                            if (sp != null) ffmpeg.av_packet_free(&sp);
                                        }
                                    }

                                    long tsP = (long)(target / ffmpeg.av_q2d(fmt->streams[videoIdx]->time_base));
                                    ffmpeg.av_seek_frame(fmt, videoIdx, tsP, ffmpeg.AVSEEK_FLAG_BACKWARD);
                                    ffmpeg.avcodec_flush_buffers(vctx);
                                    PauseAudioDecodeForSeek();
                                    if (actx != null) ffmpeg.avcodec_flush_buffers(actx);

                                    //audioOutput?.Flush(); // ← ここでフラッシュしているが、タイミングや内部状態のクリアが不十分
                                    // ── 改善: シーク時にオーディオのバッファをフラッシュし、音声オフセットをターゲット秒数で強制固定 ──
                                    audioOutput?.Flush();
                                    _audioContentOffsetSeconds = target; // ターゲット秒数でオフセットを強制固定

                                    ResumeAudioDecodeAfterSeek();
                                    audioRunning = false;
                                    lastAnchoredAudioPos = double.NaN;
                                    _catchingUpAfterSeek = true;
                                    _extBaseSeconds = target;
                                    _extClock.Restart();
                                    _extPlaying = false;
                                    FreeDenoiseFilter();

                                    //自前改善ポイント
                                    // ── 追加: シーク先のターゲット秒数を音声オフセットとして強制設定 ──
                                    _audioContentOffsetSeconds = target;

                                    Trace($"Seek -> {target:F2}s (prefetch)");
                                }

                                _demuxInterruptFlag = 0; // Demuxスレッド再開
                            }
                            else
                            {
                                long ts = (long)(target / ffmpeg.av_q2d(fmt->streams[videoIdx]->time_base));
                                ffmpeg.av_seek_frame(fmt, videoIdx, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                                ffmpeg.avcodec_flush_buffers(vctx);
                                PauseAudioDecodeForSeek();
                                if (actx != null) ffmpeg.avcodec_flush_buffers(actx);
                                audioOutput?.Flush();
                                ResumeAudioDecodeAfterSeek();
                                audioRunning = false;
                                lastAnchoredAudioPos = double.NaN;
                                _catchingUpAfterSeek = true;
                                // シーク要求から実際にここへ到達するまでの間（HW/SW切替時の
                                // コーデック再初期化のように時間がかかるケースがある）に、
                                // 外部から先にSetExternalClockが呼ばれてクロックが実時間で
                                // 進んでしまっている可能性があるため、ここで確実にtarget秒へ
                                // 巻き戻して凍結する（凍結アンカーが目標からズレたまま止まる
                                // ＝映像が出てこなくなる不具合の対策）。
                                _extBaseSeconds = target;
                                _extClock.Restart();
                                _extPlaying = false; // 最初のフレームが出るまでクロックを凍結
                                FreeDenoiseFilter(); // シーク跨ぎで時間方向の履歴が無効になるため作り直す
                                Trace($"Seek -> {target:F2}s");
                            }
                        }
                    }

                    if (_paused)
                    {
                        if (audioRunning)
                        {
                            audioOutput?.Pause();
                            audioRunning = false;
                            // 一時停止した瞬間の位置でクロックを凍結する（凍結しないと
                            // Stopwatchベースの外挿だけが実時間で進み続けてしまう）。
                            SetExternalClock(GetMasterClockSec(), false);
                        }
                        else if (_extPlaying && (audioOutput == null || !audioOutput.IsActive))
                        {
                            // 音声を持たないエンジン（ドラレコのRear等）向け。下記の実時間自走クロックを
                            // 一時停止中も進め続けないよう、停止した瞬間の位置で凍結する。
                            SetExternalClock(GetMasterClockSec(), false);
                        }
                        Thread.Sleep(10);
                        continue;
                    }

                    if (!_audioDesired && audioRunning)
                    {
                        // コマ送り/プレビュー中など、映像デコードは進めるが音声は出さない指定。
                        audioOutput?.Pause();
                        audioRunning = false;
                    }

                    if (_audioDesired && audioOutput != null && audioOutput.IsActive)
                    {
                        if (!audioRunning)
                        {
                            audioOutput.Start();
                            audioRunning = true;
                        }
                        audioOutput.SetVolume(_volume);
                        audioOutput.SetSpeedRatio(_speedRatio);

                        // 音声デバイスの実再生位置が実際に進んだ時だけマスタークロックを
                        // 再アンカーする。【今回修正1】無条件に毎ループ再アンカーしていると、
                        // ファイル終盤など音声トラックが映像より先に尽きて無音になった区間で、
                        // 変化していない同じ位置に何度も再アンカーし続けることになり、
                        // Stopwatchの外挿が毎回ゼロにリセットされてクロックが完全に停止して
                        // しまっていた（映像側はptsに対してmasterが遅れ続けるためfpsが低下する）。
                        // 位置が変化した時だけ再アンカーすれば、音声が途切れた瞬間から自動的に
                        // Stopwatchでの実時間外挿へフォールバックし、映像のペースが崩れない。
                        // 【今回修正2】SetFrequencyRatioで速度を変えた際、XAudio2のSamplesPlayedが
                        // 「コンテンツ時間」と「実時間（wall-clock）」のどちらでカウントされるか
                        // 未確定なため、等倍速(1.0)以外では音声位置からの再アンカーをやめ、
                        // Stopwatch×speedRatioの外挿のみでマスタークロックを進める
                        // （スロー/早送り中に映像が音声に引きずられて同期崩壊するのを防ぐ）。
                        // 等倍速へ戻れば通常通り音声位置への再アンカーが再開され、自己修復する。
                        if (Math.Abs(_speedRatio - 1.0) < 0.001)
                        {
                            double curAudioPos = audioOutput.GetPositionSeconds() + _audioContentOffsetSeconds;
                            if (curAudioPos != lastAnchoredAudioPos)
                            {
                                SetExternalClock(curAudioPos, true);
                                lastAnchoredAudioPos = curAudioPos;
                            }
                        }
                    }
                    else if (_audioDesired && (audioOutput == null || !audioOutput.IsActive))
                    {
                        // 音声ストリームが無い（ドラレコのRearファイル等）／音声デバイスが使えない場合、
                        // 上の音声位置からの再アンカーが一度も走らないため _desiredPlaying が false の
                        // ままになり、シーク後のキャッチアップ完了時に _extPlaying=false（クロック凍結）
                        // で解凍されて、クロックが二度と進まなくなっていた。その結果、映像ペーシングが
                        // 「pts>masterで毎フレーム最大200ms待つ」状態になり約5fpsに固定されていた。
                        // 再生中は実時間(Stopwatch×速度)で自走させる。キャッチアップ中は
                        // SetExternalClock側が _desiredPlaying の更新だけ行い、解凍は
                        // 「CatchUp done」に任せる。
                        if (!_desiredPlaying)
                            SetExternalClock(GetMasterClockSec(), true);
                    }

                    if (wantPrefetch && pktChannel != null)
                    {
                        if (!TryDequeuePrefetchedPacket(pktChannel, myGen, pkt, out bool eof))
                        {
                            if (eof)
                            {
                                _paused = true;
                                Trace($"DecodeLoop(gen={myGen}): end of stream (prefetch)");
                                int eofGen1 = myGen;
                                _ui.BeginInvoke(new Action(() => { if (eofGen1 == _generation) EndOfStream?.Invoke(); }));
                            }
                            continue;
                        }
                    }
                    else
                    {
                        int rr = ffmpeg.av_read_frame(fmt, pkt);
                        if (rr < 0)
                        {
                            _paused = true;
                            Trace($"DecodeLoop(gen={myGen}): end of stream");
                            int eofGen2 = myGen;
                            _ui.BeginInvoke(new Action(() => { if (eofGen2 == _generation) EndOfStream?.Invoke(); }));
                            continue;
                        }
                    }

                    if (pkt->stream_index == videoIdx)
                    {
                        if (ffmpeg.avcodec_send_packet(vctx, pkt) == 0)
                        {
                            while (myGen == _generation && ffmpeg.avcodec_receive_frame(vctx, frame) == 0)
                            {
                                if (!diagFirstDecodeLogged)
                                {
                                    Trace($"[DIAG] first frame decoded (gen={myGen})");
                                    diagFirstDecodeLogged = true;
                                }

                                // pts/diff の判定は sws_scale・HW転送より先に行う。シーク直後の
                                // キャッチアップ中は大量のフレームを drop することになるため、
                                // 捨てるフレームに対して毎回スケーリング処理を行うのは無駄が
                                // 大きく、それ自体がキャッチアップを遅らせる一因になっていた。
                                // pts自体は frame（HW転送前）の best_effort_timestamp から取れるため、
                                // HW転送より先に判定できる。
                                double ptsSeconds = frame->best_effort_timestamp == ffmpeg.AV_NOPTS_VALUE
                                    ? GetMasterClockSec()
                                    : frame->best_effort_timestamp * ffmpeg.av_q2d(fmt->streams[videoIdx]->time_base);

                                double master = GetMasterClockSec();
                                double diff = ptsSeconds - master;
                                bool drop = false;
                                if (diff > 0.001)
                                {
                                    int waitMs = (int)Math.Min(diff * 1000, 200);
                                    if (waitMs > 0)
                                    {
                                        Thread.Sleep(waitMs);
                                        diagWaitMsSum += waitMs; // 今回追加: 映像ペーシング待機の累積(ms)
                                    }
                                }
                                else if (diff < -0.04)
                                {
                                    drop = true;
                                }

                                if (drop) diagDropCount++; else diagShowCount++;
                                if (diagWallSw.Elapsed.TotalSeconds - diagLastLoggedWall >= 1.0)
                                {
                                    Trace($"[DIAG] wall={diagWallSw.Elapsed.TotalSeconds:F2}s master={master:F2}s audioPos={(audioOutput != null ? audioOutput.GetPositionSeconds() + _audioContentOffsetSeconds : 0):F2}s rawAudioPos={audioOutput?.GetPositionSeconds():F2}s contentOffset={_audioContentOffsetSeconds:F3}s pts={ptsSeconds:F2}s drops={diagDropCount} shows={diagShowCount} speed={_speedRatio:F2} audioForwardBlockMs={diagAudioForwardBlockMs}");
                                    diagLastLoggedWall = diagWallSw.Elapsed.TotalSeconds;
                                    diagDropCount = 0;
                                    diagShowCount = 0;
                                    diagAudioForwardBlockMs = 0;
                                }

                                // シークバードラッグ中の軽量プレビュー：追いつき前の最初の1枚を
                                // dropせず、直近キーフレームの内容をそのまま即表示する（精度より速さ優先）。
                                if (drop && _catchingUpAfterSeek && _fastSeekPreview)
                                    drop = false;

                                if (drop)
                                {
                                    // [原因究明用ログ] シーク/再オープン直後のキャッチアップ中のみ発生（通常再生時は
                                    // 到達しない）。まとまった枚数が短時間に出る前提のログなので、通常再生中に
                                    // 出続けている場合はキャッチアップが終わらない不具合を疑うこと。
                                    //Trace($"Frame dropped (behind {(-diff) * 1000:F0}ms) pts={ptsSeconds:F3}");
                                    //Thread.Sleep(1); // 大量ドロップ時にデコーダ/GPUを連続で叩き過ぎないようにする
                                    //continue;
                                    // ↑上3行をコメントアウトして、pts判定直後にcontinueするように変更。ドロップフレームの
                                    // シーク直後のキャッチアップ中はスリープを挟まず一気に消化する
                                    if (!_catchingUpAfterSeek)
                                    {
                                        Thread.Sleep(1);
                                    }
                                    continue;
                                }

                                // ── DNN超解像がビルド済みだが、前フレームの推論(Task)がまだ
                                // 完了していない場合 ──
                                // このフレームはどうせ表示されない（直前のDNN結果を表示し続ける
                                // だけ）ことが、この時点（pts判定直後）で確定している。HW転送・
                                // denoise・sws_scale・フルバッファのMarshal.Copyはすべて無駄になる
                                // ため、ここで丸ごとスキップする（従来はsws_scale等を終えた後の
                                // DNN readiness/busy判定でようやく捨てていたため、推論が遅い状況
                                // （＝DNNモードの典型状況）ほど無駄なCPU消費が積み重なっていた）。
                                // キャッチアップ解除の判定だけは pts のみで完結するためここで行う。
                                if (_dnnSrEnabled && GpuPresenter != null && _dnnSr != null &&
                                    _dnnSr.IsReadyFor(w, h) && _dnnInferenceBusy)
                                {
                                    if (_catchingUpAfterSeek)
                                    {
                                        Trace($"CatchUp done (DNN busy skip) pts={ptsSeconds:F3} desiredPlaying={_desiredPlaying}");
                                        _catchingUpAfterSeek = false;
                                        _extBaseSeconds = ptsSeconds;
                                        _extClock.Restart();
                                        _extPlaying = _desiredPlaying;
                                    }
                                    continue;
                                }

                                AVFrame* srcFrame = frame;
                                if (hwActive)
                                {
                                    long tHw0 = Stopwatch.GetTimestamp();
                                    ffmpeg.av_frame_unref(swFrame);
                                    if (ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0) < 0)
                                    {
                                        Trace("av_hwframe_transfer_data failed - frame skipped");
                                        continue;
                                    }
                                    srcFrame = swFrame;
                                    diagHwTransferTicks += Stopwatch.GetTimestamp() - tHw0;
                                }

                                // ── ノイズリダクション（段階3）──
                                // drop確定フレームには適用しない（無駄な処理を避ける、既存のsws_scale
                                // 遅延実行と同じ方針）。hqdn3dは1:1で出力するcausalフィルタのため、
                                // 通常は毎回すぐに結果フレームが得られる。
                                AVFrame* filteredFrame = null;
                                if (wantDenoise)
                                {
                                    EnsureDenoiseFilter(srcFrame, fmt, videoIdx);
                                    if (bufferSrcCtx != null && bufferSinkCtx != null &&
                                        ffmpeg.av_buffersrc_add_frame_flags(bufferSrcCtx, srcFrame, 8 /* AV_BUFFERSRC_FLAG_KEEP_REF (buffersrc.h) */) >= 0)
                                    {
                                        filteredFrame = ffmpeg.av_frame_alloc();
                                        if (ffmpeg.av_buffersink_get_frame(bufferSinkCtx, filteredFrame) >= 0)
                                        {
                                            srcFrame = filteredFrame;
                                        }
                                        else
                                        {
                                            var ff = filteredFrame;
                                            ffmpeg.av_frame_free(&ff);
                                            filteredFrame = null;
                                        }
                                    }
                                }

                                if (sws == null)
                                {
                                    sws = ffmpeg.sws_getContext(w, h, (AVPixelFormat)srcFrame->format,
                                        w, h, AVPixelFormat.AV_PIX_FMT_BGRA, 2, null, null, null);
                                    Trace($"sws_getContext created srcFmt={(AVPixelFormat)srcFrame->format}");
                                }

                                long tScale0 = Stopwatch.GetTimestamp();
                                ffmpeg.sws_scale(sws, srcFrame->data, srcFrame->linesize, 0, h,
                                    rgbFrame->data, rgbFrame->linesize);
                                diagScaleTicks += Stopwatch.GetTimestamp() - tScale0;

                                if (filteredFrame != null)
                                {
                                    var ff2 = filteredFrame;
                                    ffmpeg.av_frame_free(&ff2);
                                }

                                int stride = rgbFrame->linesize[0];
                                long tCopy0 = Stopwatch.GetTimestamp();
                                System.Runtime.InteropServices.Marshal.Copy((IntPtr)rgbFrame->data[0], managedBuf, 0, bufSize);
                                diagCopyTicks += Stopwatch.GetTimestamp() - tCopy0;
                                diagFrameProcCount++;

                                if (diagWallSw.Elapsed.TotalSeconds - diagLastLoggedFrameStats >= 1.0 && diagFrameProcCount > 0)
                                {
                                    double toMs = 1000.0 / Stopwatch.Frequency;
                                    Trace($"[DIAG-Frame] frames={diagFrameProcCount} avgHwTransferMs={(diagHwTransferTicks * toMs / diagFrameProcCount):F2} avgScaleMs={(diagScaleTicks * toMs / diagFrameProcCount):F2} avgCopyMs={(diagCopyTicks * toMs / diagFrameProcCount):F2} waitMsSum={diagWaitMsSum}");
                                    diagLastLoggedFrameStats = diagWallSw.Elapsed.TotalSeconds;
                                    diagHwTransferTicks = 0;
                                    diagScaleTicks = 0;
                                    diagCopyTicks = 0;
                                    diagFrameProcCount = 0;
                                    diagWaitMsSum = 0;
                                }

                                if (_deinterlaceEnabled)
                                    ApplyDeinterlaceBlend(managedBuf, w, h, stride);

                                if (_effectsActive && GpuPresenter == null)
                                    ApplyEffects(managedBuf, bufSize);

                                if (_catchingUpAfterSeek)
                                {
                                    // キャッチアップ完了：このフレームの時刻を基準にクロックを解凍する。
                                    // 凍結中に実時間が進んでいないため、ここで desired 再生状態へ
                                    // 復帰しても「逃げ続ける目標」問題は起きない。
                                    Trace($"CatchUp done pts={ptsSeconds:F3} desiredPlaying={_desiredPlaying}");
                                    _catchingUpAfterSeek = false;
                                    _extBaseSeconds = ptsSeconds;
                                    _extClock.Restart();
                                    _extPlaying = _desiredPlaying;
                                }

                                var localBuf = managedBuf;
                                int frameGen = myGen;
                                int frameW = w, frameH = h;
                                int frameStride = stride;
                                double shownPts = ptsSeconds;
                                _lastShownPtsSeconds = ptsSeconds;

                                // DNN超解像（段階6）：EnsureEngine（初回はTensorRTエンジンの
                                // 実ビルドが走り数十秒かかることがある）を絶対にデコードスレッド上で
                                // 同期実行しない。ビルドはバックグラウンドTaskへ逃がし、完了するまでの
                                // フレームは等倍のまま表示継続する（真っ黒/デコード停止を防ぐ）。
                                //
                                // 推論本体（TensorRT）も同様にバックグラウンドTaskへ逃がし、
                                // デコードスレッドはブロックせず次のフレームへ進めるようにする
                                // （パイプライン化）。前フレームの推論がまだ終わっていない間は
                                // このフレームのDNN処理も表示更新自体もスキップする（直前の
                                // DNN結果をそのまま表示し続ける＝処理落ち時はコマ落ちするが、
                                // 等倍とのちらつきは起きない）。音声・クロック・デコード自体は
                                // 通常通り止めずに進む。
                                bool dnnSkippedThisFrame = false;
                                if (_dnnSrEnabled && GpuPresenter != null && _dnnSr != null)
                                {
                                    if (_dnnSr.IsReadyFor(w, h))
                                    {
                                        // DNNモードでエンジン準備済みの間は、推論開始トリガーになった
                                        // フレームも含めて常に表示更新をスキップし、非同期タスクの
                                        // 完了コールバック側だけがPresentする。そうしないと「等倍→
                                        // 推論完了で4倍→次の推論開始でまた等倍→…」とGPU側の
                                        // EnsureSizeが毎回行き来してテクスチャの再確保が頻発してしまう。
                                        dnnSkippedThisFrame = true;

                                        if (!_dnnInferenceBusy)
                                        {
                                            _dnnInferenceBusy = true;
                                            // 【16の倍数アライメント対応】
                                            // upW/upH: 実際に表示する最終サイズ（元解像度×Scale、パディング分を
                                            //   除いたクロップ後のサイズ）。EnsureSize/PresentDnnHalf*系にはこちらを渡す。
                                            // alignedUpW/alignedUpH: TensorRTが実際に読み書きするパディング込みの
                                            //   サイズ（AlignedWidth/AlignedHeight×Scale）。CUDA相互運用バッファの
                                            //   確保・登録、およびTryInferZeroCopy/TryInferWithCudaOutputの引数には
                                            //   こちらを渡す（DnnSuperResolutionEngine側のドキュメント参照）。
                                            int upW = w * DnnScale, upH = h * DnnScale;
                                            var dnnLocal = _dnnSr;
                                            int alignedW = dnnLocal.AlignedWidth, alignedH = dnnLocal.AlignedHeight;
                                            int alignedUpW = alignedW * dnnLocal.Scale, alignedUpH = alignedH * dnnLocal.Scale;
                                            var gp = GpuPresenter;
                                            // 次にこのバッファへ書き込むのは前回のTask.Runが
                                            // 完了した後だけ（_dnnInferenceBusyで保証）なので、
                                            // フレーム毎の新規配列確保(Clone)を避け、使い回しの
                                            // 単一バッファへBlockCopyするだけにする。
                                            if (_dnnFrameBuf == null || _dnnFrameBuf.Length != bufSize)
                                                _dnnFrameBuf = new byte[bufSize];
                                            Buffer.BlockCopy(managedBuf, 0, _dnnFrameBuf, 0, bufSize);
                                            var bufCopy = _dnnFrameBuf;
                                            int bw = w, bh = h;
                                            int bStride = frameStride;
                                            Task.Run(() =>
                                            {
                                                try
                                                {
                                                    // 追加最適化: 入力ゼロコピー(段階6-3-1)＋IoBinding永続化に続き、
                                                    // ConvertBgraToNchwHalfGpuのUIスレッド同期待ち(_ui.Invoke)を排除。
                                                    // GpuFramePresenter側でID3D11Multithread.SetMultithreadProtected(true)
                                                    // を有効化し、かつ_gpuLockでImmediate Context操作を排他制御するように
                                                    // したため、この背景スレッド（DNN推論スレッド）から直接呼び出せる。
                                                    // これによりUI描画のタイミング（フレームレンダリング中など）による
                                                    // 推論スレッドのブロッキングが無くなる。
                                                    //
                                                    // CUDA相互運用バッファは、TensorRTが実際に読み書きするパディング込み
                                                    // サイズ（アライメント後）で確保・登録する。
                                                    IntPtr outCudaBufPtr = gp.EnsureDnnCudaBufferAndGetNativePointer(alignedUpW, alignedUpH);
                                                    IntPtr inCudaBufPtr = gp.EnsureDnnInputCudaBufferAndGetNativePointer(alignedW, alignedH);

                                                    bool zeroCopyOk = false;
                                                    if (outCudaBufPtr != IntPtr.Zero && inCudaBufPtr != IntPtr.Zero)
                                                    {
                                                        bool convOk = false;
                                                        try
                                                        {
                                                            // アップロード自体は元解像度(bw,bh)のまま。GPU側のCSBgraToNchwが
                                                            // 書き込み先(アライメント後サイズ)の行幅を別途知っているため、
                                                            // ここで渡すのは元解像度でよい（パディング領域は変換しない）。
                                                            convOk = gp.ConvertBgraToNchwHalfGpu(bufCopy, bw, bh, bStride);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            Trace($"ConvertBgraToNchwHalfGpu failed: {ex.Message}");
                                                        }

                                                        if (convOk)
                                                        {
                                                            // TryInferZeroCopyの引数はアライメント後サイズで統一する契約
                                                            // （DnnSuperResolutionEngine.TryInferZeroCopyのdocコメント参照）。
                                                            zeroCopyOk = dnnLocal.TryInferZeroCopy(
                                                                inCudaBufPtr, alignedW, alignedH, outCudaBufPtr, alignedUpW, alignedUpH);
                                                        }
                                                    }

                                                    if (zeroCopyOk)
                                                    {
                                                        _ui.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                                                        {
                                                            if (myGen != _generation) return;
                                                            try
                                                            {
                                                                // 表示・EnsureSizeにはクロップ後の最終サイズ(upW/upH)を渡す。
                                                                // GpuFramePresenter側がパディング込みバッファ(alignedUpW/H)
                                                                // から自動的にこのサイズへ切り出して表示する。
                                                                gp.EnsureSize(upW, upH);
                                                                gp.PresentDnnHalfAlreadyInBuffer(upW, upH);
                                                                FrameDisplayed?.Invoke(shownPts);
                                                            }
                                                            catch (Exception ex) { Trace($"PresentDnnHalfAlreadyInBuffer skipped: {ex.Message}"); }
                                                        }));
                                                    }
                                                    // 入力ゼロコピーが使えない環境（シェーダ未コンパイル等）向けフォールバック：
                                                    // 入力はCPU変換のまま、出力のみゼロコピー（段階6-3-4、従来どおり）。
                                                    // 出力バッファ・TryInferWithCudaOutputへの引数もアライメント後サイズで統一する。
                                                    else if (outCudaBufPtr != IntPtr.Zero &&
                                                        dnnLocal.TryInferWithCudaOutput(bufCopy, bw, bh, outCudaBufPtr, alignedUpW, alignedUpH))
                                                    {
                                                        _ui.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                                                        {
                                                            if (myGen != _generation) return;
                                                            try
                                                            {
                                                                gp.EnsureSize(upW, upH);
                                                                gp.PresentDnnHalfAlreadyInBuffer(upW, upH);
                                                                FrameDisplayed?.Invoke(shownPts);
                                                            }
                                                            catch (Exception ex) { Trace($"PresentDnnHalfAlreadyInBuffer skipped: {ex.Message}"); }
                                                        }));
                                                    }
                                                    // CPUフォールバック（TryInferToNchwHalf）はDnnSuperResolutionEngine側で
                                                    // 既にパディング分をクロップ済みの配列(uw x uh = w*Scale x h*Scale)を
                                                    // 返すため、ここは元々の呼び方のままでよい（変更不要）。
                                                    else if (dnnLocal.TryInferToNchwHalf(bufCopy, bw, bh, out var half, out var uw, out var uh))
                                                    {
                                                        _ui.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                                                        {
                                                            if (myGen != _generation) return;
                                                            try
                                                            {
                                                                gp.EnsureSize(uw, uh);
                                                                gp.PresentDnnHalf(half, uw, uh);
                                                                FrameDisplayed?.Invoke(shownPts);
                                                            }
                                                            catch (Exception ex) { Trace($"PresentDnnHalf skipped: {ex.Message}"); }
                                                        }));
                                                    }
                                                }
                                                finally
                                                {
                                                    _dnnInferenceBusy = false;
                                                }
                                            });
                                        }
                                    }
                                    else if (_dnnBuildTask == null ||
                                             (_dnnBuildTask.IsCompleted && (_dnnBuildW != w || _dnnBuildH != h)))
                                    {
                                        _dnnBuildW = w;
                                        _dnnBuildH = h;
                                        var dnn = _dnnSr;
                                        int bw = w, bh = h;
                                        Trace($"DNN超解像エンジンのバックグラウンドビルド開始: {bw}x{bh}（初回は数十秒かかることがあります）");
                                        RaiseDnnBuildState(true);
                                        var sw = System.Diagnostics.Stopwatch.StartNew();
                                        _dnnBuildTask = Task.Run(() =>
                                        {
                                            bool ok = dnn.EnsureEngine(bw, bh);
                                            sw.Stop();
                                            Trace(ok
                                                ? $"DNN超解像エンジンのビルド完了: {bw}x{bh} ({sw.ElapsedMilliseconds}ms)"
                                                : $"DNN超解像エンジンのビルド失敗: {bw}x{bh} ({sw.ElapsedMilliseconds}ms)");
                                            RaiseDnnBuildState(false);
                                        });
                                    }
                                    // ビルド中/未完了のこのフレームは等倍のまま何もしない
                                }

                                if (!dnnSkippedThisFrame)
                                {
                                    _ui.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                                    {
                                        if (frameGen != _generation) return;
                                        try
                                        {
                                            // WriteableBitmapフォールバック側は常に等倍（DNNの影響を受けない）
                                            Bitmap?.WritePixels(new Int32Rect(0, 0, w, h), managedBuf, stride, 0);
                                            GpuPresenter?.EnsureSize(frameW, frameH);
                                            GpuPresenter?.Present(localBuf, frameW, frameH, frameStride);
                                            FrameDisplayed?.Invoke(shownPts);
                                            if (!diagFirstDisplayLogged)
                                            {
                                                Trace($"[DIAG] first frame displayed (gen={frameGen}) pts={shownPts:F3}");
                                                diagFirstDisplayLogged = true;
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Trace($"WritePixels skipped: {ex.Message}");
                                        }
                                    }));
                                }
                            }
                        }
                    }
                    else if (pkt->stream_index == audioIdx && audioPktChannel != null)
                    {
                        // デコード自体はAudioDecodeLoop（専用スレッド）で行う。ここでは
                        // パケットの所有権をそちらへ渡すだけ（映像の待機処理と非同期化）。
                        AVPacket* apkt = ffmpeg.av_packet_alloc();
                        ffmpeg.av_packet_move_ref(apkt, pkt);
                        var aitem = new DemuxedPacket((IntPtr)apkt);
                        while (!audioPktChannel.Writer.TryWrite(aitem))
                        {
                            if (myGen != _generation)
                            {
                                var ap = apkt;
                                ffmpeg.av_packet_free(&ap);
                                break;
                            }
                            diagAudioForwardBlockMs++; // 今回追加: audioPktChannelが満杯でメインループが
                                                       // ブロックしている時間(ms相当)を計測する診断用
                            Thread.Sleep(1);
                        }
                    }

                    ffmpeg.av_packet_unref(pkt);
                }
            }
            catch (Exception ex)
            {
                Trace($"DecodeThread(gen={myGen}) error: {ex}");
                int failGen = myGen;
                _ui.BeginInvoke(new Action(() =>
                {
                    if (failGen != _generation) return;
                    Failed?.Invoke(ex);
                }));
            }
            finally
            {
                // ── Stage1: Demuxスレッドの停止・後始末 ──
                // fmtを解放する前に、Demuxスレッドが確実にfmtへアクセスしなくなったことを
                // 保証する必要がある（use-after-free防止）。interrupt_callback経由で
                // 低速I/O待ち中でも即座にav_read_frameから脱出させ、myGen!=_generationの
                // チェックで自然に終了させる。
                if (demuxThread != null)
                {
                    _demuxInterruptFlag = 1;
                    demuxJoinedCleanly = demuxThread.Join(5000);
                    if (!demuxJoinedCleanly)
                        Trace($"DemuxPrefetch(gen={myGen}): 5000ms待っても終了しなかった - リーク疑いあり、fmtの解放をスキップ（クラッシュ回避優先）");
                    if (_demuxThread == demuxThread) _demuxThread = null;
                }
                if (pktChannel != null)
                {
                    pktChannel.Writer.TryComplete();
                    while (pktChannel.Reader.TryRead(out var leftover))
                    {
                        var lp = (AVPacket*)leftover.PktPtr;
                        if (lp != null) ffmpeg.av_packet_free(&lp);
                    }
                    if (_pktChannel == pktChannel) _pktChannel = null;
                }
                if (interruptFlagPinned)
                {
                    if (demuxJoinedCleanly)
                    {
                        interruptFlagHandle.Free();
                    }
                    else
                    {
                        // Demuxスレッドが未終了の場合、内部でinterrupt_callback.opaque
                        // （このpin留め配列へのポインタ）をまだ参照している可能性があるため、
                        // fmtと同様に解放を見送る（意図的リーク、GC破損回避優先）。
                        Trace($"DecodeThread(gen={myGen}): Demux未終了のためinterruptFlagのpin留め解放も見送り");
                    }
                }

                // ── 音声デコードスレッドの停止・後始末（今回追加）──
                // actx/swr/audioFrame/audioOutputを解放する前に、AudioDecodeThreadが確実に
                // それらへアクセスしなくなったことを保証する（use-after-free防止）。
                if (audioDecodeThread != null)
                {
                    audioDecodeJoinedCleanly = audioDecodeThread.Join(5000);
                    if (!audioDecodeJoinedCleanly)
                        Trace($"AudioDecodeLoop(gen={myGen}): 5000ms待っても終了しなかった - リーク疑いあり、音声リソースの解放をスキップ（クラッシュ回避優先）");
                }
                if (audioPktChannel != null)
                {
                    audioPktChannel.Writer.TryComplete();
                    while (audioPktChannel.Reader.TryRead(out var leftoverA))
                    {
                        var lap = (AVPacket*)leftoverA.PktPtr;
                        if (lap != null) ffmpeg.av_packet_free(&lap);
                    }
                }

                FreeDenoiseFilter();
                if (rgbBuffer != null) ffmpeg.av_free(rgbBuffer);
                if (frame != null) { var f2 = frame; ffmpeg.av_frame_free(&f2); }
                if (swFrame != null) { var f4 = swFrame; ffmpeg.av_frame_free(&f4); }
                if (rgbFrame != null) { var f3 = rgbFrame; ffmpeg.av_frame_free(&f3); }
                if (pkt != null) { var p2 = pkt; ffmpeg.av_packet_free(&p2); }
                if (sws != null) ffmpeg.sws_freeContext(sws);

                // ── 音声関連の後始末（今回追加）──
                if (audioDecodeJoinedCleanly)
                {
                    try { audioOutput?.Dispose(); } catch { }
                    if (audioFrame != null) { var af = audioFrame; ffmpeg.av_frame_free(&af); }
                    if (swr != null) { var s3 = swr; ffmpeg.swr_free(&s3); }
                    if (actx != null) { var a3 = actx; ffmpeg.avcodec_free_context(&a3); }
                }
                else
                {
                    Trace($"DecodeThread(gen={myGen}): AudioDecodeThread未終了のため音声リソースの解放を見送り（意図的リーク、クラッシュ回避優先）");
                }

                // hw_device_ctx は avcodec_free_context() が内部で解放するため、
                // ここで自分で av_buffer_unref すると二重解放になり
                // ExecutionEngineException（ネイティブ側のメモリ破壊）の原因になる
                if (vctx != null) { var v = vctx; ffmpeg.avcodec_free_context(&v); }
                if (demuxJoinedCleanly)
                {
                    if (fmt != null) { var f = fmt; ffmpeg.avformat_close_input(&f); }
                }
                else
                {
                    Trace($"DecodeThread(gen={myGen}): Demux未終了のためfmtの解放を見送り（意図的リーク、クラッシュ回避優先）");
                }
                Trace($"DecodeThread(gen={myGen}) fully exited");
            }
        }

        // ── 音声デコード専用スレッド本体（今回追加）──
        // audioPktChannelから音声パケットを取り出し、デコード→swresample→IAudioOutputへの
        // 送出までをこのスレッド内だけで完結させる。映像デコードループ側のThread.Sleep
        // （フレーム待機）とは完全に非同期なため、映像が待っている間も音声は途切れない。
        // actx/swrへのアクセスはこのスレッドと、Seek時のPauseAudioDecodeForSeek/
        // ResumeAudioDecodeAfterSeekハンドシェイク経由のメインスレッドだけに限定している
        // （AVCodecContext/SwrContextはスレッドセーフではないため）。
        private void AudioDecodeLoop(AVCodecContext* actx, SwrContext* swr, AVFrame* audioFrame,
            IAudioOutput audioOutput, Channel<DemuxedPacket> channel, int myGen, double audioTimeBase)
        {
            byte[]? convBuf = null;
            float[]? floatBuf = null;
            AVPacket* localPkt = ffmpeg.av_packet_alloc();
            var diagSw = Stopwatch.StartNew();
            double diagLastLogged = 0;
            int diagPacketsProcessed = 0;
            long diagSubmitTicksSum = 0;
            bool needOffsetCapture = true; // 今回追加: (再)開始後、最初のフレームの実ptsを捕捉する
            bool forceOffsetRecapture = false; // 今回追加: ボイス再作成時、seekのターゲット固定とは
                                               // 別に「本当に実ptsで取り直す」ことを明示するフラグ
            try
            {
                while (myGen == _generation)
                {
                    if (_audioSeekInterruptFlag != 0)
                    {
                        _audioSeekAcked = 1;
                        while (myGen == _generation && _audioSeekInterruptFlag != 0)
                            Thread.Sleep(1);
                        _audioSeekAcked = 0;
                        needOffsetCapture = true; // Seek後の最初のフレームでオフセットを取り直す
                        continue;
                    }

                    if (!channel.Reader.TryRead(out var item))
                    {
                        if (channel.Reader.Completion.IsCompleted) break;
                        Thread.Sleep(2);
                        continue;
                    }

                    AVPacket* src = (AVPacket*)item.PktPtr;
                    ffmpeg.av_packet_move_ref(localPkt, src);
                    ffmpeg.av_packet_free(&src);
                    diagPacketsProcessed++;

                    if (ffmpeg.avcodec_send_packet(actx, localPkt) == 0)
                    {
                        while (myGen == _generation && ffmpeg.avcodec_receive_frame(actx, audioFrame) == 0)
                        {
                            if (needOffsetCapture)
                            {
                                // このフレームがXAudio2へ送る最初のサンプルになる時点で、
                                // GetPositionSeconds()はまだ0（このフレーム分もこれから送出する
                                // ため）。よってこのフレームの実pts＝コンテンツ上の開始オフセット
                                // として、以降ずっとこの分を加算する。
                                // 初回オープン時（_audioContentOffsetSecondsがまだ0の場合）、または
                                // ボイス再作成直後（forceOffsetRecapture）は音声の実ptsから取得し、
                                // 通常のシーク後の場合はメインスレッド側（Seekで設定したターゲット秒数）を維持する
                                if (_audioContentOffsetSeconds == 0.0 || forceOffsetRecapture)
                                {
                                    long rawPts = audioFrame->pts != ffmpeg.AV_NOPTS_VALUE ? audioFrame->pts : audioFrame->best_effort_timestamp;
                                    _audioContentOffsetSeconds = rawPts != ffmpeg.AV_NOPTS_VALUE ? rawPts * audioTimeBase : 0.0;
                                    forceOffsetRecapture = false;
                                }
                                needOffsetCapture = false;
                            }
                            // withAudio:false（コマ送り/シークバードラッグ中のプレビュー）中は
                            // ボイスがStart()されないため、送出し続けるとXAudio2側のキューが
                            // 溜まる一方になり、前回追加したキュー上限バックプレッシャー
                            // （58個で最大500ms待機）に毎回引っかかって音声デコードスレッド全体が
                            // 止まってしまう。Seek時の音声スレッド一時停止ハンドシェイクも巻き添えで
                            // 遅延し、「シーク中/ドラッグ中に映像が追従しない」不具合の原因になっていた。
                            // 音声が実際に鳴らない間は送出自体をスキップする。
                            if (audioOutput.IsActive && _audioDesired)
                            {
                                int outSamples = ffmpeg.swr_get_out_samples(swr, audioFrame->nb_samples);
                                if (outSamples > 0)
                                {
                                    int outBufSize = outSamples * AudioOutChannels * sizeof(float);
                                    if (convBuf == null || convBuf.Length < outBufSize)
                                        convBuf = new byte[outBufSize];

                                    int converted;
                                    fixed (byte* pOut = convBuf)
                                    {
                                        byte* outPtr = pOut;
                                        converted = ffmpeg.swr_convert(swr, &outPtr, outSamples,
                                            audioFrame->extended_data, audioFrame->nb_samples);
                                    }

                                    if (converted > 0)
                                    {
                                        int floatCount = converted * AudioOutChannels;
                                        if (floatBuf == null || floatBuf.Length < floatCount)
                                            floatBuf = new float[floatCount];
                                        System.Buffer.BlockCopy(convBuf, 0, floatBuf, 0, floatCount * sizeof(float));
                                        long t0 = Stopwatch.GetTimestamp();
                                        audioOutput.SubmitSamples(floatBuf, converted);
                                        if (audioOutput.ConsumeRecreated())
                                        {
                                            needOffsetCapture = true;
                                            forceOffsetRecapture = true; // seekのターゲット固定を上書きしてでも実ptsで取り直す
                                        }
                                        diagSubmitTicksSum += Stopwatch.GetTimestamp() - t0;
                                    }
                                }
                            }
                            ffmpeg.av_frame_unref(audioFrame);
                        }
                    }
                    ffmpeg.av_packet_unref(localPkt);

                    if (diagSw.Elapsed.TotalSeconds - diagLastLogged >= 1.0)
                    {
                        double submitMs = diagSubmitTicksSum * 1000.0 / Stopwatch.Frequency;
                        int backlog = -1;
                        try { backlog = channel.Reader.Count; } catch { }
                        Trace($"[DIAG-Audio] wall={diagSw.Elapsed.TotalSeconds:F2}s packets={diagPacketsProcessed} backlog={backlog} submitTotalMs={submitMs:F1}");
                        diagLastLogged = diagSw.Elapsed.TotalSeconds;
                        diagPacketsProcessed = 0;
                        diagSubmitTicksSum = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace($"AudioDecodeLoop(gen={myGen}) error: {ex.Message}");
            }
            finally
            {
                if (localPkt != null) { var lp = localPkt; ffmpeg.av_packet_free(&lp); }
                Trace($"AudioDecodeLoop(gen={myGen}) exited");
            }
        }

        // ── Stage1: パケット先読みスレッド本体 ──
        // av_read_frameをこの専用スレッドで回し、videoIdx/audioIdxに一致するパケットだけを
        // それぞれの専用Channelへ積む。fmtへアクセスするのはこのスレッドと、メインループの
        // Seek処理ブロック（ハンドシェイク経由でのみ）だけであることを前提にしている。
        // 【今回修正】当初は音声パケットも映像と同じchannelへ積み、メインループ（映像デコード
        // ループ）側で音声用channelへ中継していたが、映像側のペーシングSleep（最大200ms/フレーム）
        // で同じスレッドが頻繁に止まるため、その間ずっと音声パケットも運ばれず、結果的に
        // 「数秒おきに音声がまとめて届く→XAudio2が残り時間ずっと無音で干上がる→音声由来の
        // マスタークロックが進まない→映像がさらに待つ」という自己増殖ループになっていた。
        // Demuxスレッドは映像の待機とは無関係に走り続けるため、ここで直接振り分ける。
        private void DemuxPrefetchLoop(AVFormatContext* fmt, int videoIdx, int audioIdx, int myGen,
            Channel<DemuxedPacket> videoChannel, Channel<DemuxedPacket>? audioChannel)
        {
            try
            {
                while (myGen == _generation)
                {
                    // メインスレッドがSeek処理中。fmtへのアクセスを完全に止めて確認応答を出し、
                    // フラグが解除されるまで待つ（fmtへの同時アクセスを避けるための唯一の関門）。
                    if (_demuxInterruptFlag != 0)
                    {
                        _demuxAcked = 1;
                        while (myGen == _generation && _demuxInterruptFlag != 0)
                            Thread.Sleep(1);
                        _demuxAcked = 0;
                        continue;
                    }

                    AVPacket* p = ffmpeg.av_packet_alloc();
                    int rr = ffmpeg.av_read_frame(fmt, p);
                    if (rr < 0)
                    {
                        ffmpeg.av_packet_free(&p);
                        if (rr == AvErrorExit)
                        {
                            // interrupt_callbackによる中断（Seek要求）。次ループ先頭で
                            // _demuxInterruptFlagを検知し待機状態に入る。
                            continue;
                        }
                        // 本当のEOF、またはI/Oエラー
                        Trace($"DemuxPrefetch(gen={myGen}): end of stream/error rr={rr}");
                        videoChannel.Writer.TryComplete();
                        audioChannel?.Writer.TryComplete();
                        return;
                    }

                    Channel<DemuxedPacket>? destChannel =
                        p->stream_index == videoIdx ? videoChannel :
                        p->stream_index == audioIdx ? audioChannel :
                        null;

                    if (destChannel == null)
                    {
                        ffmpeg.av_packet_free(&p);
                        continue;
                    }

                    var item = new DemuxedPacket((IntPtr)p);
                    bool droppedForSeek = false;
                    while (!destChannel.Writer.TryWrite(item))
                    {
                        if (myGen != _generation)
                        {
                            ffmpeg.av_packet_free(&p);
                            return;
                        }
                        if (_demuxInterruptFlag != 0)
                        {
                            // Channel満杯のままSeekが来た。このパケットは古くなるので破棄し、
                            // 次ループ先頭のフラグ待機ブロックへ入る。
                            ffmpeg.av_packet_free(&p);
                            droppedForSeek = true;
                            break;
                        }
                        Thread.Sleep(1);
                    }
                    if (droppedForSeek) continue;
                }
            }
            catch (Exception ex)
            {
                Trace($"DemuxPrefetch(gen={myGen}) error: {ex}");
                try { videoChannel.Writer.TryComplete(ex); } catch { }
                try { audioChannel?.Writer.TryComplete(ex); } catch { }
            }
            finally
            {
                Trace($"DemuxPrefetch(gen={myGen}) exited");
            }
        }

        /// <summary>Channelから1パケット取り出し、再利用中のpktへav_packet_move_refで
        /// 移し替える。取り出せた場合true。Channelが完了(EOF/エラー)している場合はeof=trueで
        /// false、世代交代で打ち切られた場合はeof=falseでfalseを返す。</summary>
        private bool TryDequeuePrefetchedPacket(Channel<DemuxedPacket> channel, int myGen, AVPacket* pkt, out bool eof)
        {
            eof = false;
            while (myGen == _generation)
            {
                if (channel.Reader.TryRead(out var item))
                {
                    AVPacket* src = (AVPacket*)item.PktPtr;
                    ffmpeg.av_packet_move_ref(pkt, src);
                    ffmpeg.av_packet_free(&src);
                    return true;
                }
                if (channel.Reader.Completion.IsCompleted)
                {
                    eof = true;
                    return false;
                }
                Thread.Sleep(2);
            }
            return false;
        }

        private static void OpenStreamsWithHw(string path, bool wantHw, IntPtr interruptFlagPtr,
            out AVFormatContext* fmt, out AVCodecContext* vctx, out int videoIdx,
            out bool hwActive, out AVBufferRef* hwDeviceCtx)
        {
            hwActive = false;
            hwDeviceCtx = null;

            // interruptFlagPtr != IntPtr.Zero（＝Stage1先読み有効）の場合のみ、
            // avformat_open_inputより先にinterrupt_callbackを仕込む必要があるため、
            // avformat_alloc_contextで先にコンテキストを確保してから開く。
            AVFormatContext* f = ffmpeg.avformat_alloc_context();
            if (f == null)
                throw new InvalidOperationException("avformat_alloc_context failed");

            if (interruptFlagPtr != IntPtr.Zero)
            {
                f->interrupt_callback.callback = _interruptCallback;
                f->interrupt_callback.opaque = (void*)interruptFlagPtr;
            }

            if (ffmpeg.avformat_open_input(&f, path, null, null) != 0)
                throw new InvalidOperationException($"avformat_open_input failed: {path}");
            fmt = f;

            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                throw new InvalidOperationException("avformat_find_stream_info failed");

            AVCodec* vcodec = null;
            videoIdx = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &vcodec, 0);
            if (videoIdx < 0 || vcodec == null)
                throw new InvalidOperationException("no video stream");

            var vc = ffmpeg.avcodec_alloc_context3(vcodec);
            ffmpeg.avcodec_parameters_to_context(vc, fmt->streams[videoIdx]->codecpar);

            Trace($"OpenStreamsWithHw: codec={ByteToString(vcodec->name)} wantHw={wantHw}");

            if (wantHw)
            {
                try
                {
                    AVPixelFormat hwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
                    int cfgCount = 0;
                    for (int i = 0; ; i++)
                    {
                        var cfg = ffmpeg.avcodec_get_hw_config(vcodec, i);
                        if (cfg == null) break;
                        cfgCount++;
                        Trace($"  hw_config[{i}]: device_type={cfg->device_type} pix_fmt={cfg->pix_fmt} methods=0x{cfg->methods:X}");
                        if ((cfg->methods & 0x01 /* AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX（AutoGenに列挙が無いため直値） */) != 0
                            && cfg->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)
                        {
                            hwPixFmt = cfg->pix_fmt;
                            break;
                        }
                    }
                    Trace($"OpenStreamsWithHw: hw_config候補数={cfgCount} 選択pix_fmt={hwPixFmt}");

                    if (hwPixFmt != AVPixelFormat.AV_PIX_FMT_NONE)
                    {
                        AVBufferRef* devCtx = null;
                        int devRet = ffmpeg.av_hwdevice_ctx_create(&devCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
                        if (devRet == 0)
                        {
                            _negotiatedHwPixFmt = hwPixFmt;
                            vc->get_format = _getHwFormatDelegate;
                            vc->hw_device_ctx = ffmpeg.av_buffer_ref(devCtx);
                            ffmpeg.av_buffer_unref(&devCtx);
                            hwDeviceCtx = vc->hw_device_ctx;
                            hwActive = true;
                            Trace("OpenStreamsWithHw: av_hwdevice_ctx_create(D3D11VA) 成功、hw_device_ctx設定完了");
                        }
                        else
                        {
                            Trace($"OpenStreamsWithHw: av_hwdevice_ctx_create(D3D11VA) 失敗 code={devRet} - SWにフォールバック");
                        }
                    }
                    else
                    {
                        Trace("OpenStreamsWithHw: このコーデックはD3D11VAの対応構成が見つからない - SWにフォールバック");
                    }
                }
                catch (Exception ex)
                {
                    Trace($"OpenStreamsWithHw: HW初期化中に例外 - SWにフォールバック: {ex.Message}");
                    hwActive = false;
                }
            }

            int openRet = ffmpeg.avcodec_open2(vc, vcodec, null);
            if (openRet < 0)
            {
                if (hwActive)
                {
                    Trace($"OpenStreamsWithHw: HWデコーダのopenに失敗 code={openRet} - SWで再試行");
                    hwActive = false;
                    vc->get_format = null;
                    if (vc->hw_device_ctx != null) { var h2 = vc->hw_device_ctx; ffmpeg.av_buffer_unref(&h2); vc->hw_device_ctx = null; }
                    if (ffmpeg.avcodec_open2(vc, vcodec, null) < 0)
                        throw new InvalidOperationException("avcodec_open2(video) failed");
                }
                else
                {
                    throw new InvalidOperationException("avcodec_open2(video) failed");
                }
            }
            Trace($"OpenStreamsWithHw: avcodec_open2完了 hwActive={hwActive} vc->pix_fmt(SW側の想定値)={vc->pix_fmt}");
            vctx = vc;
        }

        private static unsafe string ByteToString(byte* ptr)
        {
            try { return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)ptr) ?? "?"; }
            catch { return "?"; }
        }

        // ─────────────────────────────────────────────────────────────
        // 簡易デインターレース（ブレンド方式）
        // 各ラインを直下のラインと平均化し、横縞(コーミング)を軽減する。
        // yadif等のフィルタグラフを使わないため画質は簡易的だが、失敗リスクが低い。
        // ─────────────────────────────────────────────────────────────
        private void ApplyDeinterlaceBlend(byte[] buf, int width, int height, int stride)
        {
            for (int y = 0; y < height - 1; y++)
            {
                int row = y * stride;
                int nextRow = row + stride;
                for (int x = 0; x < stride; x++)
                {
                    buf[row + x] = (byte)((buf[row + x] + buf[nextRow + x]) >> 1);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // コントラスト / 彩度 / ガンマ（BGRAバッファへ直接適用）
        // ─────────────────────────────────────────────────────────────
        private byte[]? _lut;
        private double _lutContrast = double.NaN, _lutGamma = double.NaN;

        private byte[] GetLut(double contrast, double gamma)
        {
            if (_lut != null && _lutContrast == contrast && _lutGamma == gamma) return _lut;
            var lut = new byte[256];
            double gammaExp = Math.Pow(2, gamma);      // -1〜1 → 0.5〜2.0
            double factor = 1.0 + contrast;             // -1〜1 → 0〜2.0
            for (int i = 0; i < 256; i++)
            {
                double v = Math.Pow(i / 255.0, 1.0 / gammaExp) * 255.0;
                v = (v - 128) * factor + 128;
                lut[i] = (byte)Math.Clamp(v, 0, 255);
            }
            _lut = lut; _lutContrast = contrast; _lutGamma = gamma;
            return lut;
        }

        private void ApplyEffects(byte[] buf, int len)
        {
            double contrast = _contrast, saturation = _saturation, gamma = _gamma;
            byte[]? lut = (contrast != 0 || gamma != 0) ? GetLut(contrast, gamma) : null;
            double satFactor = 1.0 + saturation;

            for (int i = 0; i + 3 < len; i += 4)
            {
                byte b = buf[i], g = buf[i + 1], r = buf[i + 2]; // BGRA
                if (lut != null) { b = lut[b]; g = lut[g]; r = lut[r]; }
                if (saturation != 0)
                {
                    double gray = 0.299 * r + 0.587 * g + 0.114 * b;
                    r = (byte)Math.Clamp(gray + (r - gray) * satFactor, 0, 255);
                    g = (byte)Math.Clamp(gray + (g - gray) * satFactor, 0, 255);
                    b = (byte)Math.Clamp(gray + (b - gray) * satFactor, 0, 255);
                }
                buf[i] = b; buf[i + 1] = g; buf[i + 2] = r;
            }
        }

        private static void Trace(string msg)
        {
#if DEBUG
            try
            {
                File.AppendAllText(
                    AppLogPaths.GetPath("trace.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} | [AVEngine] {msg}{Environment.NewLine}",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
#endif
        }
    }
}