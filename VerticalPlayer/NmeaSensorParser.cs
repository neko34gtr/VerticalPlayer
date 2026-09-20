using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// ドラレコの".NMEA"ファイルは標準NMEA-0183テキストではなく、24バイト固定長の
    /// バイナリレコードが連続する独自フォーマットだった（実機ダンプの解析により判明）。
    /// レコードレイアウト（BE=ビッグエンディアン）:
    ///
    ///   [0x00-0x03] uint32 BE  Header      — 0x00000001=GPS測位成功, 0x00000000=GPS未測位/トンネル内
    ///   [0x04-0x07] int32  BE  Latitude    — 緯度の生値（1/256秒単位）。度への変換: raw / (3600.0 * 256.0)
    ///   [0x08-0x0B] int32  BE  Longitude   — 経度の生値（1/256秒単位）。度への変換: raw / (3600.0 * 256.0)
    ///   [0x0C-0x0F] 4byte      Status      — ステータスフラグ群。意味未確定（無効時は0xFFFFFFFF）
    ///   [0x10-0x11] int16  BE  Field1      — 車速（1km/h単位、無効時は0xFFFF＝プレースホルダ、GPS非測位中は常にこれ）
    ///   [0x12-0x17] int16  BE ×3  Field2-4 — 3軸加速度の生値。/1000 した合成ベクトルの大きさが静止時に約1.0となることを実データで確認済み
    ///                                        （GPS無効時でもこの3値だけは有効な値が入る＝IMU単体で常時サンプリングされている）
    ///
    /// 【確定事項（実機ダンプ解析により確定）】
    /// - 緯度・経度は0x04-0x0Bに1/256秒単位のint32(BE)で格納されている。度への変換は
    ///   raw / (3600.0 * 256.0)。
    /// - 速度(Field1)は生値=km/h（換算係数1.0）。
    ///
    /// 【未確定事項・要再確認】
    /// - 0x0C-0x0Fのステータス4byteの意味。
    /// - Field2-4の軸割り当て（どれがX/Y/Z＝左右/前後/上下に対応するかは未確認。暫定でField2=X, Field3=Y, Field4=Zとしている）。
    /// - レコード間の時間間隔（各レコードに明示的なタイムスタンプが無いため、動画長が分かっている
    ///   場合はレコード数で均等按分し、分からない場合は10Hzサンプリングを仮定して1レコード=100msとして扱う）。
    ///
    /// 軸割り当てだけは実際の走行区間で実測値と突き合わせて校正することを推奨する。
    /// </summary>
    public static class NmeaSensorParser
    {
        private const int RecordSize = 24;

        // 実機確認: 従来の/10.0では実速度約40km/hが「4」と表示される（1桁ズレ）ことを確認したため、
        // 生値をそのままkm/hとして扱う（＝生値の単位は既に1km/h刻み）方式に修正。
        private const double SpeedRawToKmh = 1.0;

        // 暫定スケール（実データで静止時ベクトル長≒1.0を確認済み）: 加速度生値 → G
        private const double AccelRawToG = 1.0 / 1000.0;

        // ---- 測位復帰直後の速度過渡値の補正 ----
        // トンネル脱出などで未測位→測位に戻った直後、GPSの位置ジャンプに引きずられて
        // 速度が数秒かけて収束する過渡値（例: 80km/h走行中に 153→119→94→86→79）が記録される。
        // 収束後の安定値へ置き換える。ただし加速度が実際の減速/加速を示している場合は
        // 実挙動とみなして補正しない（急ブレーキ・事故の記録を消さないため）。
        private const double SettleScanSeconds = 15.0;        // 復帰点から収束点を探す最大時間
        private const double SettleStableSeconds = 3.0;       // 「安定」とみなす継続時間
        private const double SettleStableToleranceKmh = 4.0;  // 安定窓内の速度変動の許容幅
        private const double SettleMinDeviationKmh = 15.0;    // 収束値からのズレがこれ未満なら補正不要
        private const double SettleImuMinShiftG = 0.10;       // 加速度平均の変化がこれ未満なら実加減速なしとみなす下限
        private const double SettleImuExpectedRatio = 0.4;    // 速度変化から期待される加減速Gに対する比率

        // ---- 測位ロスト区間(トンネル等)の速度推定 ----
        // 測位を失っている間は速度が記録されない(0扱い)ため、区間の直前の測位速度と復帰後に収束した
        // 測位速度の間を線形補間した「推定速度」で埋める。トンネル内は概ね一定速度で走る前提。
        // 加速度センサーの積分は、ノイズ(標準偏差0.05G前後)が区間内の実際の速度変化(0.01〜0.02G相当)に
        // 埋もれるため使わない。片側の速度しか無い場合はその値を保持する。
        // 区間の長さ(ファイルをまたぐ場合は前後のファイル分を含む)がこの秒数を超える場合は推定しない
        // （既定900秒＝日本最長の道路トンネル山手トンネル約18.2kmを80km/hで走る約14分に余裕を持たせた値）。0以下で無効。
        public static double MaxEstimateGapSeconds { get; set; } = 900;

        /// <summary>ファイルをまたぐ測位ロスト区間の推定用に、前後のファイルから得た情報。
        /// SpeedBefore/After: 区間の直前/直後の測位速度(無ければnull)。GapBefore/AfterSeconds: このファイルの先頭/末尾を
        /// 起点に、その測位が得られた地点までにファイル外で経過している秒数。</summary>
        public sealed record SpeedGapContext(double? SpeedBefore, double GapBeforeSeconds, double? SpeedAfter, double GapAfterSeconds);

        /// <summary>
        /// NMEA(実体は独自バイナリ)ファイルをパースする。
        /// </summary>
        /// <param name="nmeaFilePath">対象ファイル</param>
        /// <param name="startTimeHint">動画開始時刻（絶対Timestampの算出に使用、無ければUnixEpoch基準）</param>
        /// <param name="videoDurationHint">動画の総再生時間。指定するとVideoOffsetを
        /// 「有効レコード数で均等按分」して算出する（レコード自体には明示的なタイムスタンプが
        /// 無いため、こちらを優先する）。未指定の場合は10Hzサンプリング（1レコード=100ms）を
        /// 仮定して算出する（精度は低い）。</param>
        /// <param name="gapContext">先頭/末尾が測位ロストのとき、前後のファイルから得た速度推定用の情報（省略可）。</param>
        public static List<DashcamSensorFrame> Parse(string nmeaFilePath, DateTime? startTimeHint = null, TimeSpan? videoDurationHint = null,
            SpeedGapContext? gapContext = null)
        {
            var frames = new List<DashcamSensorFrame>();
            if (!File.Exists(nmeaFilePath))
                return frames;

            byte[] data = File.ReadAllBytes(nmeaFilePath);
            var raw = new List<(bool hasFix, int latRaw, int lonRaw, short field1, short ax, short ay, short az)>();

            for (int off = 0; off + RecordSize <= data.Length; off += RecordSize)
            {
                var span = data.AsSpan(off, RecordSize);

                uint header = BinaryPrimitives.ReadUInt32BigEndian(span[0..4]);
                bool isPadding = header == 0 && IsAllZero(span);
                if (isPadding)
                    break; // 固定長バッファの未使用テールに到達（以降も全ゼロ）

                bool hasFix = header == 1;

                int latRaw = BinaryPrimitives.ReadInt32BigEndian(span[4..8]);
                int lonRaw = BinaryPrimitives.ReadInt32BigEndian(span[8..12]);
                short field1 = BinaryPrimitives.ReadInt16BigEndian(span[16..18]);
                short ax = BinaryPrimitives.ReadInt16BigEndian(span[18..20]);
                short ay = BinaryPrimitives.ReadInt16BigEndian(span[20..22]);
                short az = BinaryPrimitives.ReadInt16BigEndian(span[22..24]);

                raw.Add((hasFix, latRaw, lonRaw, field1, ax, ay, az));
            }

            if (raw.Count == 0)
                return frames;

            DateTime baseTime = startTimeHint ?? DateTime.UnixEpoch;

            // 速度（未測位は0）。測位復帰直後の過渡値はここで補正する。
            var speeds = new double[raw.Count];
            var hasFixArr = new bool[raw.Count];
            var axG = new double[raw.Count];
            var ayG = new double[raw.Count];
            var azG = new double[raw.Count];
            for (int i = 0; i < raw.Count; i++)
            {
                hasFixArr[i] = raw[i].hasFix;
                speeds[i] = raw[i].hasFix ? raw[i].field1 * SpeedRawToKmh : 0;
                axG[i] = raw[i].ax * AccelRawToG;
                ayG[i] = raw[i].ay * AccelRawToG;
                azG[i] = raw[i].az * AccelRawToG;
            }
            double secPerRecord = videoDurationHint.HasValue && raw.Count > 1
                ? videoDurationHint.Value.TotalSeconds / (raw.Count - 1)
                : 0.1;
            var estimatedArr = new bool[raw.Count];
            if (secPerRecord > 0)
            {
                CorrectReacquisitionSpeedTransient(hasFixArr, speeds, axG, ayG, azG, secPerRecord);
                EstimateSpeedInGpsLoss(hasFixArr, speeds, estimatedArr, secPerRecord, gapContext);
            }

            for (int i = 0; i < raw.Count; i++)
            {
                var r = raw[i];

                TimeSpan offset;
                if (videoDurationHint.HasValue && raw.Count > 1)
                {
                    // レコード自体には明示的なタイムスタンプが無いため、動画長が分かっていれば
                    // レコード順で均等按分する
                    offset = TimeSpan.FromTicks(videoDurationHint.Value.Ticks * i / (raw.Count - 1));
                }
                else
                {
                    // フォールバック: レコードに明示的なタイムスタンプが無いため、10Hzサンプリング
                    // （1レコード=100ms）を仮定する。videoDurationHintが取得できる経路では
                    // 上のブロックが優先されるため、通常はこちらに来ない想定。
                    offset = TimeSpan.FromMilliseconds(i * 100);
                }

                double speedKmh = speeds[i];
                double ax = r.ax * AccelRawToG;
                double ay = r.ay * AccelRawToG;
                double az = r.az * AccelRawToG;

                const double GpsCoordScale = 1.0 / (3600.0 * 256.0);

                double lat = r.latRaw * GpsCoordScale;
                double lon = r.lonRaw * GpsCoordScale;

                frames.Add(new DashcamSensorFrame(
                    VideoOffset: offset,
                    Timestamp: baseTime + offset,
                    Latitude: lat,
                    Longitude: lon,
                    SpeedKmh: speedKmh,
                    AccelX: ax,
                    AccelY: ay,
                    AccelZ: az,
                    HasGpsFix: r.hasFix,
                    SpeedEstimated: estimatedArr[i]));
            }

            return frames;
        }

        /// <summary>
        /// 未測位→測位に戻った直後の速度過渡値を、収束後の安定値へ置き換える（速度のみ。位置・加速度は触らない）。
        /// 対象は復帰点から最大SettleScanSeconds秒の範囲だけで、測位継続中の通常走行区間は一切変更しない。
        /// </summary>
        private static void CorrectReacquisitionSpeedTransient(
            bool[] hasFix, double[] speed, double[] axG, double[] ayG, double[] azG, double secPerRecord)
        {
            int n = speed.Length;
            int scanMax = Math.Max(1, (int)Math.Round(SettleScanSeconds / secPerRecord));
            int stableLen = Math.Max(2, (int)Math.Round(SettleStableSeconds / secPerRecord));

            for (int k = 1; k < n; k++)
            {
                if (!hasFix[k] || hasFix[k - 1]) continue; // 未測位→測位の遷移点のみ

                // 収束点s: 以降stableLen件がすべて測位中で、速度変動が許容幅以内になる最初の位置
                int limit = Math.Min(k + scanMax, n - stableLen);
                int s = -1;
                for (int c = k; c <= limit; c++)
                {
                    if (!hasFix[c]) break;
                    if (IsStableWindow(hasFix, speed, c, stableLen)) { s = c; break; }
                }
                if (s <= k) continue; // 収束点が見つからない、または復帰直後から安定している

                double vStable = 0;
                for (int j = s; j < s + stableLen; j++) vStable += speed[j];
                vStable /= stableLen;

                double maxDev = 0;
                for (int j = k; j < s; j++)
                    maxDev = Math.Max(maxDev, Math.Abs(speed[j] - vStable));
                if (maxDev < SettleMinDeviationKmh) continue;

                // 加速度の裏取り: 速度変化が本物なら、その区間の加速度ベクトル平均は収束後から
                // 速度変化に見合う量だけ変化しているはず（軸割り当てが未確定でも使えるようベクトル差で判定）
                double shiftG = MeanVectorDistance(axG, ayG, azG, k, s, s, s + stableLen);
                double durationSec = Math.Max((s - k) * secPerRecord, 1.0);
                double expectedG = (maxDev / 3.6) / durationSec / 9.80665;
                if (shiftG >= Math.Max(SettleImuMinShiftG, SettleImuExpectedRatio * expectedG))
                    continue; // 実際の加減速あり → 補正しない

                for (int j = k; j < s; j++)
                    speed[j] = vStable;
            }
        }

        /// <summary>測位ロスト区間の速度を、直前の測位速度と復帰後の測位速度の線形補間で埋める。</summary>
        private static void EstimateSpeedInGpsLoss(bool[] hasFix, double[] speed, bool[] estimated, double secPerRecord, SpeedGapContext? ctx)
        {
            double maxGap = MaxEstimateGapSeconds;
            if (maxGap <= 0) return;

            int n = speed.Length;
            int a = 0;
            while (a < n)
            {
                if (hasFix[a]) { a++; continue; }
                int b = a;
                while (b + 1 < n && !hasFix[b + 1]) b++;

                // 区間の前後の測位速度（ファイルの端に接する側は前後のファイルの情報を使う）
                double? v0 = null, v1 = null;
                double gapBefore = 0, gapAfter = 0;
                if (a > 0) v0 = speed[a - 1];
                else if (ctx != null) { v0 = ctx.SpeedBefore; gapBefore = ctx.GapBeforeSeconds; }
                if (b < n - 1) v1 = speed[b + 1];
                else if (ctx != null) { v1 = ctx.SpeedAfter; gapAfter = ctx.GapAfterSeconds; }

                double runSec = (b - a + 1) * secPerRecord;
                double total = gapBefore + runSec + gapAfter;

                if (total <= maxGap && (v0.HasValue || v1.HasValue))
                {
                    for (int k = a; k <= b; k++)
                    {
                        double v;
                        if (v0.HasValue && v1.HasValue && total > 0)
                        {
                            double elapsed = gapBefore + (k - a + 0.5) * secPerRecord;
                            v = v0.Value + (v1.Value - v0.Value) * (elapsed / total);
                        }
                        else
                        {
                            v = v0 ?? v1!.Value; // 片側のみ: その値を保持
                        }
                        speed[k] = Math.Max(0, v);
                        estimated[k] = true;
                    }
                }

                a = b + 1;
            }
        }

        private static bool IsStableWindow(bool[] hasFix, double[] speed, int start, int len)
        {
            if (start + len > speed.Length) return false;
            double min = double.MaxValue, max = double.MinValue;
            for (int j = start; j < start + len; j++)
            {
                if (!hasFix[j]) return false;
                if (speed[j] < min) min = speed[j];
                if (speed[j] > max) max = speed[j];
            }
            return max - min <= SettleStableToleranceKmh;
        }

        /// <summary>区間[aFrom,aTo)と区間[bFrom,bTo)の3軸加速度の平均ベクトル同士の距離(G)。</summary>
        private static double MeanVectorDistance(double[] x, double[] y, double[] z, int aFrom, int aTo, int bFrom, int bTo)
        {
            static (double, double, double) Mean(double[] px, double[] py, double[] pz, int from, int to)
            {
                double sx = 0, sy = 0, sz = 0;
                for (int j = from; j < to; j++) { sx += px[j]; sy += py[j]; sz += pz[j]; }
                int cnt = Math.Max(1, to - from);
                return (sx / cnt, sy / cnt, sz / cnt);
            }

            var (ax, ay, az) = Mean(x, y, z, aFrom, aTo);
            var (bx, by, bz) = Mean(x, y, z, bFrom, bTo);
            double dx = ax - bx, dy = ay - by, dz = az - bz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static bool IsAllZero(ReadOnlySpan<byte> span)
        {
            foreach (var b in span)
                if (b != 0) return false;
            return true;
        }
    }
}
