using System.Reflection;
using Freppuccino.UserFn.Runtime;

namespace Freppuccino.UserFn;

internal enum Ty : byte { Error, Void, Bool, Int, Float, Vec2, Vec3, Vec4, Mat2, Mat3 }

internal static class Tys
{
    public static bool IsVec(Ty t) => t is Ty.Vec2 or Ty.Vec3 or Ty.Vec4;
    public static bool IsMat(Ty t) => t is Ty.Mat2 or Ty.Mat3;
    public static bool IsFloaty(Ty t) => t == Ty.Float || IsVec(t) || IsMat(t);
    public static bool IsScalar(Ty t) => t is Ty.Float or Ty.Int or Ty.Bool;
    /// Vector size / matrix dimension; 1 for scalars.
    public static int Dim(Ty t) => t switch { Ty.Vec2 or Ty.Mat2 => 2, Ty.Vec3 or Ty.Mat3 => 3, Ty.Vec4 => 4, _ => 1 };
    /// Number of float components.
    public static int Size(Ty t) => t switch { Ty.Mat2 => 4, Ty.Mat3 => 9, _ => Dim(t) };
    public static Ty Vec(int n) => n switch { 1 => Ty.Float, 2 => Ty.Vec2, 3 => Ty.Vec3, 4 => Ty.Vec4, _ => Ty.Error };
    public static Ty Mat(int n) => n switch { 2 => Ty.Mat2, 3 => Ty.Mat3, _ => Ty.Error };
    public static string Name(Ty t) => t switch
    {
        Ty.Error => "<error>", Ty.Void => "void", Ty.Bool => "bool", Ty.Int => "int", Ty.Float => "float",
        Ty.Vec2 => "vec2", Ty.Vec3 => "vec3", Ty.Vec4 => "vec4", Ty.Mat2 => "mat2", Ty.Mat3 => "mat3", _ => "?",
    };
    public static Ty? Parse(string s) => s switch
    {
        "void" => Ty.Void, "bool" => Ty.Bool, "int" => Ty.Int, "float" => Ty.Float,
        "vec2" => Ty.Vec2, "vec3" => Ty.Vec3, "vec4" => Ty.Vec4, "mat2" => Ty.Mat2, "mat3" => Ty.Mat3, _ => null,
    };
    public static Type Clr(Ty t) => t switch
    {
        Ty.Bool => typeof(bool), Ty.Int => typeof(int), Ty.Float => typeof(double),
        Ty.Vec2 => typeof(Vec2), Ty.Vec3 => typeof(Vec3), Ty.Vec4 => typeof(Vec4),
        Ty.Mat2 => typeof(Mat2), Ty.Mat3 => typeof(Mat3), Ty.Void => typeof(void),
        _ => throw new InvalidOperationException($"no CLR type for {t}"),
    };
    public static Ty? FromClr(Type t) =>
        t == typeof(bool) ? Ty.Bool : t == typeof(int) ? Ty.Int : t == typeof(double) ? Ty.Float :
        t == typeof(Vec2) ? Ty.Vec2 : t == typeof(Vec3) ? Ty.Vec3 : t == typeof(Vec4) ? Ty.Vec4 :
        t == typeof(Mat2) ? Ty.Mat2 : t == typeof(Mat3) ? Ty.Mat3 : null;
}

internal enum SymKind { Local, Param, Const }

internal sealed class Sym(SymKind kind, string name, Ty type)
{
    public SymKind Kind = kind;
    public string Name = name;
    public Ty Type = type;
    public int ParamIndex;          // Param
    public object? Value;           // Const (null if its initializer failed)
    public bool LoopVar;            // read-only in the loop body
}

internal abstract class Node { public int Line, Col; }

// ---------- expressions ----------
internal abstract class Expr : Node
{
    public Ty Type = Ty.Error;
    /// Checker: depends only on literals / consts (foldable).
    public bool IsConst;
}
internal sealed class LitExpr : Expr { public object Value = 0; }          // double / int / bool
internal sealed class NameExpr : Expr { public string Id = ""; public Sym? Sym; }
internal sealed class UnaryExpr : Expr { public string Op = ""; public Expr E = null!; }
internal sealed class BinaryExpr : Expr { public string Op = ""; public Expr L = null!, R = null!; }
internal sealed class CondExpr : Expr { public Expr C = null!, A = null!, B = null!; }
internal sealed class CallExpr : Expr
{
    public string Name = ""; public List<Expr> Args = [];
    public MethodInfo? Builtin;     // builtin / library (Runtime.Builtins)
    public FuncDecl? User;          // user helper
    public Ty? Ctor;                // type constructor
}
internal sealed class SwizzleExpr : Expr { public Expr E = null!; public string Comps = ""; public int[] Idx = []; }
internal sealed class IndexExpr : Expr { public Expr E = null!, I = null!; }
/// Inserted by the checker: int -> float.
internal sealed class ConvExpr : Expr { public Expr E = null!; }
/// Inserted by the checker: folded constant (const symbol reference, PI).
internal sealed class ConstExpr : Expr { public object Value = 0; }

// ---------- statements ----------
internal abstract class Stmt : Node { }
internal sealed class BlockStmt : Stmt { public List<Stmt> Body = []; }
internal sealed class Declarator : Node { public string Name = ""; public Expr? Init; public Sym? Sym; }
internal sealed class DeclStmt : Stmt { public bool IsConst; public string TypeName = ""; public Ty Type; public List<Declarator> Vars = []; }
/// target op= value. Op is "=" or one of "+=" "-=" "*=" "/=" "%=". Checker coerces Value to what the op needs.
internal sealed class AssignStmt : Stmt { public Expr Target = null!; public string Op = "="; public Expr Value = null!; }
internal sealed class IncDecStmt : Stmt { public Expr Target = null!; public bool Inc; }
internal sealed class ExprStmt : Stmt { public Expr E = null!; }
internal sealed class IfStmt : Stmt { public Expr C = null!; public Stmt Then = null!; public Stmt? Else; }
internal sealed class ForStmt : Stmt
{
    public Stmt? Init; public Expr? Cond; public Stmt? Update; public Stmt Body = null!;
    // checker: normalized form  for (int V = Start; V CmpOp Bound; V += Step)
    public Sym? Var; public int Start, Bound, Step; public string CmpOp = "<";
}
internal sealed class ReturnStmt : Stmt { public Expr? E; }
internal sealed class BreakStmt : Stmt { }
internal sealed class ContinueStmt : Stmt { }

// ---------- top level ----------
internal sealed class ParamDecl : Node { public string Name = ""; public double Default; }
internal sealed class ConstDecl : Node { public DeclStmt Decl = null!; }
internal sealed class FuncParam : Node { public string TypeName = ""; public Ty Type; public string Name = ""; public Sym? Sym; }
internal sealed class FuncDecl : Node
{
    public string RetName = ""; public Ty Ret; public string Name = "";
    public List<FuncParam> Params = []; public BlockStmt Body = null!;
    public bool IsEntry;
}

internal sealed class ProgramAst
{
    public List<Node> Items = [];   // ParamDecl / ConstDecl / FuncDecl in source order
    public List<ParamDecl> Params = [];
    public List<FuncDecl> Funcs = [];
}
