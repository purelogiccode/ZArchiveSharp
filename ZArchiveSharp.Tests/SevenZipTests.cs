using System.IO.Compression;
using ZArchiveSharp.Pipeline;

namespace ZArchiveSharp.Tests;

/// <summary>
/// Tests for the archive-container stage (<see cref="SevenZip"/>): tool
/// discovery, argument construction, ISO picking, and extraction. The
/// end-to-end extraction tests need a real 7z binary and vacuous-pass
/// (early return) without one — same convention as the native-toolchain
/// parity tests; everything else is deterministic.
/// </summary>
public sealed class SevenZipTests : IDisposable
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
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // ignored
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

    [Fact]
    public void BuildArguments_PlainPaths()
    {
        Assert.Equal(
            "x \"C:\\in\\game.zip\" -o\"C:\\out\" -y -bsp1",
            SevenZip.BuildArguments(@"C:\in\game.zip", @"C:\out"));
    }

    [Fact]
    public void BuildArguments_PathsWithSpaces()
    {
        Assert.Equal(
            "x \"C:\\my games\\disc one.zip\" -o\"C:\\out dir\\temp_game\" -y -bsp1",
            SevenZip.BuildArguments(@"C:\my games\disc one.zip", @"C:\out dir\temp_game"));
    }

    [Fact]
    public void BuildArguments_TrailingSeparator_DoesNotEscapeClosingQuote()
    {
        // "...\temp_game\" would let the backslash escape the closing quote;
        // the trailing separator is dropped (roots keep theirs).
        Assert.Equal(
            "x \"C:\\in\\game.zip\" -o\"C:\\out dir\\temp_game\" -y -bsp1",
            SevenZip.BuildArguments(@"C:\in\game.zip", @"C:\out dir\temp_game\"));
        Assert.Equal(
            "x \"C:\\in\\game.zip\" -o\"C:\\\" -y -bsp1",
            SevenZip.BuildArguments(@"C:\in\game.zip", @"C:\"));
    }

    [Fact]
    public void PickIsoCandidate_NoIso_ReturnsNull()
    {
        Assert.Null(SevenZip.PickIsoCandidate(["a.txt", "sub/b.bin"]));
        Assert.Null(SevenZip.PickIsoCandidate([]));
    }

    [Fact]
    public void PickIsoCandidate_PicksFirstOrdinal_CaseInsensitive()
    {
        // Ordinal full-path order (B < a); the .ISO extension still counts.
        Assert.Equal(
            "/t/B.ISO",
            SevenZip.PickIsoCandidate(["/t/a.iso", "/t/B.ISO", "/t/c.iso"]));
        Assert.Equal(
            "/t/only.iso",
            SevenZip.PickIsoCandidate(["/t/readme.txt", "/t/only.iso"]));
    }

    [Fact]
    public void FindTool_PrefersExplicitPath()
    {
        var work = NewTempDir("seven_find");
        var fake = Path.Combine(work, "custom7z");
        File.WriteAllBytes(fake, [0x7F]);
        Assert.Equal(fake, SevenZip.FindTool(fake, [], probeWellKnownLocations: false));
    }

    [Fact]
    public void FindTool_ScansGivenDirectories()
    {
        var work = NewTempDir("seven_scan");
        var fake = Path.Combine(work, OperatingSystem.IsWindows() ? "7z.exe" : "7z");
        File.WriteAllBytes(fake, [0x7F]);
        Assert.Equal(fake, SevenZip.FindTool(@"C:\nonexistent\7z.exe", [work], probeWellKnownLocations: false));
    }

    [Fact]
    public void FindTool_NothingFound_ReturnsNull()
    {
        var work = NewTempDir("seven_empty");
        Assert.Null(SevenZip.FindTool(
            Path.Combine(work, "missing.exe"), [work], probeWellKnownLocations: false));
    }

    [Fact]
    public void Extract_MissingArchive_ThrowsFileNotFound()
    {
        var work = NewTempDir("seven_noarc");
        Assert.Throws<FileNotFoundException>(() =>
            SevenZip.Extract(Path.Combine(work, "ghost.zip"), Path.Combine(work, "out")));
    }

    [Fact]
    public void Extract_MissingTool_ThrowsFileNotFound()
    {
        var work = NewTempDir("seven_notool");
        var zip = Path.Combine(work, "game.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var entry = archive.CreateEntry("hello.txt").Open();
            entry.Write("hi"u8);
        }

        var ex = Assert.Throws<FileNotFoundException>(() =>
            SevenZip.Extract(zip, Path.Combine(work, "out"),
                toolPath: Path.Combine(work, "missing-7z.exe")));
        Assert.Contains("7z", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_Real7z_RoundTripsTree()
    {
        if (SevenZip.FindTool() == null)
        {
            return; // no 7z on this machine; failure paths above still run
        }

        var work = NewTempDir("seven_rt");
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllBytes(Path.Combine(src, "game.iso"), "fake-iso-bytes"u8.ToArray());
        File.WriteAllBytes(Path.Combine(src, "sub", "note.txt"), "note"u8.ToArray());
        var zip = Path.Combine(work, "game.zip");
        ZipFile.CreateFromDirectory(src, zip);

        var dest = Path.Combine(work, "out");
        SevenZip.Extract(zip, dest);

        Assert.Equal("fake-iso-bytes"u8.ToArray(), File.ReadAllBytes(Path.Combine(dest, "game.iso")));
        Assert.Equal("note"u8.ToArray(), File.ReadAllBytes(Path.Combine(dest, "sub", "note.txt")));
    }

    [Fact]
    public void Extract_CorruptArchive_Throws()
    {
        var work = NewTempDir("seven_bad");
        var zip = Path.Combine(work, "bogus.zip");
        File.WriteAllBytes(zip, "not a zip"u8.ToArray());

        if (SevenZip.FindTool() == null)
        {
            // Without 7z the tool lookup itself fails first.
            Assert.Throws<FileNotFoundException>(() =>
                SevenZip.Extract(zip, Path.Combine(work, "out")));
            return;
        }

        // With 7z present, 7z reports the failure (exit 2) with its last line.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SevenZip.Extract(zip, Path.Combine(work, "out")));
        Assert.Contains("7z", ex.Message, StringComparison.Ordinal);
    }
}