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

        /// <summary>
        /// NMEA(実体は独自バイナリ)ファイルをパースする。
        /// </summary>
        /// <param name="nmeaFilePath">対象ファイル</param>
        /// <param name="startTimeHint">動画開始時刻（絶対Timestampの算出に使用、無ければUnixEpoch基準）</param>
        /// <param name="videoDurationHint">動画の総再生時間。指定するとVideoOffsetを
        /// 「有効レコード数で均等按分」して算出する（レコード自体には明示的なタイムスタンプが
        /// 無いため、こちらを優先する）。未指定の場合は10Hzサンプリング（1レコード=100ms）を
        /// 仮定して算出する（精度は低い）。</param>
        public static List<DashcamSensorFrame> Parse(string nmeaFilePath, DateTime? startTimeHint = null, TimeSpan? videoDurationHint = null)
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

                double speedKmh = r.hasFix ? r.field1 * SpeedRawToKmh : 0;
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
                    HasGpsFix: r.hasFix));
            }

            return frames;
        }

        private static bool IsAllZero(ReadOnlySpan<byte> span)
        {
            foreach (var b in span)
                if (b != 0) return false;
            return true;
        }
    }
}
