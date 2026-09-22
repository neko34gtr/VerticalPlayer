using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace VerticalPlayer
{
    /// <summary>
    /// IAudioOutputのWASAPI直叩き実装。NAudio等のサードパーティライブラリは一切使用せず、
    /// 必要なCOMインターフェース（IMMDeviceEnumerator/IMMDevice/IAudioClient/IAudioRenderClient）を
    /// [ComImport]で自前定義し、CoCreateInstance相当（Type.GetTypeFromCLSID + Activator.CreateInstance）
    /// で直接呼び出す。
    ///
    /// 【共有(Shared)モード】既定デバイスのミキサーへFloat32/48kHz/stereoで接続する。
    /// AUDCLNT_STREAMFLAGS_AUTOCONVERTPCMを指定し、デバイスのミックスフォーマットと不一致でも
    /// OS側のリサンプラーに変換を任せる（IsFormatSupportedチェックを省いても初期化が通る設計）。
    ///
    /// 【排他(Exclusive)モード】デバイスを占有する。IsFormatSupportedでFloat32→32bit PCM→
    /// 24bit(32bit容器)PCM→16bit PCMの順に対応フォーマットを探し、見つかった形式でSubmitSamples時に
    /// 変換する。対応フォーマットが一つも無ければOpen()が例外を投げ、呼び出し元(AVEngine.OpenAndRun)の
    /// 既存try/catchにより「音声無しで続行」される。
    ///
    /// 【マスタークロック】IAudioClient.GetCurrentPadding()（＝まだ再生されていないバッファ残量）を
    /// 毎回計測し、これまでに書き込んだ総フレーム数からGetCurrentPaddingを差し引いた値を
    /// 「実際に再生し終えたフレーム数」として扱う。XAudio2版のSamplesPlayedに相当する。
    ///
    /// 【Flush(Seek時)】IAudioClient.Stop() → Reset()でリングバッファ内の残留音声を破棄し、
    /// 書き込み総フレーム数を0に戻してから再度Start()する。
    ///
    /// 【スロー/早送り】WASAPIにXAudio2のSetFrequencyRatio相当の機能は無いため、SubmitSamples内で
    /// 単純な線形補間によりサンプル列そのものの長さを速度倍率ぶん伸縮してから書き込む
    /// （XAudio2版と同じくピッチも変化する簡易実装。IAudioOutput.csのSetSpeedRatioのドキュメント
    /// コメント参照）。
    ///
    /// 【要実機確認】この環境からWindowsオーディオデバイスへ到達できずコンパイル・動作未検証。
    /// COMインターフェースのvtableスロット順序はWindows SDK mmdeviceapi.h/audioclient.hの定義に
    /// 基づいているが、実機ビルドで動作確認の上、問題があれば調整してください。
    /// </summary>
    public sealed class WasapiAudioOutput : IAudioOutput
    {
        private readonly bool _exclusive;
        private readonly object _lock = new();

        private IMMDeviceEnumerator? _enumerator;
        private IMMDevice? _device;
        private IAudioClient? _audioClient;
        private IAudioRenderClient? _renderClient;

        private int _sampleRate;
        private int _channels;
        private int _blockAlign;   // 1フレームのバイト数（全チャンネル分）
        private uint _bufferFrameCount; // デバイスバッファの総フレーム数
        private SampleFormat _sampleFormat = SampleFormat.Float32;

        private long _totalFramesWritten; // Open/Flush以降、ReleaseBufferで実際に書き込んだ総フレーム数
        private bool _started;
        private bool _recreatedSinceLastCheck;
        private double _speedRatio = 1.0;
        private double _volume = 1.0;

        // 変換用スクラッチバッファ（毎フレーム確保しないよう使い回す）
        private byte[]? _convertScratch;
        private float[]? _resampleScratch;

        public WasapiAudioOutput(bool exclusive)
        {
            _exclusive = exclusive;
        }

        public bool IsActive { get; private set; }

        public bool ConsumeRecreated()
        {
            lock (_lock)
            {
                bool r = _recreatedSinceLastCheck;
                _recreatedSinceLastCheck = false;
                return r;
            }
        }

        public void Open(int sampleRate, int channels)
        {
            lock (_lock)
            {
                CloseInternal();

                _sampleRate = sampleRate;
                _channels = channels;

                _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(ComGuids.CLSID_MMDeviceEnumerator, throwOnError: true)!)!;

                int hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out _device);
                ComUtil.ThrowIfFailed(hr, "GetDefaultAudioEndpoint");

                var iid = ComGuids.IID_IAudioClient;
                hr = _device!.Activate(ref iid, ClsCtx.CLSCTX_ALL, IntPtr.Zero, out object audioClientObj);
                ComUtil.ThrowIfFailed(hr, "IMMDevice.Activate(IAudioClient)");
                _audioClient = (IAudioClient)audioClientObj;

                var shareMode = _exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
                WaveFormatChoice fmt = _exclusive
                    ? NegotiateExclusiveFormat(_audioClient, sampleRate, channels)
                    : BuildSharedFormat(sampleRate, channels);
                _sampleFormat = fmt.SampleFormat;
                _blockAlign = fmt.Header.nBlockAlign;

                const long REFTIMES_PER_SEC = 10_000_000; // 100ns単位。1秒分
                // 共有モード: バッファ長はOS任せでよいので200ms程度の余裕を持たせる。
                // 排他モード: デバイスが受け付けるバッファ長を都度調整する必要があるが、
                // ここでは実用上問題の出にくい100msを要求値とし、失敗時は0（デバイス既定）で再試行する。
                long bufferDuration = _exclusive ? REFTIMES_PER_SEC / 10 : REFTIMES_PER_SEC / 5;

                uint streamFlags = _exclusive
                    ? 0u
                    : AudioClientStreamFlags.AUTOCONVERTPCM | AudioClientStreamFlags.SRC_DEFAULT_QUALITY;

                hr = InitializeAudioClient(_audioClient, shareMode, streamFlags, bufferDuration, ref fmt);
                if (hr == unchecked((int)0x88890019) /* AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED */ && _exclusive)
                {
                    // 排他モードはデバイスの内部周期に整数倍で合わせないと初期化に失敗することがある。
                    // 一度破棄し、GetBufferSizeで教えられた実際の必要長で作り直す（Windows SDKドキュメント記載の定石）。
                    hr = RetryExclusiveAfterAlignmentError(ref fmt, REFTIMES_PER_SEC);
                }
                ComUtil.ThrowIfFailed(hr, $"IAudioClient.Initialize(shareMode={shareMode})");

                hr = _audioClient!.GetBufferSize(out _bufferFrameCount);
                ComUtil.ThrowIfFailed(hr, "GetBufferSize");

                var renderIid = ComGuids.IID_IAudioRenderClient;
                hr = _audioClient.GetService(ref renderIid, out object renderObj);
                ComUtil.ThrowIfFailed(hr, "GetService(IAudioRenderClient)");
                _renderClient = (IAudioRenderClient)renderObj;

                _totalFramesWritten = 0;
                IsActive = true;

                // XAudio2版と同じ方針: 一度Start()したら動かしっぱなしにし、Pause/Start連打による
                // グリッチを避ける。「音を止める」は呼び出し側がSubmitSamplesを呼ばないことで実現する。
                hr = _audioClient.Start();
                ComUtil.ThrowIfFailed(hr, "IAudioClient.Start");
                _started = true;
            }
        }

        private WaveFormatChoice BuildSharedFormat(int sampleRate, int channels)
        {
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = WaveFormatTag.WAVE_FORMAT_IEEE_FLOAT,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                wBitsPerSample = 32,
                cbSize = 0,
            };
            fmt.nBlockAlign = (ushort)(fmt.nChannels * (fmt.wBitsPerSample / 8));
            fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign;
            return new WaveFormatChoice { Header = fmt, SampleFormat = SampleFormat.Float32 };
        }

        /// <summary>排他モード向けにFloat32→32bit PCM→24bit(32bit容器)PCM→16bit PCMの順で
        /// デバイスが受理する形式を探す。IsFormatSupportedがS_OK(0)を返した形式を採用する
        /// （排他モードはS_FALSE=近似形式提案が来ないため、見つからなければ次を試す）。</summary>
        private WaveFormatChoice NegotiateExclusiveFormat(IAudioClient client, int sampleRate, int channels)
        {
            // 1) Float32（非Extensible。多くのプロオーディオ系デバイスが対応）
            var floatFmt = new WAVEFORMATEX
            {
                wFormatTag = WaveFormatTag.WAVE_FORMAT_IEEE_FLOAT,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                wBitsPerSample = 32,
                cbSize = 0,
            };
            floatFmt.nBlockAlign = (ushort)(floatFmt.nChannels * (floatFmt.wBitsPerSample / 8));
            floatFmt.nAvgBytesPerSec = floatFmt.nSamplesPerSec * floatFmt.nBlockAlign;
            if (IsFormatSupportedExclusive(client, floatFmt))
                return new WaveFormatChoice { Header = floatFmt, SampleFormat = SampleFormat.Float32 };

            // 2) 32bit整数PCM（WAVEFORMATEXTENSIBLE、validBits=32）
            if (TryBuildExtensiblePcm(client, sampleRate, channels, containerBits: 32, validBits: 32, out var pcm32))
                return pcm32;

            // 3) 24bit(32bit容器)整数PCM。民生～プロ向けオーディオI/Fで最も一般的な排他モード形式。
            if (TryBuildExtensiblePcm(client, sampleRate, channels, containerBits: 32, validBits: 24, out var pcm24in32))
                return pcm24in32;

            // 4) 16bit整数PCM（非Extensible。最終フォールバック）
            var pcm16 = new WAVEFORMATEX
            {
                wFormatTag = WaveFormatTag.WAVE_FORMAT_PCM,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                wBitsPerSample = 16,
                cbSize = 0,
            };
            pcm16.nBlockAlign = (ushort)(pcm16.nChannels * (pcm16.wBitsPerSample / 8));
            pcm16.nAvgBytesPerSec = pcm16.nSamplesPerSec * pcm16.nBlockAlign;
            if (IsFormatSupportedExclusive(client, pcm16))
                return new WaveFormatChoice { Header = pcm16, SampleFormat = SampleFormat.Pcm16 };

            throw new InvalidOperationException(
                $"WASAPI排他モード: {sampleRate}Hz/{channels}chに対応する形式がデバイスに見つかりませんでした。");
        }

        private bool TryBuildExtensiblePcm(IAudioClient client, int sampleRate, int channels,
            int containerBits, int validBits, out WaveFormatChoice choice)
        {
            var ext = new WAVEFORMATEXTENSIBLE
            {
                Format = new WAVEFORMATEX
                {
                    wFormatTag = WaveFormatTag.WAVE_FORMAT_EXTENSIBLE,
                    nChannels = (ushort)channels,
                    nSamplesPerSec = (uint)sampleRate,
                    wBitsPerSample = (ushort)containerBits,
                    cbSize = 22, // WAVEFORMATEXTENSIBLE固有部分のサイズ（規定値）
                },
                wValidBitsPerSample = (ushort)validBits,
                dwChannelMask = channels == 1 ? 0x4u /*SPEAKER_FRONT_CENTER*/ : 0x3u /*FL|FR*/,
                SubFormat = ComGuids.KSDATAFORMAT_SUBTYPE_PCM,
            };
            ext.Format.nBlockAlign = (ushort)(ext.Format.nChannels * (containerBits / 8));
            ext.Format.nAvgBytesPerSec = ext.Format.nSamplesPerSec * ext.Format.nBlockAlign;

            if (IsFormatSupportedExclusiveExtensible(client, ext))
            {
                choice = new WaveFormatChoice
                {
                    Header = ext.Format,
                    SampleFormat = containerBits == 32
                        ? (validBits == 24 ? SampleFormat.Pcm24In32 : SampleFormat.Pcm32)
                        : SampleFormat.Pcm16,
                };
                return true;
            }
            choice = default;
            return false;
        }

        private static unsafe bool IsFormatSupportedExclusive(IAudioClient client, WAVEFORMATEX fmt)
        {
            IntPtr pFmt = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
            try
            {
                Marshal.StructureToPtr(fmt, pFmt, false);
                int hr = client.IsFormatSupported(AudioClientShareMode.Exclusive, pFmt, out IntPtr pClosest);
                if (pClosest != IntPtr.Zero) Marshal.FreeCoTaskMem(pClosest);
                return hr == 0; // 排他モードはS_OK(0)のときだけそのまま使える
            }
            finally
            {
                Marshal.FreeHGlobal(pFmt);
            }
        }

        private static bool IsFormatSupportedExclusiveExtensible(IAudioClient client, WAVEFORMATEXTENSIBLE fmt)
        {
            IntPtr pFmt = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEXTENSIBLE>());
            try
            {
                Marshal.StructureToPtr(fmt, pFmt, false);
                int hr = client.IsFormatSupported(AudioClientShareMode.Exclusive, pFmt, out IntPtr pClosest);
                if (pClosest != IntPtr.Zero) Marshal.FreeCoTaskMem(pClosest);
                return hr == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(pFmt);
            }
        }

        private static int InitializeAudioClient(IAudioClient client, AudioClientShareMode shareMode,
            uint streamFlags, long bufferDuration, ref WaveFormatChoice fmt)
        {
            IntPtr pFmt = fmt.SampleFormat is SampleFormat.Pcm24In32 or SampleFormat.Pcm32
                ? MarshalExtensible(fmt)
                : MarshalPlain(fmt.Header);
            try
            {
                return client.Initialize(shareMode, streamFlags, bufferDuration, 0, pFmt, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(pFmt);
            }
        }

        private int RetryExclusiveAfterAlignmentError(ref WaveFormatChoice fmt, long refTimesPerSec)
        {
            // GetBufferSizeで実際に要求すべきフレーム数を取得し、それを100ns単位に換算して
            // 再Initializeする（Windows SDK "Rendering a Stream" のAUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED対処と同じ）。
            var iid = ComGuids.IID_IAudioClient;
            int hr = _device!.Activate(ref iid, ClsCtx.CLSCTX_ALL, IntPtr.Zero, out object obj2);
            ComUtil.ThrowIfFailed(hr, "IMMDevice.Activate(retry)");
            var client2 = (IAudioClient)obj2;

            hr = InitializeAudioClient(client2, AudioClientShareMode.Exclusive, 0,
                (long)(refTimesPerSec / 10), ref fmt); // 一度捨て値で叩いて必要バッファ長を確認
            if (hr < 0)
            {
                // 破棄して素直に失敗を返す（これ以上粘らない）
                Marshal.ReleaseComObject(client2);
                return hr;
            }
            hr = client2.GetBufferSize(out uint frames);
            Marshal.ReleaseComObject(client2);
            if (hr < 0) return hr;

            long alignedDuration = (long)(refTimesPerSec * (double)frames / fmt.Header.nSamplesPerSec) + 1;

            hr = _device!.Activate(ref iid, ClsCtx.CLSCTX_ALL, IntPtr.Zero, out object obj3);
            if (hr < 0) return hr;
            _audioClient = (IAudioClient)obj3;
            return InitializeAudioClient(_audioClient, AudioClientShareMode.Exclusive, 0, alignedDuration, ref fmt);
        }

        private static IntPtr MarshalPlain(WAVEFORMATEX fmt)
        {
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
            Marshal.StructureToPtr(fmt, p, false);
            return p;
        }

        private static IntPtr MarshalExtensible(WaveFormatChoice fmt)
        {
            var ext = new WAVEFORMATEXTENSIBLE
            {
                Format = fmt.Header,
                wValidBitsPerSample = (ushort)(fmt.SampleFormat == SampleFormat.Pcm24In32 ? 24 : fmt.Header.wBitsPerSample),
                dwChannelMask = fmt.Header.nChannels == 1 ? 0x4u : 0x3u,
                SubFormat = ComGuids.KSDATAFORMAT_SUBTYPE_PCM,
            };
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEXTENSIBLE>());
            Marshal.StructureToPtr(ext, p, false);
            return p;
        }

        // XAudio2版のMaxQueuedBuffersに相当するバックプレッシャー: 空きが少ない間はデバイスが
        // 消化するまで待つ。500ms待っても空かなければ、詰まったとみなしクライアントを作り直す。
        public void SubmitSamples(float[] interleaved, int frameCount)
        {
            if (!IsActive) return;

            // ── 速度倍率が1.0でなければ、先に線形補間でフレーム数そのものを伸縮する ──
            float[] src = interleaved;
            int srcFrames = frameCount;
            double ratio;
            lock (_lock) { ratio = _speedRatio; }
            if (Math.Abs(ratio - 1.0) > 0.001)
            {
                src = ResampleLinear(interleaved, frameCount, _channels, ratio, out srcFrames);
            }

            int written = 0;
            int spinMs = 0;
            while (written < srcFrames)
            {
                uint padding;
                uint bufferFrames;
                lock (_lock)
                {
                    if (_audioClient == null || _renderClient == null) return;
                    if (_audioClient.GetCurrentPadding(out padding) < 0) return;
                    bufferFrames = _bufferFrameCount;
                }

                uint available = bufferFrames > padding ? bufferFrames - padding : 0;
                if (available == 0)
                {
                    if (spinMs >= 500)
                    {
                        System.Diagnostics.Debug.WriteLine("[WasapiAudioOutput] 空き待ちが500msでタイムアウト。オーディオクライアントを再作成します。");
                        if (!RecreateAudioClient()) return;
                        spinMs = 0;
                        continue;
                    }
                    Thread.Sleep(1);
                    spinMs++;
                    continue;
                }

                int framesThisPass = (int)Math.Min(available, (uint)(srcFrames - written));
                WriteFrames(src, written, framesThisPass);
                written += framesThisPass;
                _totalFramesWritten += framesThisPass;
            }
        }

        private unsafe void WriteFrames(float[] src, int startFrame, int frameCount)
        {
            lock (_lock)
            {
                if (_renderClient == null) return;
                int hr = _renderClient.GetBuffer((uint)frameCount, out IntPtr pData);
                if (hr < 0) return; // 取得できなければこの分は諦める（次のループでリトライされる）

                float vol = (float)_volume; // SetVolume()の値をここで実際にサンプルへ適用する
                int floatOffset = startFrame * _channels;
                int totalSamples = frameCount * _channels;

                switch (_sampleFormat)
                {
                    case SampleFormat.Float32:
                        {
                            var span = new Span<float>((void*)pData, totalSamples);
                            for (int i = 0; i < totalSamples; i++)
                                span[i] = src[floatOffset + i] * vol;
                            break;
                        }
                    case SampleFormat.Pcm16:
                        WriteConverted(src, floatOffset, totalSamples, vol, pData, 2, (f, dst) =>
                            BitConverter.TryWriteBytes(dst, (short)Math.Clamp(f * short.MaxValue, short.MinValue, short.MaxValue)));
                        break;
                    case SampleFormat.Pcm32:
                        WriteConverted(src, floatOffset, totalSamples, vol, pData, 4, (f, dst) =>
                            BitConverter.TryWriteBytes(dst, (int)Math.Clamp((double)f * int.MaxValue, int.MinValue, int.MaxValue)));
                        break;
                    case SampleFormat.Pcm24In32:
                        WriteConverted(src, floatOffset, totalSamples, vol, pData, 4, (f, dst) =>
                        {
                            // 24bit有効値を32bit容器の上位24bitに詰める（一般的な配置）
                            int v = (int)Math.Clamp((double)f * 8388607.0, -8388608.0, 8388607.0);
                            int shifted = v << 8;
                            BitConverter.TryWriteBytes(dst, shifted);
                        });
                        break;
                }

                _renderClient.ReleaseBuffer((uint)frameCount, 0);
            }
        }

        private void WriteConverted(float[] src, int floatOffset, int totalSamples, float vol, IntPtr pData,
            int bytesPerSample, ConvertSample convert)
        {
            int totalBytes = totalSamples * bytesPerSample;
            if (_convertScratch == null || _convertScratch.Length < totalBytes)
                _convertScratch = new byte[totalBytes];

            for (int i = 0; i < totalSamples; i++)
            {
                Span<byte> dst = _convertScratch.AsSpan(i * bytesPerSample, bytesPerSample);
                convert(src[floatOffset + i] * vol, dst);
            }
            Marshal.Copy(_convertScratch, 0, pData, totalBytes);
        }

        private delegate void ConvertSample(float sample, Span<byte> dest);

        private float[] ResampleLinear(float[] src, int srcFrames, int channels, double ratio, out int outFrames)
        {
            // ratio>1.0で再生が速くなる（＝同じ内容をより短いフレーム数に圧縮する）方向。
            // XAudio2のSetFrequencyRatioと同じくピッチも変化する簡易実装（直線補間のみ）。
            outFrames = Math.Max(1, (int)Math.Round(srcFrames / ratio));
            int needed = outFrames * channels;
            if (_resampleScratch == null || _resampleScratch.Length < needed)
                _resampleScratch = new float[needed];

            for (int of = 0; of < outFrames; of++)
            {
                double sp = of * ratio;
                int i0 = (int)sp;
                int i1 = Math.Min(i0 + 1, srcFrames - 1);
                double frac = sp - i0;
                if (i0 >= srcFrames) i0 = srcFrames - 1;
                for (int c = 0; c < channels; c++)
                {
                    float a = src[i0 * channels + c];
                    float b = src[i1 * channels + c];
                    _resampleScratch[of * channels + c] = (float)(a + (b - a) * frac);
                }
            }
            return _resampleScratch;
        }

        /// <summary>500ms空き待ちタイムアウト時の自己回復（XAudio2版RecreateSourceVoiceLockedに相当）。
        /// Stop→Reset→Startでクライアントを仕切り直す。成功したらtrue。</summary>
        private bool RecreateAudioClient()
        {
            lock (_lock)
            {
                try
                {
                    if (_audioClient != null)
                    {
                        try { _audioClient.Stop(); } catch { }
                        try { _audioClient.Reset(); } catch { }
                        try { _audioClient.Start(); } catch { }
                    }
                    _totalFramesWritten = 0;
                    _recreatedSinceLastCheck = true; // AVEngine側でコンテンツオフセットを取り直させる
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WasapiAudioOutput] オーディオクライアント再作成に失敗: {ex.Message}");
                    return false;
                }
            }
        }

        public void Start()
        {
            // XAudio2版と同じ方針: 常時Start済みのまま維持する。停止していた場合の保険のみ。
            lock (_lock) { try { _audioClient?.Start(); } catch { } }
        }

        public void Pause()
        {
            // ボイス（ストリーム）自体は止めない。「音を止める」は呼び出し元がSubmitSamplesを
            // 呼ばないことで実現する（XAudio2版と同じ設計。Stop/Start連打によるグリッチ回避）。
        }

        public void Flush()
        {
            // Seekのたびにクリーンな状態へ戻す。IAudioClient.Reset()はStop()済みでないと失敗するため、
            // 必ずStop→Reset→Startの順で呼ぶ。
            lock (_lock)
            {
                try
                {
                    _audioClient?.Stop();
                    _audioClient?.Reset();
                    _audioClient?.Start();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WasapiAudioOutput] Flush失敗: {ex.Message}");
                }
                _totalFramesWritten = 0;
            }
        }

        public void Stop()
        {
            lock (_lock) { CloseInternal(); }
        }

        public void SetSpeedRatio(double ratio)
        {
            lock (_lock) { _speedRatio = Math.Clamp(ratio, 0.1, 4.0); }
        }

        public void SetVolume(double volume)
        {
            // ISimpleAudioVolume等は使わず、書き込み前の振幅そのものに掛ける簡易実装
            // （排他/共有どちらのフォーマットでも同じコードパスで扱えるため）。
            lock (_lock) { _volume = Math.Clamp(volume, 0.0, 1.0); }
        }

        public double GetPositionSeconds()
        {
            lock (_lock)
            {
                if (_audioClient == null || _sampleRate <= 0) return 0.0;
                if (_audioClient.GetCurrentPadding(out uint padding) < 0) return 0.0;
                long played = _totalFramesWritten - padding;
                if (played < 0) played = 0;
                return (double)played / _sampleRate;
            }
        }

        private void CloseInternal()
        {
            try { _audioClient?.Stop(); } catch { }
            try { _audioClient?.Reset(); } catch { }

            if (_renderClient != null) { Marshal.ReleaseComObject(_renderClient); _renderClient = null; }
            if (_audioClient != null) { Marshal.ReleaseComObject(_audioClient); _audioClient = null; }
            if (_device != null) { Marshal.ReleaseComObject(_device); _device = null; }
            if (_enumerator != null) { Marshal.ReleaseComObject(_enumerator); _enumerator = null; }

            _started = false;
            IsActive = false;
        }

        public void Dispose()
        {
            lock (_lock) { CloseInternal(); }
        }

        // ── 内部データ型 ──

        private enum SampleFormat { Float32, Pcm16, Pcm32, Pcm24In32 }

        private struct WaveFormatChoice
        {
            public WAVEFORMATEX Header;
            public SampleFormat SampleFormat;
        }

        // ── Win32/COM 定義 ──

        private static class WaveFormatTag
        {
            public const ushort WAVE_FORMAT_PCM = 1;
            public const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
            public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
        }

        private static class AudioClientStreamFlags
        {
            public const uint AUTOCONVERTPCM = 0x80000000;
            public const uint SRC_DEFAULT_QUALITY = 0x08000000;
        }

        private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
        private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }
        private enum AudioClientShareMode { Shared = 0, Exclusive = 1 }

        private static class ClsCtx
        {
            public const uint CLSCTX_ALL = 1 /*INPROC_SERVER*/ | 2 /*INPROC_HANDLER*/ | 4 /*LOCAL_SERVER*/ | 16 /*REMOTE_SERVER*/;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WAVEFORMATEXTENSIBLE
        {
            public WAVEFORMATEX Format;
            public ushort wValidBitsPerSample;
            public uint dwChannelMask;
            [MarshalAs(UnmanagedType.Struct)]
            public Guid SubFormat;
        }

        private static class ComGuids
        {
            public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
            public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
            public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
            public static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00AA00389B71");
        }

        private static class ComUtil
        {
            public static void ThrowIfFailed(int hr, string what)
            {
                if (hr < 0)
                    throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"{what} failed: 0x{hr:X8}");
            }
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
            int RegisterEndpointNotificationCallback(IntPtr pClient);
            int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            int GetState(out uint pdwState);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            int Initialize(AudioClientShareMode shareMode, uint streamFlags, long hnsBufferDuration,
                long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
            int GetBufferSize(out uint pNumBufferFrames);
            int GetStreamLatency(out long phnsLatency);
            int GetCurrentPadding(out uint pNumPaddingFrames);
            int IsFormatSupported(AudioClientShareMode shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
            int GetMixFormat(out IntPtr ppDeviceFormat);
            int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
            int Start();
            int Stop();
            int Reset();
            int SetEventHandle(IntPtr eventHandle);
            int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        }

        [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioRenderClient
        {
            int GetBuffer(uint numFramesRequested, out IntPtr ppData);
            int ReleaseBuffer(uint numFramesWritten, uint dwFlags);
        }
    }
}
