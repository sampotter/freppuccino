using Freppuccino.Shaders;
using Freppuccino.UserFn;
using Xunit;
using Xunit.Abstractions;

namespace UserFn.Tests;

/// Emitted GLSL goes through the plugin's real pipeline: shaderc (GLSL 450 -> SPIR-V) -> SPIRV-Cross (MSL 2.1, HLSL SM5).
public class GlslTests(ITestOutputHelper log)
{
    public static readonly TheoryData<string, string> Samples = new()
    {
        { "sphere", "float sdf(vec3 p) { return length(p) - 1.0; }" },
        { "gyroid", "param float scale = 4.0;\nparam float thick = 0.1;\nfloat sdf(vec3 p) { vec3 q = p * scale; return abs(dot(sin(q), cos(q.yzx))) / scale - thick; }" },
        { "for_body_nested_shadow", "float sdf(vec3 p) {\n  float a = 0.0;\n  for (int i = 0; i < 3; i++) { { int i = 5; a += float(i); } }\n  return a;\n}" },
        { "library", """
            float sdf(vec3 p) {
                float d = sdTorus(p, vec2(2.0, 0.5));
                d = smin(d, sdCappedCylinder(p, 1.0, 0.5), 0.2);
                d = smax(d, -sdEllipsoid(p, vec3(1.0, 2.0, 3.0)), 0.1);
                d = min(d, sdCappedCone(p - vec3(0.0, 3.0, 0.0), 1.0, 0.5, 0.2));
                d = min(d, sdPyramidScaled(p + vec3(0.0, 3.0, 0.0), 1.0, 2.0));
                return min(d, sdBox(p, vec3(1.0))) + dot2(p.xy) * 0.0;
            }
            """ },
        { "helpers_params_matrices", """
            param float angle = 0.5;
            param float r = -1.0;
            const mat3 M = mat3(1.0, 2.0, 3.0, 0.0, 1.0, 4.0, 5.0, 6.0, 0.0);
            const vec3 OFF = vec3(1.0, 0.0, 0.0) * 2.0;
            const int N = 6;
            mat2 rot(float a) { float c = cos(a), s = sin(a); return mat2(c, s, -s, c); }
            float map(vec3 p) { return length(p) - r; }            // collides with the host's map() unless mangled
            float data(float x) { return x; }                        // ditto for the uniform array name
            float sdf(vec3 p) {
                vec3 u = p;                                          // local named like the uniform block instance
                u.xz = rot(angle) * u.xz;
                u = inverse(M) * (u - OFF) * transpose(M);
                mat2 m2 = mat2(M) * inverse(mat2(2.0)) + determinant(M);
                float acc = 0.0;
                for (int i = 0; i < N; i++) {
                    if (i == 3) continue;
                    acc += data(float(i)) * 0.01 + m2[1][0] + u[i % 3];
                    if (acc > 100.0) break;
                }
                for (int j = 10; j > 0; j -= 3) acc -= float(j / 2);
                bool b = u.x < 0.0 || !(u.y >= 1.0);
                int k = 7 % 3;
                k++;
                return (b ? map(u) : smoothstep(0.0, 1.0, acc)) + float(k) + PI * 0.0;
            }
            """ },
        { "builtins", """
            float sdf(vec3 p) {
                vec4 v = vec4(p, 1.0);
                vec4 a = radians(v) + degrees(v) + sin(v) + cos(v) + tan(v) + asin(v * 0.1) + acos(v * 0.1) + atan(v) + atan(v, v + 1.0);
                vec3 b = sinh(p) + cosh(p) + tanh(p) + pow(abs(p), vec3(2.0)) + exp(p) + log(abs(p) + 1.0) + exp2(p) + log2(abs(p) + 1.0);
                vec2 c = sqrt(abs(p.xy)) + inversesqrt(abs(p.xy) + 1.0) + abs(p.xy) + sign(p.xy) + floor(p.xy) + ceil(p.xy) + fract(p.xy);
                vec3 d = mod(p, 2.0) + mod(p, vec3(3.0)) + min(p, 1.0) + max(p, vec3(0.0)) + clamp(p, -1.0, 1.0) + clamp(p, vec3(0.0), vec3(1.0));
                vec3 e = mix(p, p * 2.0, 0.3) + mix(p, -p, vec3(0.5)) + step(0.5, p) + step(p, vec3(0.5)) + smoothstep(vec3(0.0), vec3(1.0), p);
                float g = length(p) + distance(p, vec3(1.0)) + dot(p, p) + length(cross(p, vec3(0.0, 1.0, 0.0))) + normalize(p + 1.0).x + reflect(p, vec3(0.0, 1.0, 0.0)).y;
                float h = float(abs(-3)) + float(sign(-2)) + float(min(1, 2)) + float(max(3, 4)) + float(clamp(5, 0, 2)) + float(int(2.7)) + float(bool(1));
                return a.x + a.w + b.y + c.x + d.z + e.x + g + h + float(v == vec4(1.0)) + float(p != vec3(0.0));
            }
            """ },
        { "edge_literals", """
            const float BIG = 1e30 * 1e30;
            const float TINY = 1.0e-30;
            const int NEG = -2147483647 - 1;
            float sdf(vec3 p) {
                float x = -0.0 + 1e-5 + .5 + 2. + 3.0f + TINY;
                if (p.x > 1e20) return BIG;
                return x + float(NEG) * 0.0 + float(-(-3));
            }
            """ },
    };

    static string Shader(params (string name, UserFnProgram prog)[] fns)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("""
            #version 450
            layout(location = 0) in vec2 vNdc;
            layout(location = 0) out vec4 outColor;
            layout(std140, set = 0, binding = 0) uniform Frame { vec4 data[8]; } u;
            const vec3 GREY = vec3(0.78);

            """);
        sb.Append(UserFnLibrary.Glsl).Append('\n');
        sb.Append("float map(vec3 p) { return length(p) - u.data[0].w; }\n\n");
        foreach (var (name, prog) in fns) sb.Append(prog.Glsl(name)).Append('\n');
        sb.Append("void main()\n{\n    vec3 p = vec3(vNdc, u.data[0].x);\n    float d = map(p);\n");
        foreach (var (name, prog) in fns)
        {
            var args = string.Concat(Enumerable.Range(0, prog.Params.Length).Select(i => $", u.data[{1 + i / 4}][{i % 4}]"));
            sb.Append($"    d = min(d, {name}(p{args}));\n");
        }
        sb.Append("    outColor = vec4(GREY * d, 1.0);\n}\n");
        return sb.ToString();
    }

    void CompileAll(string glsl)
    {
        NativeLibs.Install();
        byte[] spirv;
        try { spirv = ShaderCompiler.ToSpirv(glsl, ShaderStage.Fragment); }
        catch (ShaderCompileException) { log.WriteLine(glsl); throw; }
        Assert.Contains("main0", ShaderCompiler.ToMsl(spirv, ShaderStage.Fragment));
        Assert.Contains("main", ShaderCompiler.ToHlsl(spirv));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void CompilesToSpirvMslHlsl(string id, string src)
    {
        var prog = CpuTests.Ok(src);
        log.WriteLine($"--- {id} ---\n{prog.Glsl("ufn_" + id)}");
        CompileAll(Shader(("ufn_" + id, prog)));
        Assert.True(double.IsFinite(prog.Cpu(0.25, 0.5, 0.75, [.. prog.Params.Select(p => p.Default)])) || id == "edge_literals");
    }

    [Fact]
    public void SeveralFunctionsInOneShader()
    {
        var fns = Samples.Select((row, i) => ($"uf{i}", CpuTests.Ok((string)row[1]))).ToArray();
        CompileAll(Shader(fns));
    }

    [Fact]
    public void EmitsExpectedSignatureAndLiterals()
    {
        var prog = CpuTests.Ok("param float a = 1;\nparam float b = 2;\nfloat h(float x) { return x * 3.0 + 2; }\nfloat sdf(vec3 p) { return h(p.x) + a + b; }");
        var g = prog.Glsl("ufn");
        Assert.Contains("float ufn(vec3 l_p, float u0, float u1)", g);
        Assert.Contains("float ufn_h(float l_x, float u0, float u1)", g);
        Assert.Contains("ufn_h(l_p.x, u0, u1)", g);
        Assert.Contains("2.0", g);
        Assert.DoesNotContain("2)", g);
        var noParams = CpuTests.Ok("float sdf(vec3 p) { return 1.0; }").Glsl("x");
        Assert.Contains("float x(vec3 l_p)", noParams);
    }

    [Fact]
    public void LibraryIsVerbatimFromShaderGen()
    {
        var gen = File.ReadAllText(Path.Combine(RepoRoot(), "src/Freppuccino/Shaders/FrepShaderGen.cs"));
        if (!gen.Contains("UserFnLibrary.Glsl")) // until the integrator splices the library in, the inline copies must match
            foreach (var line in UserFnLibrary.Glsl.Split('\n').Where(l => l.Trim().Length > 0))
                Assert.Contains(line.Trim(), gen);
        CompileAll("#version 450\nlayout(location = 0) out vec4 o;\n" + UserFnLibrary.Glsl +
                   "\nvoid main() { vec3 p = vec3(gl_FragCoord.xy, 0.0); o = vec4(sdBox(p, vec3(1.0)) + sdTorus(p, vec2(1.0, 0.2)) + sdCappedCylinder(p, 1.0, 1.0)" +
                   " + sdCappedCone(p, 1.0, 1.0, 0.5) + sdEllipsoid(p, vec3(1.0)) + sdPyramidScaled(p, 1.0, 1.0) + smax(1.0, 2.0, 0.5)); }");
    }

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Freppuccino.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
