using System.Buffers.Binary;
using System.Text;

namespace API.Services.Archives;

/// <summary>
/// Adds or removes a single small file inside an existing ZIP archive <b>without repacking it</b>.
///
/// A game archive here is routinely gigabytes on a network share, so the only acceptable cost for
/// "write the instructions into the download" is one proportional to the archive's *index*, not its
/// contents. That is possible because of how ZIP is laid out: the file data comes first, the index
/// (the central directory) sits at the end, and readers find that index by scanning **backwards**
/// from the end of the file for the end-of-central-directory signature.
///
/// So this appends, and only appends:
///
///     before:  [ file data ][ central dir ][ EOCD ]
///     after:   [ file data ][ dead bytes ][ guide entry ][ new central dir ][ new EOCD ]
///
/// The original index becomes unreachable bytes in the middle, which every ZIP reader already
/// tolerates — it is the same shape a self-extracting archive has. Existing entries keep their byte
/// offsets, so their index records are copied across verbatim and their data is never read, moved or
/// rewritten.
///
/// <b>Do not replace this with System.IO.Compression.</b> ZipArchiveMode.Update is the obvious API and
/// documents itself as holding "the content of the entire archive in memory", writing nothing until
/// dispose — a 4 GB allocation and a full 4 GB rewrite for the sake of a one-kilobyte text file. It
/// would pass every test written against a small archive and fail on a real library.
///
/// Failure is recoverable by construction: nothing before the original end of file is modified, so
/// the recovery for any error is to truncate back to the length recorded on entry. That matters
/// because the archive is frequently the only copy anyone has.
/// </summary>
public static class ZipGuideWriter
{
    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint EocdSignature = 0x06054b50;
    private const uint Zip64EocdSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;

    private const int EocdMinimumLength = 22;
    private const int Zip64EocdLength = 56;
    private const int Zip64LocatorLength = 20;

    /// <summary>The largest value a 32-bit ZIP field can hold; anything more needs ZIP64.</summary>
    private const uint Max32 = 0xFFFFFFFF;
    private const ushort Max16 = 0xFFFF;

    /// <summary>
    /// An EOCD comment may be up to 65535 bytes, so the signature can sit that far from the end. This
    /// is the whole window that ever needs searching.
    /// </summary>
    private const int MaxEocdSearch = EocdMinimumLength + ushort.MaxValue;

    /// <summary>Thrown when the file is not a ZIP this code is willing to touch. Never a partial write.</summary>
    public sealed class UnsupportedArchiveException(string message) : Exception(message);

    /// <summary>
    /// Writes <paramref name="content"/> into the archive as <paramref name="entryName"/>, replacing
    /// any existing entry of that name.
    /// </summary>
    public static void AddOrReplace(string archivePath, string entryName, byte[] content)
        => Apply(archivePath, entryName, content);

    /// <summary>
    /// Removes <paramref name="entryName"/> if present. Returns whether there was one to remove.
    ///
    /// "Removal" here means the entry stops appearing in the index — its bytes stay behind as dead
    /// weight, because reclaiming them would mean moving everything after them, which is the repack
    /// this class exists to avoid. For a text file that is a kilobyte.
    /// </summary>
    public static bool Remove(string archivePath, string entryName)
        => Apply(archivePath, entryName, content: null);

    /// <summary>Whether the entry is currently listed in the archive's index.</summary>
    public static bool Contains(string archivePath, string entryName)
    {
        using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var directory = ReadDirectory(file);
        return directory.Records.Any(r => r.Name == entryName);
    }

    private static bool Apply(string archivePath, string entryName, byte[]? content)
    {
        var nameBytes = Encoding.UTF8.GetBytes(entryName);
        if (nameBytes.Length > Max16)
            throw new UnsupportedArchiveException("The entry name is too long for a ZIP archive.");

        using var file = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var directory = ReadDirectory(file);
        var kept = directory.Records.Where(r => r.Name != entryName).ToList();
        var replaced = kept.Count != directory.Records.Count;

        // Nothing to do, and — importantly — nothing written. Removing an entry that is not there
        // must not append a fresh index for no reason; re-running would grow the file every time.
        if (content is null && !replaced) return false;

        // The rollback point. Everything below this offset is left exactly as it was found, so
        // truncating back to it undoes the operation completely whatever goes wrong.
        var originalLength = file.Length;

        try
        {
            file.Seek(originalLength, SeekOrigin.Begin);

            var newRecords = new List<byte[]>(kept.Count + 1);
            foreach (var record in kept) newRecords.Add(record.Raw);

            if (content is not null)
            {
                // The entry's offset as the archive itself counts them. For an ordinary ZIP the
                // prefix is zero; for one with data glued on the front (a self-extracting stub) every
                // stored offset is relative to where the ZIP begins, not to the start of the file.
                var entryOffset = originalLength - directory.Prefix;

                WriteLocalHeader(file, nameBytes, content);
                file.Write(content);

                newRecords.Add(BuildCentralRecord(nameBytes, content, entryOffset));
            }

            var centralDirectoryOffset = file.Position - directory.Prefix;
            long centralDirectorySize = 0;
            foreach (var record in newRecords)
            {
                file.Write(record);
                centralDirectorySize += record.Length;
            }

            WriteEndOfCentralDirectory(file, newRecords.Count, centralDirectorySize,
                centralDirectoryOffset, directory.Prefix);

            file.Flush(flushToDisk: true);
            return replaced || content is not null;
        }
        catch
        {
            // Append-only means the original archive is still intact below this point. Restoring it is
            // a truncate rather than a repair, which is the only reason mutating someone's single copy
            // of a multi-gigabyte file is defensible at all.
            try
            {
                file.SetLength(originalLength);
                file.Flush(flushToDisk: true);
            }
            catch
            {
                // Nothing further to try. The outer exception is the one worth reporting.
            }
            throw;
        }
    }

    // ------------------------------------------------------------------ reading the existing index

    private sealed record CentralRecord(string Name, byte[] Raw);

    /// <summary>
    /// The archive's index, plus how far into the file the ZIP actually starts.
    /// </summary>
    private sealed record Directory(List<CentralRecord> Records, long Prefix);

    private static Directory ReadDirectory(FileStream file)
    {
        var eocdPosition = FindEocd(file, out var eocd);

        // A ZIP split across several files. Every offset then refers to a disk that is not this file,
        // so there is nothing safe to do with it.
        if (BinaryPrimitives.ReadUInt16LittleEndian(eocd.AsSpan(4)) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(eocd.AsSpan(6)) != 0)
        {
            throw new UnsupportedArchiveException("This is one part of a split ZIP archive.");
        }

        long entryCount = BinaryPrimitives.ReadUInt16LittleEndian(eocd.AsSpan(10));
        long directorySize = BinaryPrimitives.ReadUInt32LittleEndian(eocd.AsSpan(12));
        long directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd.AsSpan(16));

        // Any of the three saturated means the real values live in a ZIP64 record ahead of the EOCD.
        // Archives here cross 4 GB routinely, so this is the normal path, not an exotic one.
        var directoryEnd = eocdPosition;
        if (entryCount == Max16 || directorySize == Max32 || directoryOffset == Max32)
        {
            var zip64 = ReadZip64Eocd(file, eocdPosition);
            entryCount = zip64.EntryCount;
            directorySize = zip64.DirectorySize;
            directoryOffset = zip64.DirectoryOffset;
            directoryEnd = zip64.RecordPosition;
        }

        if (directorySize < 0 || directorySize > directoryEnd)
            throw new UnsupportedArchiveException("The ZIP index is not where the archive says it is.");

        // Where the index physically sits, versus where the archive claims it does. The difference is
        // data prepended to the ZIP — a self-extracting stub, most often — and every offset inside the
        // archive is short by exactly that much.
        var actualDirectoryPosition = directoryEnd - directorySize;
        var prefix = actualDirectoryPosition - directoryOffset;
        if (prefix < 0)
            throw new UnsupportedArchiveException("The ZIP index is not where the archive says it is.");

        var raw = new byte[directorySize];
        file.Seek(actualDirectoryPosition, SeekOrigin.Begin);
        file.ReadExactly(raw);

        return new Directory(ParseRecords(raw, entryCount), prefix);
    }

    private static List<CentralRecord> ParseRecords(byte[] raw, long expectedCount)
    {
        var records = new List<CentralRecord>((int)Math.Min(expectedCount, 4096));
        var position = 0;

        while (position + 46 <= raw.Length)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(position)) != CentralHeaderSignature)
                break;

            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(position + 28));
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(position + 30));
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(position + 32));
            var length = 46 + nameLength + extraLength + commentLength;

            if (position + length > raw.Length)
                throw new UnsupportedArchiveException("The ZIP index is truncated.");

            // Copied whole and never interpreted beyond its name. Each record already describes an
            // entry that is not moving, so anything in it — ZIP64 extras, unicode path fields, unix
            // permissions, comments — stays correct without this code needing to understand it.
            var record = raw[position..(position + length)];
            var name = DecodeName(raw.AsSpan(position + 46, nameLength),
                                  BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(position + 8)));

            records.Add(new CentralRecord(name, record));
            position += length;
        }

        if (records.Count != expectedCount)
            throw new UnsupportedArchiveException(
                $"The ZIP index lists {expectedCount} entries but {records.Count} could be read.");

        return records;
    }

    /// <summary>
    /// Names are UTF-8 when the general-purpose bit 11 is set, and IBM Code Page 437 otherwise.
    ///
    /// Only used to recognise our own pure-ASCII entry name, which decodes identically either way —
    /// so a wrong guess on some other entry's name cannot cause the wrong record to be dropped.
    /// </summary>
    private static string DecodeName(ReadOnlySpan<byte> nameBytes, ushort flags)
        => (flags & 0x0800) != 0
            ? Encoding.UTF8.GetString(nameBytes)
            : Encoding.Latin1.GetString(nameBytes);

    private static long FindEocd(FileStream file, out byte[] eocd)
    {
        var searchLength = (int)Math.Min(file.Length, MaxEocdSearch);
        if (searchLength < EocdMinimumLength)
            throw new UnsupportedArchiveException("The file is too small to be a ZIP archive.");

        var tail = new byte[searchLength];
        file.Seek(file.Length - searchLength, SeekOrigin.Begin);
        file.ReadExactly(tail);

        // Backwards, and the first match wins: an archive that contains another archive as a stored
        // entry has EOCD signatures in its file data too, and the real one is the last in the file.
        for (var offset = searchLength - EocdMinimumLength; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset)) != EocdSignature)
                continue;

            // The comment length has to account for exactly the rest of the file, or this is a
            // signature that happens to appear inside some entry's data.
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 20));
            if (offset + EocdMinimumLength + commentLength != searchLength) continue;

            eocd = tail[offset..(offset + EocdMinimumLength)];
            return file.Length - searchLength + offset;
        }

        throw new UnsupportedArchiveException("This file is not a ZIP archive.");
    }

    private sealed record Zip64Eocd(long EntryCount, long DirectorySize, long DirectoryOffset, long RecordPosition);

    private static Zip64Eocd ReadZip64Eocd(FileStream file, long eocdPosition)
    {
        var locatorPosition = eocdPosition - Zip64LocatorLength;
        if (locatorPosition < 0)
            throw new UnsupportedArchiveException("A ZIP64 archive is missing its locator.");

        var locator = new byte[Zip64LocatorLength];
        file.Seek(locatorPosition, SeekOrigin.Begin);
        file.ReadExactly(locator);

        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature)
            throw new UnsupportedArchiveException("A ZIP64 archive is missing its locator.");

        // The ZIP64 record sits immediately before its locator, so look there first and only fall
        // back to the offset the locator states.
        //
        // That order matters for an archive with data prepended to it: the stated offset is relative
        // to where the ZIP begins, not to the start of the file, and the prefix that reconciles the
        // two is not known until the index has been located — which is what this call is for. Reading
        // the adjacent record sidesteps the circularity entirely.
        var record = new byte[Zip64EocdLength];
        var recordPosition = locatorPosition - Zip64EocdLength;

        if (recordPosition < 0 || !TryReadSignatureAt(file, recordPosition, record, Zip64EocdSignature))
        {
            recordPosition = BinaryPrimitives.ReadInt64LittleEndian(locator.AsSpan(8));
            if (recordPosition < 0
                || recordPosition + Zip64EocdLength > file.Length
                || !TryReadSignatureAt(file, recordPosition, record, Zip64EocdSignature))
            {
                throw new UnsupportedArchiveException("The ZIP64 index is not where the archive says it is.");
            }
        }

        return new Zip64Eocd(
            EntryCount: BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(32)),
            DirectorySize: BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(40)),
            DirectoryOffset: BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(48)),
            // The ZIP64 record sits between the index and the locator, so it — not the EOCD — is
            // where the index ends.
            RecordPosition: recordPosition);
    }

    /// <summary>Reads a fixed-size structure and reports whether it carries the signature expected.</summary>
    private static bool TryReadSignatureAt(FileStream file, long position, byte[] buffer, uint signature)
    {
        if (position < 0 || position + buffer.Length > file.Length) return false;

        file.Seek(position, SeekOrigin.Begin);
        file.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer) == signature;
    }

    // ------------------------------------------------------------------ writing the new entry

    /// <summary>
    /// Stored, never deflated. The guide is under a kilobyte, so compressing it would save nothing
    /// worth the code — and it keeps the entry readable with a hex editor if anyone ever has to work
    /// out what this class did to their archive.
    /// </summary>
    private const ushort MethodStored = 0;

    private static void WriteLocalHeader(Stream file, byte[] nameBytes, byte[] content)
    {
        var (time, date) = DosDateTime(DateTime.Now);
        var header = new byte[30];

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), LocalHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 20);            // needs ZIP 2.0
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 0);             // no flags
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), MethodStored);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), time);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), date);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), Crc32(content));
        // The guide is small, so its own sizes never need ZIP64 — only its offset can, and that is
        // recorded in the central record rather than here.
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), (uint)content.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), (uint)content.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), 0);            // no extra field

        file.Write(header);
        file.Write(nameBytes);
    }

    private static byte[] BuildCentralRecord(byte[] nameBytes, byte[] content, long entryOffset)
    {
        // Past 4 GB the offset cannot be expressed in the base record, so it moves into a ZIP64 extra
        // field and the base field is saturated to say so. Game archives reach this routinely, and
        // getting it wrong would corrupt large archives only — invisible in any small-file test.
        var needsZip64 = entryOffset > Max32;
        var extraLength = needsZip64 ? 12 : 0;

        var record = new byte[46 + nameBytes.Length + extraLength];
        var (time, date) = DosDateTime(DateTime.Now);

        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0), CentralHeaderSignature);
        // Host 0 (MS-DOS) so the external attributes below are read as DOS attributes.
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), (ushort)(needsZip64 ? 45 : 20));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)(needsZip64 ? 45 : 20));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8), 0);             // no flags
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(10), MethodStored);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), time);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), date);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), Crc32(content));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), (uint)content.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)content.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(28), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(30), (ushort)extraLength);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(32), 0);            // no comment
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(34), 0);            // first disk
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(36), 0);            // binary content
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(38), 0x20);         // DOS "archive"
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(42),
            needsZip64 ? Max32 : (uint)entryOffset);

        nameBytes.CopyTo(record.AsSpan(46));

        if (needsZip64)
        {
            // Readers work out which ZIP64 fields are present by checking which base fields are
            // saturated, in a fixed order: uncompressed size, compressed size, offset, disk. Only the
            // offset is saturated above, so only the offset may appear here — writing the sizes too
            // would misalign the whole field for anything reading it to spec.
            var extra = record.AsSpan(46 + nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(extra, 0x0001);               // ZIP64 extra id
            BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 8);               // bytes that follow
            BinaryPrimitives.WriteInt64LittleEndian(extra[4..], entryOffset);
        }

        return record;
    }

    private static void WriteEndOfCentralDirectory(
        Stream file, int entryCount, long directorySize, long directoryOffset, long prefix)
    {
        // Any one of these overflowing forces the ZIP64 pair. Note that an archive can need it for the
        // entry count alone while being well under 4 GB.
        var needsZip64 = entryCount > Max16 || directorySize > Max32 || directoryOffset > Max32;

        if (needsZip64)
        {
            var zip64EocdPosition = file.Position - prefix;

            var record = new byte[Zip64EocdLength];
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0), Zip64EocdSignature);
            // The size of this record, not counting the signature and this field itself.
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(4), Zip64EocdLength - 12);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), 45);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), 45);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), 0);        // this disk
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), 0);        // disk with index
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), entryCount);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(32), entryCount);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(40), directorySize);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(48), directoryOffset);
            file.Write(record);

            var locator = new byte[Zip64LocatorLength];
            BinaryPrimitives.WriteUInt32LittleEndian(locator.AsSpan(0), Zip64LocatorSignature);
            BinaryPrimitives.WriteUInt32LittleEndian(locator.AsSpan(4), 0);        // disk with record
            BinaryPrimitives.WriteInt64LittleEndian(locator.AsSpan(8), zip64EocdPosition);
            BinaryPrimitives.WriteUInt32LittleEndian(locator.AsSpan(16), 1);       // total disks
            file.Write(locator);
        }

        var eocd = new byte[EocdMinimumLength];
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(0), EocdSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(4), 0);               // this disk
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(6), 0);               // disk with index
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(8),
            entryCount > Max16 ? Max16 : (ushort)entryCount);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(10),
            entryCount > Max16 ? Max16 : (ushort)entryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(12),
            directorySize > Max32 ? Max32 : (uint)directorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(16),
            directoryOffset > Max32 ? Max32 : (uint)directoryOffset);
        // No comment. A reader scanning backwards stops at the first signature whose comment length
        // reaches exactly the end of the file, so leaving this at zero is what makes the new EOCD the
        // one that is found rather than the old one further up.
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(20), 0);

        file.Write(eocd);
    }

    // ------------------------------------------------------------------ odds and ends

    /// <summary>
    /// ZIP timestamps are MS-DOS: two-second resolution, and no year before 1980.
    /// </summary>
    private static (ushort Time, ushort Date) DosDateTime(DateTime value)
    {
        if (value.Year < 1980) value = new DateTime(1980, 1, 1);

        var date = (ushort)(((value.Year - 1980) << 9) | (value.Month << 5) | value.Day);
        var time = (ushort)((value.Hour << 11) | (value.Minute << 5) | (value.Second / 2));
        return (time, date);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        // The standard reflected CRC-32 used by ZIP. Twenty lines rather than a package reference:
        // this is the only hashing the appliance needs beyond what the BCL already provides, and a
        // self-contained release is the point of the whole deployment story.
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
