using API.Services.Archives;
using System.IO.Compression;
using System.Text;

namespace GameTown.Tests;

/// <summary>
/// The ZIP surgery, on its own.
///
/// Every archive here is read back with <see cref="ZipArchive"/> rather than with the writer's own
/// parser. Checking the writer against itself would prove only that it is self-consistent; what has
/// to hold is that a completely independent implementation — the one the operating system's own
/// "Extract All" is closest to — still reads the file, and still reads every entry that was in it
/// before.
///
/// The failure this guards is not "the guide is missing". It is a contributor's multi-gigabyte
/// archive being quietly corrupted by a feature that adds a text file to it.
/// </summary>
public class ZipGuideWriterTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gametown-zip-tests", Guid.NewGuid().ToString("N"));

    public ZipGuideWriterTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private const string GuideName = "GameTownGuide.txt";

    /// <summary>An ordinary archive with a few entries, one of them compressible.</summary>
    private string MakeArchive(string name = "game.zip", int entries = 3, int entryBytes = 4096)
    {
        var path = Path.Combine(_directory, name);

        using (var file = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            for (var i = 0; i < entries; i++)
            {
                var entry = zip.CreateEntry($"data/file{i}.bin", CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(Enumerable.Repeat((byte)('a' + i), entryBytes).ToArray());
            }
        }

        return path;
    }

    private static Dictionary<string, byte[]> ReadAll(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        });
    }

    // ------------------------------------------------------------------ the basic operation

    [Fact]
    public void The_guide_is_readable_and_the_original_entries_survive()
    {
        var path = MakeArchive();
        var before = ReadAll(path);

        ZipGuideWriter.AddOrReplace(path, GuideName, Encoding.UTF8.GetBytes("How to play"));

        var after = ReadAll(path);

        Assert.Equal("How to play", Encoding.UTF8.GetString(after[GuideName]));
        foreach (var (name, content) in before)
            Assert.Equal(content, after[name]);
    }

    /// <summary>
    /// The whole justification for this class: the cost is the size of the index, not the archive. If
    /// this ever regresses to a repack it will still pass every other test here — just slowly, and on
    /// someone's 8 GB upload rather than on a 60 KB fixture.
    /// </summary>
    [Fact]
    public void Existing_file_data_is_not_rewritten()
    {
        var path = MakeArchive(entries: 4, entryBytes: 64 * 1024);

        var originalLength = new FileInfo(path).Length;
        var originalPrefix = new byte[originalLength];
        using (var file = File.OpenRead(path)) file.ReadExactly(originalPrefix);

        ZipGuideWriter.AddOrReplace(path, GuideName, Encoding.UTF8.GetBytes("How to play"));

        // Every byte the archive had before is still there, unmoved. Only new bytes were added.
        var afterPrefix = new byte[originalLength];
        using (var file = File.OpenRead(path)) file.ReadExactly(afterPrefix);

        Assert.Equal(originalPrefix, afterPrefix);
        Assert.True(new FileInfo(path).Length > originalLength);
    }

    [Fact]
    public void Rewriting_the_guide_leaves_exactly_one_of_them()
    {
        var path = MakeArchive();

        ZipGuideWriter.AddOrReplace(path, GuideName, Encoding.UTF8.GetBytes("first"));
        ZipGuideWriter.AddOrReplace(path, GuideName, Encoding.UTF8.GetBytes("second"));
        ZipGuideWriter.AddOrReplace(path, GuideName, Encoding.UTF8.GetBytes("third"));

        using var zip = ZipFile.OpenRead(path);
        var guides = zip.Entries.Where(e => e.FullName == GuideName).ToList();

        // Not just "the newest wins" — the stale entries must be gone from the index, or `unzip -l`
        // shows three copies and the archive grows visibly with every edit of the instructions.
        Assert.Single(guides);

        using var reader = new StreamReader(guides[0].Open());
        Assert.Equal("third", reader.ReadToEnd());
    }

    [Fact]
    public void Removing_the_guide_takes_it_out_of_the_index_and_leaves_the_rest()
    {
        var path = MakeArchive();
        var before = ReadAll(path);

        ZipGuideWriter.AddOrReplace(path, GuideName, Encoding.UTF8.GetBytes("How to play"));
        Assert.True(ZipGuideWriter.Remove(path, GuideName));

        var after = ReadAll(path);
        Assert.DoesNotContain(GuideName, after.Keys);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (name, content) in before)
            Assert.Equal(content, after[name]);
    }

    /// <summary>
    /// Removing what is not there must be a no-op *including on disk*. Writing a fresh index anyway
    /// would mean the file grew every time someone saved a game with the toggle already off.
    /// </summary>
    [Fact]
    public void Removing_an_absent_guide_changes_nothing()
    {
        var path = MakeArchive();
        var length = new FileInfo(path).Length;

        Assert.False(ZipGuideWriter.Remove(path, GuideName));

        Assert.Equal(length, new FileInfo(path).Length);
    }

    [Fact]
    public void Contains_reports_what_the_index_lists()
    {
        var path = MakeArchive();
        Assert.False(ZipGuideWriter.Contains(path, GuideName));

        ZipGuideWriter.AddOrReplace(path, GuideName, "x"u8.ToArray());
        Assert.True(ZipGuideWriter.Contains(path, GuideName));

        ZipGuideWriter.Remove(path, GuideName);
        Assert.False(ZipGuideWriter.Contains(path, GuideName));
    }

    // ------------------------------------------------------------------ awkward archives

    /// <summary>
    /// An archive with an EOCD comment. The backwards scan has to accept a signature only when its
    /// comment length reaches exactly the end of the file — otherwise it stops at the wrong place.
    /// </summary>
    [Fact]
    public void An_archive_with_a_trailing_comment_is_handled()
    {
        var path = MakeArchive();

        // Append a comment by hand: bump the EOCD's comment length and add the bytes.
        var comment = "packed by somebody"u8.ToArray();
        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Seek(-2, SeekOrigin.End);
            file.Write(BitConverter.GetBytes((ushort)comment.Length));
            file.Write(comment);
        }

        ZipGuideWriter.AddOrReplace(path, GuideName, "How to play"u8.ToArray());

        Assert.Equal("How to play", Encoding.UTF8.GetString(ReadAll(path)[GuideName]));
    }

    /// <summary>
    /// Data glued on the front, as a self-extracting archive has. Every offset such an archive records
    /// is relative to where the ZIP begins rather than to the start of the file, so an index written
    /// without accounting for that points every entry 8192 bytes past where it lives.
    ///
    /// Verified by *stripping the stub back off* and reading the remainder, because
    /// <see cref="ZipArchive"/> cannot open a prefixed archive at all — it reads the index offset and
    /// seeks straight to it, landing inside the stub. That limitation is the reason for the shape of
    /// this test, and it is also the direct check: the stripped file is only a valid ZIP if the
    /// offsets written were archive-relative.
    /// </summary>
    [Fact]
    public void An_archive_with_data_prepended_keeps_working()
    {
        var path = MakeArchive();
        var before = ReadAll(path);

        var stub = Enumerable.Repeat((byte)0x90, 8192).ToArray();
        var withPrefix = Path.Combine(_directory, "sfx.zip");
        using (var output = new FileStream(withPrefix, FileMode.Create))
        {
            output.Write(stub);
            using var original = File.OpenRead(path);
            original.CopyTo(output);
        }

        ZipGuideWriter.AddOrReplace(withPrefix, GuideName, "How to play"u8.ToArray());

        // The stub is untouched — for a real self-extracting archive it is the executable half.
        var prefixed = File.ReadAllBytes(withPrefix);
        Assert.Equal(stub, prefixed[..stub.Length]);

        var stripped = Path.Combine(_directory, "stripped.zip");
        File.WriteAllBytes(stripped, prefixed[stub.Length..]);

        var after = ReadAll(stripped);
        Assert.Equal("How to play", Encoding.UTF8.GetString(after[GuideName]));
        foreach (var (name, content) in before)
            Assert.Equal(content, after[name]);
    }

    /// <summary>
    /// A ZIP64 archive. .NET writes the ZIP64 records once the entry count or a size demands them, and
    /// an index written without handling that corrupts large archives *only* — which is exactly the
    /// class of bug that survives a test suite built on small fixtures.
    /// </summary>
    [Fact]
    public void A_zip64_archive_is_handled()
    {
        // Past 65535 entries the EOCD's 16-bit count saturates and the ZIP64 records become mandatory,
        // which reaches the same code paths as a >4 GB archive without writing 4 GB to a test runner.
        var path = Path.Combine(_directory, "many.zip");
        using (var file = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            for (var i = 0; i < 70_000; i++)
                zip.CreateEntry($"f{i}.txt", CompressionLevel.NoCompression);
        }

        ZipGuideWriter.AddOrReplace(path, GuideName, "How to play"u8.ToArray());

        using var reopened = ZipFile.OpenRead(path);
        Assert.Equal(70_001, reopened.Entries.Count);
        Assert.NotNull(reopened.GetEntry(GuideName));
    }

    [Fact]
    public void An_empty_archive_is_handled()
    {
        var path = MakeArchive(entries: 0);

        ZipGuideWriter.AddOrReplace(path, GuideName, "How to play"u8.ToArray());

        Assert.Equal("How to play", Encoding.UTF8.GetString(ReadAll(path)[GuideName]));
    }

    /// <summary>
    /// A ZIP that contains another ZIP, stored rather than compressed — so a valid EOCD signature
    /// appears in the middle of the file data. The scan must not stop there.
    /// </summary>
    [Fact]
    public void A_nested_stored_archive_does_not_confuse_the_scan()
    {
        var inner = MakeArchive("inner.zip", entries: 2);
        var innerBytes = File.ReadAllBytes(inner);

        var path = Path.Combine(_directory, "outer.zip");
        using (var file = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("nested.zip", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(innerBytes);
        }

        ZipGuideWriter.AddOrReplace(path, GuideName, "How to play"u8.ToArray());

        var after = ReadAll(path);
        Assert.Equal("How to play", Encoding.UTF8.GetString(after[GuideName]));
        Assert.Equal(innerBytes, after["nested.zip"]);
    }

    // ------------------------------------------------------------------ refusal and rollback

    [Fact]
    public void A_file_that_is_not_a_zip_is_refused()
    {
        var path = Path.Combine(_directory, "not-a-zip.zip");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x7F, 5000).ToArray());

        Assert.Throws<ZipGuideWriter.UnsupportedArchiveException>(
            () => ZipGuideWriter.AddOrReplace(path, GuideName, "x"u8.ToArray()));
    }

    /// <summary>
    /// The property that makes mutating someone's only copy defensible: a refusal leaves the file
    /// byte-for-byte as it was found, because nothing is written before the archive is understood.
    /// </summary>
    [Fact]
    public void A_refused_archive_is_left_exactly_as_it_was()
    {
        var path = Path.Combine(_directory, "truncated.zip");

        // A real archive with its index lopped off — the shape an interrupted upload leaves.
        var complete = File.ReadAllBytes(MakeArchive("source.zip"));
        File.WriteAllBytes(path, complete[..(complete.Length / 2)]);

        var before = File.ReadAllBytes(path);

        Assert.ThrowsAny<Exception>(() => ZipGuideWriter.AddOrReplace(path, GuideName, "x"u8.ToArray()));

        Assert.Equal(before, File.ReadAllBytes(path));
    }
}
