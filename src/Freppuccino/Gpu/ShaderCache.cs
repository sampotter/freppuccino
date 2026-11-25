using System.IO.Compression;
using Rhino.FileIO;

namespace Freppuccino.Gpu;

public static class ShaderCache
{
    const int Major = 1, Minor = 0;
    const string Format = "d3d11";
    const int MaxEntries = 8;
    const int MaxBytes = 16 << 20;
    static readonly string Build = typeof(ShaderCache).Assembly.ManifestModule.ModuleVersionId.ToString("N");
    static readonly Dictionary<string, (byte[] Vs, byte[] Ps)> entries = [];
    static readonly List<string> recent = [];

    internal static void Add(string hash, byte[] vs, byte[] ps)
    {
        lock (entries)
        {
            entries[hash] = (vs, ps);
            Touch(hash);
            while (recent.Count > MaxEntries)
            {
                entries.Remove(recent[0]);
                recent.RemoveAt(0);
            }
        }
    }

    internal static bool TryGet(string hash, out (byte[] Vs, byte[] Ps) code)
    {
        lock (entries)
        {
            if (!entries.TryGetValue(hash, out code)) return false;
            Touch(hash);
            return true;
        }
    }

    internal static void Drop(string hash)
    {
        lock (entries)
        {
            entries.Remove(hash);
            recent.Remove(hash);
        }
    }

    static void Touch(string hash)
    {
        recent.Remove(hash);
        recent.Add(hash);
    }

    public static bool Has(string? hash) => hash != null && TryGet(hash, out _);

    public static void Write(BinaryArchiveWriter w, string? hash)
    {
        w.Write3dmChunkVersion(Major, Minor);
        w.WriteString(Format);
        w.WriteString(Build);
        if (hash == null || !TryGet(hash, out var code))
        {
            w.WriteInt(0);
            return;
        }
        w.WriteInt(1);
        w.WriteString(hash);
        w.WriteByteArray(Pack(code.Vs));
        w.WriteByteArray(Pack(code.Ps));
    }

    public static void Read(BinaryArchiveReader r)
    {
        r.Read3dmChunkVersion(out var major, out _);
        if (major != Major || r.ReadString() != Format || r.ReadString() != Build) return;
        int count = r.ReadInt();
        for (int i = 0; i < count; i++)
        {
            var hash = r.ReadString();
            var vs = Unpack(r.ReadByteArray());
            var ps = Unpack(r.ReadByteArray());
            if (vs.Length > 0 && ps.Length > 0) Add(hash, vs, ps);
        }
    }

    static byte[] Pack(byte[] data)
    {
        var m = new MemoryStream();
        using (var d = new DeflateStream(m, CompressionLevel.Optimal)) d.Write(data);
        return m.ToArray();
    }

    static byte[] Unpack(byte[] data)
    {
        using var d = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        var m = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = d.Read(buffer)) > 0)
        {
            if (m.Length + n > MaxBytes) throw new InvalidDataException("stored shader is too large");
            m.Write(buffer, 0, n);
        }
        return m.ToArray();
    }
}
