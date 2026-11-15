using System.Globalization;
using System.Text;
using Freppuccino.UserFn.Runtime;

namespace Freppuccino.UserFn;

/// Checked AST -> GLSL 450. Mangling: entry -> {name}, helpers -> {name}_{helper}, locals -> l_{id},
/// params -> u{index} (extra trailing args on every function), consts folded to literals at each use.
internal sealed class GlslEmitter
{
    readonly StringBuilder sb = new();
    readonly string name;
    readonly int nParams;
    int indent;

    GlslEmitter(string name, int nParams) { this.name = name; this.nParams = nParams; }

    public static string Emit(ProgramAst prog, string name)
    {
        var g = new GlslEmitter(name, prog.Params.Count);
        foreach (var f in prog.Funcs) g.Func(f);
        return g.sb.ToString();
    }

    string FuncName(FuncDecl f) => f.IsEntry ? name : name + "_" + f.Name;
    static string Local(string id) => "l_" + id;

    void Line(string s) { sb.Append(' ', indent * 4).Append(s).Append('\n'); }

    void Func(FuncDecl f)
    {
        var ps = f.Params.Select(p => $"{Tys.Name(p.Type)} {Local(p.Name)}")
            .Concat(Enumerable.Range(0, nParams).Select(i => $"float u{i}"));
        Line($"{Tys.Name(f.Ret)} {FuncName(f)}({string.Join(", ", ps)})");
        Block(f.Body);
        sb.Append('\n');
    }

    void Block(BlockStmt b)
    {
        Line("{");
        indent++;
        foreach (var s in b.Body) St(s);
        indent--;
        Line("}");
    }

    void Sub(Stmt s)
    {
        if (s is BlockStmt b) { Block(b); return; }
        Line("{"); indent++; St(s); indent--; Line("}");
    }

    void St(Stmt s)
    {
        switch (s)
        {
            case BlockStmt b: Block(b); break;
            case DeclStmt d:
                foreach (var v in d.Vars)
                {
                    if (v.Sym!.Kind == SymKind.Const) continue;
                    Line($"{Tys.Name(d.Type)} {Local(v.Name)} = {(v.Init != null ? Ex(v.Init) : Zero(d.Type))};");
                }
                break;
            case AssignStmt a: Line($"{Ex(a.Target)} {a.Op} {Ex(a.Value)};"); break;
            case IncDecStmt i: Line($"{Ex(i.Target)}{(i.Inc ? "++" : "--")};"); break;
            case ExprStmt e: Line($"{Ex(e.E)};"); break;
            case IfStmt i:
                Line($"if ({Strip(Ex(i.C))})");
                Sub(i.Then);
                if (i.Else != null) { Line("else"); Sub(i.Else); }
                break;
            case ForStmt f:
            {
                var v = Local(f.Var!.Name);
                Line($"for (int {v} = {Int(f.Start)}; {v} {f.CmpOp} {Int(f.Bound)}; {v} += {Int(f.Step)})");
                Sub(f.Body);
                break;
            }
            case ReturnStmt r: Line($"return {Strip(Ex(r.E!))};"); break;
            case BreakStmt: Line("break;"); break;
            case ContinueStmt: Line("continue;"); break;
            default: throw new InvalidOperationException($"unsupported statement {s.GetType().Name}");
        }
    }

    static string Strip(string s) => s.Length > 1 && s[0] == '(' && s[^1] == ')' && Balanced(s[1..^1]) ? s[1..^1] : s;
    static bool Balanced(string s)
    {
        int d = 0;
        foreach (var c in s) { if (c == '(') d++; else if (c == ')' && --d < 0) return false; }
        return d == 0;
    }

    string Args(IEnumerable<Expr> args) => string.Join(", ", args.Select(a => Strip(Ex(a))));

    /// Every compound expression comes back parenthesized, so precedence never depends on GLSL's rules.
    string Ex(Expr e)
    {
        switch (e)
        {
            case LitExpr l: return Lit(l.Value);
            case ConstExpr c: return Lit(c.Value);
            case NameExpr n: return n.Sym!.Kind == SymKind.Param ? "u" + n.Sym.ParamIndex : Local(n.Id);
            case ConvExpr cv:
                return cv.E is LitExpr { Value: int i } ? Float(i) : cv.E is ConstExpr { Value: int ci } ? Float(ci) : $"float({Strip(Ex(cv.E))})";
            case UnaryExpr u: return u.Op == "+" ? Ex(u.E) : $"({u.Op}{Ex(u.E)})";
            case BinaryExpr b: return $"({Ex(b.L)} {b.Op} {Ex(b.R)})";
            case CondExpr c: return $"({Ex(c.C)} ? {Ex(c.A)} : {Ex(c.B)})";
            case CallExpr c:
                if (c.User != null)
                    return $"{FuncName(c.User)}({string.Join(", ", c.Args.Select(a => Strip(Ex(a))).Concat(Enumerable.Range(0, nParams).Select(i => "u" + i)))})";
                return $"{c.Name}({Args(c.Args)})"; // builtin, library, constructor: same names in GLSL
            case SwizzleExpr s: return $"{Ex(s.E)}.{new string(s.Idx.Select(i => "xyzw"[i]).ToArray())}";
            case IndexExpr ix: return $"{Ex(ix.E)}[{Strip(Ex(ix.I))}]";
        }
        throw new InvalidOperationException($"unsupported expression {e.GetType().Name}");
    }

    static string Zero(Ty t) => t switch
    {
        Ty.Bool => "false", Ty.Int => "0", Ty.Float => "0.0", _ => $"{Tys.Name(t)}(0.0)",
    };

    static string Int(int i) => i < 0 ? (i == int.MinValue ? "(-2147483647 - 1)" : $"({i.ToString(CultureInfo.InvariantCulture)})") : i.ToString(CultureInfo.InvariantCulture);

    internal static string Float(double d)
    {
        if (double.IsNaN(d)) return "uintBitsToFloat(0x7fc00000u)";
        if (double.IsInfinity(d) || Math.Abs(d) > float.MaxValue) return d > 0 ? "uintBitsToFloat(0x7f800000u)" : "uintBitsToFloat(0xff800000u)";
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        if (s.IndexOfAny(['.', 'E', 'e']) < 0) s += ".0";
        return d < 0 || (d == 0 && double.IsNegative(d)) ? $"({(s[0] == '-' ? s : "-" + s)})" : s;
    }

    static string Lit(object v) => v switch
    {
        double d => Float(d),
        int i => Int(i),
        bool b => b ? "true" : "false",
        Vec2 a => $"vec2({Float(a.X)}, {Float(a.Y)})",
        Vec3 a => $"vec3({Float(a.X)}, {Float(a.Y)}, {Float(a.Z)})",
        Vec4 a => $"vec4({Float(a.X)}, {Float(a.Y)}, {Float(a.Z)}, {Float(a.W)})",
        Mat2 m => $"mat2({Float(m.C0.X)}, {Float(m.C0.Y)}, {Float(m.C1.X)}, {Float(m.C1.Y)})",
        Mat3 m => $"mat3({string.Join(", ", new[] { m.C0, m.C1, m.C2 }.SelectMany(c => new[] { c.X, c.Y, c.Z }).Select(Float))})",
        _ => throw new InvalidOperationException($"bad constant {v}"),
    };
}
