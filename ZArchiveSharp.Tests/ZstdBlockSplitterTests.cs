using System.Security.Cryptography;
using ZArchiveSharp.Zstd;

namespace ZArchiveSharp.Tests;

/// <summary>
/// Regression tests for the post-block splitter (levels whose strategy is
/// btopt or deeper with windowLog ≥ 17). A raw/RLE partition is never decoded,
/// so its repcode updates must not advance the simulated decompression
/// repeat-offset history used by later partitions: native
/// <c>ZSTD_compressSeqStore_singleBlock</c> snapshots <c>dRepOriginal</c> at
/// entry, before <c>ZSTD_seqStore_resolveOffCodes</c>. When the snapshot was
/// taken after the resolve, seed 6385 emitted a frame the decoder rejects with
/// "Invalid repeat offset.".
/// </summary>
public sealed class ZstdBlockSplitterTests
{
    [Fact]
    public void RawPartitionAfterRepcodeHistory_RoundTrips()
    {
        var input = MakeSplitterRegressionInput(seed: 6385);

        // Pin the generator: the round-trip only guards the splitter when the
        // input is exactly the vector that reproduced the bug.
        Assert.Equal(
            "1F2C0FCE6C9E9492E7DF149EDF9F23B169CCCD089DBBC6ED6520D6AEBC8763CB",
            Convert.ToHexString(SHA256.HashData(input)));

        var compressor = new ZstdCompressor(ZstdCompressionOptions.FromLevel(16));
        var frame = compressor.CompressBlock(input);
        var decoded = ZstdDecompressor.Decompress(frame);
        Assert.Equal(input, decoded);
    }

    // Deterministic mixed-entropy 128 KiB block: segments of random bytes,
    // zeros, cyclic text, small-alphabet noise and repeated tails. Seed 6385
    // makes the parser split the block and emit a raw partition after
    // repcode-bearing sequences.
    private static byte[] MakeSplitterRegressionInput(int seed)
    {
        const int size = 131072;
        var rng = new Random((seed * 7919) + 13);
        var buf = new byte[size];
        var pos = 0;
        while (pos < size)
        {
            var kind = rng.Next(10);
            var len = Math.Min(size - pos, 2048 + (rng.Next(16) * 2048));
            switch (kind)
            {
                case 0:
                    rng.NextBytes(buf.AsSpan(pos, len));
                    break;
                case 1:
                    Array.Clear(buf, pos, len);
                    break;
                case 2:
                {
                    const string sample = "The quick brown fox jumps over the lazy dog. ZArchive block 64 KiB. ";
                    var ascii = System.Text.Encoding.ASCII.GetBytes(sample);
                    for (var i = 0; i < len; i++)
                    {
                        buf[pos + i] = ascii[(i + seed) % ascii.Length];
                    }

                    break;
                }

                case 3:
                    for (var i = 0; i < len; i++)
                    {
                        buf[pos + i] = (byte)rng.Next(4);
                    }

                    break;
                case 4:
                    if (pos > 0)
                    {
                        var take = Math.Min(len, pos);
                        Array.Copy(buf, pos - take, buf, pos, take);
                        len = take;
                    }
                    else
                    {
                        rng.NextBytes(buf.AsSpan(pos, len));
                    }

                    break;
                default:
                    for (var i = 0; i < len; i++)
                    {
                        buf[pos + i] = rng.Next(100) < 60 ? (byte)rng.Next(6) : (byte)rng.Next(256);
                    }

                    break;
            }

            pos += len;
        }

        return buf;
    }
}
