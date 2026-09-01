namespace VerticalPlayer
{
    /// <summary>
    /// デコード済みBGRAフレームの表示先を差し替え可能にするための抽象化。
    ///
    /// 「高画質化エンジン設計提案」2.3節・段階1（D3DImage土台構築）用。
    /// 現時点では AVEngine 側の CPU パイプライン（sws_scale → managed byte[]）は
    /// 一切変更せず、最終的な「表示する」だけを WriteableBitmap から
    /// D3DImage 経由へ差し替える。CPUコピー自体をなくす最適化（GPUテクスチャを
    /// 直接渡す）は、後続のCompute Shaderフィルタ導入と合わせて行う想定。
    /// </summary>
    public interface IFramePresenter
    {
        /// <summary>動画サイズが変わった時に呼ばれる。内部リソースの再生成に使う。</summary>
        void EnsureSize(int width, int height);

        /// <summary>1フレーム分のBGRAピクセルデータを表示する。AVEngineのデコードスレッドから
        /// UIスレッドへディスパッチされた後（WritePixels相当のタイミング）に呼ばれる想定。</summary>
        void Present(byte[] bgra, int width, int height, int stride);

        /// <summary>コントラスト/彩度/ガンマ（段階2：Compute Shader版）を設定する。
        /// 値の意味・範囲は AVEngine.SetEffects と同一（-1〜1、0=無効）。</summary>
        void SetEffects(double contrast, double saturation, double gamma);

        /// <summary>ダイナミックコントラスト（段階4：シーン平均輝度ベースの簡易オートレベル）の強さ。
        /// 0〜1、0で完全無効。GPU完結のCompute Shaderでのみ実装されており、
        /// WriteableBitmap（非GPU）経路では何もしない。</summary>
        void SetDynamicContrast(float strength);
    }
}
