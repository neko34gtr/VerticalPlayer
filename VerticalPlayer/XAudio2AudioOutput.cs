using System;
using Vortice.Multimedia;
using Vortice.XAudio2;

namespace VerticalPlayer
{
    /// <summary>
    /// IAudioOutputのVortice.XAudio2版実装。
    ///
    /// マスタークロックは IXAudio2SourceVoice.State.SamplesPlayed（ボイス作成以降の累計再生
    /// サンプル数）から算出する。Flush()（Seek時）ではボイス自体は再利用しつつ、その時点の
    /// SamplesPlayedをオフセットとして記録することで「Flush後の経過秒数」を0から数え直す。
    ///
    /// 【今回追加】再生中に出力デバイスが切り替わる（既定デバイス変更／切断）と、
    /// _sourceVoice.State等へのCOM呼び出しが例外を投げるようになる。これを素通りさせると
    /// 呼び出し元（AVEngine.AudioDecodeLoop / DecodeThread）のtry/catchまで抜けてスレッドが
    /// 終了し、「デバイス切替時に再生が完全に止まったまま戻らない」不具合になっていた。
    /// 対策は二段構え：
    ///   (1) AudioDeviceChangeNotifier経由でWindowsの既定デバイス変更通知を受け、
    ///       検知した時点でバックグラウンドにXAudio2エンジン全体を再構築する（積極対応）
    ///   (2) SubmitSamples/GetPositionSeconds等のCOM呼び出しを例外から保護し、
    ///       万一(1)で拾いきれなかった場合でも例外は握りつぶしてエンジン再構築を
    ///       トリガーするだけに留め、呼び出し元スレッドは絶対に落とさない（保険）
    /// 再構築後はIsActiveは維持したままConsumeRecreated()経由で呼び出し元にオフセット再取得を
    /// 促す、という既存の仕組み（Seek劣化対策で導入済み）をそのまま流用している。
    ///
    /// 【要ビルド確認】この環境からnuget.orgへ到達できずコンパイル未検証。CreateMasteringVoice/
    /// CreateSourceVoice/AudioBuffer/WaveFormat.CreateCustomFormatの引数はVortice.Windows
    /// リポジトリのソース・Issueから確認したものだが、お使いのバージョンで多少シグネチャが
    /// 異なる場合はビルドエラー箇所を都度調整してください。
    /// </summary>
    public sealed class XAudio2AudioOutput : IAudioOutput
    {
        private IXAudio2? _xaudio2;
        private IXAudio2MasteringVoice? _masteringVoice;
        private IXAudio2SourceVoice? _sourceVoice;
        private WaveFormat? _format;

        private int _sampleRate;
        private int _channels;
        private ulong _samplesPlayedOffset; // Flush()時点のSamplesPlayedを引くためのオフセット（SamplesPlayedはulong）
        private readonly object _lock = new();
        private bool _recreatedSinceLastCheck;

        // 【今回追加】既定デバイス変更の検知と、エンジン再構築の多重実行防止
        private AudioDeviceChangeNotifier? _deviceNotifier;
        private int _recoveringFlag; // 0=待機中, 1=再構築処理中（Interlockedで排他）

        public bool ConsumeRecreated()
        {
            lock (_lock)
            {
                bool r = _recreatedSinceLastCheck;
                _recreatedSinceLastCheck = false;
                return r;
            }
        }

        public bool IsActive { get; private set; }

        public void Open(int sampleRate, int channels)
        {
            lock (_lock)
            {
                CloseInternal();

                _sampleRate = sampleRate;
                _channels = channels;

                // Float32 (IEEE Float) PCM固定。AVEngine側のswresample出力もこの形式に合わせる。
                _format = WaveFormat.CreateCustomFormat(
                    WaveFormatEncoding.IeeeFloat,
                    sampleRate,
                    channels,
                    sampleRate * channels * 4, // avgBytesPerSec (float=4byte)
                    channels * 4,              // blockAlign
                    32);                       // bitsPerSample

                BuildAudioObjects_CallerHoldsLock();
            }

            // 【今回追加】既定デバイス変更通知の登録はこのインスタンスの生存期間中1回だけでよい
            // （Open()はファイル切替のたびに呼ばれるため、_lockの外・かつnullチェックしてから）。
            // COM登録呼び出しを_lock内で行うと、通知コールバックが別スレッドから飛んできて
            // TriggerFullRecreateAsync→lock取得という経路でデッドロックする余地があるため、
            // 意図的に_lockの外に出している。
            if (_deviceNotifier == null)
            {
                _deviceNotifier = new AudioDeviceChangeNotifier(OnDefaultAudioDeviceChanged);
            }
        }

        /// <summary>_xaudio2/_masteringVoice/_sourceVoiceを_formatから新規に組み立てる。
        /// 呼び出し元が既に_lockを保持している前提のヘルパー（Open()・RecreateEngineFullyLockedから使用）。</summary>
        private void BuildAudioObjects_CallerHoldsLock()
        {
            _xaudio2 = XAudio2.XAudio2Create();
            // deviceIdを明示せずCreateMasteringVoiceを呼ぶことで仮想オーディオクライアント扱いに
            // なり、本来はOS側が既定デバイス変更を自動追従してくれるはずだが、実機では追従されない
            // ケースが報告されているため、AudioDeviceChangeNotifierによる明示的な再構築も併用する。
            _masteringVoice = _xaudio2.CreateMasteringVoice((uint)_channels, (uint)_sampleRate);
            _sourceVoice = _xaudio2.CreateSourceVoice(_format!, VoiceFlags.None);

            _samplesPlayedOffset = 0;
            IsActive = true;

            // 【以前修正済】ボイスは生成直後に一度Start()する。以前は「以後は動かしっぱなしにし、
            // 音を止めるのはSubmitSamples側の送出停止だけで行う」方式だったが、それだと一時停止時に
            // 既にキュー済みのバッファがそのまま鳴り続けてしまうため、現在はPause()で実際に
            // Stop()する方式に変更している（Pause()のコメント参照）。
            _sourceVoice.Start();
        }

        // XAudio2はソースボイスに同時キューできるバッファ数に上限があり(XAUDIO2_MAX_QUEUED_BUFFERS=64)、
        // 超えるとSubmitSourceBufferが失敗する。デコード側がバースト的に大量のパケットを一気に
        // 処理する場面（ファイル冒頭のプリフェッチ分消化時など）でこれを超えると、失敗した分の
        // 音声がキューされず丸ごと欠落し、「音声が突然先の内容に飛ぶ」形で聞こえる不具合になっていた。
        // 安全マージンを取ってこの数値未満に制御する。
        private const int MaxQueuedBuffers = 58;

        public void SubmitSamples(float[] interleaved, int frameCount)
        {
            if (_sourceVoice == null || !IsActive) return;

            try
            {
                // interleaved全体を使わずframeCount分だけ切り出したい場合に備え、必要なら縮める。
                byte[] bytes;
                int byteCount = frameCount * _channels * sizeof(float);
                bytes = new byte[byteCount];
                Buffer.BlockCopy(interleaved, 0, bytes, 0, byteCount);

                // キューが上限に近い場合、XAudio2が実際に再生してキューを消化するまで待つ。
                // これによりデコード側の投入速度が自然に実再生速度へペーシングされる。
                int spinUsed = 0;
                for (; spinUsed < 500; spinUsed++) // 500ms相当で諦める（無限ブロック防止）
                {
                    int queued;
                    lock (_lock)
                    {
                        if (_sourceVoice == null) return;
                        queued = (int)_sourceVoice.State.BuffersQueued;
                    }
                    if (queued < MaxQueuedBuffers) break;
                    System.Threading.Thread.Sleep(1);
                }
                if (spinUsed >= 500)
                {
                    // 500ms待ってもキューが消化されない＝ボイスが実質的に詰まって動いていないと
                    // 判断し、ソースボイス自体を作り直して復帰させる（デバイス切替ではなく、
                    // Seek連発等によるキュー詰まりの既知対策。エンジン自体は壊れていない前提の軽い復旧）。
                    System.Diagnostics.Debug.WriteLine("[XAudio2AudioOutput] キュー解消待ちが500msでタイムアウト。ソースボイスを再作成します。");
                    if (!RecreateSourceVoiceLocked())
                        return; // このサンプルは諦める。次回の呼び出しでも状況を見て再試行される
                }

                lock (_lock)
                {
                    if (_sourceVoice == null) return;
                    var buffer = new AudioBuffer(bytes, BufferFlags.None);
                    _sourceVoice.SubmitSourceBuffer(buffer);
                }
            }
            catch (Exception ex)
            {
                // 【今回追加】出力デバイスの切断/切替時、_sourceVoice.State や SubmitSourceBuffer
                // へのCOM呼び出しが例外を投げることがある。これを素通りさせると呼び出し元
                // AudioDecodeLoopのtry/catchまで抜けてスレッドごと終了し、「デバイス切替時に
                // 再生が完全に止まったまま戻らない」不具合の直接原因になっていた。
                // ここで捕捉し、このチャンクは諦めてエンジン再構築をバックグラウンドで
                // トリガーするだけに留める（呼び出し元スレッドは生かし続ける）。
                System.Diagnostics.Debug.WriteLine($"[XAudio2AudioOutput] SubmitSamples失敗（{ex.Message}）。エンジン再構築をトリガーします。");
                TriggerFullRecreateAsync("SubmitSamples失敗: " + ex.Message);
            }
        }

        /// <summary>ソースボイスのみを作り直す（Stop→Destroy→Create→Start、同一_xaudio2/_masteringVoiceを再利用）。
        /// キュー詰まりタイムアウト時専用の軽い復旧。呼び出し元は_lockを保持していない状態で呼ぶこと（内部でlockを取る）。
        /// デバイス自体が無効化された場合の復旧には力不足なので、その場合はRecreateEngineFullyLockedを使うこと。</summary>
        private bool RecreateSourceVoiceLocked()
        {
            lock (_lock)
            {
                try
                {
                    if (_sourceVoice != null)
                    {
                        try { _sourceVoice.Stop(); } catch { }
                        try { _sourceVoice.DestroyVoice(); } catch { }
                    }
                    _sourceVoice = _xaudio2!.CreateSourceVoice(_format!, VoiceFlags.None);
                    _samplesPlayedOffset = 0;
                    _sourceVoice.Start();
                    _recreatedSinceLastCheck = true; // AVEngine側でコンテンツオフセットを取り直させる
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[XAudio2AudioOutput] ソースボイス再作成に失敗: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>【今回追加】出力デバイス切替／切断時用の重い復旧。_xaudio2・マスタリングボイス・
        /// ソースボイスを全て作り直す（CreateMasteringVoiceをdeviceId未指定で呼び直すことで、
        /// その時点の既定デバイスに新規バインドされる）。
        /// 呼び出し元は_lockを保持していない状態で呼ぶこと（内部でlockを取る）。</summary>
        private bool RecreateEngineFullyLocked()
        {
            lock (_lock)
            {
                try
                {
                    try { _sourceVoice?.Stop(); } catch { }
                    try { _sourceVoice?.DestroyVoice(); } catch { }
                    _sourceVoice = null;

                    try { _masteringVoice?.DestroyVoice(); } catch { }
                    _masteringVoice = null;

                    try { _xaudio2?.Dispose(); } catch { }
                    _xaudio2 = null;

                    if (_format == null)
                    {
                        // Open()未実施のまま呼ばれた場合は何もできない
                        IsActive = false;
                        return false;
                    }

                    BuildAudioObjects_CallerHoldsLock();
                    _recreatedSinceLastCheck = true; // AVEngine側でコンテンツオフセットを取り直させる
                    return true;
                }
                catch (Exception ex)
                {
                    // 例：切替先も含めて有効な出力デバイスが1つも無い、等。この場合は諦めて
                    // IsActive=falseにしておき、次にデバイスが復活したときの通知 or 次回
                    // SubmitSamples呼び出しでの再試行に委ねる。
                    System.Diagnostics.Debug.WriteLine($"[XAudio2AudioOutput] エンジン再構築に失敗: {ex.Message}");
                    IsActive = false;
                    return false;
                }
            }
        }

        /// <summary>【今回追加】AudioDeviceChangeNotifierのコールバック（COM通知スレッドから呼ばれる）。
        /// 重い処理をこのスレッドで行ってはいけないため、ThreadPoolに投げるだけに留める。</summary>
        private void OnDefaultAudioDeviceChanged()
        {
            TriggerFullRecreateAsync("既定の再生デバイスが変更されました");
        }

        /// <summary>【今回追加】RecreateEngineFullyLockedをバックグラウンドスレッドで実行する。
        /// _recoveringFlagにより多重実行を防止（デバイス変更通知とSubmitSamples例外の両方から
        /// ほぼ同時に呼ばれても1回しか再構築しない）。</summary>
        private void TriggerFullRecreateAsync(string reason)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _recoveringFlag, 1, 0) != 0)
                return; // 既に再構築処理中

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[XAudio2AudioOutput] 出力エンジンを再構築します（理由: {reason}）");
                    RecreateEngineFullyLocked();
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _recoveringFlag, 0);
                }
            });
        }

        public void Start()
        {
            // ボイスは常時Start済みのまま維持するため、ここでは念のための再Start()のみ試みる。
            lock (_lock)
            {
                try { _sourceVoice?.Start(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[XAudio2AudioOutput] Start失敗（{ex.Message}）。エンジン再構築をトリガーします。");
                    TriggerFullRecreateAsync("Start失敗: " + ex.Message);
                }
            }
        }

        public void Pause()
        {
            // 【今回修正】以前はボイス自体を止めず、SubmitSamples側の送出を止めるだけで
            // 「音を止める」方式にしていた（ドラッグシーク中の連打Stop/Startでボイスが
            // 詰まる不具合の回避策）。しかしAVEngine側は既にaudioRunningフラグにより
            // 実際の状態遷移1回につき1回しかPause()/Start()を呼ばないようdebounce済みで、
            // 連打を引き起こしていた実体はドラッグシーク時のSeek()都度のFlush()（ソース
            // ボイス再作成）であり、Pause()/Start()自体の連打ではなかった。そのFlush()側は
            // 既に別途対策済み（RecreateSourceVoiceLockedで都度クリーンに作り直す設計）のため、
            // ここでボイスを実際に止めても連打問題とは無関係で安全と判断した。
            // 素通しのまま（送出停止だけ）だと、一時停止した瞬間に既にキュー済みの再生中
            // バッファ（最大約58個分、数百ms～1秒強）がそのまま鳴り続けてしまい、「一時停止を
            // 押しても音声だけしばらく再生される」不具合になっていた。Stop()はキュー済みの
            // バッファを破棄しないため、再開(Start())時は続きからシームレスに再生される。
            lock (_lock)
            {
                try { _sourceVoice?.Stop(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[XAudio2AudioOutput] Pause失敗（{ex.Message}）。エンジン再構築をトリガーします。");
                    TriggerFullRecreateAsync("Pause失敗: " + ex.Message);
                }
            }
        }

        public void Flush()
        {
            // FlushSourceBuffers()だけでは内部状態が完全にはクリアされず、Seekを繰り返すたびに
            // 劣化が蓄積して「音声の実再生速度が本来の1/5程度まで落ち込み、その後なかなか回復
            // しない」不具合の原因になっていた。Seekのたびに確実にクリーンな状態へ戻すため、
            // タイムアウト回復時と同じ「ソースボイスを毎回作り直す」方式にする
            // （こちらはデバイス自体は生きている前提の軽い復旧でよいので、Fully版ではなく
            // 既存のRecreateSourceVoiceLockedのままでよい）。
            RecreateSourceVoiceLocked();
        }

        public void Stop()
        {
            lock (_lock) { CloseInternal(); }
        }

        public void SetSpeedRatio(double ratio)
        {
            lock (_lock)
            {
                try
                {
                    // ピッチ保持なし（単純レート変換）。WPF MediaElement.SpeedRatioが行っていた
                    // タイムストレッチとは体感が異なる点に注意（要フォローアップ）。
                    _sourceVoice?.SetFrequencyRatio((float)Math.Clamp(ratio, 0.1, 4.0), 0u);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[XAudio2AudioOutput] SetSpeedRatio失敗（{ex.Message}）。");
                    TriggerFullRecreateAsync("SetSpeedRatio失敗: " + ex.Message);
                }
            }
        }

        public void SetVolume(double volume)
        {
            lock (_lock)
            {
                try { _sourceVoice?.SetVolume((float)Math.Clamp(volume, 0.0, 1.0)); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[XAudio2AudioOutput] SetVolume失敗（{ex.Message}）。");
                    TriggerFullRecreateAsync("SetVolume失敗: " + ex.Message);
                }
            }
        }

        public double GetPositionSeconds()
        {
            lock (_lock)
            {
                if (_sourceVoice == null || _sampleRate <= 0) return 0.0;
                try
                {
                    ulong playedRaw = _sourceVoice.State.SamplesPlayed;
                    ulong played = playedRaw >= _samplesPlayedOffset ? playedRaw - _samplesPlayedOffset : 0;
                    return (double)played / _sampleRate;
                }
                catch (Exception ex)
                {
                    // 【今回追加】ここも同様にCOM例外で呼び出し元スレッド（DecodeThread）を
                    // 巻き込んで落とさないよう保護する。位置は直前値ではなく0を返す簡易対応
                    // （すぐ後でConsumeRecreated()により呼び出し元がオフセットを取り直す）。
                    System.Diagnostics.Debug.WriteLine($"[XAudio2AudioOutput] GetPositionSeconds失敗（{ex.Message}）。エンジン再構築をトリガーします。");
                    TriggerFullRecreateAsync("GetPositionSeconds失敗: " + ex.Message);
                    return 0.0;
                }
            }
        }

        private void CloseInternal()
        {
            try { _sourceVoice?.Stop(); } catch { }
            try { _sourceVoice?.DestroyVoice(); } catch { }
            _sourceVoice = null;

            try { _masteringVoice?.DestroyVoice(); } catch { }
            _masteringVoice = null;

            try { _xaudio2?.Dispose(); } catch { }
            _xaudio2 = null;

            IsActive = false;
        }

        public void Dispose()
        {
            lock (_lock) { CloseInternal(); }
            // 【今回追加】デバイス変更通知の登録解除はインスタンス破棄時のみ（Stop()では行わない。
            // 同一インスタンスが次のOpen()で再利用されるケースがあるため）。
            _deviceNotifier?.Dispose();
            _deviceNotifier = null;
        }
    }
}
