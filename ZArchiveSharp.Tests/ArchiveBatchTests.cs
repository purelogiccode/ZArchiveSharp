using System.IO.Compression;
using ZArchiveSharp.Pipeline;

namespace ZArchiveSharp.Tests;

/// <summary>
/// Black-box tests for the batch archive-container stage:
/// <c>zar --batch</c> must drive <c>.zip/.7z/.rar</c> through 7z and
/// keep the result going down the pipeline (ISO → <c>.zar</c>, anything else
/// → packed directory → <c>.zar</c>), and the ISO legs must convert directly.
/// 7z/XISO legs vacuous-pass (early return) when the tool or support is
/// missing; the failure-path tests are deterministic everywhere.
/// </summary>
public sealed class ArchiveBatchTests
{
    private static void AssertExtractsTree(
        string cli, string work, string zarPath, Dictionary<string, byte[]> expected)
    {
        var outDir = Path.Combine(work, "extracted_" + Guid.NewGuid().ToString("N"));
        RedumpIsoTests.RunCli(cli, work, zarPath, outDir);
        foreach (var (rel, data) in expected)
        {
            var full = Path.Combine(outDir, rel);
            Assert.True(File.Exists(full), $"Missing {rel} in {zarPath}.");
            Assert.Equal(data, File.ReadAllBytes(full));
        }
    }

    private static void ZipTree(string sourceDir, string zipPath)
    {
        ZipFile.CreateFromDirectory(sourceDir, zipPath);
    }

    private static void ZipSingleFile(string filePath, string entryName, string zipPath)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(filePath, entryName);
    }

    [Fact]
    public void Batch_Auto_ZipOfDir_PacksToZar()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null || SevenZip.FindTool() is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("arcbatch");
        try
        {
            var inDir = Path.Combine(work, "in");
            var stage = Path.Combine(work, "stage", "game");
            Directory.CreateDirectory(Path.Combine(stage, "sub"));
            var hello = "Hello batch world! "u8.ToArray();
            var blob = new byte[70000];
            new Random(7).NextBytes(blob);
            File.WriteAllBytes(Path.Combine(stage, "hello.txt"), hello);
            File.WriteAllBytes(Path.Combine(stage, "sub", "blob.bin"), blob);
            Directory.CreateDirectory(inDir);
            var zip = Path.Combine(inDir, "game.zip");
            ZipTree(stage, zip);
            var outDir = Path.Combine(work, "out");

            RedumpIsoTests.RunCli(cli, work, "--batch", inDir, outDir);

            // Chain output: the extracted tree packs to game.zar ...
            var zar = Path.Combine(outDir, "game.zar");
            Assert.True(File.Exists(zar), "Expected game.zar from the zip-of-dir chain.");
            AssertExtractsTree(cli, work, zar, new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["hello.txt"] = hello,
                [Path.Combine("sub", "blob.bin")] = blob,
            });

            // ... while keep-originals (default) keeps the zip and the tree.
            Assert.True(File.Exists(zip), "Source zip must be kept by default.");
            Assert.True(File.Exists(Path.Combine(outDir, "game", "hello.txt")),
                "Intermediate tree must be kept by default.");
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: temp cleanup must not fail the test.
            }
        }
    }

    [Fact]
    public void Batch_Auto_ZipOfIso_PacksToZar()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null || SevenZip.FindTool() is null || !RedumpIsoTests.CliSupportsIso(cli))
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("arciso");
        try
        {
            var inDir = Path.Combine(work, "in");
            Directory.CreateDirectory(inDir);
            var payload = RedumpIsoTests.Payload();
            var iso = Path.Combine(work, "game.iso");
            RedumpIsoTests.WriteMinimalXiso(iso, 0, "hello.txt", payload);
            var isoBytes = File.ReadAllBytes(iso);
            ZipSingleFile(iso, "game.iso", Path.Combine(inDir, "game.zip"));
            var outDir = Path.Combine(work, "out");

            RedumpIsoTests.RunCli(cli, work, "--batch", inDir, outDir);

            var zar = Path.Combine(outDir, "game.zar");
            Assert.True(File.Exists(zar), "Expected game.zar from the zip-of-ISO chain.");
            RedumpIsoTests.AssertExtractsPayload(cli, work, zar, "hello.txt", payload);
            Assert.Equal(isoBytes, File.ReadAllBytes(Path.Combine(outDir, "game.iso")));
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: temp cleanup must not fail the test.
            }
        }
    }

    [Fact]
    public void Batch_Auto_DirectIso_PacksToZar()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null || !RedumpIsoTests.CliSupportsIso(cli))
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("arcisodirect");
        try
        {
            var inDir = Path.Combine(work, "in");
            Directory.CreateDirectory(inDir);
            var payload = RedumpIsoTests.Payload();
            RedumpIsoTests.WriteMinimalXiso(Path.Combine(inDir, "game.iso"), 0, "hello.txt", payload);
            var outDir = Path.Combine(work, "out");

            RedumpIsoTests.RunCli(cli, work, "--batch", inDir, outDir);

            var zar = Path.Combine(outDir, "game.zar");
            Assert.True(File.Exists(zar), "A plain ISO in --batch auto must convert to .zar.");
            RedumpIsoTests.AssertExtractsPayload(cli, work, zar, "hello.txt", payload);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: temp cleanup must not fail the test.
            }
        }
    }

    [Fact]
    public void Batch_ExtractIso_PacksIso()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null || !RedumpIsoTests.CliSupportsIso(cli))
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("arcextractiso");
        try
        {
            var inDir = Path.Combine(work, "in");
            Directory.CreateDirectory(inDir);
            var payload = RedumpIsoTests.Payload();
            RedumpIsoTests.WriteMinimalXiso(Path.Combine(inDir, "game.iso"), 0, "hello.txt", payload);
            var outDir = Path.Combine(work, "out");

            RedumpIsoTests.RunCli(cli, work, "--batch", "--mode", "extract-iso", inDir, outDir);

            var zar = Path.Combine(outDir, "game.zar");
            Assert.True(File.Exists(zar), "--mode extract-iso must convert ISOs to .zar.");
            RedumpIsoTests.AssertExtractsPayload(cli, work, zar, "hello.txt", payload);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: temp cleanup must not fail the test.
            }
        }
    }

    [Fact]
    public void Batch_ExtractArchive_KeepsTreeAndIso()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null || SevenZip.FindTool() is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("arcextractarc");
        try
        {
            var inDir = Path.Combine(work, "in");
            Directory.CreateDirectory(inDir);
            var stage = Path.Combine(work, "stage", "game");
            Directory.CreateDirectory(stage);
            File.WriteAllBytes(Path.Combine(stage, "hello.txt"), "hi"u8.ToArray());
            ZipTree(stage, Path.Combine(inDir, "game.zip"));

            var iso = Path.Combine(work, "disc.iso");
            File.WriteAllBytes(iso, "fake-iso-bytes"u8.ToArray());
            ZipSingleFile(iso, "disc.iso", Path.Combine(inDir, "disc.zip"));
            var outDir = Path.Combine(work, "out");

            RedumpIsoTests.RunCli(cli, work, "--batch", "--mode", "extract-archive", inDir, outDir);

            // The stage stops after extraction: no .zar anywhere ...
            Assert.Empty(Directory.GetFiles(outDir, "*.zar", SearchOption.AllDirectories));
            // ... the tree lands as a directory ...
            Assert.Equal("hi"u8.ToArray(), File.ReadAllBytes(Path.Combine(outDir, "game", "hello.txt")));
            // ... and the ISO lands as a kept file, byte-identical.
            Assert.Equal("fake-iso-bytes"u8.ToArray(), File.ReadAllBytes(Path.Combine(outDir, "disc.iso")));
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: temp cleanup must not fail the test.
            }
        }
    }

    [Fact]
    public void Batch_Auto_DeleteSource_RemovesChainInputs()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null || SevenZip.FindTool() is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("arcdelsrc");
        try
        {
            var inDir = Path.Combine(work, "in");
            Directory.CreateDirectory(inDir);
            var stage = Path.Combine(work, "stage", "game");
            Directory.CreateDirectory(stage);
            File.WriteAllBytes(Path.Combine(stage, "hello.txt"), "hi"u8.ToArray());
            var zip = Path.Combine(inDir, "game.zip");
            ZipTree(stage, zip);
            var outDir = Path.Combine(work, "out");

            RedumpIsoTests.RunCli(cli, work, "--batch", "--delete-source", inDir, outDir);

            Assert.True(File.Exists(Path.Combine(outDir, "game.zar")), "The .zar must survive --delete-source.");
            Assert.False(File.Exists(zip), "The source archive must go with --delete-source.");
            Assert.False(Directory.Exists(Path.Combine(outDir, "game")),
                "The intermediate tree must go with --delete-source.");
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: temp cleanup must not fail the test.
            }
        }
    }

    [Fact]
    public void Batch_Auto_DeleteSource_DownstreamFailure_KeepsSource()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null || SevenZip.FindTool() is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("arcdelfail");
        try
        {
            var inDir = Path.Combine(work, "in");
            Directory.CreateDirectory(inDir);
            var stage = Path.Combine(work, "stage", "game");
            Directory.CreateDirectory(stage);
            File.WriteAllBytes(Path.Combine(stage, "hello.txt"), "hi"u8.ToArray());
            var zip = Path.Combine(inDir, "game.zip");
            ZipTree(stage, zip);
            var outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);
            // Occupy the downstream .zar so the terminal pack refuses under
            // the default fail policy: the source must survive the failure.
            File.WriteAllBytes(Path.Combine(outDir, "game.zar"), "occupied"u8.ToArray());

            var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work,
                ["--batch", "--delete-source", inDir, outDir]);
            if (!started)
            {
                return;
            }

            // Collision-only batch failures report the documented -11.
            Assert.Equal(-11, exit);
            Assert.Contains("game.zip", stderr, StringComparison.Ordinal);
            Assert.True(File.Exists(zip),
                "The source archive was deleted even though the downstream .zar stage failed.");
            Assert.Equal("occupied"u8.ToArray(), File.ReadAllBytes(Path.Combine(outDir, "game.zar")));
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: temp cleanup must not fail the test.
            }
        }
    }

    [Fact]
    public void Batch_Archive_BogusFile_FailsItemNotBatch()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("arcbogus");
        try
        {
            var inDir = Path.Combine(work, "in");
            Directory.CreateDirectory(inDir);
            File.WriteAllBytes(Path.Combine(inDir, "bogus.zip"), "not a zip"u8.ToArray());
            var outDir = Path.Combine(work, "out");

            // Deterministic with and without 7z: with it, 7z rejects the
            // file; without it, the tool lookup fails the item. Either way
            // the item fails and the batch exits -13.
            var (started, exit, _, stderr) =
                RedumpIsoTests.TryRunCli(cli, work, ["--batch", inDir, outDir]);
            if (!started)
            {
                return;
            }

            Assert.Equal(-13, exit);
            Assert.Contains("bogus.zip", stderr, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: temp cleanup must not fail the test.
            }
        }
    }

    [Fact]
    public void Batch_SevenZipOverride_MissingPath_UsageError()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("arcoverride");
        try
        {
            var inDir = Path.Combine(work, "in");
            Directory.CreateDirectory(inDir);
            using (var archive = ZipFile.Open(Path.Combine(inDir, "game.zip"), ZipArchiveMode.Create))
            {
                using var entry = archive.CreateEntry("hello.txt").Open();
                entry.Write("hi"u8);
            }

            var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work,
                ["--batch", "--seven-zip", Path.Combine(work, "missing-7z.exe"), inDir, Path.Combine(work, "out")]);
            if (!started)
            {
                return;
            }

            Assert.Equal(-1, exit);
            Assert.Contains("7z binary not found", stderr, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: temp cleanup must not fail the test.
            }
        }
    }
}