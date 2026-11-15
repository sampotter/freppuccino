using System.Reflection;
using Freppuccino.UserFn.Runtime;

namespace Freppuccino.UserFn;

/// Resolves names, types every expression (inserting int->float conversions and folding consts), validates
/// statements (lvalues, loop shape and bounds, returns). Annotates the AST in place; errors go to diags.
internal sealed class Checker
{
    public const int MaxLoopIterations = 256;
    public const string Entry = "sdf";

    internal static readonly ILookup<string, MethodInfo> BuiltinOverloads =
        typeof(Builtins).GetMethods(BindingFlags.Public | BindingFlags.Static).ToLookup(m => m.Name);

    readonly List<UserFnDiagnostic> diags;
    readonly Dictionary<string, Sym> globals = [];
    readonly Dictionary<string, FuncDecl> funcs = [];
    readonly HashSet<string> allFuncNames = [];
    readonly List<Dictionary<string, Sym>> scopes = [];
    FuncDecl? cur;
    int loopDepth;
    bool syntaxErrors; // skip the missing-return check: a skipped statement may have held the return

    Checker(List<UserFnDiagnostic> diags) => this.diags = diags;

    public static void Check(ProgramAst prog, List<UserFnDiagnostic> diags) => new Checker(diags).Run(prog);

    void Error(Node at, string msg) => diags.Add(new(at.Line, at.Col, msg));

    void Run(ProgramAst prog)
    {
        syntaxErrors = diags.Count > 0;
        foreach (var f in prog.Funcs) allFuncNames.Add(f.Name);
        int paramIndex = 0;
        FuncDecl? entry = null;
        foreach (var item in prog.Items)
        {
            switch (item)
            {
                case ParamDecl p:
                    if (DefinedGlobally(p.Name, p)) break;
                    globals[p.Name] = new Sym(SymKind.Param, p.Name, Ty.Float) { ParamIndex = paramIndex++ };
                    break;
                case ConstDecl c:
                    foreach (var v in c.Decl.Vars)
                    {
                        var sym = CheckDeclarator(c.Decl, v);
                        if (sym != null && !DefinedGlobally(v.Name, v)) globals[v.Name] = sym;
                    }
                    break;
                case FuncDecl f:
                    if (BuiltinOverloads.Contains(f.Name)) Error(f, $"cannot redefine builtin function '{f.Name}'");
                    else if (funcs.ContainsKey(f.Name)) Error(f, $"function '{f.Name}' is already defined");
                    else if (globals.ContainsKey(f.Name)) Error(f, $"'{f.Name}' is already defined");
                    CheckFunc(f);
                    if (f.Name == Entry)
                    {
                        f.IsEntry = true;
                        if (f.Ret != Ty.Float || f.Params.Count != 1 || f.Params[0].Type != Ty.Vec3)
                            Error(f, $"entry point must be declared 'float {Entry}(vec3 p)'");
                        entry ??= f;
                    }
                    funcs.TryAdd(f.Name, f);
                    break;
            }
        }
        if (entry == null) diags.Add(new(1, 1, $"missing entry point 'float {Entry}(vec3 p)'"));
    }

    bool DefinedGlobally(string name, Node at)
    {
        if (globals.ContainsKey(name) || allFuncNames.Contains(name)) { Error(at, $"'{name}' is already defined"); return true; }
        return false;
    }

    // ---------------- functions / statements ----------------
    void CheckFunc(FuncDecl f)
    {
        cur = f;
        if (f.Ret == Ty.Void) Error(f, $"function '{f.Name}' must return a value (void functions are not supported)");
        var scope = new Dictionary<string, Sym>();
        scopes.Add(scope);
        foreach (var p in f.Params)
        {
            if (p.Type == Ty.Void) Error(p, "parameter cannot be void");
            p.Sym = new Sym(SymKind.Local, p.Name, p.Type);
            if (!scope.TryAdd(p.Name, p.Sym)) Error(p, $"parameter '{p.Name}' is already defined");
        }
        foreach (var s in f.Body.Body) CheckStmt(s); // body shares the parameters' scope (GLSL)
        scopes.RemoveAt(scopes.Count - 1);
        if (f.Ret != Ty.Void && !syntaxErrors && !Returns(f.Body))
            Error(f, $"function '{f.Name}' must end with a return statement (not every path returns a value)");
        cur = null;
    }

    static bool Returns(Stmt s) => s switch
    {
        ReturnStmt => true,
        BlockStmt b => b.Body.Any(Returns),
        IfStmt i => i.Else != null && Returns(i.Then) && Returns(i.Else),
        _ => false,
    };

    void Declare(Node at, Sym sym)
    {
        if (!scopes[^1].TryAdd(sym.Name, sym)) Error(at, $"'{sym.Name}' is already defined in this scope");
    }

    void CheckStmt(Stmt s)
    {
        switch (s)
        {
            case BlockStmt b:
                scopes.Add([]);
                foreach (var x in b.Body) CheckStmt(x);
                scopes.RemoveAt(scopes.Count - 1);
                break;
            case DeclStmt d:
                foreach (var v in d.Vars)
                {
                    var sym = CheckDeclarator(d, v);
                    if (sym != null) Declare(v, sym);
                }
                break;
            case AssignStmt a: CheckAssign(a); break;
            case IncDecStmt i:
            {
                var t = CheckLValue(i.Target);
                if (t != Ty.Error && t != Ty.Int && t != Ty.Float) Error(i, $"'{(i.Inc ? "++" : "--")}' needs an int or float variable, not {Tys.Name(t)}");
                break;
            }
            case ExprStmt e: e.E = CheckExpr(e.E); break;
            case IfStmt i:
                i.C = Expect(CheckExpr(i.C), Ty.Bool, "if condition");
                CheckScoped(i.Then);
                if (i.Else != null) CheckScoped(i.Else);
                break;
            case ForStmt f: CheckFor(f); break;
            case ReturnStmt r:
                if (cur == null) break;
                if (r.E == null) { Error(r, $"function '{cur.Name}' must return a {Tys.Name(cur.Ret)}"); break; }
                r.E = Coerce(CheckExpr(r.E), cur.Ret, "return value");
                break;
            case BreakStmt or ContinueStmt:
                if (loopDepth == 0) Error(s, $"'{(s is BreakStmt ? "break" : "continue")}' outside a loop");
                break;
        }
    }

    /// GLSL gives if/for bodies their own scope even without braces.
    void CheckScoped(Stmt s)
    {
        scopes.Add([]);
        CheckStmt(s);
        scopes.RemoveAt(scopes.Count - 1);
    }

    Sym? CheckDeclarator(DeclStmt d, Declarator v)
    {
        if (d.Type == Ty.Void) { Error(v, "variables cannot be void"); return null; }
        if (v.Init != null) v.Init = Coerce(CheckExpr(v.Init), d.Type, $"initializer of '{v.Name}'");
        if (!d.IsConst) return v.Sym = new Sym(SymKind.Local, v.Name, d.Type);
        var sym = new Sym(SymKind.Const, v.Name, d.Type);
        if (v.Init == null) Error(v, $"const '{v.Name}' must be initialized");
        else if (v.Init.Type != Ty.Error)
        {
            if (!v.Init.IsConst) Error(v.Init, $"initializer of const '{v.Name}' is not a constant expression");
            else sym.Value = Fold(v.Init);
        }
        return v.Sym = sym;
    }

    object? Fold(Expr e)
    {
        try { return CpuEmitter.EvalConst(e); }
        catch (Exception ex) { Error(e, $"cannot evaluate constant: {ex.Message}"); return null; }
    }

    void CheckAssign(AssignStmt a)
    {
        var tt = CheckLValue(a.Target);
        var v = CheckExpr(a.Value);
        if (tt == Ty.Error || v.Type == Ty.Error) { a.Value = v; return; }
        if (a.Op == "=") { a.Value = Coerce(v, tt, "assignment"); return; }
        string op = a.Op[..1];
        if (op == "%")
        {
            if (tt != Ty.Int || v.Type != Ty.Int) Error(a, $"'%=' needs int operands, not {Tys.Name(tt)} and {Tys.Name(v.Type)}");
            a.Value = v;
            return;
        }
        var l =(Expr)new ConstExpr { Type = tt, Line = a.Line, Col = a.Col }; // stand-in for the target's type
        var r = v;
        var res = ArithType(op, ref l, ref r, a);
        a.Value = r;
        if (res != Ty.Error && res != tt)
            Error(a, $"'{a.Op}': result is {Tys.Name(res)}, cannot store it in {Tys.Name(tt)}");
    }

    Ty CheckLValue(Expr target)
    {
        NameExpr? n = target as NameExpr;
        SwizzleExpr? sw = null;
        if (n == null && target is SwizzleExpr s && s.E is NameExpr sn) { n = sn; sw = s; }
        if (n == null) { Error(target, "can only assign to a variable or a swizzle of one (e.g. q.xz)"); return Ty.Error; }
        var sym = Lookup(n.Id);
        if (sym == null) { Error(n, $"undefined identifier '{n.Id}'"); return Ty.Error; }
        n.Sym = sym; n.Type = sym.Type;
        if (sym.Kind == SymKind.Param) { Error(n, $"cannot assign to param '{n.Id}'"); return Ty.Error; }
        if (sym.Kind == SymKind.Const) { Error(n, $"cannot assign to const '{n.Id}'"); return Ty.Error; }
        if (sym.LoopVar) { Error(n, $"cannot modify loop variable '{n.Id}'"); return Ty.Error; }
        if (sw == null) return sym.Type;
        var t = CheckSwizzle(sw);
        if (t == Ty.Error) return t;
        if (sw.Idx.Distinct().Count() != sw.Idx.Length) { Error(sw, $"cannot assign to swizzle '{sw.Comps}' with repeated components"); return Ty.Error; }
        return t;
    }

    void CheckFor(ForStmt f)
    {
        scopes.Add([]);
        const string shape = "for loops must look like 'for (int i = A; i < B; i++)' with constant int A and B";
        bool ok = true;
        // init
        if (f.Init is DeclStmt d && !d.IsConst && d.Type == Ty.Int && d.Vars.Count == 1 && d.Vars[0].Init != null)
        {
            var v = d.Vars[0];
            var sym = CheckDeclarator(d, v);
            if (sym != null) { Declare(v, sym); f.Var = sym; }
            if (v.Init!.Type == Ty.Int && v.Init.IsConst && Fold(v.Init) is int a) f.Start = a;
            else { if (v.Init.Type != Ty.Error) Error(v.Init, "loop start must be a constant int"); ok = false; }
        }
        else { Error(f.Init ?? (Node)f, shape); ok = false; }
        // cond
        if (f.Cond is BinaryExpr { Op: "<" or "<=" or ">" or ">=" or "!=" } c && c.L is NameExpr ln && f.Var != null && ln.Id == f.Var.Name)
        {
            f.CmpOp = c.Op;
            f.Cond = CheckExpr(c);
            var bound = c.R;
            if (bound.Type == Ty.Int && bound.IsConst && Fold(bound) is int b) f.Bound = b;
            else { if (bound.Type != Ty.Error) Error(bound, "loop bound must be a constant int (literal or const)"); ok = false; }
        }
        else { if (ok) Error(f.Cond ?? (Node)f, shape); ok = false; }
        // update
        if (f.Var != null && f.Update is IncDecStmt { Target: NameExpr un } inc && un.Id == f.Var.Name)
        {
            CheckStmt(inc);
            f.Step = inc.Inc ? 1 : -1;
        }
        else if (f.Var != null && f.Update is AssignStmt { Op: "+=" or "-=", Target: NameExpr an } asg && an.Id == f.Var.Name)
        {
            CheckStmt(asg);
            if (asg.Value.Type == Ty.Int && asg.Value.IsConst && Fold(asg.Value) is int st) f.Step = asg.Op == "+=" ? st : -st;
            else { if (asg.Value.Type != Ty.Error) Error(asg.Value, "loop step must be a constant int"); ok = false; }
        }
        else { if (ok) Error(f.Update ?? (Node)f, shape); ok = false; }

        if (ok)
        {
            long i = f.Start, n = 0;
            while (Cmp(i, f.CmpOp, f.Bound) && n <= MaxLoopIterations) { i += f.Step; n++; }
            if (n > MaxLoopIterations) Error(f, $"loop runs more than {MaxLoopIterations} iterations");
        }
        if (f.Var != null) f.Var.LoopVar = true;
        loopDepth++;
        // GLSL: the body doesn't open a scope of its own; its top level shares the init's (nested blocks may shadow).
        if (f.Body is BlockStmt body) foreach (var s in body.Body) CheckStmt(s);
        else CheckStmt(f.Body);
        loopDepth--;
        scopes.RemoveAt(scopes.Count - 1);
    }

    internal static bool Cmp(long a, string op, long b) => op switch
    {
        "<" => a < b, "<=" => a <= b, ">" => a > b, ">=" => a >= b, "!=" => a != b, _ => false,
    };

    // ---------------- expressions ----------------
    Sym? Lookup(string id)
    {
        for (int i = scopes.Count - 1; i >= 0; i--)
            if (scopes[i].TryGetValue(id, out var s)) return s;
        return globals.GetValueOrDefault(id);
    }

    Expr Coerce(Expr e, Ty to, string what)
    {
        if (e.Type == to || e.Type == Ty.Error || to == Ty.Error) return e;
        if (e.Type == Ty.Int && to == Ty.Float) return Conv(e);
        Error(e, $"{what}: cannot convert {Tys.Name(e.Type)} to {Tys.Name(to)}");
        return e;
    }

    Expr Expect(Expr e, Ty t, string what)
    {
        if (e.Type != t && e.Type != Ty.Error) Error(e, $"{what} must be {Tys.Name(t)}, not {Tys.Name(e.Type)}");
        return e;
    }

    static Expr Conv(Expr e) => new ConvExpr { E = e, Type = Ty.Float, IsConst = e.IsConst, Line = e.Line, Col = e.Col };

    Expr CheckExpr(Expr e)
    {
        switch (e)
        {
            case LitExpr l:
                l.Type = l.Value switch { int => Ty.Int, bool => Ty.Bool, _ => Ty.Float };
                l.IsConst = true;
                return l;
            case NameExpr n:
            {
                var sym = Lookup(n.Id);
                if (sym == null)
                {
                    if (n.Id == "PI") return new ConstExpr { Value = Math.PI, Type = Ty.Float, IsConst = true, Line = n.Line, Col = n.Col };
                    Error(n, allFuncNames.Contains(n.Id) || BuiltinOverloads.Contains(n.Id)
                        ? $"'{n.Id}' is a function; call it with (...)" : $"undefined identifier '{n.Id}'");
                    return n;
                }
                n.Sym = sym;
                if (sym.Kind == SymKind.Const)
                {
                    if (sym.Value == null) return n; // its initializer already errored
                    return new ConstExpr { Value = sym.Value, Type = sym.Type, IsConst = true, Line = n.Line, Col = n.Col };
                }
                n.Type = sym.Type;
                return n;
            }
            case UnaryExpr u:
            {
                u.E = CheckExpr(u.E);
                var t = u.E.Type;
                u.IsConst = u.E.IsConst;
                if (t == Ty.Error) return u;
                if (u.Op == "!")
                {
                    if (t != Ty.Bool) Error(u, $"operator '!' needs a bool, not {Tys.Name(t)}");
                    else u.Type = Ty.Bool;
                }
                else if (u.Op is "-" or "+")
                {
                    if (t == Ty.Bool) Error(u, $"operator '{u.Op}' cannot be applied to bool");
                    else u.Type = t;
                }
                return u;
            }
            case BinaryExpr b: return CheckBinary(b);
            case CondExpr c:
            {
                c.C = Expect(CheckExpr(c.C), Ty.Bool, "?: condition");
                c.A = CheckExpr(c.A);
                c.B = CheckExpr(c.B);
                c.IsConst = c.C.IsConst && c.A.IsConst && c.B.IsConst;
                if (c.A.Type == Ty.Error || c.B.Type == Ty.Error) return c;
                if (c.A.Type == Ty.Int && c.B.Type == Ty.Float) c.A = Conv(c.A);
                else if (c.B.Type == Ty.Int && c.A.Type == Ty.Float) c.B = Conv(c.B);
                if (c.A.Type != c.B.Type) Error(c, $"?: branches have different types ({Tys.Name(c.A.Type)} and {Tys.Name(c.B.Type)})");
                else c.Type = c.A.Type;
                return c;
            }
            case CallExpr call: return CheckCall(call);
            case SwizzleExpr s:
                s.Type = CheckSwizzle(s);
                return s;
            case IndexExpr ix:
            {
                ix.E = CheckExpr(ix.E);
                ix.I = CheckExpr(ix.I);
                ix.IsConst = ix.E.IsConst && ix.I.IsConst;
                var t = ix.E.Type;
                if (t == Ty.Error || ix.I.Type == Ty.Error) return ix;
                if (ix.I.Type != Ty.Int) { Error(ix.I, $"index must be an int, not {Tys.Name(ix.I.Type)}"); return ix; }
                if (!Tys.IsVec(t) && !Tys.IsMat(t)) { Error(ix, $"cannot index a {Tys.Name(t)}"); return ix; }
                if (ix.I.IsConst && Fold(ix.I) is int k && (k < 0 || k >= Tys.Dim(t)))
                {
                    Error(ix.I, $"index {k} out of range for {Tys.Name(t)}");
                    return ix;
                }
                ix.Type = Tys.IsVec(t) ? Ty.Float : Tys.Vec(Tys.Dim(t));
                return ix;
            }
            case ConvExpr or ConstExpr: return e;
        }
        Error(e, "unsupported expression");
        return e;
    }

    Ty CheckSwizzle(SwizzleExpr s)
    {
        s.E = CheckExpr(s.E);
        s.IsConst = s.E.IsConst;
        var t = s.E.Type;
        if (t == Ty.Error) return Ty.Error;
        if (!Tys.IsVec(t)) { Error(s, $"cannot swizzle a {Tys.Name(t)} ('.{s.Comps}')"); return Ty.Error; }
        if (s.Comps.Length is < 1 or > 4) { Error(s, $"invalid swizzle '.{s.Comps}'"); return Ty.Error; }
        string[] sets = ["xyzw", "rgba", "stpq"];
        var set = sets.FirstOrDefault(st => s.Comps.All(ch => st.Contains(ch)));
        if (set == null) { Error(s, $"invalid swizzle '.{s.Comps}' (use one of xyzw / rgba / stpq)"); return Ty.Error; }
        s.Idx = s.Comps.Select(ch => set.IndexOf(ch)).ToArray();
        if (s.Idx.Any(i => i >= Tys.Dim(t))) { Error(s, $"swizzle '.{s.Comps}' out of range for {Tys.Name(t)}"); return Ty.Error; }
        return Tys.Vec(s.Idx.Length);
    }

    static string Verb(string op) => op switch { "+" => "add", "-" => "subtract", "*" => "multiply", "/" => "divide", _ => "apply '" + op + "' to" };

    Expr CheckBinary(BinaryExpr b)
    {
        b.L = CheckExpr(b.L);
        b.R = CheckExpr(b.R);
        b.IsConst = b.L.IsConst && b.R.IsConst;
        Ty lt = b.L.Type, rt = b.R.Type;
        if (lt == Ty.Error || rt == Ty.Error) return b;
        switch (b.Op)
        {
            case "&&" or "||":
                if (lt != Ty.Bool || rt != Ty.Bool) Error(b, $"operator '{b.Op}' needs bool operands, not {Tys.Name(lt)} and {Tys.Name(rt)}");
                else b.Type = Ty.Bool;
                return b;
            case "==" or "!=":
                Unify(b);
                if (b.L.Type != b.R.Type) Error(b, $"cannot compare {Tys.Name(b.L.Type)} and {Tys.Name(b.R.Type)}");
                else b.Type = Ty.Bool;
                return b;
            case "<" or ">" or "<=" or ">=":
                Unify(b);
                if (b.L.Type != b.R.Type || (b.L.Type != Ty.Int && b.L.Type != Ty.Float))
                    Error(b, $"operator '{b.Op}' needs int or float operands, not {Tys.Name(b.L.Type)} and {Tys.Name(b.R.Type)}");
                else b.Type = Ty.Bool;
                return b;
            case "%":
                if (lt != Ty.Int || rt != Ty.Int) Error(b, $"operator '%' needs int operands (use mod() for floats), not {Tys.Name(lt)} and {Tys.Name(rt)}");
                else b.Type = Ty.Int;
                return b;
            case "+" or "-" or "*" or "/":
            {
                var l = b.L; var r = b.R;
                b.Type = ArithType(b.Op, ref l, ref r, b);
                b.L = l; b.R = r;
                return b;
            }
        }
        Error(b, $"operator '{b.Op}' is not supported");
        return b;
    }

    static void Unify(BinaryExpr b)
    {
        if (b.L.Type == Ty.Int && b.R.Type == Ty.Float) b.L = Conv(b.L);
        else if (b.R.Type == Ty.Int && b.L.Type == Ty.Float) b.R = Conv(b.R);
    }

    /// GLSL arithmetic typing (+ - * /); converts an int operand to float when the other side is float-based.
    Ty ArithType(string op, ref Expr l, ref Expr r, Node at)
    {
        Ty lt = l.Type, rt = r.Type;
        if (lt == Ty.Error || rt == Ty.Error) return Ty.Error;
        if (lt == Ty.Bool || rt == Ty.Bool) { Error(at, $"cannot {Verb(op)} {Tys.Name(lt)} and {Tys.Name(rt)}"); return Ty.Error; }
        if (lt == Ty.Int && rt == Ty.Int) return Ty.Int;
        if (lt == Ty.Int) { l = Conv(l); lt = Ty.Float; }
        if (rt == Ty.Int) { r = Conv(r); rt = Ty.Float; }
        if (lt == rt)
            return lt; // float, vecN, matN (mat * mat = linear algebra, same type)
        if (lt == Ty.Float) return rt;
        if (rt == Ty.Float) return lt;
        if (op == "*" && Tys.IsMat(lt) && Tys.IsVec(rt) && Tys.Dim(lt) == Tys.Dim(rt)) return rt;
        if (op == "*" && Tys.IsVec(lt) && Tys.IsMat(rt) && Tys.Dim(lt) == Tys.Dim(rt)) return lt;
        Error(at, $"cannot {Verb(op)} {Tys.Name(lt)} and {Tys.Name(rt)}");
        return Ty.Error;
    }

    Expr CheckCall(CallExpr c)
    {
        for (int i = 0; i < c.Args.Count; i++) c.Args[i] = CheckExpr(c.Args[i]);
        bool anyErr = c.Args.Any(a => a.Type == Ty.Error);
        bool allConst = c.Args.All(a => a.IsConst);
        var ctor = Tys.Parse(c.Name);
        if (ctor != null)
        {
            c.Ctor = ctor;
            c.IsConst = allConst;
            if (!anyErr) c.Type = CheckCtor(c, ctor.Value);
            return c;
        }
        if (funcs.TryGetValue(c.Name, out var f))
        {
            if (f.IsEntry) { Error(c, $"cannot call the entry point '{Entry}'"); return c; }
            c.User = f;
            c.Type = f.Ret;
            if (c.Args.Count != f.Params.Count) { Error(c, $"'{c.Name}' expects {f.Params.Count} argument{(f.Params.Count == 1 ? "" : "s")}, got {c.Args.Count}"); return c; }
            for (int i = 0; i < c.Args.Count; i++) c.Args[i] = Coerce(c.Args[i], f.Params[i].Type, $"argument {i + 1} of '{c.Name}'");
            return c;
        }
        if (cur != null && c.Name == cur.Name) { Error(c, "recursion is not supported"); return c; }
        if (allFuncNames.Contains(c.Name)) { Error(c, $"function '{c.Name}' must be defined before it is used"); return c; }
        if (!BuiltinOverloads.Contains(c.Name))
        {
            Error(c, Lookup(c.Name) != null ? $"'{c.Name}' is not a function" : $"undefined function '{c.Name}'");
            return c;
        }
        if (anyErr) return c;
        var cands = BuiltinOverloads[c.Name].ToList();
        var argTys = c.Args.Select(a => a.Type).ToArray();
        MethodInfo? best = null; int bestConv = int.MaxValue;
        foreach (var m in cands)
        {
            var ps = m.GetParameters();
            if (ps.Length != argTys.Length) continue;
            int conv = 0;
            bool fits = true;
            for (int i = 0; i < ps.Length && fits; i++)
            {
                var pt = Tys.FromClr(ps[i].ParameterType);
                if (pt == argTys[i]) continue;
                if (pt == Ty.Float && argTys[i] == Ty.Int) conv++;
                else fits = false;
            }
            if (fits && conv < bestConv) { best = m; bestConv = conv; }
        }
        if (best == null)
        {
            var counts = cands.Select(m => m.GetParameters().Length).Distinct().Order().ToList();
            if (!counts.Contains(argTys.Length))
                Error(c, $"'{c.Name}' expects {string.Join(" or ", counts)} argument{(counts is [1] ? "" : "s")}, got {argTys.Length}");
            else
                Error(c, $"no overload of '{c.Name}' takes ({string.Join(", ", argTys.Select(Tys.Name))})");
            return c;
        }
        var bps = best.GetParameters();
        for (int i = 0; i < c.Args.Count; i++) c.Args[i] = Coerce(c.Args[i], Tys.FromClr(bps[i].ParameterType)!.Value, "argument");
        c.Builtin = best;
        c.Type = Tys.FromClr(best.ReturnType)!.Value;
        c.IsConst = allConst;
        return c;
    }

    Ty CheckCtor(CallExpr c, Ty t)
    {
        string name = Tys.Name(t);
        var args = c.Args;
        if (t == Ty.Void) { Error(c, "cannot construct void"); return Ty.Error; }
        if (args.Count == 0) { Error(c, $"{name} constructor needs arguments"); return Ty.Error; }
        if (Tys.IsScalar(t))
        {
            if (args.Count != 1) { Error(c, $"{name} constructor takes 1 argument, got {args.Count}"); return Ty.Error; }
            var at = args[0].Type;
            bool ok = Tys.IsScalar(at) || (t == Ty.Float && Tys.IsVec(at));
            if (!ok) { Error(c, $"cannot convert {Tys.Name(at)} to {name}"); return Ty.Error; }
            return t;
        }
        if (args.Count == 1)
        {
            var at = args[0].Type;
            if (at is Ty.Int or Ty.Float) { args[0] = Coerce(args[0], Ty.Float, name); return t; } // broadcast / diagonal
            if (Tys.IsVec(t) && Tys.IsVec(at) && Tys.Dim(at) >= Tys.Dim(t)) return t;          // truncate
            if (Tys.IsMat(t) && Tys.IsMat(at)) return t;                                        // resize
        }
        int need = Tys.Size(t), have = 0;
        for (int i = 0; i < args.Count; i++)
        {
            var at = args[i].Type;
            if (at is Ty.Int or Ty.Float) { args[i] = Coerce(args[i], Ty.Float, name); have++; }
            else if (Tys.IsVec(at)) have += Tys.Dim(at);
            else { Error(args[i], $"{name} constructor cannot take a {Tys.Name(at)} here"); return Ty.Error; }
        }
        if (have != need) { Error(c, $"{name} constructor needs {need} components, got {have}"); return Ty.Error; }
        return t;
    }
}
