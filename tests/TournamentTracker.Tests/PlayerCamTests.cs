using System;
using System.IO;
using System.Threading.Tasks;
using TournamentTracker.PlayerCam;
using Xunit;

namespace TournamentTracker.Tests;

public class PlayerCamTests
{
    /// <summary>A test picture: colour bars over a gradient, with a white square near the top left.</summary>
    private static byte[] Picture(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int bar = x * 6 / w;
                px[i] = (byte)(bar is 0 or 3 or 4 ? 230 : y * 255 / h);
                px[i + 1] = (byte)(bar is 1 or 3 or 5 ? 210 : 40);
                px[i + 2] = (byte)(bar is 2 or 4 or 5 ? 220 : x * 255 / w);
                px[i + 3] = 255;
                if (x >= 10 && x < 40 && y >= 10 && y < 40) px[i] = px[i + 1] = px[i + 2] = 255;
            }
        return px;
    }

    [Fact]
    public void Pictures_become_JPEGs_of_the_right_size()
    {
        const int w = 250, h = 141;                            // not a multiple of 16 either way
        var jpeg = new JpegEncoder().Encode(Picture(w, h), w, h, 85);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]);
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpeg[^2..]);
        int sof = IndexOf(jpeg, 0xFF, 0xC0);
        Assert.True(sof > 0);
        Assert.Equal(h, jpeg[sof + 5] << 8 | jpeg[sof + 6]);
        Assert.Equal(w, jpeg[sof + 7] << 8 | jpeg[sof + 8]);
        Assert.True(jpeg.Length < w * h * 3 / 4, "compressed");
        // For a look by eye or with an image tool.
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "tt-playercam-test.jpg"), jpeg);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "tt-playercam-test.rgba"), Picture(w, h));
    }

    [Fact]
    public void Bottom_up_pixels_come_out_the_right_way_up()
    {
        const int w = 32, h = 32;
        var top = Picture(w, h);
        var flipped = new byte[top.Length];
        for (int y = 0; y < h; y++) Array.Copy(top, y * w * 4, flipped, (h - 1 - y) * w * 4, w * 4);
        var enc = new JpegEncoder();
        Assert.Equal(enc.Encode(top, w, h), enc.Encode(flipped, w, h, bottomUp: true));
    }

    [Fact]
    public async Task Frames_are_made_one_at_a_time_and_only_while_someone_asks()
    {
        var now = new DateTime(2026, 1, 1);
        using var feed = new PlayerCamFeed(() => now, inline: true);
        Assert.False(feed.Wanted);
        feed.Asked();
        Assert.True(feed.Wanted);
        var buffer = feed.Take(16 * 16 * 4)!;
        Array.Copy(Picture(16, 16), buffer, buffer.Length);
        feed.Submit(16, 16, bottomUp: true);
        var (seq, jpeg) = await feed.NextAsync(0, TimeSpan.FromMilliseconds(10));
        Assert.Equal(1, seq);
        Assert.NotNull(jpeg);
        // Nothing newer: an empty answer after the wait.
        Assert.Null((await feed.NextAsync(1, TimeSpan.FromMilliseconds(10))).Jpeg);
        var framed = PlayerCamFeed.Frame(seq, jpeg!);
        Assert.Equal(1, BitConverter.ToInt64(framed, 0));
        Assert.Equal(0xFF, framed[8]);
        now = now.AddSeconds(4);
        Assert.False(feed.Wanted);                             // nobody asked lately: the game stops drawing it
    }

    private static int IndexOf(byte[] data, byte a, byte b)
    {
        for (int i = 0; i + 1 < data.Length; i++) if (data[i] == a && data[i + 1] == b) return i;
        return -1;
    }
}
