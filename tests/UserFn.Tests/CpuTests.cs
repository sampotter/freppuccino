using Freppuccino.UserFn;
using Xunit;

namespace UserFn.Tests;

public class CpuTests
{
    internal static UserFnProgram Ok(string src)
    {
        var r = UserFnCompiler.Compile(src);
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        Assert.Empty(r.Diagnostics);
        return r.Program!;
    }

    static double Eval(string src, double x, double y, double z, params double[] u) => Ok(src).Cpu(x, y, z, u);

    const double Tol = 1e-12;

    [Fact]
    public void Sphere()
    {
        const string src = "float sdf(vec3 p) { return length(p) - 1.0; }";
        Assert.Equal(1.0, Eval(src, 2, 0, 0), Tol);
        Assert.Equal(-1.0, Eval(src, 0, 0, 0), Tol);
        Assert.Equal(Math.Sqrt(3) - 1, Eval(src, 1, 1, 1), Tol);
    }

    [Fact]
    public void BoxViaLibrary()
    {
        const string src = "float sdf(vec3 p) { return sdBox(p, vec3(1.0, 2.0, 3.0)); }";
        Assert.Equal(1.0, Eval(src, 2, 0, 0), Tol);
        Assert.Equal(-1.0, Eval(src, 0, 0, 0), Tol);
        Assert.Equal(Math.Sqrt(3), Eval(src, 2, 3, 4), Tol);
    }

    [Fact]
    public void Gyroid()
    {
        const string src = "float sdf(vec3 p) { return dot(sin(p), cos(p.yzx)); }";
        double x = 0.3, y = -1.2, z = 2.5;
        Assert.Equal(Math.Sin(x) * Math.Cos(y) + Math.Sin(y) * Math.Cos(z) + Math.Sin(z) * Math.Cos(x), Eval(src, x, y, z), Tol);
    }

    [Fact]
    public void ParamsHelpersConsts()
    {
        const string src = """
            param float r = 1.5;
            param float k = -0.25;
            const vec3 OFF = vec3(1.0, 0.0, 0.0) * 2.0;
            float sphere(vec3 q, float rad) { return length(q) - rad; }
            float sdf(vec3 p) { return sphere(p - OFF, r) + k; }
            """;
        var prog = Ok(src);
        Assert.Equal([new UserFnParam("r", 1.5), new UserFnParam("k", -0.25)], prog.Params.ToArray());
        Assert.Equal(1.0 - 1.5 - 0.25, prog.Cpu(3, 0, 0, [1.5, -0.25]), Tol);
        Assert.Equal(1.0 - 3.0 + 1.0, prog.Cpu(3, 0, 0, [3.0, 1.0]), Tol);
        // short / null param arrays fall back to defaults
        Assert.Equal(1.0 - 3.0 - 0.25, prog.Cpu(3, 0, 0, [3.0]), Tol);
        Assert.Equal(1.0 - 1.5 - 0.25, prog.Cpu(3, 0, 0, null!), Tol);
    }

    [Fact]
    public void ParamsVisibleInHelpers()
    {
        const string src = """
            param float s = 2.0;
            float g(float a) { return a * s; }
            float sdf(vec3 p) { return g(p.x) + s; }
            """;
        Assert.Equal(3 * 5 + 5, Eval(src, 3, 0, 0, 5), Tol);
    }

    [Fact]
    public void Loops()
    {
        const string src = """
            const int N = 5;
            float sdf(vec3 p) {
                float s = 0.0;
                for (int i = 0; i < N; i++) { s += float(i); }       // 0..4 = 10
                for (int j = 10; j >= 0; j -= 5) s += 1.0;           // 10,5,0 = 3
                for (int k = 0; k <= 100; k += 1) {
                    if (k == 2) continue;
                    if (k > 4) break;
                    s += 100.0;                                      // k = 0,1,3,4
                }
                return s + p.x;
            }
            """;
        Assert.Equal(10 + 3 + 400 + 0.5, Eval(src, 0.5, 0, 0), Tol);
    }

    [Fact]
    public void Rotation()
    {
        const string src = """
            mat2 rot(float a) { float c = cos(a), s = sin(a); return mat2(c, s, -s, c); }
            float sdf(vec3 p) {
                p.xz = rot(PI / 2.0) * p.xz;
                return p.z;
            }
            """;
        // column-major mat2(c, s, -s, c) rotates (1,0) -> (c, s) = (0, 1)
        Assert.Equal(1.0, Eval(src, 1, 0, 0), 1e-12);
    }

    [Fact]
    public void Matrices()
    {
        const string src = """
            float sdf(vec3 p) {
                mat3 m = mat3(vec3(1.0, 2.0, 3.0), vec3(0.0, 1.0, 4.0), vec3(5.0, 6.0, 0.0));
                vec3 a = m * p;                // columns weighted by p
                vec3 b = p * m;                // dots with columns
                mat3 mi = inverse(m);
                vec3 back = mi * a;            // == p
                float d = determinant(m);      // 1
                mat3 t = transpose(m);
                mat2 m2 = mat2(2.0);           // diag
                vec2 v2 = m2 * vec2(1.0, 3.0) * mat2(m);
                return a.x * 1000.0 + b.y * 100.0 + d * 10.0 + distance(back, p) + t[0][1] * 0.1 + v2.x * 0.01;
            }
            """;
        // p = (1,1,1): a = (6, 9, 7); b = (6, 5, 11); t[0][1] = m[1][0] = 0; v2 = (2,6)*mat2(1,2,0,1) = (2+12, 6) -> 14
        Assert.Equal(6000 + 500 + 10 + 0 + 0 + 0.14, Eval(src, 1, 1, 1), 1e-9);
    }

    [Fact]
    public void SwizzleAssignAndCtors()
    {
        const string src = """
            float sdf(vec3 p) {
                vec4 v = vec4(p.xy, 7.0, 8);
                v.wx = v.xw;                 // swap -> (8, y, 7, x)
                v.y += 10.0;
                v.z *= 2;
                vec2 q = vec2(v.zw);
                vec3 r = vec3(v);            // truncation
                return v.x * 1000.0 + v.y * 100.0 + q.x + r.z * 0.01 + v.a * 0.0001;
            }
            """;
        // p = (1, 2, 0): v = (1,2,7,8) -> (8,2,7,1) -> y = 12 -> z = 14; q = (14, 1); r = (8,12,14)
        Assert.Equal(8000 + 1200 + 14 + 0.14 + 0.0001, Eval(src, 1, 2, 0), 1e-9);
    }

    [Fact]
    public void IntsBoolsTernary()
    {
        const string src = """
            float sdf(vec3 p) {
                int a = 7 / 2;               // 3
                int b = 7 % 3;               // 1
                int z = 0;
                int c = 5 / z;               // CPU: 0 (undefined in GLSL, must not throw)
                bool neg = p.x < 0.0 && !(p.y > 0.0);
                float s = neg ? -1.0 : 1;
                a++; --b;
                return float(a * 10 + b + c) * s + float(true) * 0.5 + abs(-2) + max(1, 2.5);
            }
            """;
        Assert.Equal(-40 + 0.5 + 2 + 2.5, Eval(src, -1, -1, 0), Tol);
        Assert.Equal(40 + 0.5 + 2 + 2.5, Eval(src, 1, -1, 0), Tol);
    }

    [Fact]
    public void BuiltinsMatchGlslDefinitions()
    {
        const string src = """
            float sdf(vec3 p) {
                float a = mod(-1.5, 1.0);                       // 0.5
                float b = fract(-0.25);                         // 0.75
                float c = clamp(2.0, 0.0, 1.0) + sign(-3.0);    // 0
                float d = mix(1.0, 3.0, 0.25);                  // 1.5
                float e = smoothstep(0.0, 1.0, 0.5);            // 0.5
                float g = step(1.0, 0.5);                       // 0
                vec3 n = normalize(vec3(3.0, 0.0, 4.0));        // (0.6, 0, 0.8)
                vec3 rf = reflect(vec3(1.0, -1.0, 0.0), vec3(0.0, 1.0, 0.0)); // (1, 1, 0)
                vec3 cr = cross(vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0));     // (0, 0, 1)
                return a + b + c + d + e + g + n.z + rf.y + cr.z + atan(1.0, 1.0) + exp2(3.0) + inversesqrt(4.0);
            }
            """;
        Assert.Equal(0.5 + 0.75 + 0 + 1.5 + 0.5 + 0 + 0.8 + 1 + 1 + Math.PI / 4 + 8 + 0.5, Eval(src, 0, 0, 0), 1e-12);
    }

    [Fact]
    public void LibraryFunctions()
    {
        const string src = """
            float sdf(vec3 p) {
                float d = sdTorus(p, vec2(2.0, 0.5));
                d = smin(d, sdCappedCylinder(p, 1.0, 0.5), 0.2);
                d = smax(d, -sdEllipsoid(p, vec3(1.0, 2.0, 3.0)), 0.1);
                d = min(d, sdCappedCone(p - vec3(0.0, 3.0, 0.0), 1.0, 0.5, 0.2));
                d = min(d, sdPyramidScaled(p + vec3(0.0, 3.0, 0.0), 1.0, 2.0));
                return d + dot2(vec2(0.0));
            }
            """;
        var prog = Ok(src);
        Assert.Equal(1.5 - 0.5, Ok("float sdf(vec3 p) { return sdTorus(p, vec2(2.0, 0.5)); }").Cpu(3.5, 0, 0, []), Tol);
        Assert.Equal(0.5, Ok("float sdf(vec3 p) { return sdPyramid(p, 1.0); }").Cpu(0, 1.5, 0, []), Tol);
        Assert.True(double.IsFinite(prog.Cpu(0.3, 0.2, 0.1, [])));
    }

    [Fact]
    public void ShadowingAndScopes()
    {
        const string src = """
            float sdf(vec3 p) {
                float x = 1.0;
                { float x = x + 10.0; p.x += x; }   // inner x = 11, initializer reads the outer x
                if (true) { float x = 100.0; }
                return p.x + x;
            }
            """;
        Assert.Equal(11 + 1, Eval(src, 0, 0, 0), Tol);
    }

    [Fact]
    public void ThreadSafe()
    {
        var prog = Ok("""
            float g(vec3 q) { vec3 a = abs(q); a.x += 1.0; return max(a.x, max(a.y, a.z)); }
            float sdf(vec3 p) { float s = 0.0; for (int i = 0; i < 8; i++) s += g(p * float(i)); return s; }
            """);
        var expect = new double[10000];
        for (int i = 0; i < expect.Length; i++) expect[i] = prog.Cpu(i * 0.01, -i * 0.02, 0.5, []);
        var got = new double[expect.Length];
        Parallel.For(0, expect.Length, i => got[i] = prog.Cpu(i * 0.01, -i * 0.02, 0.5, []));
        Assert.Equal(expect, got);
    }

    [Fact]
    public void CacheAndHash()
    {
        const string a = "float sdf(vec3 p) { return length(p) - 1.0; }";
        Assert.Same(UserFnCompiler.Compile(a), UserFnCompiler.Compile(a));
        // whitespace / comments change the source but not the emitted code
        var b = UserFnCompiler.Compile("float sdf(vec3 p) {\n  // sphere\n  return length(p) - 1.0;\n}");
        Assert.Equal(UserFnCompiler.Compile(a).Program!.Hash, b.Program!.Hash);
        Assert.Equal(16, b.Program.Hash.Length);
        Assert.NotEqual(b.Program.Hash, Ok("float sdf(vec3 p) { return length(p) - 2.0; }").Hash);
    }
}
