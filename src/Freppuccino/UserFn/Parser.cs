namespace Freppuccino.UserFn;

/// Recursive descent over the token list. On a syntax error: record it, skip to a statement / item boundary, go on.
internal sealed class Parser
{
    sealed class ParseError : Exception { }

    static readonly HashSet<string> Reserved =
    [
        "if", "else", "for", "while", "do", "return", "break", "continue", "const", "param", "in", "out", "inout",
        "true", "false", "discard", "struct", "uniform", "switch", "case", "default", "void",
        "float", "int", "bool", "vec2", "vec3", "vec4", "mat2", "mat3",
    ];
    static readonly HashSet<string> UnsupportedTypes =
    [
        "uint", "double", "mat4", "mat2x2", "mat2x3", "mat2x4", "mat3x2", "mat3x3", "mat3x4", "mat4x2", "mat4x3", "mat4x4",
        "ivec2", "ivec3", "ivec4", "uvec2", "uvec3", "uvec4", "bvec2", "bvec3", "bvec4", "dvec2", "dvec3", "dvec4",
        "sampler2D", "sampler3D", "dmat2", "dmat3", "dmat4",
    ];

    readonly List<Token> toks;
    readonly List<UserFnDiagnostic> diags;
    int pos;

    Parser(List<Token> toks, List<UserFnDiagnostic> diags) { this.toks = toks; this.diags = diags; }

    public static ProgramAst Parse(List<Token> toks, List<UserFnDiagnostic> diags) => new Parser(toks, diags).Program();

    Token Peek(int k = 0) => toks[Math.Min(pos + k, toks.Count - 1)];
    Token Next() { var t = Peek(); if (pos < toks.Count - 1) pos++; return t; }
    bool Accept(string s) { if (Peek().Is(s)) { Next(); return true; } return false; }
    Token Expect(string s, string? what = null)
    {
        if (Peek().Is(s)) return Next();
        throw Fail(Peek(), $"expected '{s}'{(what != null ? " " + what : "")}, found {Peek()}");
    }
    ParseError Fail(Token at, string msg) { Error(at, msg); return new ParseError(); }
    void Error(Token at, string msg)
    {
        if (diags.Count > 0 && diags[^1].Line == at.Line && diags[^1].Column == at.Col) return; // one per spot
        diags.Add(new(at.Line, at.Col, msg));
    }
    static T At<T>(T n, Token t) where T : Node { n.Line = t.Line; n.Col = t.Col; return n; }

    bool IsTypeName(Token t) => t.Kind == Tk.Ident && (Tys.Parse(t.Text) != null || UnsupportedTypes.Contains(t.Text));

    Token ExpectIdent(string what)
    {
        var t = Peek();
        if (t.Kind != Tk.Ident) throw Fail(t, $"expected {what}, found {t}");
        if (Reserved.Contains(t.Text)) throw Fail(t, $"'{t.Text}' is a reserved word");
        if (t.Text.Contains("__")) Error(t, $"identifier '{t.Text}' must not contain '__'");
        else if (t.Text.StartsWith("gl_")) Error(t, $"identifier '{t.Text}' must not start with 'gl_'");
        return Next();
    }

    (string name, Ty ty) ParseType()
    {
        var t = Peek();
        if (t.Kind != Tk.Ident) throw Fail(t, $"expected a type, found {t}");
        var ty = Tys.Parse(t.Text);
        if (ty == null)
        {
            if (UnsupportedTypes.Contains(t.Text)) throw Fail(t, $"type '{t.Text}' is not supported");
            throw Fail(t, $"unknown type '{t.Text}'");
        }
        Next();
        return (t.Text, ty.Value);
    }

    // ---------------- top level ----------------
    ProgramAst Program()
    {
        var prog = new ProgramAst();
        while (Peek().Kind != Tk.Eof)
        {
            int start = pos;
            try { TopItem(prog); }
            catch (ParseError) { SyncTop(start); }
        }
        return prog;
    }

    void SyncTop(int start)
    {
        if (pos == start) Next();
        int depth = 0;
        while (Peek().Kind != Tk.Eof)
        {
            var t = Peek();
            if (t.Is("{")) depth++;
            else if (t.Is("}")) { depth--; if (depth <= 0) { Next(); return; } }
            else if (t.Is(";") && depth == 0) { Next(); return; }
            Next();
        }
    }

    void TopItem(ProgramAst prog)
    {
        var t = Peek();
        if (t.Is(";")) { Next(); return; }
        if (t.Is("param"))
        {
            Next();
            var (_, ty) = ParseType();
            if (ty != Ty.Float) Error(t, "param must be of type float");
            var name = ExpectIdent("param name");
            Expect("=", "(params need a default value)");
            bool neg = Accept("-");
            var num = Peek();
            if (num.Kind != Tk.Int && num.Kind != Tk.Float) throw Fail(num, "param default must be a number literal");
            Next();
            Expect(";");
            var p = At(new ParamDecl { Name = name.Text, Default = neg ? -num.Num : num.Num }, name);
            prog.Params.Add(p);
            prog.Items.Add(p);
            return;
        }
        if (t.Is("const"))
        {
            var d = Decl();
            Expect(";");
            prog.Items.Add(At(new ConstDecl { Decl = d }, t));
            return;
        }
        if (t.Is("uniform") || t.Is("in") || t.Is("out")) throw Fail(t, $"'{t.Text}' declarations are not supported; use 'param float name = value;'");
        if (t.Is("struct")) throw Fail(t, "structs are not supported");
        // function or global var
        var (retName, ret) = ParseType();
        var nameTok = ExpectIdent("a function name");
        if (!Peek().Is("("))
            throw Fail(nameTok, "global variables must be 'const' or 'param'");
        var f = At(new FuncDecl { RetName = retName, Ret = ret, Name = nameTok.Text }, nameTok);
        Next();
        if (!Peek().Is(")"))
        {
            if (Peek().Is("void") && Peek(1).Is(")")) Next();
            else
            {
                do
                {
                    var q = Peek();
                    if (q.Is("out") || q.Is("inout")) { Error(q, $"'{q.Text}' parameters are not supported"); Next(); }
                    else if (q.Is("in") || q.Is("const")) Next();
                    var (pn, pt) = ParseType();
                    var pname = ExpectIdent("a parameter name");
                    if (Peek().Is("[")) throw Fail(Peek(), "arrays are not supported");
                    f.Params.Add(At(new FuncParam { TypeName = pn, Type = pt, Name = pname.Text }, pname));
                } while (Accept(","));
            }
        }
        Expect(")");
        if (!Peek().Is("{")) throw Fail(Peek(), "expected '{' (function prototypes are not supported)");
        f.Body = Block();
        prog.Funcs.Add(f);
        prog.Items.Add(f);
    }

    // ---------------- statements ----------------
    BlockStmt Block()
    {
        var open = Expect("{");
        var b = At(new BlockStmt(), open);
        while (!Peek().Is("}"))
        {
            if (Peek().Kind == Tk.Eof) throw Fail(Peek(), "expected '}' (block opened at " + open.Line + ":" + open.Col + ")");
            int start = pos;
            try { b.Body.Add(Statement()); }
            catch (ParseError) { SyncStmt(start); }
        }
        Next();
        return b;
    }

    void SyncStmt(int start)
    {
        if (pos == start && !Peek().Is("}")) Next();
        int depth = 0;
        while (Peek().Kind != Tk.Eof)
        {
            var t = Peek();
            if (t.Is("{")) depth++;
            else if (t.Is("}")) { if (depth == 0) return; depth--; if (depth == 0) { Next(); return; } }
            else if (t.Is(";") && depth == 0) { Next(); return; }
            Next();
        }
    }

    bool StartsDecl()
    {
        var t = Peek();
        if (t.Is("const")) return true;
        if (IsTypeName(t) && Peek(1).Kind == Tk.Ident) return true;
        // unknown type: `foo bar = ...`
        return t.Kind == Tk.Ident && !Reserved.Contains(t.Text) && Peek(1).Kind == Tk.Ident && !Reserved.Contains(Peek(1).Text);
    }

    Stmt Statement()
    {
        var t = Peek();
        if (t.Is("{")) return Block();
        if (t.Is(";")) { Next(); return At(new BlockStmt(), t); }
        if (t.Is("if"))
        {
            Next(); Expect("(");
            var s = At(new IfStmt { C = Expression() }, t);
            Expect(")");
            s.Then = Statement();
            if (Accept("else")) s.Else = Statement();
            return s;
        }
        if (t.Is("for"))
        {
            Next(); Expect("(");
            var s = At(new ForStmt(), t);
            if (!Peek().Is(";")) s.Init = StartsDecl() ? Decl() : Simple();
            Expect(";");
            if (!Peek().Is(";")) s.Cond = Expression();
            Expect(";");
            if (!Peek().Is(")")) s.Update = Simple();
            Expect(")");
            s.Body = Statement();
            return s;
        }
        if (t.Is("while") || t.Is("do")) throw Fail(t, $"'{t.Text}' loops are not supported; use 'for (int i = 0; i < N; i++)'");
        if (t.Is("switch") || t.Is("discard")) throw Fail(t, $"'{t.Text}' is not supported");
        if (t.Is("return"))
        {
            Next();
            var r = At(new ReturnStmt(), t);
            if (!Peek().Is(";")) r.E = Expression();
            Expect(";");
            return r;
        }
        if (t.Is("break")) { Next(); Expect(";"); return At(new BreakStmt(), t); }
        if (t.Is("continue")) { Next(); Expect(";"); return At(new ContinueStmt(), t); }
        Stmt st = StartsDecl() ? Decl() : Simple();
        Expect(";");
        return st;
    }

    DeclStmt Decl()
    {
        var t = Peek();
        var d = At(new DeclStmt(), t);
        if (Accept("const")) d.IsConst = true;
        (d.TypeName, d.Type) = ParseType();
        do
        {
            var name = ExpectIdent("a variable name");
            if (Peek().Is("[")) throw Fail(Peek(), "arrays are not supported");
            var v = At(new Declarator { Name = name.Text }, name);
            if (Accept("=")) v.Init = Expression();
            d.Vars.Add(v);
        } while (Accept(","));
        return d;
    }

    static readonly HashSet<string> AssignOps = ["=", "+=", "-=", "*=", "/=", "%="];

    /// assignment / ++ / -- / expression statement (no trailing ';').
    Stmt Simple()
    {
        var t = Peek();
        if (t.Is("++") || t.Is("--"))
        {
            Next();
            return At(new IncDecStmt { Target = Postfix(), Inc = t.Text == "++" }, t);
        }
        var e = Expression();
        var op = Peek();
        if (op.Kind == Tk.Punct && AssignOps.Contains(op.Text))
        {
            Next();
            return At(new AssignStmt { Target = e, Op = op.Text, Value = Expression() }, op);
        }
        if (op.Is("++") || op.Is("--"))
        {
            Next();
            return At(new IncDecStmt { Target = e, Inc = op.Text == "++" }, op);
        }
        return At(new ExprStmt { E = e }, t);
    }

    // ---------------- expressions ----------------
    Expr Expression() => Ternary();

    Expr Ternary()
    {
        var c = Binary(0);
        var q = Peek();
        if (!Accept("?")) return c;
        var a = Expression();
        Expect(":", "in ?: expression");
        var b = Ternary();
        return At(new CondExpr { C = c, A = a, B = b }, q);
    }

    static readonly string[][] Levels =
    [
        ["||"], ["^^"], ["&&"], ["|"], ["^"], ["&"], ["==", "!="], ["<", ">", "<=", ">="], ["<<", ">>"], ["+", "-"], ["*", "/", "%"],
    ];

    Expr Binary(int level)
    {
        if (level == Levels.Length) return Unary();
        var l = Binary(level + 1);
        while (true)
        {
            var op = Peek();
            if (op.Kind != Tk.Punct || Array.IndexOf(Levels[level], op.Text) < 0) return l;
            Next();
            if (op.Text is "|" or "^" or "&" or "<<" or ">>" or "^^") Error(op, $"operator '{op.Text}' is not supported");
            var r = Binary(level + 1);
            l = At(new BinaryExpr { Op = op.Text, L = l, R = r }, op);
        }
    }

    Expr Unary()
    {
        var t = Peek();
        if (t.Is("-") || t.Is("+") || t.Is("!") || t.Is("~"))
        {
            Next();
            if (t.Text == "~") Error(t, "operator '~' is not supported");
            return At(new UnaryExpr { Op = t.Text, E = Unary() }, t);
        }
        if (t.Is("++") || t.Is("--")) throw Fail(t, $"'{t.Text}' is only supported as a statement");
        return Postfix();
    }

    Expr Postfix()
    {
        var e = Primary();
        while (true)
        {
            var t = Peek();
            if (t.Is("."))
            {
                Next();
                var c = Peek();
                if (c.Kind != Tk.Ident) throw Fail(c, "expected swizzle after '.'");
                Next();
                e = At(new SwizzleExpr { E = e, Comps = c.Text }, c);
            }
            else if (t.Is("["))
            {
                Next();
                var i = Expression();
                Expect("]");
                e = At(new IndexExpr { E = e, I = i }, t);
            }
            else if (t.Is("("))
                throw Fail(t, "only named functions can be called");
            else return e;
        }
    }

    Expr Primary()
    {
        var t = Peek();
        switch (t.Kind)
        {
            case Tk.Int: Next(); return At(new LitExpr { Value = (int)t.Num }, t);
            case Tk.Float: Next(); return At(new LitExpr { Value = t.Num }, t);
            case Tk.Ident:
                if (t.Is("true") || t.Is("false")) { Next(); return At(new LitExpr { Value = t.Text == "true" }, t); }
                if (Peek(1).Is("("))
                {
                    if (UnsupportedTypes.Contains(t.Text)) throw Fail(t, $"type '{t.Text}' is not supported");
                    Next(); Next();
                    var call = At(new CallExpr { Name = t.Text }, t);
                    if (!Peek().Is(")"))
                        do call.Args.Add(Expression()); while (Accept(","));
                    Expect(")", "to close the argument list");
                    return call;
                }
                if (Reserved.Contains(t.Text)) throw Fail(t, $"unexpected {t}");
                Next();
                return At(new NameExpr { Id = t.Text }, t);
            case Tk.Punct when t.Is("("):
                Next();
                var e = Expression();
                Expect(")");
                return e;
            default:
                throw Fail(t, $"expected an expression, found {t}");
        }
    }
}
