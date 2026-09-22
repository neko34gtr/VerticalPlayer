using System;

namespace VerticalPlayer
{
    /// <summary>
    /// 音声出力バックエンドの種別。設定UIのドロップダウン（AudioBackendCombo）と
    /// AppSettings.AudioBackend（保存文字列はこのenum名をそのまま使う）で共有する。
    /// </summary>
    public enum AudioBackendKind
    {
        /// <summary>Vortice.XAudio2経由（既定、完成済み）。</summary>
        XAudio2,

        /// <summary>WASAPI直叩き・共有モード。システムのミキサーを介して他アプリと音声出力を共有する。
        /// サンプルレート変換はOS側（AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM）に任せるため互換性が高い。</summary>
        WasapiShared,

        /// <summary>WASAPI直叩き・排他モード。デバイスを占有し、ミキサーを経由しないぶん低レイテンシー。
        /// デバイスが対応する形式でのみ動作するため、非対応環境ではOpen()が例外を投げ、
        /// 呼び出し元（AVEngine.OpenAndRun）の既存のtry/catchにより「音声無しで続行」となる。</summary>
        WasapiExclusive,
    }

    /// <summary>AudioBackendKindからIAudioOutput実装を生成するファクトリ。
    /// AVEngine/FfmpegMediaElementはこの型経由でのみ具象クラスに触れる
    /// （IAudioOutput.csの設計方針どおり、Vortice.XAudio2やWASAPI COM型を直接参照しない）。</summary>
    public static class AudioOutputFactory
    {
        public static IAudioOutput Create(AudioBackendKind kind)
        {
            return kind switch
            {
                AudioBackendKind.WasapiShared => new WasapiAudioOutput(exclusive: false),
                AudioBackendKind.WasapiExclusive => new WasapiAudioOutput(exclusive: true),
                _ => new XAudio2AudioOutput(),
            };
        }
    }
}
