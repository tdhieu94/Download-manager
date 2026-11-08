using System.Buffers.Binary;
using System.Text;

namespace DoMa.Core.PostProcess;

/// <summary>
/// Chuyển video MPEG-TS (.ts, thường có từ luồng HLS) sang MP4 mà không nén lại: chỉ đóng gói lại
/// hình H.264 và tiếng AAC nên rất nhanh và giữ nguyên chất lượng. Codec khác (HEVC, MP3, AC-3…) thì báo
/// <see cref="NotSupportedException"/> để giữ nguyên tệp .ts.
/// </summary>
public static class TsToMp4
{
    private const int PacketSize = 188;
    private const long Wrap = 1L << 33;

    public static bool CanConvert(string path) => Path.GetExtension(path).Equals(".ts", StringComparison.OrdinalIgnoreCase);

    /// <summary>Chuyển <paramref name="input"/> thành <paramref name="output"/>. Lỗi thì xoá tệp output dở dang.</summary>
    public static void Convert(string input, string output, CancellationToken ct = default)
    {
        try
        {
            using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var target = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
            new Remuxer(source, target, ct).Run();
        }
        catch
        {
            try
            {
                File.Delete(output);
            }
            catch (IOException)
            {
            }
            throw;
        }
    }

    private sealed class Track
    {
        public required bool Video;
        public readonly List<long> Offsets = [];
        public readonly List<int> Sizes = [];
        public readonly List<long> Dts = [];
        public readonly List<long> Pts = [];
        public readonly List<bool> Sync = [];
        public byte[]? Sps;
        public byte[]? Pps;
        public int SampleRate;
        public int Channels;
        public int AacProfile;
        public int FrequencyIndex;
        public int Width;
        public int Height;
        public int Timescale => Video ? 90000 : SampleRate;
        public int Count => Sizes.Count;
    }

    private sealed class Pes
    {
        public readonly MemoryStream Data = new();
        public long? Pts;
        public long? Dts;
    }

    private sealed class Remuxer(Stream source, Stream target, CancellationToken ct)
    {
        private readonly Dictionary<int, Pes> _pes = [];
        private readonly Dictionary<int, long> _lastTs = [];
        private int _pmtPid = -1;
        private int _videoPid = -1;
        private int _audioPid = -1;
        private readonly Track _video = new() { Video = true };
        private readonly Track _audio = new() { Video = false };
        private long _mdatStart;

        public void Run()
        {
            WriteFtyp();
            _mdatStart = target.Position;
            // mdat dùng "largesize" 64-bit để không giới hạn 4 GB; kích thước ghi lại ở cuối.
            target.Write([0, 0, 0, 1, (byte)'m', (byte)'d', (byte)'a', (byte)'t', 0, 0, 0, 0, 0, 0, 0, 0]);

            var packet = new byte[PacketSize];
            var count = 0;
            while (ReadPacket(packet))
            {
                if (++count % 4096 == 0)
                    ct.ThrowIfCancellationRequested();
                HandlePacket(packet);
            }
            foreach (var pid in _pes.Keys.ToList())
                Flush(pid);

            if (_video.Count == 0)
                throw new NotSupportedException(_videoPid < 0 ? L.T("Không tìm thấy hình H.264 trong tệp.") : L.T("Tệp không có khung hình nào."));
            if (_video.Sps is null || _video.Pps is null)
                throw new NotSupportedException(L.T("Thiếu thông tin giải mã H.264 (SPS/PPS)."));

            var mdatEnd = target.Position;
            target.Position = _mdatStart + 8;
            Span<byte> size = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(size, mdatEnd - _mdatStart);
            target.Write(size);
            target.Position = mdatEnd;
            WriteMoov();
        }

        private bool ReadPacket(byte[] packet)
        {
            while (true)
            {
                var read = source.ReadAtLeast(packet, PacketSize, throwOnEndOfStream: false);
                if (read < PacketSize)
                    return false;
                if (packet[0] == 0x47)
                    return true;
                // Mất đồng bộ: dò byte 0x47 tiếp theo.
                var at = Array.IndexOf(packet, (byte)0x47, 1);
                if (at < 0)
                    continue;
                source.Seek(at - PacketSize, SeekOrigin.Current);
            }
        }

        private void HandlePacket(byte[] p)
        {
            var start = (p[1] & 0x40) != 0;
            var pid = ((p[1] & 0x1F) << 8) | p[2];
            var adaptation = (p[3] >> 4) & 0x3;
            var offset = 4;
            if (adaptation is 2 or 3)
                offset += 1 + p[4];
            if (adaptation is 0 or 2 || offset >= PacketSize)
                return;
            var payload = p.AsSpan(offset);

            if (pid == 0)
                ParsePat(payload, start);
            else if (pid == _pmtPid)
                ParsePmt(payload, start);
            else if (pid == _videoPid || pid == _audioPid)
            {
                if (start)
                {
                    Flush(pid);
                    var pes = new Pes();
                    if (ParsePesHeader(payload, pes, out var headerLength))
                    {
                        pes.Data.Write(payload[headerLength..]);
                        _pes[pid] = pes;
                    }
                }
                else if (_pes.TryGetValue(pid, out var pes))
                    pes.Data.Write(payload);
            }
        }

        private void ParsePat(ReadOnlySpan<byte> s, bool start)
        {
            if (!start || s.Length < 1)
                return;
            s = s[(1 + s[0])..];
            if (s.Length < 8)
                return;
            var sectionLength = ((s[1] & 0x0F) << 8) | s[2];
            var end = Math.Min(s.Length, 3 + sectionLength - 4);
            for (var i = 8; i + 4 <= end; i += 4)
            {
                var program = (s[i] << 8) | s[i + 1];
                if (program != 0)
                {
                    _pmtPid = ((s[i + 2] & 0x1F) << 8) | s[i + 3];
                    return;
                }
            }
        }

        private void ParsePmt(ReadOnlySpan<byte> s, bool start)
        {
            if (!start || s.Length < 1)
                return;
            s = s[(1 + s[0])..];
            if (s.Length < 12)
                return;
            var sectionLength = ((s[1] & 0x0F) << 8) | s[2];
            var end = Math.Min(s.Length, 3 + sectionLength - 4);
            var infoLength = ((s[10] & 0x0F) << 8) | s[11];
            var otherVideo = false;
            for (var i = 12 + infoLength; i + 5 <= end;)
            {
                var type = s[i];
                var pid = ((s[i + 1] & 0x1F) << 8) | s[i + 2];
                var esInfo = ((s[i + 3] & 0x0F) << 8) | s[i + 4];
                if (type == 0x1B && _videoPid < 0)
                    _videoPid = pid;
                else if (type == 0x0F && _audioPid < 0)
                    _audioPid = pid;
                else if (type is 0x24 or 0x02 or 0x10)
                    otherVideo = true;
                i += 5 + esInfo;
            }
            if (_videoPid < 0 && otherVideo)
                throw new NotSupportedException(L.T("Video không dùng H.264 (ví dụ HEVC), DoMa giữ nguyên tệp .ts."));
        }

        private static bool ParsePesHeader(ReadOnlySpan<byte> s, Pes pes, out int headerLength)
        {
            headerLength = 0;
            if (s.Length < 9 || s[0] != 0 || s[1] != 0 || s[2] != 1)
                return false;
            var flags = s[7];
            headerLength = 9 + s[8];
            if (headerLength > s.Length)
                return false;
            if ((flags & 0x80) != 0 && s.Length >= 14)
                pes.Pts = ReadTimestamp(s[9..]);
            if ((flags & 0x40) != 0 && s.Length >= 19)
                pes.Dts = ReadTimestamp(s[14..]);
            return true;
        }

        private static long ReadTimestamp(ReadOnlySpan<byte> b) =>
            ((long)(b[0] & 0x0E) << 29) | ((long)b[1] << 22) | ((long)(b[2] & 0xFE) << 14) | ((long)b[3] << 7) | ((long)b[4] >> 1);

        /// <summary>Bỏ hiện tượng quay vòng 33 bit của đồng hồ 90 kHz để thời gian luôn tăng.</summary>
        private long Unwrap(int pid, long ts)
        {
            if (_lastTs.TryGetValue(pid, out var last))
            {
                var baseTs = last - (last % Wrap);
                var candidate = baseTs + ts;
                if (candidate < last - Wrap / 2)
                    candidate += Wrap;
                else if (candidate > last + Wrap / 2)
                    candidate -= Wrap;
                ts = candidate;
            }
            _lastTs[pid] = ts;
            return ts;
        }

        private void Flush(int pid)
        {
            if (!_pes.Remove(pid, out var pes) || pes.Data.Length == 0)
                return;
            var data = pes.Data.GetBuffer().AsSpan(0, (int)pes.Data.Length);
            if (pid == _videoPid)
                AddVideo(data, pes);
            else
                AddAudio(data, pes);
        }

        private void AddVideo(ReadOnlySpan<byte> annexB, Pes pes)
        {
            var sample = new MemoryStream();
            var key = false;
            Span<byte> len = stackalloc byte[4];
            foreach (var (start, length) in NalUnits(annexB))
            {
                var nal = annexB.Slice(start, length);
                var type = nal[0] & 0x1F;
                switch (type)
                {
                    case 9: // Access unit delimiter: không cần trong MP4.
                        continue;
                    case 7:
                        _video.Sps ??= nal.ToArray();
                        if (_video.Width == 0)
                            (_video.Width, _video.Height) = SpsSize(nal);
                        break;
                    case 8:
                        _video.Pps ??= nal.ToArray();
                        break;
                    case 5:
                        key = true;
                        break;
                }
                BinaryPrimitives.WriteInt32BigEndian(len, length);
                sample.Write(len);
                sample.Write(nal);
            }
            if (sample.Length == 0)
                return;
            if (pes.Pts is null && _video.Count > 0)
            {
                // Khung hình bị chia thành nhiều PES: nối vào mẫu trước.
                target.Write(sample.GetBuffer().AsSpan(0, (int)sample.Length));
                _video.Sizes[^1] += (int)sample.Length;
                _video.Sync[^1] |= key;
                return;
            }
            if (pes.Pts is not { } rawPts)
                return;
            var pts = Unwrap(_videoPid, rawPts);
            var dts = pes.Dts is { } rawDts ? pts - ((rawPts - rawDts + Wrap) % Wrap) : pts;
            _video.Offsets.Add(target.Position);
            _video.Sizes.Add((int)sample.Length);
            _video.Pts.Add(pts);
            _video.Dts.Add(dts);
            _video.Sync.Add(key);
            target.Write(sample.GetBuffer().AsSpan(0, (int)sample.Length));
        }

        private void AddAudio(ReadOnlySpan<byte> adts, Pes pes)
        {
            long? next = pes.Pts is { } pts ? Unwrap(_audioPid, pts) : null;
            var i = 0;
            while (i + 7 <= adts.Length)
            {
                if (adts[i] != 0xFF || (adts[i + 1] & 0xF0) != 0xF0)
                {
                    i++;
                    continue;
                }
                var protectionAbsent = (adts[i + 1] & 0x01) != 0;
                var profile = (adts[i + 2] >> 6) & 0x3;
                var freqIndex = (adts[i + 2] >> 2) & 0xF;
                var channels = ((adts[i + 2] & 0x1) << 2) | ((adts[i + 3] >> 6) & 0x3);
                var frameLength = ((adts[i + 3] & 0x3) << 11) | (adts[i + 4] << 3) | ((adts[i + 5] >> 5) & 0x7);
                var header = protectionAbsent ? 7 : 9;
                if (frameLength < header || i + frameLength > adts.Length || freqIndex >= Frequencies.Length)
                    break;
                if (_audio.SampleRate == 0)
                {
                    _audio.SampleRate = Frequencies[freqIndex];
                    _audio.FrequencyIndex = freqIndex;
                    _audio.Channels = channels;
                    _audio.AacProfile = profile + 1;
                }
                // Thời điểm tính theo 90 kHz; khung AAC dài 1024 mẫu.
                var at = next ?? (_audio.Count > 0 ? _audio.Pts[^1] + 1024L * 90000 / _audio.SampleRate : 0);
                _audio.Offsets.Add(target.Position);
                _audio.Sizes.Add(frameLength - header);
                _audio.Pts.Add(at);
                _audio.Dts.Add(at);
                _audio.Sync.Add(true);
                target.Write(adts.Slice(i + header, frameLength - header));
                next = at + 1024L * 90000 / _audio.SampleRate;
                i += frameLength;
            }
        }

        private static readonly int[] Frequencies = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

        private static IEnumerable<(int Start, int Length)> NalUnits(ReadOnlySpan<byte> data)
        {
            var list = new List<(int, int)>();
            var i = 0;
            var start = -1;
            while (i + 3 <= data.Length)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                {
                    if (start >= 0)
                        list.Add((start, TrimEnd(data, start, i)));
                    i += 3;
                    start = i;
                    continue;
                }
                i++;
            }
            if (start >= 0 && start < data.Length)
                list.Add((start, data.Length - start));
            return list.Where(n => n.Item2 > 0);

            static int TrimEnd(ReadOnlySpan<byte> d, int s, int e)
            {
                while (e > s && d[e - 1] == 0)
                    e--;
                return e - s;
            }
        }

        // ───────── MP4 ─────────

        private void WriteFtyp()
        {
            var box = new BoxWriter();
            box.Begin("ftyp");
            box.Ascii("isom");
            box.U32(512);
            foreach (var brand in new[] { "isom", "iso2", "avc1", "mp41" })
                box.Ascii(brand);
            box.End();
            target.Write(box.ToArray());
        }

        private void WriteMoov()
        {
            var tracks = new List<Track> { _video };
            if (_audio.Count > 0)
                tracks.Add(_audio);

            // Mốc thời gian chung: thời điểm hiển thị sớm nhất của mọi track (90 kHz).
            var origin = tracks.Min(t => t.Pts.Min());
            var movieScale = 1000;
            var durations = tracks.Select(t => ToScale(End(t) - origin, 90000, movieScale)).ToList();

            var b = new BoxWriter();
            b.Begin("moov");
            b.Begin("mvhd", 0, 0);
            b.U32(0); b.U32(0); b.U32((uint)movieScale); b.U32((uint)durations.Max());
            b.U32(0x00010000); b.U16(0x0100); b.Zero(10);
            Matrix(b);
            b.Zero(24);
            b.U32((uint)tracks.Count + 1);
            b.End();
            for (var t = 0; t < tracks.Count; t++)
                WriteTrak(b, tracks[t], t + 1, origin, durations[t], movieScale);
            b.End();
            target.Write(b.ToArray());
        }

        private static long End(Track t)
        {
            var last = t.Count - 1;
            var step = t.Count > 1 ? t.Dts[last] - t.Dts[last - 1] : t.Video ? 3600 : 1024L * 90000 / Math.Max(1, t.SampleRate);
            return t.Pts.Max() + Math.Max(1, step);
        }

        private static long ToScale(long value, int from, int to) => value * to / from;

        private static void Matrix(BoxWriter b)
        {
            foreach (var v in new uint[] { 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000 })
                b.U32(v);
        }

        private static void WriteTrak(BoxWriter b, Track t, int id, long origin, long movieDuration, int movieScale)
        {
            var scale = t.Timescale;
            // Đổi thời điểm (90 kHz) sang thang thời gian của track.
            long Conv(long ts) => (ts - origin) * scale / 90000;
            var dts = t.Dts.Select(Conv).ToList();
            var pts = t.Pts.Select(Conv).ToList();
            var durations = new List<long>(t.Count);
            for (var i = 0; i < t.Count; i++)
            {
                var d = i + 1 < t.Count ? dts[i + 1] - dts[i] : (i > 0 ? durations[^1] : (t.Video ? scale / 25 : 1024));
                durations.Add(t.Video ? Math.Max(1, d) : 1024);
            }
            var mediaDuration = durations.Sum();

            b.Begin("trak");
            b.Begin("tkhd", 0, 3);
            b.U32(0); b.U32(0); b.U32((uint)id); b.U32(0); b.U32((uint)movieDuration);
            b.Zero(8); b.U16(0); b.U16(t.Video ? (ushort)0 : (ushort)1); b.U16(t.Video ? (ushort)0 : (ushort)0x0100); b.U16(0);
            Matrix(b);
            b.U32((uint)(t.Video ? t.Width << 16 : 0)); b.U32((uint)(t.Video ? t.Height << 16 : 0));
            b.End();

            // Danh sách sửa (edit list): bù khoảng trống đầu track và độ lệch hiển thị của B-frame.
            var firstPts = pts.Min();
            b.Begin("edts");
            var empty = ToScale(firstPts * 90000 / scale, 90000, movieScale);
            b.Begin("elst", 0, 0);
            b.U32(empty > 0 ? 2u : 1u);
            if (empty > 0)
            {
                b.U32((uint)empty); b.U32(0xFFFFFFFF); b.U32(0x00010000);
            }
            b.U32((uint)Math.Max(0, movieDuration - Math.Max(0, empty)));
            b.U32((uint)(firstPts - dts[0]));
            b.U32(0x00010000);
            b.End();
            b.End();

            b.Begin("mdia");
            b.Begin("mdhd", 0, 0);
            b.U32(0); b.U32(0); b.U32((uint)scale); b.U32((uint)mediaDuration);
            b.U16(0x55C4); b.U16(0); // ngôn ngữ "und"
            b.End();
            b.Begin("hdlr", 0, 0);
            b.U32(0); b.Ascii(t.Video ? "vide" : "soun"); b.Zero(12);
            b.Bytes(Encoding.ASCII.GetBytes(t.Video ? "VideoHandler\0" : "SoundHandler\0"));
            b.End();
            b.Begin("minf");
            if (t.Video)
            {
                b.Begin("vmhd", 0, 1); b.Zero(8); b.End();
            }
            else
            {
                b.Begin("smhd", 0, 0); b.Zero(4); b.End();
            }
            b.Begin("dinf");
            b.Begin("dref", 0, 0); b.U32(1); b.Begin("url ", 0, 1); b.End(); b.End();
            b.End();

            b.Begin("stbl");
            b.Begin("stsd", 0, 0);
            b.U32(1);
            if (t.Video)
                WriteAvc1(b, t);
            else
                WriteMp4a(b, t);
            b.End();

            // stts: thời lượng mỗi mẫu (gộp các mẫu liền nhau cùng thời lượng).
            var stts = RunLength(durations);
            b.Begin("stts", 0, 0);
            b.U32((uint)stts.Count);
            foreach (var (count, value) in stts)
            {
                b.U32((uint)count); b.U32((uint)value);
            }
            b.End();

            if (t.Video)
            {
                var offsets = pts.Zip(dts, (p, d) => p - d).ToList();
                if (offsets.Any(o => o != 0))
                {
                    var ctts = RunLength(offsets);
                    b.Begin("ctts", 1, 0);
                    b.U32((uint)ctts.Count);
                    foreach (var (count, value) in ctts)
                    {
                        b.U32((uint)count); b.U32((uint)(int)value);
                    }
                    b.End();
                }
                var sync = Enumerable.Range(0, t.Count).Where(i => t.Sync[i]).ToList();
                if (sync.Count < t.Count)
                {
                    b.Begin("stss", 0, 0);
                    b.U32((uint)sync.Count);
                    foreach (var i in sync)
                        b.U32((uint)i + 1);
                    b.End();
                }
            }

            b.Begin("stsc", 0, 0);
            b.U32(1); b.U32(1); b.U32(1); b.U32(1);
            b.End();
            b.Begin("stsz", 0, 0);
            b.U32(0); b.U32((uint)t.Count);
            foreach (var size in t.Sizes)
                b.U32((uint)size);
            b.End();
            b.Begin("co64", 0, 0);
            b.U32((uint)t.Count);
            foreach (var offset in t.Offsets)
                b.U64((ulong)offset);
            b.End();
            b.End(); // stbl
            b.End(); // minf
            b.End(); // mdia
            b.End(); // trak
        }

        private static List<(int Count, long Value)> RunLength(List<long> values)
        {
            var result = new List<(int, long)>();
            foreach (var v in values)
            {
                if (result.Count > 0 && result[^1].Item2 == v)
                    result[^1] = (result[^1].Item1 + 1, v);
                else
                    result.Add((1, v));
            }
            return result;
        }

        private static void WriteAvc1(BoxWriter b, Track t)
        {
            b.Begin("avc1");
            b.Zero(6); b.U16(1);
            b.Zero(16);
            b.U16((ushort)t.Width); b.U16((ushort)t.Height);
            b.U32(0x00480000); b.U32(0x00480000); b.U32(0); b.U16(1);
            b.Zero(32);
            b.U16(0x0018); b.U16(0xFFFF);
            b.Begin("avcC");
            var sps = t.Sps!;
            b.U8(1); b.U8(sps[1]); b.U8(sps[2]); b.U8(sps[3]); b.U8(0xFF);
            b.U8(0xE1); b.U16((ushort)sps.Length); b.Bytes(sps);
            b.U8(1); b.U16((ushort)t.Pps!.Length); b.Bytes(t.Pps);
            b.End();
            b.End();
        }

        private static void WriteMp4a(BoxWriter b, Track t)
        {
            b.Begin("mp4a");
            b.Zero(6); b.U16(1);
            b.Zero(8);
            b.U16((ushort)Math.Max(1, t.Channels)); b.U16(16); b.U16(0); b.U16(0);
            b.U32((uint)(t.SampleRate << 16));
            b.Begin("esds", 0, 0);
            // AudioSpecificConfig: loại AAC (5 bit), chỉ số tần số (4 bit), số kênh (4 bit).
            var asc = new byte[]
            {
                (byte)((t.AacProfile << 3) | (t.FrequencyIndex >> 1)),
                (byte)(((t.FrequencyIndex & 1) << 7) | (t.Channels << 3)),
            };
            // ES_Descriptor > DecoderConfigDescriptor > DecoderSpecificInfo, + SLConfigDescriptor.
            b.U8(0x03); b.U8((byte)(3 + 2 + 13 + 2 + asc.Length + 3));
            b.U16(1); b.U8(0);
            b.U8(0x04); b.U8((byte)(13 + 2 + asc.Length));
            b.U8(0x40); b.U8(0x15); b.U8(0); b.U16(0); b.U32(0); b.U32(0);
            b.U8(0x05); b.U8((byte)asc.Length); b.Bytes(asc);
            b.U8(0x06); b.U8(1); b.U8(0x02);
            b.End();
            b.End();
        }

        /// <summary>Đọc kích thước khung hình từ SPS H.264.</summary>
        private static (int Width, int Height) SpsSize(ReadOnlySpan<byte> nal)
        {
            try
            {
                var r = new BitReader(RemoveEmulation(nal[1..]));
                var profile = r.Bits(8);
                r.Bits(16);
                r.Ue();
                var chroma = 1;
                if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
                {
                    chroma = r.Ue();
                    if (chroma == 3)
                        r.Bits(1);
                    r.Ue();
                    r.Ue();
                    r.Bits(1);
                    if (r.Bits(1) == 1)
                    {
                        for (var i = 0; i < (chroma != 3 ? 8 : 12); i++)
                        {
                            if (r.Bits(1) == 0)
                                continue;
                            var size = i < 6 ? 16 : 64;
                            int last = 8, next = 8;
                            for (var j = 0; j < size; j++)
                            {
                                if (next != 0)
                                    next = (last + r.Se() + 256) % 256;
                                last = next == 0 ? last : next;
                            }
                        }
                    }
                }
                r.Ue();
                var pocType = r.Ue();
                if (pocType == 0)
                    r.Ue();
                else if (pocType == 1)
                {
                    r.Bits(1);
                    r.Se();
                    r.Se();
                    var n = r.Ue();
                    for (var i = 0; i < n; i++)
                        r.Se();
                }
                r.Ue();
                r.Bits(1);
                var widthMbs = r.Ue() + 1;
                var heightMapUnits = r.Ue() + 1;
                var frameMbsOnly = r.Bits(1);
                if (frameMbsOnly == 0)
                    r.Bits(1);
                r.Bits(1);
                int cl = 0, cr = 0, ctop = 0, cb = 0;
                if (r.Bits(1) == 1)
                {
                    cl = r.Ue(); cr = r.Ue(); ctop = r.Ue(); cb = r.Ue();
                }
                var cropX = chroma is 1 or 2 ? 2 : 1;
                var cropY = (chroma == 1 ? 2 : 1) * (2 - frameMbsOnly);
                var width = widthMbs * 16 - (cl + cr) * cropX;
                var height = (2 - frameMbsOnly) * heightMapUnits * 16 - (ctop + cb) * cropY;
                return (Math.Max(0, width), Math.Max(0, height));
            }
            catch (IndexOutOfRangeException)
            {
                return (0, 0);
            }
        }

        private static byte[] RemoveEmulation(ReadOnlySpan<byte> s)
        {
            var output = new List<byte>(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                if (i >= 2 && s[i] == 3 && s[i - 1] == 0 && s[i - 2] == 0)
                    continue;
                output.Add(s[i]);
            }
            return [.. output];
        }
    }

    private sealed class BitReader(byte[] data)
    {
        private int _bit;

        public int Bits(int n)
        {
            var v = 0;
            for (var i = 0; i < n; i++, _bit++)
                v = (v << 1) | ((data[_bit >> 3] >> (7 - (_bit & 7))) & 1);
            return v;
        }

        public int Ue()
        {
            var zeros = 0;
            while (Bits(1) == 0)
                zeros++;
            return zeros == 0 ? 0 : (1 << zeros) - 1 + Bits(zeros);
        }

        public int Se()
        {
            var k = Ue();
            return (k & 1) == 1 ? (k + 1) / 2 : -(k / 2);
        }
    }

    /// <summary>Ghi box MP4 lồng nhau; kích thước mỗi box được điền khi đóng.</summary>
    private sealed class BoxWriter
    {
        private readonly MemoryStream _s = new();
        private readonly Stack<long> _starts = new();

        public void Begin(string type)
        {
            _starts.Push(_s.Position);
            U32(0);
            Ascii(type);
        }

        public void Begin(string type, byte version, uint flags)
        {
            Begin(type);
            U32(((uint)version << 24) | flags);
        }

        public void End()
        {
            var start = _starts.Pop();
            var end = _s.Position;
            _s.Position = start;
            U32((uint)(end - start));
            _s.Position = end;
        }

        public void U8(byte v) => _s.WriteByte(v);

        public void U16(ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, v);
            _s.Write(b);
        }

        public void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            _s.Write(b);
        }

        public void U64(ulong v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, v);
            _s.Write(b);
        }

        public void Zero(int n) => _s.Write(new byte[n]);

        public void Ascii(string s) => _s.Write(Encoding.ASCII.GetBytes(s));

        public void Bytes(ReadOnlySpan<byte> b) => _s.Write(b);

        public byte[] ToArray() => _s.ToArray();
    }
}
