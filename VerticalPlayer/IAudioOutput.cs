using System;

namespace VerticalPlayer
{
    /// <summary>
    /// AVEngineがデコードしたPCM音声を実際の音声デバイスへ出力するバックエンドの抽象。
    ///
    /// 【経緯】従来はAVEngineが映像のみを扱い、音声はWPFの MediaElement(_audio) が同じファイルを
    /// 別途独自に開いて再生していた。この「同じファイルを映像用/音声用で二重に開く」構成が、
    /// SDカード等の低速メディアでI/O競合を起こし、起動直後の「音声が数百ms〜数秒出ない」
    /// 「その間マスタークロックが進まず映像もカクつく」不具合の根本原因だった
    /// （2026年のtrace.log解析で特定）。
    ///
    /// 対応として、AVEngine自身が音声ストリームもデコードし、このIAudioOutputへ直接PCMを渡す
    /// 構成に変更する。実装は現在Vortice.XAudio2版（XAudio2AudioOutput）のみだが、将来WASAPI
    /// 直叩き実装に差し替えられるよう、AVEngine/FfmpegMediaElement側はこのインターフェースにのみ
    /// 依存し、Vortice.XAudio2の型を直接参照しないこと。
    /// </summary>
    public interface IAudioOutput : IDisposable
    {
        /// <summary>出力フォーマットを確定してデバイスを開く。sampleRateはHz、channelsは1または2を想定
        /// （それ以外のチャンネル数は呼び出し側でダウンミックスしてから渡すこと）。
        /// 音声トラックが無いファイルの場合はこのメソッド自体を呼ばない（IsActiveはfalseのまま）。</summary>
        void Open(int sampleRate, int channels);

        /// <summary>デコード済みPCM（Float32 interleaved、Open()で指定したsampleRate/channels形式）を
        /// 再生キューへ積む。frameCountはチャンネルをまとめた1フレーム単位のサンプル数
        /// （= interleaved.Length / channels）。呼び出し側は渡した配列をこの後書き換えないこと。</summary>
        void SubmitSamples(float[] interleaved, int frameCount);

        /// <summary>出力を開始/再開する。</summary>
        void Start();

        /// <summary>出力を一時停止する（キュー内容は保持）。</summary>
        void Pause();

        /// <summary>再生位置をリセットし、キュー中の未再生バッファを破棄する（Seek時に使用）。
        /// 呼び出し後、GetPositionSeconds()は0から再スタートする。</summary>
        void Flush();

        /// <summary>出力を停止しキューを破棄する。Open()からやり直せる状態に戻る。</summary>
        void Stop();

        /// <summary>再生速度倍率。現状はXAudio2のFrequencyRatio（単純なサンプルレート変換＝
        /// ピッチも変化する）で実現している。ピッチ保持（WPF MediaElementのSpeedRatioが従来
        /// 行っていたタイムストレッチ）が必要な場合は、将来このメソッドの実装を差し替えるか、
        /// SubmitSamples前段でWSOLA/SoundTouch等の処理を挟む形で対応する。</summary>
        void SetSpeedRatio(double ratio);

        /// <summary>出力音量。0.0〜1.0。</summary>
        void SetVolume(double volume);

        /// <summary>デバイスが実際に再生し終えた総秒数。AVEngineのマスタークロックの基準として使う
        /// （Stopwatchのような自走クロックではなく、ハードウェアの実再生位置に基づく値）。</summary>
        /// <summary>実装が内部的にデバイス/ボイスを作り直した場合、その通知を1回だけ返す
        /// （呼び出すと内部フラグはfalseに戻る）。呼び出し側（AVEngine）は、trueが返ったら
        /// 「音声位置とコンテンツ時刻の対応関係が仕切り直しになった」とみなし、次に届く
        /// フレームの実ptsでコンテンツオフセットを取り直す（Seek直後の扱いと同じ）。</summary>
        bool ConsumeRecreated();

        double GetPositionSeconds();

        /// <summary>現在このバックエンドが有効な音声トラックを持って動作中か
        /// （音声トラック無しのファイルではOpen()自体が呼ばれずfalseのまま）。</summary>
        bool IsActive { get; }
    }
}
