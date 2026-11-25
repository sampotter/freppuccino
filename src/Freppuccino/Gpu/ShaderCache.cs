using System.IO.Compression;
using Rhino.FileIO;

namespace Freppuccino.Gpu;

public static class ShaderCache
{
    const int Major = 1, Minor = 0;
    const string Format = "d3d11";
    static readonly Dictionary<string, (byte[] Vs, byte[] Ps)> entries = [];

    internal static void Add(string hash, byte[] vs, byte[] ps)
    {
        lock (entries) entries[hash] = (vs, ps);
    }

    internal static bool TryGet(string hash, out (byte[] Vs, byte[] Ps) code)
    {
        lock (entries) return entries.TryGetValue(hash, out code);
    }

    internal static void Drop(string hash)
    {
        lock (entries) entries.Remove(hash);
    }

    public static bool Has(string? hash) => hash != null && TryGet(hash, out _);

    public static void Write(BinaryArchiveWriter w, string? hash)
    {
        w.Write3dmChunkVersion(Major, Minor);
        w.WriteString(Format);
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
        if (major != Major || r.ReadString() != Format) return;
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
        d.CopyTo(m);
        return m.ToArray();
    }
}
