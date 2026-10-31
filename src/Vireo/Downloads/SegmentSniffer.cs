namespace Vireo.Downloads;

/// <summary>
/// Some sites put a few bytes of a picture (a tiny PNG, GIF or JPEG) in front of every piece of a stream, so that the pieces
/// look like images to anything that does not play them. A player ignores the picture; so must a saved file, or the
/// result is not a video. This finds where the real data starts.
/// </summary>
public static class SegmentSniffer
{
    private const int TsPacket = 188;
    private const int MaxScan = 512 * 1024;

    /// <summary>The piece without any picture in front of it, or the same bytes when there is none.</summary>
    public static byte[] Unwrap(byte[] data)
    {
        if (data.Length < 16) return data;
        if (data.Length > TsPacket && data[0] == 0x47 && data[TsPacket] == 0x47) return data; // already a transport stream ("GIF8" also starts with 0x47)
        if (!LooksLikePicture(data)) return data;
        var start = FindTransportStream(data) ?? FindMp4(data);
        return start is > 0 ? data[start.Value..] : data;
    }

    private static bool LooksLikePicture(byte[] d) =>
        d is [0x89, 0x50, 0x4E, 0x47, ..]               // PNG
        || d is [0x47, 0x49, 0x46, 0x38, ..]            // GIF8
        || d is [0xFF, 0xD8, 0xFF, ..]                  // JPEG
        || d is [0x42, 0x4D, ..]                        // BM
        || d is [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..]; // WEBP

    /// <summary>Where a run of 47-packets begins (three in a row, 188 bytes apart: one stray 0x47 in picture data does not count).</summary>
    private static int? FindTransportStream(byte[] d)
    {
        var last = Math.Min(d.Length - 2 * TsPacket - 1, MaxScan);
        for (var i = 0; i <= last; i++)
            if (d[i] == 0x47 && d[i + TsPacket] == 0x47 && d[i + 2 * TsPacket] == 0x47) return i;
        return null;
    }

    /// <summary>Where an MP4 piece begins: the size of its first box, then a box name that fragments start with.</summary>
    private static int? FindMp4(byte[] d)
    {
        var last = Math.Min(d.Length - 12, MaxScan);
        for (var i = 0; i <= last; i++)
        {
            var name = (d[i + 4], d[i + 5], d[i + 6], d[i + 7]);
            var known = name is ((byte)'f', (byte)'t', (byte)'y', (byte)'p') or ((byte)'s', (byte)'t', (byte)'y', (byte)'p')
                or ((byte)'m', (byte)'o', (byte)'o', (byte)'f') or ((byte)'s', (byte)'i', (byte)'d', (byte)'x') or ((byte)'m', (byte)'o', (byte)'o', (byte)'v');
            if (!known) continue;
            var size = (long)d[i] << 24 | (long)d[i + 1] << 16 | (long)d[i + 2] << 8 | d[i + 3];
            if (size >= 8 && i + size <= d.Length) return i;
        }
        return null;
    }
}
