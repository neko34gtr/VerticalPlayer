using System;
using System.Runtime.InteropServices;

namespace VerticalPlayer
{
    /// <summary>
    /// 既定の音声出力デバイスが変更された（Windowsのサウンド設定で既定デバイスが切り替わった、
    /// もしくは再生中デバイスが無効化/切断された）ことを検知する軽量クラス。
    ///
    /// MMDeviceEnumerator/IMMNotificationClientのCOM相互運用宣言はこのクラス内にprivateネスト
    /// している。他ファイル（WasapiAudioOutput.cs等）に同名のCOM宣言があっても名前空間レベルでは
    /// 衝突しない。将来WASAPI側からもそのまま使い回せる。
    ///
    /// 【要ビルド確認】この環境からnuget.orgへ到達できずコンパイル未検証。COMインターフェースの
    /// GUID/メソッド順はWindows SDK mmdeviceapi.hの定義（Vista以降不変）に基づく。
    /// </summary>
    internal sealed class AudioDeviceChangeNotifier : IDisposable
    {
        private readonly Action _onDefaultDeviceChanged;
        private IMMDeviceEnumerator? _enumerator;
        private NotificationClient? _client;
        private bool _registered;

        public AudioDeviceChangeNotifier(Action onDefaultDeviceChanged)
        {
            _onDefaultDeviceChanged = onDefaultDeviceChanged;
            try
            {
                var comType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
                if (comType == null) throw new InvalidOperationException("MMDeviceEnumerator CLSID解決失敗");
                _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(comType)!;
                _client = new NotificationClient(_onDefaultDeviceChanged);
                int hr = _enumerator.RegisterEndpointNotificationCallback(_client);
                _registered = hr == 0;
                if (!_registered)
                    System.Diagnostics.Debug.WriteLine($"[AudioDeviceChangeNotifier] RegisterEndpointNotificationCallback失敗: 0x{hr:X8}");
            }
            catch (Exception ex)
            {
                // 【重要】ここで失敗してもアプリ全体は継続させる。デバイス変更の自動検知が
                // 無効になるだけで、XAudio2AudioOutput側のSubmitSamples/GetPositionSeconds
                // の例外捕捉（もう一段の保険）は引き続き機能する。
                System.Diagnostics.Debug.WriteLine($"[AudioDeviceChangeNotifier] 初期化失敗（デバイス変更の自動検知は無効化されます）: {ex.Message}");
                _enumerator = null;
                _client = null;
                _registered = false;
            }
        }

        public void Dispose()
        {
            try
            {
                if (_registered && _enumerator != null && _client != null)
                    _enumerator.UnregisterEndpointNotificationCallback(_client);
            }
            catch { }
            _registered = false;

            try
            {
                if (_enumerator != null && Marshal.IsComObject(_enumerator))
                    Marshal.ReleaseComObject(_enumerator);
            }
            catch { }
            _enumerator = null;
            _client = null;
        }

        // ── COM相互運用宣言（このクラス内に閉じたprivateネスト） ──

        private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
        private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public int pid;
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr ppDevices);
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IntPtr ppEndpoint);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IntPtr ppDevice);
            int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
            int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
        }

        [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMNotificationClient
        {
            void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int dwNewState);
            void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
            void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
            void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string pwstrDefaultDeviceId);
            void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PropertyKey key);
        }

        // IMMNotificationClientの実装本体。COMコールバックスレッドから呼ばれるため、
        // ここでは絶対に重い処理をせず、フラグ通知程度に留めること
        // （MS公式remarksでも「このコールバック内からIMMDeviceEnumeratorの取得元メソッドを
        // 呼び直すな」と明記されている）。
        private sealed class NotificationClient : IMMNotificationClient
        {
            private readonly Action _onDefaultDeviceChanged;
            public NotificationClient(Action onDefaultDeviceChanged) => _onDefaultDeviceChanged = onDefaultDeviceChanged;

            public void OnDeviceStateChanged(string pwstrDeviceId, int dwNewState) { }
            public void OnDeviceAdded(string pwstrDeviceId) { }
            public void OnDeviceRemoved(string pwstrDeviceId) { }

            public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string pwstrDefaultDeviceId)
            {
                // 再生(eRender)側の既定デバイス変更のみ対象。ロール(Console/Multimedia/
                // Communications)ごとに複数回飛んでくることがあるが、呼び出し先
                // （XAudio2AudioOutput）側で多重実行防止済みなので気にせずそのまま通知する。
                if (flow == EDataFlow.eRender)
                {
                    try { _onDefaultDeviceChanged(); } catch { }
                }
            }

            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        }
    }
}
