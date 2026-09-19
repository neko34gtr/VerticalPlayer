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

                _xaudio2 = XAudio2.XAudio2Create();
                _masteringVoice = _xaudio2.CreateMasteringVoice((uint)channels, (uint)sampleRate);
                _sourceVoice = _xaudio2.CreateSourceVoice(_format, VoiceFlags.None);

                _samplesPlayedOffset = 0;
                IsActive = true;

                // 【今回修正】以前はPause()/Start()のたびに_sourceVoice.Stop()/Start()を実際に
                // 呼んでいたが、シークバードラッグ中は毎秒数十回のPlay(false)/Pause()が飛んでくる
                // ため、極めて短い間隔でStop/Startを連打する形になり、その後ボイスがキューを
                // 消化しなくなる（再生が止まったまま戻らない）不具合の原因になっていた。
                // ボイスは生成直後に一度Start()したら、以後は基本的に動かしっぱなしにする。
                // 「音を止める」はSubmitSamples側の送出自体を止めるだけにし（呼び出し元の
                // AudioDecodeLoopが_audioDesired==falseの間SubmitSamplesを呼ばない）、
                // キューが尽きれば自然に無音になる方式にする。
                _sourceVoice.Start();
            }
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
                // 判断し、ソースボイス自体を作り直して復帰させる。
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

        /// <summary>ソースボイスを実際に作り直す（Stop→Destroy→Create→Start）。成功したらtrue。
        /// 呼び出し元は_lockを保持していない状態で呼ぶこと（内部でlockを取る）。</summary>
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

        public void Start()
        {
            // 今回修正: ボイスは常時Start済みのまま維持するため、ここでは何もしない
            // （念のため、万一停止していた場合の保険として再Start()だけ試みる）。
            lock (_lock) { try { _sourceVoice?.Start(); } catch { } }
        }

        public void Pause()
        {
            // 今回修正: ボイス自体は止めない（Stop/Startの連打が原因の再生停止不具合を避けるため）。
            // 「音を止める」は呼び出し元(AudioDecodeLoop)がSubmitSamples自体を呼ばないことで実現する。
        }

        public void Flush()
        {
            // 【今回修正】FlushSourceBuffers()だけでは内部状態が完全にはクリアされず、
            // Seekを繰り返すたびに劣化が蓄積して「音声の実再生速度が本来の1/5程度まで
            // 落ち込み、その後なかなか回復しない」不具合の原因になっていた
            // （trace.logの rawAudioPos / wall の比較で確認）。Seekのたびに確実にクリーンな
            // 状態へ戻すため、タイムアウト回復時と同じ「ソースボイスを毎回作り直す」方式にする。
            // 頻繁なSeek（シークバードラッグ中等）でもボイス再作成自体は軽い操作。
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
                // ピッチ保持なし（単純レート変換）。WPF MediaElement.SpeedRatioが行っていた
                // タイムストレッチとは体感が異なる点に注意（要フォローアップ）。
                _sourceVoice?.SetFrequencyRatio((float)Math.Clamp(ratio, 0.1, 4.0), 0u);
            }
        }

        public void SetVolume(double volume)
        {
            lock (_lock) { _sourceVoice?.SetVolume((float)Math.Clamp(volume, 0.0, 1.0)); }
        }

        public double GetPositionSeconds()
        {
            lock (_lock)
            {
                if (_sourceVoice == null || _sampleRate <= 0) return 0.0;
                ulong playedRaw = _sourceVoice.State.SamplesPlayed;
                ulong played = playedRaw >= _samplesPlayedOffset ? playedRaw - _samplesPlayedOffset : 0;
                return (double)played / _sampleRate;
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
        }
    }
}
