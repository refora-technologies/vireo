using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Vireo.Downloads;
using Vireo.Media;
using Xunit;

namespace Vireo.Tests;

public class TsRemuxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vireo-ts-" + Guid.NewGuid().ToString("N")[..8]);

    public TsRemuxTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* still in use */ } }

    private static string? Clip()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "tests", "server", "media", "clip.mp4"))) d = d.Parent;
        return d is null ? null : Path.Combine(d.FullName, "tests", "server", "media", "clip.mp4");
    }

    private static string Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return exe.Contains("ffprobe") ? output : output + err.Result;
    }

    private JsonElement Probe(string path) =>
        JsonDocument.Parse(Run("ffprobe", $"-v error -count_frames -show_streams -show_format -of json \"{path}\"")).RootElement.Clone();

    private static double Num(JsonElement e, string name) => double.Parse(e.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);

    private string Make(string name, string args)
    {
        var path = Path.Combine(_dir, name);
        Run("ffmpeg", $"-v error -y -i \"{Clip()}\" {args} \"{path}\"");
        Assert.True(File.Exists(path), "ffmpeg could not make the test file");
        return path;
    }

    private static bool Ready => FfmpegTools.Available && Clip() is not null;

    [Fact]
    public void A_transport_stream_with_picture_and_sound_becomes_a_playable_mp4()
    {
        if (!Ready) return;
        var ts = Make("a.ts", "-c copy -bsf:v h264_mp4toannexb -f mpegts");
        Assert.True(TsRemuxer.CanRemux(ts));
        var mp4 = Path.Combine(_dir, "a.mp4");
        TsRemuxer.Remux([ts], mp4);

        var info = Probe(mp4);
        var streams = info.GetProperty("streams").EnumerateArray().ToList();
        var v = streams.Single(s => s.GetProperty("codec_type").GetString() == "video");
        var a = streams.Single(s => s.GetProperty("codec_type").GetString() == "audio");
        Assert.Equal("h264", v.GetProperty("codec_name").GetString());
        Assert.Equal((1280, 720), (v.GetProperty("width").GetInt32(), v.GetProperty("height").GetInt32()));
        Assert.Equal("aac", a.GetProperty("codec_name").GetString());
        Assert.InRange(Num(info.GetProperty("format"), "duration"), 7.8, 8.3);
        Assert.Equal(240, int.Parse(v.GetProperty("nb_read_frames").GetString()!)); // every picture is there
        Assert.Equal("", Run("ffmpeg", $"-v error -i \"{mp4}\" -f null -").Trim()); // and everything decodes
        Assert.InRange(Math.Abs(Num(v, "start_time") - Num(a, "start_time")), 0, 0.1);
    }

    [Fact]
    public void Pictures_with_reordering_keep_their_display_order()
    {
        if (!Ready) return;
        // B-frames: the pictures are stored in a different order than they are shown
        var ts = Make("b.ts", "-c:v libx264 -bf 3 -g 30 -c:a aac -f mpegts");
        var mp4 = Path.Combine(_dir, "b.mp4");
        TsRemuxer.Remux([ts], mp4);
        var packets = Run("ffprobe", $"-v error -select_streams v -show_entries packet=pts_time,dts_time -of csv=p=0 \"{mp4}\"")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Split(',')).Where(p => p.Length == 2 && p[0] != "N/A").ToList();
        Assert.True(packets.Count > 200);
        Assert.Equal("", Run("ffmpeg", $"-v error -i \"{mp4}\" -f null -").Trim());
        var sorted = packets.Select(p => double.Parse(p[0], CultureInfo.InvariantCulture)).Order().ToList();
        Assert.True(sorted.Zip(sorted.Skip(1), (x, y) => y - x).All(d => d is > 0.02 and < 0.05), "the display times should step evenly");
    }

    [Fact]
    public void Separate_picture_and_sound_files_are_joined_even_when_the_sound_is_a_bare_aac_stream()
    {
        if (!Ready) return;
        var video = Make("v.ts", "-an -c copy -bsf:v h264_mp4toannexb -f mpegts");
        var audio = Make("a.aac", "-vn -c copy -f adts -write_id3v2 1");
        Assert.True(TsRemuxer.CanRemux(video, audio));
        var mp4 = Path.Combine(_dir, "joined.mp4");
        TsRemuxer.Remux([video, audio], mp4);
        var info = Probe(mp4);
        Assert.Equal(["video", "audio"], info.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()));
        Assert.InRange(Num(info.GetProperty("format"), "duration"), 7.8, 8.3);
        Assert.Equal("", Run("ffmpeg", $"-v error -i \"{mp4}\" -f null -").Trim());
    }

    [Fact]
    public void Picture_and_sound_in_two_transport_streams_that_reuse_the_same_pid_are_not_mixed_up()
    {
        if (!Ready) return;
        var video = Make("v2.ts", "-an -c copy -bsf:v h264_mp4toannexb -f mpegts");
        var audio = Make("a2.ts", "-vn -c copy -f mpegts");
        var mp4 = Path.Combine(_dir, "joined2.mp4");
        TsRemuxer.Remux([video, audio], mp4);
        var info = Probe(mp4);
        Assert.Equal(["video", "audio"], info.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()));
        Assert.Equal("", Run("ffmpeg", $"-v error -i \"{mp4}\" -f null -").Trim());
    }

    [Fact]
    public void Pieces_of_a_stream_laid_end_to_end_come_out_as_one_continuous_file()
    {
        if (!Ready) return;
        var playlist = Make("p.m3u8", $"-c:v libx264 -g 30 -c:a aac -f hls -hls_time 2 -hls_playlist_type vod -hls_segment_filename \"{_dir}/seg%03d.ts\"");
        var parts = Directory.GetFiles(_dir, "seg*.ts").OrderBy(x => x).ToList();
        Assert.True(parts.Count >= 3);
        var whole = Path.Combine(_dir, "whole.ts");
        using (var o = File.Create(whole)) foreach (var p in parts) o.Write(File.ReadAllBytes(p));
        var mp4 = Path.Combine(_dir, "whole.mp4");
        TsRemuxer.Remux([whole], mp4);
        var info = Probe(mp4);
        Assert.InRange(Num(info.GetProperty("format"), "duration"), 7.7, 8.4);
        Assert.Equal("", Run("ffmpeg", $"-v error -i \"{mp4}\" -f null -").Trim());
        Assert.Equal(240, int.Parse(info.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "video").GetProperty("nb_read_frames").GetString()!));
    }

    // ---- A transport stream whose PES packets do not line up with the pictures (Apple's own test streams are like this) ----

    private static byte[] Stamp(long v, int marker) =>
    [
        (byte)(marker << 4 | (int)((v >> 29) & 0x0E) | 1), (byte)(v >> 22), (byte)((v >> 14) & 0xFE | 1), (byte)(v >> 7), (byte)((v << 1) & 0xFE | 1),
    ];

    private static void Packets(Stream o, int pid, byte[] data, ref int cc)
    {
        for (var at = 0; at < data.Length; at += 184)
        {
            var n = Math.Min(184, data.Length - at);
            var packet = new byte[188];
            packet[0] = 0x47;
            packet[1] = (byte)((at == 0 ? 0x40 : 0) | (pid >> 8));
            packet[2] = (byte)pid;
            if (n == 184) { packet[3] = (byte)(0x10 | (cc++ & 15)); Buffer.BlockCopy(data, at, packet, 4, n); }
            else
            {
                // pad with an adaptation field so the packet is full
                var afLength = 183 - n;
                packet[3] = (byte)(0x30 | (cc++ & 15));
                packet[4] = (byte)afLength;
                if (afLength > 0) { packet[5] = 0; for (var i = 6; i < 5 + afLength; i++) packet[i] = 0xFF; }
                Buffer.BlockCopy(data, at, packet, 5 + afLength, n);
            }
            o.Write(packet);
        }
    }

    private static byte[] Section(int pid, byte[] body)
    {
        var data = new byte[1 + body.Length + 4];
        Buffer.BlockCopy(body, 0, data, 1, body.Length); // pointer field 0, body, a CRC we leave empty (the reader does not check it)
        return data;
    }

    /// <summary>Video made into a transport stream where each PES packet carries the end of the previous picture and the start of the next.</summary>
    private static byte[] Misaligned(IReadOnlyList<byte[]> pictures, bool misalign)
    {
        using var o = new MemoryStream();
        int ccPat = 0, ccPmt = 0, ccVideo = 0;
        Packets(o, 0, Section(0, [0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, 0xF0, 0x00]), ref ccPat);
        Packets(o, 0x1000, Section(0x1000, [0x02, 0xB0, 0x12, 0x00, 0x01, 0xC1, 0x00, 0x00, 0xE1, 0x00, 0xF0, 0x00, 0x1B, 0xE1, 0x00, 0xF0, 0x00]), ref ccPmt);
        byte[] tail = [];
        for (var i = 0; i < pictures.Count; i++)
        {
            var pts = 90000L + i * 3000L;
            var picture = pictures[i];
            var cut = misalign ? picture.Length * 6 / 10 : picture.Length;
            byte[] payload = [.. tail, .. picture.AsSpan(0, cut).ToArray()];
            tail = picture[cut..];
            byte[] pes = [0, 0, 1, 0xE0, 0, 0, 0x80, 0xC0, 10, .. Stamp(pts, 3), .. Stamp(pts, 1), .. payload];
            Packets(o, 0x100, pes, ref ccVideo);
        }
        if (tail.Length > 0) Packets(o, 0x100, [0, 0, 1, 0xE0, 0, 0, 0x80, 0x00, 0, .. tail], ref ccVideo);
        return o.ToArray();
    }

    [Fact]
    public void A_picture_split_over_two_packets_is_put_back_together()
    {
        if (!Ready) return;
        var h264 = Make("raw.h264", "-an -c:v libx264 -bf 0 -g 15 -t 3 -bsf:v h264_mp4toannexb -f h264");
        var sizes = Run("ffprobe", $"-v error -show_entries packet=size -of csv=p=0 \"{h264}\"").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => int.Parse(l.Trim().TrimEnd(','))).ToList();
        var all = File.ReadAllBytes(h264);
        var pictures = new List<byte[]>();
        var at = 0;
        foreach (var size in sizes) { pictures.Add(all[at..(at + size)]); at += size; }
        Assert.True(pictures.Count >= 60);

        foreach (var misalign in new[] { false, true })
        {
            var ts = Path.Combine(_dir, misalign ? "split.ts" : "whole.ts");
            File.WriteAllBytes(ts, Misaligned(pictures, misalign));
            var mp4 = Path.Combine(_dir, misalign ? "split.mp4" : "whole2.mp4");
            TsRemuxer.Remux([ts], mp4);
            var info = Probe(mp4);
            var v = info.GetProperty("streams").EnumerateArray().Single();
            Assert.Equal(pictures.Count, int.Parse(v.GetProperty("nb_read_frames").GetString()!)); // no picture lost or doubled
            Assert.Equal("", Run("ffmpeg", $"-v error -i \"{mp4}\" -f null -").Trim());        // and every picture whole
        }
    }

    [Fact]
    public void Things_that_are_not_h264_and_aac_are_left_alone()
    {
        var junk = Path.Combine(_dir, "junk.ts");
        File.WriteAllBytes(junk, new Random(7).NextBytes(188 * 400));
        Assert.False(TsRemuxer.CanRemux(junk));
        Assert.False(TsRemuxer.CanRemux(Path.Combine(_dir, "missing.ts")));
        if (!Ready) return;
        var mpeg2 = Make("m2.ts", "-c:v mpeg2video -c:a mp2 -f mpegts");
        Assert.False(TsRemuxer.CanRemux(mpeg2));
    }

    [Fact]
    public async Task A_real_transport_stream_download_ends_as_an_mp4_made_by_vireo_itself()
    {
        if (!Ready) return;
        {
            var dir = Path.Combine(_dir, "site");
            Directory.CreateDirectory(dir);
            Run("ffmpeg", $"-v error -y -i \"{Clip()}\" -c:v libx264 -g 30 -c:a aac -f hls -hls_time 2 -hls_playlist_type vod -hls_segment_filename \"{dir}/s%02d.ts\" \"{dir}/index.m3u8\"");
            using var server = new FolderServer(dir);
            using var box = new Sandbox();
            using var engine = box.Engine(connections: 4);
            var item = engine.Add(new DownloadRequest(server.BaseUrl + "/index.m3u8", "show.mp4", Stream: new StreamSpec(server.BaseUrl + "/index.m3u8", null, false, 1_000_000)));
            await engine.WhenIdleAsync(TimeSpan.FromSeconds(40));

            Assert.Equal(DownloadState.Completed, item.State);
            Assert.EndsWith(".mp4", item.FileName);
            Assert.Null(item.Note);
            Assert.Equal(["video", "audio"], FfmpegTools.StreamKinds(item.FinalPath));
            Assert.Equal("", FfmpegTools.DecodeErrors(item.FinalPath));
            Assert.InRange(FfmpegTools.Duration(item.FinalPath), 7.7, 8.4);
            Assert.Empty(Directory.GetFiles(box.Files, "*.vireopart"));
        }
    }
}
