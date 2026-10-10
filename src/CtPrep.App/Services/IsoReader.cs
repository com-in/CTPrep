using System.Text;

namespace CtPrep.App.Services;

/// <summary>
/// 不挂载、直接从 ISO 里读出文件。
/// Windows 7 既没有 Mount-DiskImage 也没有 Storage 模块，挂载这条路走不通，
/// 所以自带一个只读解析器：先按 UDF 解（Windows 10/11 的 install.wim 超过 4GB，
/// 只存在于 UDF 里），失败再退回 ISO 9660。
///
/// 只实现「按路径找到一个文件并复制出来」所需的最小集合，不做写入、不做全盘遍历。
/// </summary>
public sealed class IsoReader : IDisposable
{
    private const int SectorSize = 2048;

    private readonly Stream _stream;
    private uint _partitionStart;
    private uint _fsdLba;

    // ISO 9660 回退用
    private uint _isoRootLba;
    private uint _isoRootSize;

    private IsoReader(Stream stream) => _stream = stream;

    /// <summary>实际采用的解析方式（UDF / ISO9660），仅用于日志与排错。</summary>
    public string Mode { get; private set; } = string.Empty;

    /// <summary>打开 ISO 并初始化解析（UDF 优先，其次 ISO 9660）。</summary>
    public static bool TryOpen(string isoPath, out IsoReader? reader, out string? error)
    {
        reader = null;
        error = null;
        Stream? stream = null;
        try
        {
            stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                SectorSize * 256, FileOptions.SequentialScan);
            var instance = new IsoReader(stream);
            if (instance.TryInitUdf())
            {
                instance.Mode = "UDF";
                reader = instance;
                return true;
            }

            if (instance.TryInitIso9660())
            {
                instance.Mode = "ISO9660";
                reader = instance;
                return true;
            }

            error = "neither UDF nor ISO 9660 structures were found";
            instance.Dispose();
            return false;
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            error = ex.Message;
            return false;
        }
    }

    // ---------------------------------------------------------------- UDF

    private bool TryInitUdf()
    {
        // Anchor Volume Descriptor Pointer 固定在 LBA 256（也可能在末尾 -256，先试前面）
        if (!TryReadSector(256, out var avdp) || U16(avdp, 0) != 2)
        {
            return false;
        }

        uint vdsLength = U32(avdp, 16);
        uint vdsLba = U32(avdp, 20);
        if (vdsLba == 0 || vdsLength == 0 || vdsLength > 1024 * 1024)
        {
            return false;
        }

        // 注意：ReadBytes 收的是字节偏移，vdsLba 是块号
        var vds = ReadBytes(vdsLba * SectorSize, vdsLength);
        uint? partitionStart = null;
        uint? fsdLba = null;

        // 卷描述符序列里的描述符基本都是 512 字节对齐的，按 512 步进扫即可，
        // 遇到终止描述符（tag id 8）停下。
        for (uint offset = 0; offset + 16 <= vdsLength; offset += 512)
        {
            ushort id = U16(vds, offset);
            if (id == 8)
            {
                break;
            }

            if (id == 5 && offset + 192 <= vdsLength)
            {
                // Partition Descriptor: 分区起始 LBA
                partitionStart = U32(vds, offset + 188);
            }
            else if (id == 6 && offset + 256 <= vdsLength)
            {
                // Logical Volume Descriptor: 内容区（File Set Descriptor）的位置
                fsdLba = U32(vds, offset + 252);
            }
        }

        if (partitionStart is null || fsdLba is null)
        {
            return false;
        }

        _partitionStart = partitionStart.Value;
        _fsdLba = fsdLba.Value;
        return true;
    }

    /// <summary>在 UDF 里查找 path（如 "sources/install.wim"），返回物理偏移与字节数。</summary>
    private bool TryLocateUdf(string[] parts, out long offset, out long length)
    {
        offset = 0;
        length = 0;

        // File Set Descriptor -> 根目录 ICB
        var fsd = ReadBytes(Physical(_fsdLba), 512);
        if (U16(fsd, 0) != 256)
        {
            return false;
        }

        uint current = U32(fsd, 404); // Root Directory ICB 的 LBA（相对分区）

        // 逐级往下走：每一级都读出「当前目录」的内容，在 File Identifier Descriptor
        // 列表里找到下一级的 ICB 编号。循环结束时 current 才是最终目标（文件）的条目，
        // 这时才去读它的数据区。
        foreach (var part in parts)
        {
            if (!TryReadEntryData(current, out long dirLba, out long dirLength))
            {
                return false;
            }

            uint? child = null;
            var dir = ReadBytes(dirLba, dirLength);
            uint pos = 0;
            while (pos + 38 <= dir.Length)
            {
                if (U16(dir, pos) != 257)
                {
                    break;
                }

                byte nameLength = dir[pos + 19];
                uint icbLba = U32(dir, pos + 24);
                ushort useLength = U16(dir, pos + 36);
                uint nameAt = pos + 38 + useLength;
                if (nameAt + nameLength > dir.Length)
                {
                    break;
                }

                if (ReadDString(dir, nameAt, nameLength).Equals(part, StringComparison.OrdinalIgnoreCase))
                {
                    child = icbLba;
                    break;
                }

                uint total = 38u + useLength + nameLength;
                uint padded = (total + 3u) & ~3u;
                if (padded <= total)
                {
                    break;
                }

                pos += padded;
            }

            if (child is null)
            {
                return false;
            }

            current = child.Value;
        }

        // current 已经是目标文件本身，读它的数据区
        return TryReadEntryData(current, out offset, out length);
    }

    /// <summary>读出相对分区 LBA 处那个条目的数据区（物理 LBA 与字节数）。</summary>
    private bool TryReadEntryData(uint lbaWithinPartition, out long offset, out long length)
    {
        offset = 0;
        length = 0;
        var entry = ReadBytes(Physical(lbaWithinPartition), 2048);
        ushort tag = U16(entry, 0);
        if (tag != 261 && tag != 266)
        {
            return false;
        }

        return TryGetEntryData(entry, out offset, out length);
    }

    /// <summary>从 File Entry 里取出数据区（首块的物理 LBA 与字节数，支持多段）。</summary>
    private bool TryGetEntryData(byte[] entry, out long lba, out long length)
    {
        lba = 0;
        length = 0;
        uint eaLength = U32(entry, 168);
        uint adLength = U32(entry, 172);
        long informationLength = (long)U64(entry, 56);
        int adStart = 176 + (int)eaLength;
        if (adStart < 0 || adStart + adLength > entry.Length || adLength == 0)
        {
            return false;
        }

        // 先按 short_ad（8 字节）解；解出来不合理再按 long_ad（16 字节）试一次。
        if (TrySumExtents(entry, adStart, adLength, 8, informationLength, out lba, out length))
        {
            return true;
        }

        return TrySumExtents(entry, adStart, adLength, 16, informationLength, out lba, out length);
    }

    private bool TrySumExtents(byte[] entry, int start, uint total, int stride, long expected,
        out long lba, out long length)
    {
        lba = 0;
        length = 0;
        if (total % (uint)stride != 0)
        {
            return false;
        }

        bool first = true;
        long sum = 0;
        for (uint offset = 0; offset + (uint)stride <= total; offset += (uint)stride)
        {
            uint raw = U32(entry, start + offset);
            uint extentType = raw >> 30;
            uint extentLength = raw & 0x3FFFFFFF;
            if (extentType is 2 or 3)
            {
                continue; // 未记录 / 描述符续接
            }

            uint block = U32(entry, start + offset + 4);
            if (first)
            {
                lba = Physical(block);
                first = false;
            }

            sum += extentLength;
        }

        // 注意：short_ad / long_ad 的 extentLength 已经是「字节」，不是块数
        // （实测：根目录 infoLength=548 字节，对应的 extentLength 就是 548）。
        long bytes = sum;
        if (bytes <= 0)
        {
            return false;
        }

        if (expected > 0 && bytes < expected)
        {
            return false;
        }

        length = expected > 0 ? Math.Min(bytes, expected) : bytes;
        return true;
    }

    // ------------------------------------------------------------ ISO 9660

    private bool TryInitIso9660()
    {
        if (!TryReadSector(16, out var pvd))
        {
            return false;
        }

        if (pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001")
        {
            return false;
        }

        uint blockSize = U16(pvd, 128);
        if (blockSize != SectorSize)
        {
            return false;
        }

        // 根目录记录（Directory Record）位于 PVD 偏移 156
        _isoRootLba = U32(pvd, 156 + 2);
        _isoRootSize = U32(pvd, 156 + 10);
        return _isoRootLba != 0 && _isoRootSize != 0;
    }

    private bool TryLocateIso9660(string[] parts, out long offset, out long length)
    {
        offset = 0;
        length = 0;
        uint lba = _isoRootLba;
        uint size = _isoRootSize;

        for (int i = 0; i < parts.Length; i++)
        {
            var dir = ReadBytes(lba * SectorSize, size);
            bool isLast = i == parts.Length - 1;
            bool found = false;
            uint pos = 0;

            while (pos + 33 < dir.Length)
            {
                byte recordLength = dir[pos];
                if (recordLength == 0)
                {
                    // 目录记录不跨扇区，遇到 0 就跳到下一个扇区
                    pos = ((pos / SectorSize) + 1) * SectorSize;
                    continue;
                }

                uint extent = U32(dir, pos + 2);
                uint dataLength = U32(dir, pos + 10);
                byte flags = dir[pos + 25];
                byte nameLength = dir[pos + 32];
                string name = Encoding.ASCII.GetString(dir, (int)pos + 33, nameLength);

                // ISO 9660 的文件名形如 "INSTALL.WIM;1"
                int semi = name.IndexOf(';');
                if (semi >= 0)
                {
                    name = name[..semi];
                }

                if (name.Equals(parts[i], StringComparison.OrdinalIgnoreCase))
                {
                    if (isLast)
                    {
                        offset = extent * SectorSize;
                        length = dataLength;
                        return true;
                    }

                    if ((flags & 0x02) != 0)
                    {
                        lba = extent;
                        size = dataLength;
                        found = true;
                        break;
                    }
                }

                pos += recordLength;
            }

            if (!found)
            {
                return false;
            }
        }

        return false;
    }

    // -------------------------------------------------------------- 对外

    /// <summary>
    /// 在 ISO 里依次尝试若干路径（如 sources/install.wim、sources/install.esd），
    /// 返回第一个命中的名字与它的物理偏移、字节数。
    /// </summary>
    public bool TryLocateFirst(string[] candidatePaths, out string matched, out long offset, out long length)
    {
        foreach (var path in candidatePaths)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            if (TryLocateUdf(parts, out offset, out length) ||
                TryLocateIso9660(parts, out offset, out length))
            {
                matched = string.Join('/', parts);
                return true;
            }
        }

        matched = string.Empty;
        offset = 0;
        length = 0;
        return false;
    }

    /// <summary>把 ISO 里从 offset 开始的 length 字节写到目标文件。</summary>
    public async Task CopyOutAsync(long offset, long length, string destination,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        _stream.Position = offset;

        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
            FileShare.None, 1024 * 1024, FileOptions.SequentialScan);

        var buffer = new byte[1024 * 1024];
        long remaining = length;
        long written = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = await _stream.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new IOException($"ISO 数据提前结束：还差 {remaining} 字节（已写 {written}）");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            remaining -= read;
            written += read;
            var seconds = watch.Elapsed.TotalSeconds;
            progress?.Report(new DownloadProgress(written, length, seconds > 0 ? written / seconds : 0));
        }
    }

    // -------------------------------------------------------------- 工具

    private long Physical(uint lbaWithinPartition) => (_partitionStart + lbaWithinPartition) * SectorSize;

    private bool TryReadSector(long lba, out byte[] sector)
    {
        sector = new byte[SectorSize];
        if (lba < 0 || lba * SectorSize >= _stream.Length)
        {
            return false;
        }

        _stream.Position = lba * SectorSize;
        int read = _stream.Read(sector, 0, SectorSize);
        return read == SectorSize;
    }

    private byte[] ReadBytes(long byteOffset, long count)
    {
        if (byteOffset < 0 || count <= 0)
        {
            return Array.Empty<byte>();
        }

        count = Math.Min(count, _stream.Length - byteOffset);
        if (count <= 0)
        {
            return Array.Empty<byte>();
        }

        var buffer = new byte[count];
        _stream.Position = byteOffset;
        int done = 0;
        while (done < count)
        {
            int read = _stream.Read(buffer, done, (int)(count - done));
            if (read <= 0)
            {
                break;
            }

            done += read;
        }

        return buffer;
    }

    private static ushort U16(byte[] buffer, long offset) =>
        (ushort)(buffer[offset] | (buffer[offset + 1] << 8));

    private static uint U32(byte[] buffer, long offset) =>
        (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) |
               (buffer[offset + 3] << 24));

    private static ulong U64(byte[] buffer, long offset) =>
        U32(buffer, offset) | ((ulong)U32(buffer, offset + 4) << 32);

    /// <summary>UDF 的 dstring：首字节是压缩标识（8=单字节字符，16=UCS-2BE）。</summary>
    private static string ReadDString(byte[] buffer, long offset, int length)
    {
        if (length <= 1)
        {
            return string.Empty;
        }

        int compression = buffer[offset];
        int count = length - 1;
        if (compression == 16)
        {
            var sb = new StringBuilder();
            for (int i = 0; i + 1 < count; i += 2)
            {
                sb.Append((char)((buffer[offset + 1 + i] << 8) | buffer[offset + 2 + i]));
            }

            return sb.ToString().TrimEnd('\0');
        }

        return Encoding.ASCII.GetString(buffer, (int)offset + 1, count).TrimEnd('\0', ' ');
    }

    public void Dispose() => _stream.Dispose();
}
