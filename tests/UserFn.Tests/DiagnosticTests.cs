using Freppuccino.UserFn;
using Xunit;

namespace UserFn.Tests;

public class DiagnosticTests
{
    static UserFnDiagnostic[] Errors(string src)
    {
        var r = UserFnCompiler.Compile(src);
        Assert.False(r.Ok);
        Assert.NotEmpty(r.Diagnostics);
        return [.. r.Diagnostics];
    }

    static void Has(string src, int line, int col, string fragment)
    {
        var errs = Errors(src);
        Assert.True(errs.Any(e => e.Line == line && e.Column == col && e.Message.Contains(fragment)),
            $"expected {line}:{col} '{fragment}', got:\n{string.Join("\n", errs)}");
    }

    [Theory]
    [InlineData("float sdf(vec3 p) { return q; }", 1, 28,"undefined identifier 'q'")]
    [InlineData("float sdf(vec3 p) {\n  return p + vec2(1.0);\n}", 2, 12, "cannot add vec3 and vec2")]
    [InlineData("float sdf(vec3 p) {\n  return sdBox(p);\n}", 2, 10, "'sdBox' expects 2 arguments, got 1")]
    [InlineData("float g(vec3 p) { return 1.0; }", 1, 1, "missing entry point 'float sdf(vec3 p)'")]
    [InlineData("float sdf(vec2 p) { return 1.0; }", 1, 7, "entry point must be declared")]
    [InlineData("int N = 3;\nfloat sdf(vec3 p) { return 1.0; }", 1, 5, "global variables must be 'const' or 'param'")]
    [InlineData("float sdf(vec3 p) {\n  float n = 3.0;\n  for (int i = 0; i < int(n); i++) {}\n  return 0.0;\n}", 3, 23, "loop bound must be a constant int")]
    [InlineData("float sdf(vec3 p) {\n  for (int i = 0; i < 1000; i++) {}\n  return 0.0;\n}", 2, 3, "more than 256 iterations")]
    [InlineData("float sdf(vec3 p) {\n  for (int i = 0; i < 10; i--) {}\n  return 0.0;\n}", 2, 3, "more than 256 iterations")]
    [InlineData("float sdf(vec3 p) {\n  for (int i = 0; i < 10; i++) { i = 2; }\n  return 0.0;\n}", 2, 34, "cannot modify loop variable 'i'")]
    [InlineData("float sdf(vec3 p) { float x = 1.0 }", 1, 35, "expected ';'")]
    [InlineData("float sdf(vec3 p) { if (p.x > 0.0) return 1.0; }", 1, 7, "must end with a return")]
    [InlineData("float sdf(vec3 p) { return g(p); }\nfloat g(vec3 q) { return 0.0; }", 1, 28, "must be defined before it is used")]
    [InlineData("float g(float x) { return g(x); }\nfloat sdf(vec3 p) { return 0.0; }", 1, 27, "recursion is not supported")]
    [InlineData("param float r = 1.0;\nfloat sdf(vec3 p) { r = 2.0; return r; }", 2, 21, "cannot assign to param 'r'")]
    [InlineData("param vec3 r = 1.0;\nfloat sdf(vec3 p) { return 0.0; }", 1, 1, "param must be of type float")]
    [InlineData("float sdf(vec3 p) { mat4 m; return 0.0; }", 1, 21, "type 'mat4' is not supported")]
    [InlineData("float sdf(vec3 p) { return p.xw.x; }", 1, 30, "out of range for vec3")]
    [InlineData("float sdf(vec3 p) { return clamp(p, vec2(0.0), 1.0).x; }", 1, 28, "no overload of 'clamp' takes (vec3, vec2, float)")]
    [InlineData("float sdf(vec3 p) { return vec3(1.0, 2.0); }", 1, 28, "vec3 constructor needs 3 components, got 2")]
    [InlineData("float sdf(vec3 p) { int i = 1.5; return 0.0; }", 1, 29, "cannot convert float to int")]
    [InlineData("float sdf(vec3 p) { float a = 1.0; float a = 2.0; return a; }", 1, 42, "'a' is already defined")]
    [InlineData("float sdf(vec3 p) {\n  for (int i = 0; i < 3; i++) { int i = 5; }\n  return 0.0;\n}", 2, 37, "'i' is already defined")]
    [InlineData("float sdf(vec3 p) {\n  for (int i = 0; i < 3; i++) int i = 5;\n  return 0.0;\n}", 2, 35, "'i' is already defined")]
    [InlineData("float sdf(vec3 p) { break; return 0.0; }", 1, 21, "'break' outside a loop")]
    [InlineData("const float K = sin(1.0);\nfloat sdf(vec3 p) { float x = 1.0; const float L = x; return K; }", 2, 52, "not a constant expression")]
    [InlineData("float sdf(vec3 p) { while (true) {} return 0.0; }", 1, 21, "'while' loops are not supported")]
    [InlineData("float sdf(vec3 p) { return p.x % 2.0; }", 1, 32, "needs int operands")]
    [InlineData("float sin(float x) { return x; }\nfloat sdf(vec3 p) { return 0.0; }", 1, 7, "cannot redefine builtin function 'sin'")]
    [InlineData("float g(out float x) { return 0.0; }\nfloat sdf(vec3 p) { return 0.0; }", 1, 9, "'out' parameters are not supported")]
    [InlineData("float sdf(vec3 p) { float x = 1.0; x *= p; return x; }", 1, 38, "cannot store it in float")]
    [InlineData("float sdf(vec3 p) { return 1.0 @ 2.0; }", 1, 32,"unexpected character '@'")]
    public void ReportsErrorAtPosition(string src, int line, int col, string fragment) => Has(src, line, col, fragment);

    [Fact]
    public void ReportsMultipleErrors()
    {
        var errs = Errors("""
            float sdf(vec3 p) {
                float a = b;
                vec2 v = p;
                return c + 1.0;
            }
            """);
        Assert.Contains(errs, e => e.Line == 2 && e.Message.Contains("'b'"));
        Assert.Contains(errs, e => e.Line == 3 && e.Message.Contains("cannot convert vec3 to vec2"));
        Assert.Contains(errs, e => e.Line == 4 && e.Message.Contains("'c'"));
    }

    [Fact]
    public void RecoversAfterSyntaxError()
    {
        var errs = Errors("""
            float g(vec3 p) { return 1.0 +; }
            float sdf(vec3 p) { return zz; }
            """);
        Assert.Contains(errs, e => e.Line == 1);
        Assert.Contains(errs, e => e.Line == 2 && e.Message.Contains("'zz'"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{{{{")]
    [InlineData("float sdf(vec3 p) { return")]
    [InlineData("float sdf(vec3 p) { return ((((((1.0; }")]
    [InlineData("/* unterminated")]
    [InlineData("float sdf(vec3 p) { for (;;) {} return 0.0; }")]
    [InlineData("float sdf(vec3 p) { for (int i = 0; i < 4;) {} return 0.0; }")]
    [InlineData("float sdf(vec3 p) { p = 1; return p[5]; }")]
    [InlineData("float sdf(vec3 p) { return float(p.q) + 1.0.x; }")]
    public void NeverThrows(string src)
    {
        var r = UserFnCompiler.Compile(src);
        Assert.False(r.Ok);
        Assert.All(r.Diagnostics, d => Assert.DoesNotContain("internal compiler error", d.Message));
    }
}
