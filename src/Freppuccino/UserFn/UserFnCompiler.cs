using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Freppuccino.UserFn;

public static partial class UserFnCompiler
{
    const int CacheLimit = 512;
    static readonly ConcurrentDictionary<string, UserFnResult> cache = new();

    public static partial UserFnResult Compile(string source)
    {
        source ??= "";
        if (cache.TryGetValue(source, out var hit)) return hit;
        var r = CompileUncached(source);
        if (cache.Count >= CacheLimit) cache.Clear(); // typing produces many one-off sources
        return cache.GetOrAdd(source, r);
    }

    static UserFnResult CompileUncached(string source)
    {
        var diags = new List<UserFnDiagnostic>();
        try
        {
            var toks = Lexer.Lex(source, diags);
            var prog = Parser.Parse(toks, diags);
            Checker.Check(prog, diags);
            if (diags.Count > 0) return Fail(diags);
            var cpu = CpuEmitter.Build(prog);
            var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(GlslEmitter.Emit(prog, "F"))))[..16];
            var program = new UserFnProgram
            {
                Source = source,
                Hash = hash,
                Params = [.. prog.Params.Select(p => new UserFnParam(p.Name, p.Default))],
                Cpu = cpu,
                Glsl = name => GlslEmitter.Emit(prog, name),
            };
            return new UserFnResult(program, []);
        }
        catch (Exception ex)
        {
            diags.Add(new(1, 1, $"internal compiler error: {ex.Message}"));
            return Fail(diags);
        }
    }

    static UserFnResult Fail(List<UserFnDiagnostic> diags) =>
        new(null, [.. diags.Distinct().OrderBy(d => d.Line).ThenBy(d => d.Column)]);
}
