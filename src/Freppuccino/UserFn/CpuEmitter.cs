using System.Linq.Expressions;
using Freppuccino.UserFn.Runtime;
using E = System.Linq.Expressions.Expression;

namespace Freppuccino.UserFn;

/// Checked AST -> System.Linq.Expressions -> delegates. Each helper compiles to its own delegate (called through
/// a constant), taking its GLSL parameters plus the param array. Compiled code only touches locals: thread-safe.
internal sealed class CpuEmitter
{
    static readonly string[] F = ["X", "Y", "Z", "W"];

    readonly Dictionary<Sym, ParameterExpression> vars = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<FuncDecl, Delegate> compiled;
    readonly ParameterExpression? u;
    readonly LabelTarget? ret;
    readonly Stack<(LabelTarget brk, LabelTarget cont)> loops = new();
    List<ParameterExpression> blockVars = [];

    CpuEmitter(Dictionary<FuncDecl, Delegate> compiled, ParameterExpression? u, LabelTarget? ret)
    {
        this.compiled = compiled;
        this.u = u;
        this.ret = ret;
    }

    /// Evaluates a checked constant expression (only literals / consts / builtins). Returns double/int/bool/VecN/MatN.
    public static object EvalConst(Expr e)
    {
        var body = new CpuEmitter([], null, null).Ex(e);
        return E.Lambda<Func<object>>(E.Convert(body, typeof(object))).Compile(preferInterpretation: true)();
    }

    public static UserFnEval Build(ProgramAst prog)
    {
        var compiled = new Dictionary<FuncDecl, Delegate>(ReferenceEqualityComparer.Instance);
        UserFnEval? entry = null;
        foreach (var f in prog.Funcs)
        {
            var u = E.Parameter(typeof(double[]), "u");
            var ret = E.Label(Tys.Clr(f.Ret), "ret");
            var em = new CpuEmitter(compiled, u, ret);
            if (f.IsEntry)
            {
                var x = E.Parameter(typeof(double), "x");
                var y = E.Parameter(typeof(double), "y");
                var z = E.Parameter(typeof(double), "z");
                var p = E.Variable(typeof(Vec3), f.Params[0].Name);
                em.vars[f.Params[0].Sym!] = p;
                var body = em.FuncBody(f, E.Assign(p, E.New(typeof(Vec3).GetConstructor([typeof(double), typeof(double), typeof(double)])!, x, y, z)));
                entry ??= E.Lambda<UserFnEval>(E.Block([p], body), f.Name, [x, y, z, u]).Compile();
            }
            else
            {
                var ps = f.Params.Select(fp => em.vars[fp.Sym!] = E.Parameter(Tys.Clr(fp.Type), fp.Name)).ToList();
                ps.Add(u);
                compiled[f] = E.Lambda(em.FuncBody(f, null), f.Name, ps).Compile();
            }
        }
        if (entry == null) throw new InvalidOperationException("no entry point");
        int n = prog.Params.Count;
        if (n == 0) return entry;
        var defaults = prog.Params.Select(p => p.Default).ToArray();
        var inner = entry;
        return (x, y, z, vals) => inner(x, y, z, vals != null && vals.Length >= n ? vals : Pad(vals, defaults));
    }

    static double[] Pad(double[]? vals, double[] defaults)
    {
        var r = (double[])defaults.Clone();
        if (vals != null) Array.Copy(vals, r, Math.Min(vals.Length, r.Length));
        return r;
    }

    E FuncBody(FuncDecl f, E? prologue)
    {
        var list = new List<E>();
        if (prologue != null) list.Add(prologue);
        blockVars = [];
        foreach (var s in f.Body.Body) list.Add(St(s));
        list.Add(E.Label(ret!, E.Default(Tys.Clr(f.Ret))));
        return E.Block(Tys.Clr(f.Ret), blockVars, list);
    }

    // ---------------- statements ----------------
    E Scoped(Stmt s)
    {
        var saved = blockVars;
        blockVars = [];
        var body = s is BlockStmt b ? b.Body.Select(St).ToList() : [St(s)];
        var r = E.Block(typeof(void), blockVars, body.Count == 0 ? [E.Empty()] : body);
        blockVars = saved;
        return r;
    }

    E St(Stmt s)
    {
        switch (s)
        {
            case BlockStmt: return Scoped(s);
            case DeclStmt d:
            {
                var list = new List<E>();
                foreach (var v in d.Vars)
                {
                    if (v.Sym!.Kind == SymKind.Const) continue; // folded at every use
                    var t = Tys.Clr(d.Type);
                    var init = v.Init != null ? Ex(v.Init) : E.Default(t);
                    var pv = E.Variable(t, v.Name);
                    // assign after evaluating init: `float x = x + 1.0;` reads the outer x
                    var tmp = E.Variable(t);
                    list.Add(E.Block([tmp], E.Assign(tmp, init), E.Assign(pv, tmp)));
                    vars[v.Sym] = pv;
                    blockVars.Add(pv);
                }
                return list.Count == 0 ? E.Empty() : E.Block(typeof(void), list);
            }
            case AssignStmt a:
            {
                var value = Ex(a.Value);
                if (a.Op != "=") value = Arith(a.Op[..1], Ex(a.Target), a.Target.Type, value, a.Value.Type);
                return Store(a.Target, value);
            }
            case IncDecStmt i:
            {
                var t = i.Target.Type;
                E one = t == Ty.Int ? E.Constant(1) : E.Constant(1.0);
                var cur = Ex(i.Target);
                return Store(i.Target, i.Inc ? E.Add(cur, one) : E.Subtract(cur, one));
            }
            case ExprStmt es: return E.Block(typeof(void), Ex(es.E));
            case IfStmt ifs:
                return ifs.Else == null ? E.IfThen(Ex(ifs.C), Scoped(ifs.Then)) : E.IfThenElse(Ex(ifs.C), Scoped(ifs.Then), Scoped(ifs.Else));
            case ForStmt f:
            {
                var i = E.Variable(typeof(int), f.Var!.Name);
                vars[f.Var] = i;
                var brk = E.Label("brk");
                var cont = E.Label("cont");
                E bound = E.Constant(f.Bound);
                E cond = f.CmpOp switch
                {
                    "<" => E.LessThan(i, bound), "<=" => E.LessThanOrEqual(i, bound), ">" => E.GreaterThan(i, bound),
                    ">=" => E.GreaterThanOrEqual(i, bound), _ => E.NotEqual(i, bound),
                };
                loops.Push((brk, cont));
                var body = Scoped(f.Body);
                loops.Pop();
                return E.Block([i],
                    E.Assign(i, E.Constant(f.Start)),
                    E.Loop(E.Block(
                        E.IfThen(E.Not(cond), E.Break(brk)),
                        body,
                        E.Label(cont),
                        E.Assign(i, E.Add(i, E.Constant(f.Step)))), brk));
            }
            case ReturnStmt r: return E.Return(ret!, Ex(r.E!));
            case BreakStmt: return E.Break(loops.Peek().brk);
            case ContinueStmt: return E.Continue(loops.Peek().cont);
        }
        throw new InvalidOperationException($"unsupported statement {s.GetType().Name}");
    }

    E Store(Expr target, E value)
    {
        if (target is NameExpr n) return E.Assign(vars[n.Sym!], value);
        var sw = (SwizzleExpr)target;
        var v = vars[((NameExpr)sw.E).Sym!];
        if (sw.Idx.Length == 1) return E.Assign(E.Field(v, F[sw.Idx[0]]), value);
        var tmp = E.Variable(value.Type);
        var list = new List<E> { E.Assign(tmp, value) };
        for (int k = 0; k < sw.Idx.Length; k++) list.Add(E.Assign(E.Field(v, F[sw.Idx[k]]), E.Field(tmp, F[k])));
        return E.Block(typeof(void), [tmp], list);
    }

    // ---------------- expressions ----------------
    E Ex(Expr e)
    {
        switch (e)
        {
            case LitExpr l: return E.Constant(l.Value, Tys.Clr(l.Type));
            case ConstExpr c: return E.Constant(c.Value, Tys.Clr(c.Type));
            case NameExpr n:
                return n.Sym!.Kind == SymKind.Param ? E.ArrayIndex(u!, E.Constant(n.Sym.ParamIndex)) : vars[n.Sym];
            case ConvExpr cv: return E.Convert(Ex(cv.E), typeof(double));
            case UnaryExpr un:
                return un.Op switch { "-" => E.Negate(Ex(un.E)), "!" => E.Not(Ex(un.E)), _ => Ex(un.E) };
            case BinaryExpr b:
            {
                var l = Ex(b.L); var r = Ex(b.R);
                return b.Op switch
                {
                    "&&" => E.AndAlso(l, r), "||" => E.OrElse(l, r),
                    "==" => E.Equal(l, r), "!=" => E.NotEqual(l, r),
                    "<" => E.LessThan(l, r), ">" => E.GreaterThan(l, r), "<=" => E.LessThanOrEqual(l, r), ">=" => E.GreaterThanOrEqual(l, r),
                    "%" => E.Call(typeof(Rt).GetMethod(nameof(Rt.IMod))!, l, r),
                    _ => Arith(b.Op, l, b.L.Type, r, b.R.Type),
                };
            }
            case CondExpr c: return E.Condition(Ex(c.C), Ex(c.A), Ex(c.B));
            case CallExpr call:
                if (call.Builtin != null) return E.Call(call.Builtin, call.Args.Select(Ex));
                if (call.User != null) return E.Invoke(E.Constant(compiled[call.User]), call.Args.Select(Ex).Append(u!));
                return Ctor(call);
            case SwizzleExpr s:
            {
                var v = Ex(s.E);
                if (s.Idx.Length == 1) return E.Field(v, F[s.Idx[0]]);
                var temps = new List<ParameterExpression>(); var pre = new List<E>();
                var t = Spill(v, temps, pre);
                return Wrap(temps, pre, NewVec(s.Idx.Select(i => (E)E.Field(t, F[i])).ToList()));
            }
            case IndexExpr ix:
                return E.Call(typeof(Rt).GetMethod(nameof(Rt.Idx), [Tys.Clr(ix.E.Type), typeof(int)])!, Ex(ix.E), Ex(ix.I));
        }
        throw new InvalidOperationException($"unsupported expression {e.GetType().Name}");
    }

    static E Arith(string op, E l, Ty lt, E r, Ty rt)
    {
        if (lt == Ty.Int && rt == Ty.Int && op == "/") return E.Call(typeof(Rt).GetMethod(nameof(Rt.IDiv))!, l, r);
        return op switch
        {
            "+" => E.Add(l, r), "-" => E.Subtract(l, r), "*" => E.Multiply(l, r), "/" => E.Divide(l, r),
            _ => throw new InvalidOperationException($"bad arithmetic op {op}"),
        };
    }

    static ParameterExpression Spill(E v, List<ParameterExpression> temps, List<E> pre)
    {
        if (v is ParameterExpression p) return p;
        var t = E.Variable(v.Type);
        temps.Add(t);
        pre.Add(E.Assign(t, v));
        return t;
    }

    static E Wrap(List<ParameterExpression> temps, List<E> pre, E result) =>
        temps.Count == 0 ? result : E.Block(result.Type, temps, pre.Append(result));

    static E NewVec(List<E> comps) => comps.Count == 1 ? comps[0] :
        E.New(Tys.Clr(Tys.Vec(comps.Count)).GetConstructor(Enumerable.Repeat(typeof(double), comps.Count).ToArray())!, comps);

    E Ctor(CallExpr c)
    {
        var t = c.Ctor!.Value;
        var args = c.Args.Select(Ex).ToList();
        var ats = c.Args.Select(a => a.Type).ToList();
        if (Tys.IsScalar(t))
        {
            var a = args[0]; var at = ats[0];
            if (Tys.IsVec(at)) a = E.Field(a, "X");
            if (at == t || Tys.IsVec(at)) return a;
            return (t, at) switch
            {
                (Ty.Float, Ty.Int) => E.Convert(a, typeof(double)),
                (Ty.Float, Ty.Bool) => E.Condition(a, E.Constant(1.0), E.Constant(0.0)),
                (Ty.Int, Ty.Float) => E.Call(typeof(Rt).GetMethod(nameof(Rt.FtoI))!, a),
                (Ty.Int, Ty.Bool) => E.Condition(a, E.Constant(1), E.Constant(0)),
                (Ty.Bool, Ty.Int) => E.NotEqual(a, E.Constant(0)),
                (Ty.Bool, Ty.Float) => E.NotEqual(a, E.Constant(0.0)),
                _ => throw new InvalidOperationException("bad scalar ctor"),
            };
        }
        var temps = new List<ParameterExpression>(); var pre = new List<E>();
        int n = Tys.Dim(t);
        if (args.Count == 1)
        {
            var at = ats[0];
            if (at == Ty.Float)
            {
                if (Tys.IsMat(t)) return E.Call(typeof(Rt).GetMethod(t == Ty.Mat2 ? nameof(Rt.Mat2Diag) : nameof(Rt.Mat3Diag))!, args[0]);
                var s = Spill(args[0], temps, pre);
                return Wrap(temps, pre, NewVec(Enumerable.Repeat((E)s, n).ToList()));
            }
            if (Tys.IsMat(at))
            {
                if (at == t) return args[0];
                return E.Call(typeof(Rt).GetMethod(t == Ty.Mat2 ? nameof(Rt.Mat2From) : nameof(Rt.Mat3From))!, args[0]);
            }
            if (Tys.IsVec(t) && Tys.IsVec(at) && Tys.Dim(at) >= n)
            {
                if (at == t) return args[0];
                var v = Spill(args[0], temps, pre);
                return Wrap(temps, pre, NewVec(Enumerable.Range(0, n).Select(i => (E)E.Field(v, F[i])).ToList()));
            }
        }
        var comps = new List<E>();
        for (int i = 0; i < args.Count; i++)
        {
            var s = Spill(args[i], temps, pre);
            if (ats[i] == Ty.Float) comps.Add(s);
            else for (int k = 0; k < Tys.Dim(ats[i]); k++) comps.Add(E.Field(s, F[k]));
        }
        if (Tys.IsVec(t)) return Wrap(temps, pre, NewVec(comps));
        var cols = Enumerable.Range(0, n).Select(ci => NewVec(comps.GetRange(ci * n, n))).ToArray();
        return Wrap(temps, pre, E.New(Tys.Clr(t).GetConstructor(Enumerable.Repeat(Tys.Clr(Tys.Vec(n)), n).ToArray())!, cols));
    }
}
