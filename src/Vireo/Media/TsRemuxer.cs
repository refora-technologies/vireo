using System.Buffers.Binary;
using System.IO;

namespace Vireo.Media;

/// <summary>
/// Turns MPEG-TS (what most HLS streams are made of) into an ordinary MP4 without re-encoding: H.264 video and AAC sound
/// are lifted out of the transport stream and written into MP4 boxes. Other codecs are left alone (CanRemux says no).
/// </summary>
public static class TsRemuxer
{
    private const int Packet = 188;

    // ---- What is inside ----

    private enum Codec { None, H264, Aac }

    /// <summary>Whether these files hold only video/sound this converter understands (H.264, AAC) and at least one of them.</summary>
    public static bool CanRemux(params string[] files)
    {
        try
        {
            var found = false;
            foreach (var f in files)
            {
                using var s = File.OpenRead(f);
                var head = new byte[Math.Min(s.Length, 3_000_000)];
                s.ReadExactly(head);
                if (IsAdts(head)) { found = true; continue; }
                var kinds = ProgramStreams(head);
                if (kinds is null) return false;
                if (kinds.Count == 0) return false;
                foreach (var (_, type) in kinds)
                {
                    if (type is 0x1B or 0x0F) found = true;
                    else if (type is 0x24 or 0x02 or 0x10 or 0x03 or 0x04 or 0x81 or 0x87 or 0x11 or 0xEA) return false; // codecs we do not handle
                }
            }
            return found;
        }
        catch { return false; }
    }

    private static bool IsAdts(byte[] d)
    {
        var i = SkipId3(d, 0);
        return i + 1 < d.Length && d[i] == 0xFF && (d[i + 1] & 0xF6) == 0xF0;
    }

    private static int SkipId3(byte[] d, int i)
    {
        while (i + 10 <= d.Length && d[i] == 'I' && d[i + 1] == 'D' && d[i + 2] == '3')
            i += 10 + ((d[i + 6] & 0x7F) << 21 | (d[i + 7] & 0x7F) << 14 | (d[i + 8] & 0x7F) << 7 | (d[i + 9] & 0x7F));
        return i;
    }

    /// <summary>The (pid, stream type) pairs the first program declares, or null when no table was found.</summary>
    private static List<(int Pid, int Type)>? ProgramStreams(byte[] d)
    {
        int pmtPid = -1;
        for (var p = 0; p + Packet <= d.Length; p += Packet)
        {
            if (d[p] != 0x47) { p = Resync(d, p) - Packet; if (p < -Packet) return null; continue; }
            var pid = (d[p + 1] & 0x1F) << 8 | d[p + 2];
            var pusi = (d[p + 1] & 0x40) != 0;
            var at = PayloadStart(d, p);
            if (at < 0 || !pusi) continue;
            var section = at + 1 + d[at]; // pointer field
            if (section + 12 > p + Packet) continue;
            if (pid == 0 && d[section] == 0x00)
            {
                for (var i = section + 8; i + 4 <= p + Packet && i + 4 <= section + 3 + (((d[section + 1] & 0x0F) << 8) | d[section + 2]) - 4; i += 4)
                {
                    var program = d[i] << 8 | d[i + 1];
                    if (program != 0) { pmtPid = (d[i + 2] & 0x1F) << 8 | d[i + 3]; break; }
                }
            }
            else if (pid == pmtPid && d[section] == 0x02)
            {
                var length = ((d[section + 1] & 0x0F) << 8) | d[section + 2];
                var infoLength = ((d[section + 10] & 0x0F) << 8) | d[section + 11];
                var list = new List<(int, int)>();
                var end = Math.Min(p + Packet, section + 3 + length - 4);
                for (var i = section + 12 + infoLength; i + 5 <= end;)
                {
                    list.Add((((d[i + 1] & 0x1F) << 8) | d[i + 2], d[i]));
                    i += 5 + (((d[i + 3] & 0x0F) << 8) | d[i + 4]);
                }
                return list;
            }
        }
        return null;
    }

    private static int Resync(byte[] d, int from)
    {
        for (var i = from; i + 2 * Packet < d.Length; i++) if (d[i] == 0x47 && d[i + Packet] == 0x47 && d[i + 2 * Packet] == 0x47) return i;
        return int.MaxValue / 2;
    }

    private static int PayloadStart(byte[] d, int p)
    {
        var afc = (d[p + 3] >> 4) & 3;
        if ((afc & 1) == 0) return -1;
        var at = p + 4;
        if ((afc & 2) != 0) at += 1 + d[p + 4];
        return at < p + Packet ? at : -1;
    }

    // ---- Collected pieces ----

    private sealed class Sample
    {
        public long Offset; public int Size; public long Dts; public long Pts; public bool Key; public int Chunk;
    }

    private sealed class VideoTrack
    {
        public byte[]? Sps, Pps;
        public int Width, Height;
        public readonly List<Sample> Samples = [];
        public long FirstDts = long.MinValue;
        public bool Started;
    }

    private sealed class AudioTrack
    {
        public int Profile, Frequency, Channels;
        public int FrequencyIndex;
        public readonly List<Sample> Samples = [];
        public long StartPts = long.MinValue;
        public byte[] Carry = [];
        public long NextPts = long.MinValue;
        /// <summary>A bare AAC stream has no clock of its own; it starts where the picture starts.</summary>
        public bool Raw;
    }

    private static readonly int[] Rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    // ---- Reading ----

    /// <summary>Converts the transport streams (in order) into one MP4. The inputs may be one file with both video and sound, or a video file and a sound file.</summary>
    public static void Remux(IReadOnlyList<string> inputs, string output)
    {
        var video = new VideoTrack();
        var audio = new AudioTrack();
        using var outFile = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
        try
        {
            // ftyp, then an mdat whose size is filled in at the end
            var ftyp = Box("ftyp", w => { w.Write(Ascii("isom")); w.Write(U32(512)); w.Write(Ascii("isomiso2avc1mp41")); });
            outFile.Write(ftyp);
            var mdatAt = outFile.Position;
            outFile.Write(new byte[16]); // size(4) 'mdat' size64(8): big enough for any file
            var sink = new Sink(outFile, video, audio);

            foreach (var path in inputs) ReadFile(path, sink);
            sink.Flush();

            var mdatEnd = outFile.Position;
            outFile.Position = mdatAt;
            outFile.Write(U32(1)); outFile.Write(Ascii("mdat")); outFile.Write(U64((ulong)(mdatEnd - mdatAt)));
            outFile.Position = mdatEnd;
            if (video.Samples.Count == 0 && audio.Samples.Count == 0) throw new InvalidDataException("The stream held no video or sound that can be saved.");
            outFile.Write(BuildMoov(video, audio));
        }
        catch
        {
            outFile.Close();
            try { File.Delete(output); } catch { /* nothing there */ }
            throw;
        }
    }

    /// <summary>Takes finished samples and writes their bytes straight to the output.</summary>
    private sealed class Sink(FileStream file, VideoTrack video, AudioTrack audio)
    {
        public readonly Dictionary<int, Pes> Open = [];
        public int VideoPid = -1, AudioPid = -1, PmtPid = -1;
        private int _chunk;
        private char _last = ' ';

        public void Write(char track, byte[] data, Sample s)
        {
            if (track != _last) { _chunk++; _last = track; }
            s.Chunk = _chunk;
            s.Offset = file.Position;
            s.Size = data.Length;
            file.Write(data);
        }

        public void MarkRaw() => audio.Raw = true;

        /// <summary>Every transport stream file has its own table of what each PID carries (separate picture and sound files often reuse one PID).</summary>
        public void ResetPids() { VideoPid = AudioPid = PmtPid = -1; Open.Clear(); }

        public void Flush()
        {
            foreach (var p in Open.Values.ToList()) Finish(p);
            Open.Clear();
            FinishVideo();
        }

        public void Finish(Pes pes)
        {
            if (pes.Length == 0) return;
            var data = pes.Bytes();
            if (data.Length < 9 || data[0] != 0 || data[1] != 0 || data[2] != 1) { pes.Reset(); return; }
            var flags = data[7] >> 6;
            var headerLength = data[8];
            long pts = -1, dts = -1;
            if ((flags & 2) != 0 && data.Length >= 14) pts = Timestamp(data, 9);
            if (flags == 3 && data.Length >= 19) dts = Timestamp(data, 14);
            if (dts < 0) dts = pts;
            var start = 9 + headerLength;
            if (start > data.Length) { pes.Reset(); return; }
            var es = data.AsSpan(start);
            if (pes.Pid == VideoPid) AddVideo(es, pts, dts);
            else if (pes.Pid == AudioPid) AddAudio(es, pts);
            pes.Reset();
        }

        private static long Timestamp(byte[] d, int i) =>
            ((long)(d[i] & 0x0E) << 29) | ((long)d[i + 1] << 22) | ((long)(d[i + 2] & 0xFE) << 14) | ((long)d[i + 3] << 7) | ((long)(d[i + 4] >> 1));

        private long _lastDts = long.MinValue, _wrap;

        private long Unwrap(long v, ref long last)
        {
            // 33-bit clocks wrap after about 26 hours
            var u = v + _wrap;
            if (last != long.MinValue && u < last - (1L << 32)) { _wrap += 1L << 33; u = v + _wrap; }
            last = u;
            return u;
        }

        private long _lastPts = long.MinValue;

        // ---- Video: pictures are rebuilt from the byte stream, not taken one transport packet at a time ----
        // Some encoders cut one picture into several PES packets (and some put several pictures in one), so the
        // bytes of all of them are joined, split into NAL units at their start codes, and grouped into pictures.

        private byte[] _es = new byte[1 << 16];
        private int _esLength;
        private long _esBase;                                        // stream position of _es[0]
        private readonly Queue<(long At, long Pts, long Dts)> _stamps = new();
        private readonly List<(byte[] Nal, long Start)> _picture = [];
        private bool _pictureHasSlice, _pictureKey;
        private (long Pts, long Dts)? _lastStamp;
        private long _frameLength = 3000;

        private void AddVideo(ReadOnlySpan<byte> es, long pts, long dts)
        {
            if (pts >= 0) _stamps.Enqueue((_esBase + _esLength, pts, dts));
            if (_esLength + es.Length > _es.Length) Array.Resize(ref _es, Math.Max(_es.Length * 2, _esLength + es.Length));
            es.CopyTo(_es.AsSpan(_esLength));
            _esLength += es.Length;
            DrainVideo(final: false);
        }

        /// <summary>The end of a file: the last picture has no start code after it, so it is finished here.</summary>
        private void FinishVideo()
        {
            DrainVideo(final: true);
            EmitPicture();
        }

        /// <summary>Cuts finished NAL units out of the pending bytes and feeds them to the picture builder.</summary>
        private void DrainVideo(bool final)
        {
            var starts = new List<(int Code, int Payload)>(); // start code position and where the NAL data begins
            for (var i = 0; i + 2 < _esLength; i++)
            {
                if (_es[i] != 0 || _es[i + 1] != 0 || _es[i + 2] != 1) continue;
                starts.Add((i > 0 && _es[i - 1] == 0 ? i - 1 : i, i + 3));
                i += 2;
            }
            if (starts.Count == 0) { if (_esLength > 1 << 20) { _esBase += _esLength; _esLength = 0; } return; } // no start code in a megabyte: junk
            var complete = final ? starts.Count : starts.Count - 1; // the last NAL may still be arriving
            for (var k = 0; k < complete; k++)
            {
                var end = k + 1 < starts.Count ? starts[k + 1].Code : _esLength;
                var length = end - starts[k].Payload;
                while (length > 0 && _es[starts[k].Payload + length - 1] == 0) length--; // padding zeros before the next start code
                if (length > 0) AddNal(_es.AsSpan(starts[k].Payload, length).ToArray(), _esBase + starts[k].Code);
            }
            // keep what has not been used: from the start of the first unfinished NAL
            var keepFrom = final ? _esLength : starts[^1].Code;
            if (keepFrom > 0)
            {
                Buffer.BlockCopy(_es, keepFrom, _es, 0, _esLength - keepFrom);
                _esLength -= keepFrom;
                _esBase += keepFrom;
            }
        }

        private void AddNal(byte[] nal, long start)
        {
            var type = nal[0] & 0x1F;
            switch (type)
            {
                case 9: EmitPicture(); return;                               // an access unit delimiter always starts a new picture
                case 12: case 10: case 11: return;                           // filler and end markers
                case 7: case 8: case 6:
                    if (_pictureHasSlice) EmitPicture();                      // parameters and notes belong to the next picture
                    break;
                case 1: case 5:
                    if (_pictureHasSlice && nal.Length > 1 && (nal[1] & 0x80) != 0) EmitPicture(); // first_mb_in_slice == 0: a new picture
                    _pictureHasSlice = true;
                    _pictureKey |= type == 5;
                    break;
            }
            _picture.Add((nal, start));
        }

        private void EmitPicture()
        {
            var nals = _picture.ToList();
            var key = _pictureKey;
            _picture.Clear();
            _pictureHasSlice = false;
            _pictureKey = false;
            if (nals.Count == 0 || !nals.Any(n => (n.Nal[0] & 0x1F) is 1 or 5)) return;

            var avcc = new MemoryStream();
            Span<byte> len = stackalloc byte[4];
            foreach (var (nal, _) in nals)
            {
                var type = nal[0] & 0x1F;
                if (type == 7) { video.Sps ??= nal; if (video.Width == 0) (video.Width, video.Height) = Sps.Size(nal); continue; }
                if (type == 8) { video.Pps ??= nal; continue; }
                BinaryPrimitives.WriteInt32BigEndian(len, nal.Length);
                avcc.Write(len);
                avcc.Write(nal);
            }
            if (!video.Started)
            {
                if (!key || video.Sps is null || video.Pps is null) { DropStamps(nals[0].Start); return; } // start at the first picture that can be shown on its own
                video.Started = true;
            }

            // The clock of the PES packet this picture began in.
            var at = nals[0].Start;
            while (_stamps.Count > 1 && _stamps.ElementAt(1).At <= at) _stamps.Dequeue();
            (long Pts, long Dts) stamp;
            if (_stamps.Count > 0 && _stamps.Peek().At <= at) { var st = _stamps.Peek(); stamp = (st.Pts, st.Dts); }
            else if (_lastStamp is { } last) stamp = (last.Pts + _frameLength, last.Dts + _frameLength);
            else return;
            if (_lastStamp is { } prev && prev == stamp) stamp = (stamp.Pts + _frameLength, stamp.Dts + _frameLength); // several pictures under one clock reading
            else if (_lastStamp is { } before && stamp.Dts > before.Dts && stamp.Dts - before.Dts < 90000) _frameLength = stamp.Dts - before.Dts;
            _lastStamp = stamp;

            var d = Unwrap(stamp.Dts, ref _lastDts);
            var p = stamp.Pts + _wrap;
            if (p < d - (1L << 32)) p += 1L << 33;
            if (video.FirstDts == long.MinValue) video.FirstDts = d;
            var s = new Sample { Dts = d, Pts = p, Key = key };
            Write('v', avcc.ToArray(), s);
            video.Samples.Add(s);
        }

        private void DropStamps(long before)
        {
            while (_stamps.Count > 1 && _stamps.ElementAt(1).At <= before) _stamps.Dequeue();
        }

        private void AddAudio(ReadOnlySpan<byte> es, long pts)
        {
            var buf = audio.Carry.Length == 0 ? es.ToArray() : [.. audio.Carry, .. es.ToArray()];
            var i = 0;
            var first = true;
            while (i + 7 <= buf.Length)
            {
                if (buf[i] != 0xFF || (buf[i + 1] & 0xF6) != 0xF0) { i++; continue; }
                var header = (buf[i + 1] & 1) == 1 ? 7 : 9;
                var frame = ((buf[i + 3] & 3) << 11) | (buf[i + 4] << 3) | (buf[i + 5] >> 5);
                if (frame < header + 1) { i++; continue; }
                if (i + frame > buf.Length) break;
                if (audio.Frequency == 0)
                {
                    audio.Profile = (buf[i + 2] >> 6) + 1;
                    audio.FrequencyIndex = (buf[i + 2] >> 2) & 15;
                    audio.Frequency = audio.FrequencyIndex < Rates.Length ? Rates[audio.FrequencyIndex] : 44100;
                    audio.Channels = ((buf[i + 2] & 1) << 2) | (buf[i + 3] >> 6);
                }
                if (first && pts < 0 && audio.Raw && audio.NextPts == long.MinValue) { audio.NextPts = 0; audio.StartPts = 0; }
                if (first && pts >= 0)
                {
                    // Where this run of frames begins on the clock; the frames after it follow one by one.
                    var start = pts + _wrap;
                    if (audio.NextPts == long.MinValue || Math.Abs(start - audio.NextPts) > 90000 / 2) audio.NextPts = start;
                    if (audio.StartPts == long.MinValue) audio.StartPts = audio.NextPts;
                }
                first = false;
                if (audio.NextPts == long.MinValue) { i += frame; continue; }
                var s = new Sample { Pts = audio.NextPts, Dts = audio.NextPts, Key = true };
                Write('a', buf.AsSpan(i + header, frame - header).ToArray(), s);
                audio.Samples.Add(s);
                audio.NextPts += 1024L * 90000 / Math.Max(1, audio.Frequency);
                i += frame;
            }
            audio.Carry = i < buf.Length ? buf[i..] : [];
        }
    }

    private sealed class Pes(int pid)
    {
        public int Pid { get; } = pid;
        private readonly MemoryStream _data = new();
        public int Length => (int)_data.Length;
        public void Add(ReadOnlySpan<byte> d) => _data.Write(d);
        public byte[] Bytes() => _data.ToArray();
        public void Reset() => _data.SetLength(0);
    }

    private static void ReadFile(string path, Sink sink)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var first = new byte[Math.Min(s.Length, 4096)];
        s.ReadExactly(first);
        s.Position = 0;
        if (IsAdts(first))
        {
            // A bare AAC stream (some HLS sound playlists): the whole file is the sound.
            sink.AudioPid = 0x1FFF;
            sink.MarkRaw();
            var all = File.ReadAllBytes(path);
            var at = 0;
            var pes = new Pes(0x1FFF);
            while (at < all.Length)
            {
                at = SkipId3(all, at);
                var next = at;
                while (next < all.Length && !(next + 2 < all.Length && all[next] == 'I' && all[next + 1] == 'D' && all[next + 2] == '3')) next++;
                if (next == at) break;
                // fake a PES so the same code reads the frames; timing continues from the previous frames
                var body = all.AsSpan(at, next - at).ToArray();
                pes.Reset();
                pes.Add([0, 0, 1, 0xC0, 0, 0, 0x80, 0x00, 0x00]);
                pes.Add(body);
                sink.Finish(pes);
                at = next;
            }
            return;
        }

        sink.ResetPids();
        var buffer = new byte[Packet * 2048];
        var carry = 0;
        while (true)
        {
            var read = s.Read(buffer, carry, buffer.Length - carry);
            if (read <= 0) break;
            var total = carry + read;
            var p = 0;
            while (p + Packet <= total)
            {
                if (buffer[p] != 0x47) { p++; continue; }
                HandlePacket(buffer, p, sink);
                p += Packet;
            }
            carry = total - p;
            Buffer.BlockCopy(buffer, p, buffer, 0, carry);
        }
        // pieces of an HLS stream are separate TS files: close the open PES so the next file starts clean
        sink.Flush();
    }

    private static void HandlePacket(byte[] d, int p, Sink sink)
    {
        if ((d[p + 1] & 0x80) != 0) return; // transport error
        var pid = (d[p + 1] & 0x1F) << 8 | d[p + 2];
        var pusi = (d[p + 1] & 0x40) != 0;
        var at = PayloadStart(d, p);
        if (at < 0) return;
        var end = p + Packet;

        if (pid == 0 && pusi) { ReadPat(d, at, end, sink); return; }
        if (pid == sink.PmtPid && pusi) { ReadPmt(d, at, end, sink); return; }
        if (pid != sink.VideoPid && pid != sink.AudioPid) return;

        if (!sink.Open.TryGetValue(pid, out var pes)) sink.Open[pid] = pes = new Pes(pid);
        if (pusi) sink.Finish(pes);
        pes.Add(d.AsSpan(at, end - at));
    }

    private static void ReadPat(byte[] d, int at, int end, Sink sink)
    {
        var section = at + 1 + d[at];
        if (section + 12 > end || d[section] != 0) return;
        var stop = Math.Min(end, section + 3 + (((d[section + 1] & 0x0F) << 8) | d[section + 2]) - 4);
        for (var i = section + 8; i + 4 <= stop; i += 4)
            if ((d[i] << 8 | d[i + 1]) != 0) { sink.PmtPid = (d[i + 2] & 0x1F) << 8 | d[i + 3]; return; }
    }

    private static void ReadPmt(byte[] d, int at, int end, Sink sink)
    {
        var section = at + 1 + d[at];
        if (section + 12 > end || d[section] != 2) return;
        var length = ((d[section + 1] & 0x0F) << 8) | d[section + 2];
        var info = ((d[section + 10] & 0x0F) << 8) | d[section + 11];
        var stop = Math.Min(end, section + 3 + length - 4);
        for (var i = section + 12 + info; i + 5 <= stop;)
        {
            var type = d[i];
            var pid = ((d[i + 1] & 0x1F) << 8) | d[i + 2];
            if (type == 0x1B && sink.VideoPid < 0) sink.VideoPid = pid;
            else if (type == 0x0F && sink.AudioPid < 0) sink.AudioPid = pid;
            i += 5 + (((d[i + 3] & 0x0F) << 8) | d[i + 4]);
        }
    }

    // ---- H.264 helpers ----

    private static class Sps
    {
        public static (int Width, int Height) Size(ReadOnlySpan<byte> nal)
        {
            try
            {
                // remove emulation prevention bytes
                var rbsp = new List<byte>(nal.Length);
                for (var i = 1; i < nal.Length; i++)
                {
                    if (i >= 3 && nal[i] == 3 && nal[i - 1] == 0 && nal[i - 2] == 0) continue;
                    rbsp.Add(nal[i]);
                }
                var r = new Bits([.. rbsp]);
                var profile = r.Get(8); r.Get(8); r.Get(8);
                r.Ue();
                var chroma = 1;
                if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
                {
                    chroma = r.Ue();
                    if (chroma == 3) r.Get(1);
                    r.Ue(); r.Ue(); r.Get(1);
                    if (r.Get(1) == 1)
                        for (var i = 0; i < (chroma != 3 ? 8 : 12); i++)
                            if (r.Get(1) == 1)
                            {
                                int last = 8, next = 8;
                                for (var j = 0; j < (i < 6 ? 16 : 64); j++)
                                {
                                    if (next != 0) next = (last + r.Se() + 256) % 256;
                                    last = next == 0 ? last : next;
                                }
                            }
                }
                r.Ue();
                var poc = r.Ue();
                if (poc == 0) r.Ue();
                else if (poc == 1)
                {
                    r.Get(1); r.Se(); r.Se();
                    var n = r.Ue();
                    for (var i = 0; i < n; i++) r.Se();
                }
                r.Ue(); r.Get(1);
                var widthMbs = r.Ue() + 1;
                var heightMaps = r.Ue() + 1;
                var frameMbsOnly = r.Get(1);
                if (frameMbsOnly == 0) r.Get(1);
                r.Get(1);
                int left = 0, right = 0, top = 0, bottom = 0;
                if (r.Get(1) == 1) { left = r.Ue(); right = r.Ue(); top = r.Ue(); bottom = r.Ue(); }
                var cropX = chroma is 1 or 2 ? 2 : 1;
                var cropY = (chroma == 1 ? 2 : 1) * (2 - frameMbsOnly);
                if (chroma == 0) { cropX = 1; cropY = 2 - frameMbsOnly; }
                return (widthMbs * 16 - (left + right) * cropX, heightMaps * 16 * (2 - frameMbsOnly) - (top + bottom) * cropY);
            }
            catch { return (1280, 720); }
        }
    }

    private sealed class Bits(byte[] d)
    {
        private int _pos;
        public int Get(int n)
        {
            var v = 0;
            for (var i = 0; i < n; i++)
            {
                if (_pos >> 3 >= d.Length) throw new EndOfStreamException();
                v = (v << 1) | ((d[_pos >> 3] >> (7 - (_pos & 7))) & 1);
                _pos++;
            }
            return v;
        }
        public int Ue()
        {
            var zeros = 0;
            while (Get(1) == 0) { if (++zeros > 31) throw new InvalidDataException(); }
            return zeros == 0 ? 0 : (int)((1L << zeros) - 1 + Get(zeros));
        }
        public int Se() { var k = Ue(); return (k & 1) == 1 ? (k + 1) / 2 : -(k / 2); }
    }

    // ---- MP4 boxes ----

    private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);
    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] U32(int v) => U32((uint)v);
    private static byte[] U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); return b; }
    private static byte[] U64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); return b; }

    private static byte[] Box(string type, Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        body(w);
        w.Flush();
        var content = ms.ToArray();
        return [.. U32(content.Length + 8), .. Ascii(type), .. content];
    }

    private static byte[] Box(string type, params byte[][] children) => Box(type, w => { foreach (var c in children) w.Write(c); });

    private static byte[] Full(string type, int version, int flags, Action<BinaryWriter> body) =>
        Box(type, w => { w.Write((byte)version); w.Write((byte)(flags >> 16)); w.Write((byte)(flags >> 8)); w.Write((byte)flags); body(w); });

    private static byte[] Moov(params byte[][] parts) => Box("moov", parts);

    private static readonly byte[] Matrix = [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x40, 0, 0, 0];

    private static byte[] BuildMoov(VideoTrack v, AudioTrack a)
    {
        // Zero of the movie clock = the earliest moment of either track.
        long VideoStart() => v.Samples.Count == 0 ? long.MaxValue : v.Samples.Min(s => s.Pts);
        long AudioStart() => a.Samples.Count == 0 ? long.MaxValue : a.Raw && v.Samples.Count > 0 ? VideoStart() : a.StartPts;
        var zero = Math.Min(VideoStart(), AudioStart());

        var traks = new List<byte[]>();
        double movieSeconds = 0;
        var id = 1;
        if (v.Samples.Count > 0)
        {
            var (trak, seconds) = VideoTrak(v, id++, zero);
            traks.Add(trak);
            movieSeconds = Math.Max(movieSeconds, seconds);
        }
        if (a.Samples.Count > 0)
        {
            var (trak, seconds) = AudioTrak(a, v, id++, zero);
            traks.Add(trak);
            movieSeconds = Math.Max(movieSeconds, seconds);
        }
        var mvhd = Full("mvhd", 0, 0, w =>
        {
            w.Write(U32(0)); w.Write(U32(0)); w.Write(U32(1000)); w.Write(U32((uint)(movieSeconds * 1000)));
            w.Write(U32(0x00010000)); w.Write(U16(0x0100)); w.Write(new byte[10]); w.Write(Matrix); w.Write(new byte[24]); w.Write(U32(id));
        });
        return Moov([mvhd, .. traks]);
    }

    private static byte[] Tkhd(int id, double seconds, int w, int h, bool audio) => Full("tkhd", 0, 7, x =>
    {
        x.Write(U32(0)); x.Write(U32(0)); x.Write(U32(id)); x.Write(U32(0)); x.Write(U32((uint)(seconds * 1000)));
        x.Write(new byte[8]); x.Write(U16(0)); x.Write(U16(0)); x.Write(U16(audio ? 0x0100 : 0)); x.Write(U16(0));
        x.Write(Matrix); x.Write(U32(w << 16)); x.Write(U32(h << 16));
    });

    private static byte[] Mdhd(uint scale, long duration) => Full("mdhd", 0, 0, w =>
    {
        w.Write(U32(0)); w.Write(U32(0)); w.Write(U32(scale)); w.Write(U32((uint)duration)); w.Write(U16(0x55C4)); w.Write(U16(0));
    });

    private static byte[] Hdlr(string kind, string name) => Full("hdlr", 0, 0, w =>
    {
        w.Write(U32(0)); w.Write(Ascii(kind)); w.Write(new byte[12]); w.Write(Ascii(name)); w.Write((byte)0);
    });

    private static byte[] Dinf() => Box("dinf", Full("dref", 0, 0, w => { w.Write(U32(1)); w.Write(Full("url ", 0, 1, _ => { })); }));

    /// <summary>Edit list: an empty stretch for a late start, then the media from the point its first picture shows.</summary>
    private static byte[] Edts(double emptySeconds, long mediaTime, double playSeconds, uint scale)
    {
        var entries = new List<(uint Duration, int MediaTime)>();
        if (emptySeconds > 0.001) entries.Add(((uint)(emptySeconds * 1000), -1));
        entries.Add(((uint)(playSeconds * 1000), (int)mediaTime));
        return Box("edts", Full("elst", 0, 0, w =>
        {
            w.Write(U32(entries.Count));
            foreach (var (d, m) in entries) { w.Write(U32(d)); w.Write(U32(m)); w.Write(U16(1)); w.Write(U16(0)); }
        }));
    }

    private static (byte[] Stsc, byte[] Stco) ChunkTables(List<Sample> samples)
    {
        var chunks = samples.GroupBy(s => s.Chunk).Select(g => (First: g.First().Offset, Count: g.Count())).ToList();
        var stsc = new List<(int FirstChunk, int PerChunk)>();
        for (var i = 0; i < chunks.Count; i++)
            if (stsc.Count == 0 || stsc[^1].PerChunk != chunks[i].Count) stsc.Add((i + 1, chunks[i].Count));
        var big = chunks.Any(c => c.First > uint.MaxValue);
        return (Full("stsc", 0, 0, w => { w.Write(U32(stsc.Count)); foreach (var (f, n) in stsc) { w.Write(U32(f)); w.Write(U32(n)); w.Write(U32(1)); } }),
            Full(big ? "co64" : "stco", 0, 0, w => { w.Write(U32(chunks.Count)); foreach (var c in chunks) w.Write(big ? U64((ulong)c.First) : U32((uint)c.First)); }));
    }

    private static byte[] Stsz(List<Sample> samples) => Full("stsz", 0, 0, w =>
    {
        w.Write(U32(0)); w.Write(U32(samples.Count));
        foreach (var s in samples) w.Write(U32(s.Size));
    });

    private static (byte[] Trak, double Seconds) VideoTrak(VideoTrack v, int id, long zero)
    {
        var samples = v.Samples;
        // durations from the gaps between decode times; a jump (ad break, restart) is replaced by the usual frame length
        var durations = new long[samples.Count];
        long typical = 3003;
        for (var i = 0; i + 1 < samples.Count; i++)
        {
            var d = samples[i + 1].Dts - samples[i].Dts;
            durations[i] = d is > 0 and < 90000 * 10 ? d : typical;
            if (d is > 0 and < 90000) typical = d;
        }
        if (durations.Length > 0) durations[^1] = typical;
        long dtsAt = 0;
        var times = new long[samples.Count];
        for (var i = 0; i < samples.Count; i++) { times[i] = dtsAt; dtsAt += durations[i]; }
        var total = dtsAt;

        var (stscBox, stcoBox) = ChunkTables(samples);
        var stts = RunLength(durations);
        var offsets = samples.Select(s => Math.Max(0, s.Pts - s.Dts)).ToArray();
        var hasOffsets = offsets.Any(o => o != 0);
        var minPresentation = samples.Select((s, i) => times[i] + offsets[i]).Min(); // media time of the first picture shown

        var avcc = Box("avcC", w =>
        {
            w.Write((byte)1); w.Write(v.Sps![1]); w.Write(v.Sps[2]); w.Write(v.Sps[3]); w.Write((byte)0xFF);
            w.Write((byte)0xE1); w.Write(U16(v.Sps.Length)); w.Write(v.Sps);
            w.Write((byte)1); w.Write(U16(v.Pps!.Length)); w.Write(v.Pps);
        });
        var avc1 = Box("avc1", w =>
        {
            w.Write(new byte[6]); w.Write(U16(1)); w.Write(new byte[16]); w.Write(U16(v.Width)); w.Write(U16(v.Height));
            w.Write(U32(0x00480000)); w.Write(U32(0x00480000)); w.Write(U32(0)); w.Write(U16(1)); w.Write(new byte[32]); w.Write(U16(0x18)); w.Write(U16(0xFFFF));
            w.Write(avcc);
        });
        var parts = new List<byte[]>
        {
            Full("stsd", 0, 0, w => { w.Write(U32(1)); w.Write(avc1); }),
            Full("stts", 0, 0, w => { w.Write(U32(stts.Count)); foreach (var (c, d) in stts) { w.Write(U32((uint)c)); w.Write(U32((uint)d)); } }),
        };
        if (hasOffsets)
        {
            var runs = RunLength(offsets);
            parts.Add(Full("ctts", 0, 0, w => { w.Write(U32(runs.Count)); foreach (var (c, d) in runs) { w.Write(U32((uint)c)); w.Write(U32((uint)d)); } }));
        }
        var keys = samples.Select((s, i) => (s, i)).Where(x => x.s.Key).Select(x => x.i + 1).ToList();
        parts.Add(Full("stss", 0, 0, w => { w.Write(U32(keys.Count)); foreach (var k in keys) w.Write(U32(k)); }));
        parts.Add(stscBox); parts.Add(Stsz(samples)); parts.Add(stcoBox);

        var seconds = total / 90000.0;
        var startOffset = (samples.Min(s => s.Pts) - zero) / 90000.0;
        var presentationEnd = samples.Select((x, i) => times[i] + offsets[i] + durations[i]).Max(); // the last picture stays up for its own length
        var playSeconds = Math.Max(0, (presentationEnd - minPresentation) / 90000.0);
        var minf = Box("minf", Full("vmhd", 0, 1, w => w.Write(new byte[8])), Dinf(), Box("stbl", [.. parts]));
        var trak = Box("trak", Tkhd(id, startOffset + playSeconds, v.Width, v.Height, false), Edts(startOffset, minPresentation, playSeconds, 90000),
            Box("mdia", Mdhd(90000, total), Hdlr("vide", "VideoHandler"), minf));
        return (trak, startOffset + playSeconds);
    }

    private static (byte[] Trak, double Seconds) AudioTrak(AudioTrack a, VideoTrack video, int id, long zero)
    {
        var samples = a.Samples;
        var scale = (uint)a.Frequency;
        var total = samples.Count * 1024L;
        var (stscBox, stcoBox) = ChunkTables(samples);
        var asc = new byte[] { (byte)((a.Profile << 3) | (a.FrequencyIndex >> 1)), (byte)(((a.FrequencyIndex & 1) << 7) | (a.Channels << 3)) };
        var esds = Full("esds", 0, 0, w =>
        {
            w.Write(new byte[] { 0x03, (byte)(23 + asc.Length), 0, 0, 0 });
            w.Write(new byte[] { 0x04, (byte)(15 + asc.Length), 0x40, 0x15, 0, 0, 0 });
            w.Write(U32(0)); w.Write(U32(0));
            w.Write(new byte[] { 0x05, (byte)asc.Length }); w.Write(asc);
            w.Write(new byte[] { 0x06, 0x01, 0x02 });
        });
        var mp4a = Box("mp4a", w =>
        {
            w.Write(new byte[6]); w.Write(U16(1)); w.Write(new byte[8]); w.Write(U16(Math.Max(1, a.Channels))); w.Write(U16(16)); w.Write(U32(0));
            w.Write(U32((uint)a.Frequency << 16)); w.Write(esds);
        });
        var parts = new List<byte[]>
        {
            Full("stsd", 0, 0, w => { w.Write(U32(1)); w.Write(mp4a); }),
            Full("stts", 0, 0, w => { w.Write(U32(1)); w.Write(U32(samples.Count)); w.Write(U32(1024)); }),
            stscBox, Stsz(samples), stcoBox,
        };
        var seconds = (double)total / scale;
        var startOffset = ((a.Raw && video.Samples.Count > 0 ? video.Samples.Min(x => x.Pts) : a.StartPts) - zero) / 90000.0;
        var minf = Box("minf", Full("smhd", 0, 0, w => w.Write(new byte[4])), Dinf(), Box("stbl", [.. parts]));
        var trak = Box("trak", Tkhd(id, startOffset + seconds, 0, 0, true), Edts(startOffset, 0, seconds, scale),
            Box("mdia", Mdhd(scale, total), Hdlr("soun", "SoundHandler"), minf));
        return (trak, startOffset + seconds);
    }

    private static List<(long Count, long Value)> RunLength(long[] values)
    {
        var list = new List<(long, long)>();
        foreach (var v in values)
        {
            if (list.Count > 0 && list[^1].Item2 == v) list[^1] = (list[^1].Item1 + 1, v);
            else list.Add((1, v));
        }
        return list;
    }
}
