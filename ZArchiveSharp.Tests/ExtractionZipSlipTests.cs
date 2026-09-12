using ZArchiveSharp.Pipeline;

namespace ZArchiveSharp.Tests;

/// <summary>
/// Regression tests for extraction path traversal (zip-slip): entry names
/// are used verbatim as filesystem paths, so a crafted archive carrying
/// <c>..</c>, rooted, drive-qualified or Windows device names must be
/// rejected before anything is created outside the destination root.
/// </summary>
public sealed class ExtractionZipSlipTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // ignored: best-effort temp cleanup.
            }
        }
    }

    private string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "zarsharp", prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static byte[] BuildArchive(Action<ZArchiveWriter> build)
    {
        using var ms = new MemoryStream();
        using (var writer = new ZArchiveWriter(ms))
        {
            build(writer);
            writer.Finalize();
        }

        return ms.ToArray();
    }

    private static string WriteArchive(string root, byte[] bytes)
    {
        var path = Path.Combine(root, "crafted.zar");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void TraversalEntry_IsRejected_AndNothingEscapesTheRoot()
    {
        // ".." is creatable through the writer's public API, so the fixture
        // exercises the real archive format rather than a byte patch.
        var zar = BuildArchive(w =>
        {
            Assert.True(w.MakeDir(".."));
            Assert.True(w.StartNewFile("../evil.txt"));
            w.AppendData("pwned"u8);
        });

        var root = NewTempDir("zipslip");
        var zarPath = WriteArchive(root, zar);
        var dest = Path.Combine(root, "dest");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ZarPackEngine.ExtractEntries(zarPath, dest));
        Assert.Contains("not safe to extract", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "evil.txt")),
            "The payload escaped the extraction root (zip-slip).");
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "evil.txt")),
            "The payload escaped the extraction root (zip-slip).");
        Assert.False(Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any(),
            "The rejected archive left entries behind.");
    }

    [Theory]
    [InlineData("..")]
    [InlineData("C:evil.txt")]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    [InlineData("lpt1")]
    public void UnsafeEntryNames_AreRejected(string name)
    {
        var zar = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile(name));
            w.AppendData("x"u8);
        });

        var root = NewTempDir("zipname");
        var zarPath = WriteArchive(root, zar);
        var dest = Path.Combine(root, "dest");
        Assert.Throws<InvalidOperationException>(() => ZarPackEngine.ExtractEntries(zarPath, dest));
    }

    [Fact]
    public void SafeEntryNames_StillExtract()
    {
        var zar = BuildArchive(w =>
        {
            Assert.True(w.MakeDir("sub"));
            Assert.True(w.StartNewFile("sub/hello.txt"));
            w.AppendData("hi"u8);
        });

        var root = NewTempDir("zipok");
        var zarPath = WriteArchive(root, zar);
        var dest = Path.Combine(root, "dest");
        ZarPackEngine.ExtractEntries(zarPath, dest);
        Assert.Equal("hi"u8.ToArray(), File.ReadAllBytes(Path.Combine(dest, "sub", "hello.txt")));
    }
}