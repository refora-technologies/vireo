using System.Buffers.Binary;
using System.IO;

namespace Vireo.Media;

/// <summary>
/// Joins a video file and an audio file, both fragmented MP4 (what YouTube and most HLS/DASH streams deliver),
/// into one file with both tracks. No re-encoding: the fragments are copied as they are, interleaved by time,
/// with the audio track renumbered so the two do not clash.
/// </summary>
public static class Fmp4Merger
{
    private sealed record Box(string Type, long Start, long Size, int HeaderSize)
    {
        public long End => Start + Size;
        public long PayloadStart => Start + HeaderSize;
    }

    private sealed class Fragment
    {
        public required FileStream Source;
        public long Start, Length;
        public double Time;
        public bool IsAudio;
        public int SequencePos = -1;     // where mfhd's sequence number sits, relative to Start
        public List<int> TrackIdPos = []; // where each tfhd's track id sits, relative to Start
    }

    private sealed class Track
    {
        public required byte[] Trak;       // the whole trak box
        public required byte[] Trex;       // the matching trex box
        public required byte[] Mvhd;
        public int TrackId;
        public uint Timescale;
    }

    public static bool CanMerge(string videoPath, string audioPath)
    {
        try { return Tracks(videoPath) && Tracks(audioPath); }
        catch { return false; }
    }

    private static bool Tracks(string path)
    {
        using var f = File.OpenRead(path);
        var top = ReadBoxes(f, 0, f.Length).ToList();
        return top.Any(b => b.Type == "moov") && top.Any(b => b.Type == "moof");
    }

    public static void Merge(string videoPath, string audioPath, string outPath)
    {
        using var v = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var a = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

        var vTop = ReadBoxes(v, 0, v.Length).ToList();
        var aTop = ReadBoxes(a, 0, a.Length).ToList();
        var ftyp = vTop.First(b => b.Type == "ftyp");
        var vMoov = vTop.FirstOrDefault(b => b.Type == "moov") ?? throw new InvalidDataException("The video has no header.");
        var aMoov = aTop.FirstOrDefault(b => b.Type == "moov") ?? throw new InvalidDataException("The audio has no header.");

        var vt = ReadTrack(v, vMoov);
        var at = ReadTrack(a, aMoov);
        var newAudioId = Math.Max(vt.TrackId, at.TrackId) + 1;
        PatchTrackId(at, newAudioId);

        var fragments = new List<Fragment>();
        fragments.AddRange(ReadFragments(v, vTop, vt.Timescale, isAudio: false));
        fragments.AddRange(ReadFragments(a, aTop, at.Timescale, isAudio: true));
        if (fragments.Count == 0) throw new InvalidDataException("There is nothing to merge.");
        fragments.Sort((x, y) => x.Time != y.Time ? x.Time.CompareTo(y.Time) : x.IsAudio.CompareTo(y.IsAudio));

        using var o = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        Copy(v, ftyp.Start, ftyp.Size, o);
        o.Write(BuildMoov(vt, at, newAudioId));

        uint sequence = 1;
        foreach (var f in fragments)
        {
            var bytes = new byte[f.Length];
            f.Source.Seek(f.Start, SeekOrigin.Begin);
            f.Source.ReadExactly(bytes);
            if (f.SequencePos >= 0) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(f.SequencePos), sequence++);
            if (f.IsAudio) foreach (var pos in f.TrackIdPos) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(pos), (uint)newAudioId);
            o.Write(bytes);
        }
    }

    // ---- Reading boxes ----

    private static IEnumerable<Box> ReadBoxes(Stream s, long from, long to)
    {
        var head = new byte[16];
        var pos = from;
        while (pos + 8 <= to)
        {
            s.Seek(pos, SeekOrigin.Begin);
            s.ReadExactly(head, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(head);
            var type = System.Text.Encoding.ASCII.GetString(head, 4, 4);
            var header = 8;
            if (size == 1) { s.ReadExactly(head, 8, 8); size = (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(8)); header = 16; }
            else if (size == 0) size = to - pos;
            if (size < header || pos + size > to) yield break; // a cut-off last box is ignored
            yield return new Box(type, pos, size, header);
            pos += size;
        }
    }

    private static byte[] Read(Stream s, Box b)
    {
        var bytes = new byte[b.Size];
        s.Seek(b.Start, SeekOrigin.Begin);
        s.ReadExactly(bytes);
        return bytes;
    }

    private static void Copy(Stream from, long start, long length, Stream to)
    {
        var bytes = new byte[length];
        from.Seek(start, SeekOrigin.Begin);
        from.ReadExactly(bytes);
        to.Write(bytes);
    }

    private static Track ReadTrack(Stream s, Box moov)
    {
        var children = ReadBoxes(s, moov.PayloadStart, moov.End).ToList();
        var mvhd = children.First(b => b.Type == "mvhd");
        var trak = children.First(b => b.Type == "trak");
        var trakBytes = Read(s, trak);

        // trak -> tkhd gives the track id; trak -> mdia -> mdhd gives the clock the track counts in.
        var trakChildren = ReadBoxes(s, trak.PayloadStart, trak.End).ToList();
        var tkhd = trakChildren.First(b => b.Type == "tkhd");
        var tkhdBytes = Read(s, tkhd);
        var trackId = (int)BinaryPrimitives.ReadUInt32BigEndian(tkhdBytes.AsSpan(tkhdBytes[8] == 1 ? 28 : 20));
        var mdia = trakChildren.First(b => b.Type == "mdia");
        var mdhd = ReadBoxes(s, mdia.PayloadStart, mdia.End).First(b => b.Type == "mdhd");
        var mdhdBytes = Read(s, mdhd);
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(mdhdBytes.AsSpan(mdhdBytes[8] == 1 ? 28 : 20));

        var mvex = children.FirstOrDefault(b => b.Type == "mvex");
        byte[]? trex = null;
        if (mvex is not null)
        {
            foreach (var t in ReadBoxes(s, mvex.PayloadStart, mvex.End).Where(b => b.Type == "trex"))
            {
                var bytes = Read(s, t);
                if (BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)) == trackId) trex = bytes;
            }
        }
        trex ??= DefaultTrex(trackId);
        return new Track { Trak = trakBytes, Trex = trex, Mvhd = Read(s, mvhd), TrackId = trackId, Timescale = timescale == 0 ? 1 : timescale };
    }

    private static byte[] DefaultTrex(int trackId)
    {
        var b = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(b, 32);
        "trex"u8.CopyTo(b.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), (uint)trackId);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), 1); // default sample description index
        return b;
    }

    private static void PatchTrackId(Track t, int newId)
    {
        // tkhd sits somewhere inside the trak bytes: find it by its type marker.
        var i = t.Trak.AsSpan(8).IndexOf("tkhd"u8) + 8;
        var tkhd = i - 4; // start of the box
        BinaryPrimitives.WriteUInt32BigEndian(t.Trak.AsSpan(tkhd + (t.Trak[tkhd + 8] == 1 ? 28 : 20)), (uint)newId);
        BinaryPrimitives.WriteUInt32BigEndian(t.Trex.AsSpan(12), (uint)newId);
        t.TrackId = newId;
    }

    private static byte[] MakeBox(string type, params byte[][] children)
    {
        var size = 8 + children.Sum(c => c.Length);
        var b = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)size);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        var o = 8;
        foreach (var c in children) { c.CopyTo(b, o); o += c.Length; }
        return b;
    }

    private static byte[] BuildMoov(Track video, Track audio, int audioId)
    {
        var mvhd = (byte[])video.Mvhd.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(mvhd.Length - 4), (uint)(audioId + 1)); // next free track id
        return MakeBox("moov", mvhd, video.Trak, audio.Trak, MakeBox("mvex", video.Trex, audio.Trex));
    }

    private static List<Fragment> ReadFragments(FileStream s, List<Box> top, uint timescale, bool isAudio)
    {
        var result = new List<Fragment>();
        for (var i = 0; i < top.Count; i++)
        {
            if (top[i].Type != "moof") continue;
            var moof = top[i];
            var mdat = top.Skip(i + 1).FirstOrDefault(b => b.Type == "mdat") ?? throw new InvalidDataException("A fragment is missing its data.");
            var f = new Fragment { Source = s, Start = moof.Start, Length = mdat.End - moof.Start, IsAudio = isAudio };

            foreach (var child in ReadBoxes(s, moof.PayloadStart, moof.End))
            {
                if (child.Type == "mfhd") f.SequencePos = (int)(child.PayloadStart + 4 - moof.Start);
                if (child.Type != "traf") continue;
                foreach (var t in ReadBoxes(s, child.PayloadStart, child.End))
                {
                    if (t.Type == "tfhd")
                    {
                        var bytes = Read(s, t);
                        if ((BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)) & 0x1) != 0) throw new NotSupportedException("This file uses absolute offsets and cannot be merged this way.");
                        f.TrackIdPos.Add((int)(t.PayloadStart + 4 - moof.Start));
                    }
                    else if (t.Type == "tfdt")
                    {
                        var bytes = Read(s, t);
                        var time = bytes[8] == 1 ? (double)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(12)) : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12));
                        f.Time = time / timescale;
                    }
                }
            }
            result.Add(f);
        }
        return result;
    }
}
