namespace ZArchiveSharp.CliBattleTests;

/// <summary>Deterministic fixture trees both CLIs must pack/extract identically.</summary>
public static class BattleFixtures
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in All.Keys)
            {
                data.Add(name);
            }

            return data;
        }
    }

    public static readonly IReadOnlyDictionary<string, Action<string>> All =
        new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            ["minimal"] = dir =>
            {
                File.WriteAllText(Path.Combine(dir, "a.txt"), "hello world\n");
                Directory.CreateDirectory(Path.Combine(dir, "sub"));
                File.WriteAllLines(Path.Combine(dir, "sub", "b.bin"),
                    Enumerable.Range(1, 100).Select(i => $"line {i}"));
            },
            ["nested"] = dir =>
            {
                File.WriteAllText(Path.Combine(dir, "a.txt"), "hi\n");
                Directory.CreateDirectory(Path.Combine(dir, "sub", "deep"));
                File.WriteAllText(Path.Combine(dir, "sub", "b.txt"), "mid\n");
                File.WriteAllText(Path.Combine(dir, "sub", "deep", "c.txt"), "deep\n");
            },
            ["empty_dir"] = dir => { Directory.CreateDirectory(Path.Combine(dir, "empty_sub")); },
            ["empty_files"] = dir =>
            {
                File.WriteAllBytes(Path.Combine(dir, "empty.bin"), []);
                File.WriteAllText(Path.Combine(dir, "note.txt"), "not empty\n");
                Directory.CreateDirectory(Path.Combine(dir, "hollow"));
            },
            ["boundaries"] = dir =>
            {
                // 64 KiB block edges: empty, 1 byte, just under/at/over one block, multi-block.
                WritePattern(Path.Combine(dir, "f0.bin"), 0);
                WritePattern(Path.Combine(dir, "f1.bin"), 1);
                WritePattern(Path.Combine(dir, "f63k.bin"), (64 * 1024) - 1);
                WritePattern(Path.Combine(dir, "f64k.bin"), 64 * 1024);
                WritePattern(Path.Combine(dir, "f64k1.bin"), (64 * 1024) + 1);
                WritePattern(Path.Combine(dir, "f128k13.bin"), (128 * 1024) + 13);
            },
            ["mixed_big"] = dir =>
            {
                var text = string.Concat(
                    Enumerable.Repeat("the quick brown fox jumps over the lazy dog 0123456789\n", 5000));
                File.WriteAllText(Path.Combine(dir, "text.txt"), text);
                var rng = new Random(12345);
                var random = new byte[300_000];
                rng.NextBytes(random);
                File.WriteAllBytes(Path.Combine(dir, "rand.bin"), random);
                WritePattern(Path.Combine(dir, "exact64k.bin"), 64 * 1024);
                File.WriteAllBytes(Path.Combine(dir, "empty.bin"), []);
                Directory.CreateDirectory(Path.Combine(dir, "empty_sub"));
            },
            ["latin1_names"] = dir =>
            {
                File.WriteAllText(Path.Combine(dir, "caf\u00e9.txt"), "coffee\n");
                File.WriteAllText(Path.Combine(dir, "UPPERCASE.TXT"), "loud\n");
                File.WriteAllText(Path.Combine(dir, "with space.txt"), "spaced\n");
                var sub = Directory.CreateDirectory(Path.Combine(dir, "na\u00efve dir"));
                File.WriteAllText(Path.Combine(sub.FullName, "r\u00e9sum\u00e9.txt"), "cv\n");
            },
        };

    private static void WritePattern(string path, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Creates two independent copies of one fixture under <paramref name="root"/>.</summary>
    public static (string OracleSrc, string MineSrc) CreateTwinCopies(string root, string fixtureName)
    {
        var oracleSrc = Path.Combine(root, "src_oracle");
        var mineSrc = Path.Combine(root, "src_mine");
        Directory.CreateDirectory(oracleSrc);
        Directory.CreateDirectory(mineSrc);
        All[fixtureName](oracleSrc);
        All[fixtureName](mineSrc);
        return (oracleSrc, mineSrc);
    }
}

/// <summary>Directory-tree equality: same relative entries (forward slashes) plus byte-equal files.</summary>
public static class TreeAssert
{
    public static void EqualDirectories(string expected, string actual)
    {
        var expectedEntries = RelativeEntries(expected);
        var actualEntries = RelativeEntries(actual);
        Assert.Equal(expectedEntries.Keys.OrderBy(k => k, StringComparer.Ordinal),
            actualEntries.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var (rel, kind) in expectedEntries)
        {
            if (kind == EntryKind.Directory)
            {
                Assert.True(Directory.Exists(Path.Combine(actual, ToOsPath(rel))),
                    $"Missing directory '{rel}' in '{actual}'.");
            }
            else
            {
                var left = Path.Combine(expected, ToOsPath(rel));
                var right = Path.Combine(actual, ToOsPath(rel));
                Assert.True(File.Exists(right), $"Missing file '{rel}' in '{actual}'.");
                Assert.True(File.ReadAllBytes(left).SequenceEqual(File.ReadAllBytes(right)),
                    $"File bytes differ: '{rel}'.");
            }
        }
    }

    private enum EntryKind
    {
        File,
        Directory,
    }

    private static Dictionary<string, EntryKind> RelativeEntries(string root)
    {
        var map = new Dictionary<string, EntryKind>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            map[ToForwardSlash(Path.GetRelativePath(root, dir))] = EntryKind.Directory;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            map[ToForwardSlash(Path.GetRelativePath(root, file))] = EntryKind.File;
        }

        return map;
    }

    private static string ToForwardSlash(string rel)
    {
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string ToOsPath(string rel)
    {
        return rel.Replace('/', Path.DirectorySeparatorChar);
    }
}

/// <summary>Log normalizations shared by the chatter comparisons.</summary>
public static class ZarLog
{
    public static string SlashToForward(string s)
    {
        return s.Replace('\\', '/');
    }

    public static string FileName(string path)
    {
        return path.Replace('\\', '/').Split('/').Last();
    }
}