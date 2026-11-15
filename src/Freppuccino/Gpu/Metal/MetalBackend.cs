using Freppuccino.Shaders;
using Rhino;
using Rhino.Display;
using static Freppuccino.Gpu.Metal.ObjC;

namespace Freppuccino.Gpu.Metal;

/// Render pass attachment formats; not queryable from an encoder, so assumed (see docs/viewport-shaders.md §3).
public readonly record struct MetalPassFormat(nuint Color, nuint DepthStencil, nuint Samples)
{
    public static readonly MetalPassFormat Default = new(Color: 70 /*RGBA8Unorm*/, DepthStencil: 260 /*Depth32Float_Stencil8*/, Samples: 4);
}

/// Draws on Rhino's borrowed MTLRenderCommandEncoder (never ended/retained/released). Owns its library/function/PSO/DSS objects.
public sealed unsafe class MetalBackend : IGpuBackend
{
    const nuint CompareAlways = 7, CullNone = 0, PrimitiveTriangle = 3;

    static readonly IntPtr selNewLibrary = Sel("newLibraryWithSource:options:error:"), selNewFunction = Sel("newFunctionWithName:"),
        selSetVertexFn = Sel("setVertexFunction:"), selSetFragmentFn = Sel("setFragmentFunction:"),
        selColorAttachments = Sel("colorAttachments"), selAtIndex = Sel("objectAtIndexedSubscript:"), selSetPixelFormat = Sel("setPixelFormat:"),
        selSetDepthFormat = Sel("setDepthAttachmentPixelFormat:"), selSetStencilFormat = Sel("setStencilAttachmentPixelFormat:"),
        selSetSampleCount = Sel("setRasterSampleCount:"), selNewPso = Sel("newRenderPipelineStateWithDescriptor:error:"),
        selSetDepthCompare = Sel("setDepthCompareFunction:"), selSetDepthWrite = Sel("setDepthWriteEnabled:"),
        selNewDss = Sel("newDepthStencilStateWithDescriptor:"),
        selSetPso = Sel("setRenderPipelineState:"), selSetDss = Sel("setDepthStencilState:"), selSetCull = Sel("setCullMode:"),
        selDraw = Sel("drawPrimitives:vertexStart:vertexCount:"),
        selSetVertexBytes = Sel("setVertexBytes:length:atIndex:"), selSetFragmentBytes = Sel("setFragmentBytes:length:atIndex:");

    IntPtr device, dss;                                                  // device borrowed (identity only); dss owned
    readonly Dictionary<string, (IntPtr vs, IntPtr fs)> functions = [];  // per ShaderSource.Hash; zeros = compile failed
    readonly Dictionary<(string, MetalPassFormat), IntPtr> psos = [];    // zero = build failed
    readonly Dictionary<string, string> errors = [];                     // per ShaderSource.Hash: shader compile failure
    readonly Dictionary<(string, MetalPassFormat), string> psoErrors = []; // per PSO: pipeline state failure
    bool loggedUnavailable, loggedException;

    public bool Draw(DisplayPipeline pipeline, ShaderSource program, ReadOnlySpan<byte> uniforms)
    {
        try
        {
            if (RhinoMetal.Error is { } err) { LogOnce(ref loggedUnavailable, "Metal access unavailable: " + err); return false; }
            var (dev, enc) = RhinoMetal.Get(pipeline);
            if (dev == IntPtr.Zero || enc == IntPtr.Zero) return false;
            if (dev != device) { ReleaseAll(); device = dev; }

            var pso = Pipeline(program, MetalPassFormat.Default);
            if (pso == IntPtr.Zero) return false;
            if (dss == IntPtr.Zero && (dss = BuildDepthStencil()) == IntPtr.Zero) return false;

            Send(enc, selSetPso, pso);
            Send(enc, selSetDss, dss);
            Send(enc, selSetCull, CullNone);
            if (!uniforms.IsEmpty)
                fixed (byte* u = uniforms)
                {
                    // copied by Metal at encode time; index chosen to stay clear of Rhino's buffer slots (see Uniforms.MetalIndex)
                    Send(enc, selSetVertexBytes, u, (nuint)uniforms.Length, Uniforms.MetalIndex);
                    Send(enc, selSetFragmentBytes, u, (nuint)uniforms.Length, Uniforms.MetalIndex);
                }
            Send(enc, selDraw, PrimitiveTriangle, 0, program.VertexCount);
            return true;
        }
        catch (Exception ex)
        {
            LogOnce(ref loggedException, ex.ToString());
            return false;
        }
    }

    IntPtr Pipeline(ShaderSource program, MetalPassFormat fmt)
    {
        var key = (program.Hash, fmt);
        if (psos.TryGetValue(key, out var pso)) return pso;
        psos[key] = IntPtr.Zero; // failures are not retried
        var (vs, fs) = Functions(program);
        if (vs == IntPtr.Zero) return IntPtr.Zero;

        var pool = objc_autoreleasePoolPush();
        var desc = New("MTLRenderPipelineDescriptor");
        try
        {
            Send(desc, selSetVertexFn, vs);
            Send(desc, selSetFragmentFn, fs);
            Send(Send(Send(desc, selColorAttachments), selAtIndex, (nuint)0), selSetPixelFormat, fmt.Color);
            Send(desc, selSetDepthFormat, fmt.DepthStencil);
            Send(desc, selSetStencilFormat, fmt.DepthStencil);
            Send(desc, selSetSampleCount, fmt.Samples);
            IntPtr error = IntPtr.Zero;
            pso = Send(device, selNewPso, desc, &error);
            if (pso == IntPtr.Zero)
            {
                psoErrors[key] = $"Metal pipeline state ({fmt}): {ErrorText(error)}";
                Log($"pipeline state failed ({program.Hash[..8]}, {fmt}): {ErrorText(error)}");
            }
            return psos[key] = pso;
        }
        finally
        {
            Release(ref desc);
            objc_autoreleasePoolPop(pool);
        }
    }

    (IntPtr vs, IntPtr fs) Functions(ShaderSource program)
    {
        if (functions.TryGetValue(program.Hash, out var f)) return f;
        functions[program.Hash] = default;
        IntPtr vs = IntPtr.Zero, fs = IntPtr.Zero;
        try
        {
            vs = Function(ShaderCompiler.ToMsl(ShaderCompiler.ToSpirv(program.Vertex, ShaderStage.Vertex), ShaderStage.Vertex), "vertex");
            fs = Function(ShaderCompiler.ToMsl(ShaderCompiler.ToSpirv(program.Fragment, ShaderStage.Fragment), ShaderStage.Fragment), "fragment");
            return functions[program.Hash] = (vs, fs);
        }
        catch (ShaderCompileException ex)
        {
            Release(ref vs); Release(ref fs);
            errors[program.Hash] = ex.Message;
            Log($"shader {program.Hash[..8]} failed: {ex.Message}");
            return default;
        }
    }

    /// +1 MTLFunction "main0" compiled from MSL with Rhino's device. Throws ShaderCompileException.
    IntPtr Function(string msl, string stage)
    {
        var pool = objc_autoreleasePoolPush();
        var src = NewString(msl);
        var name = NewString("main0");
        IntPtr lib = IntPtr.Zero;
        try
        {
            IntPtr error = IntPtr.Zero;
            lib = Send(device, selNewLibrary, src, IntPtr.Zero, &error);
            if (lib == IntPtr.Zero) throw new ShaderCompileException($"MSL {stage}: {ErrorText(error)}");
            var fn = Send(lib, selNewFunction, name);
            if (fn == IntPtr.Zero) throw new ShaderCompileException($"MSL {stage}: no main0");
            return fn;
        }
        finally
        {
            Release(ref lib); // function keeps what it needs
            Release(ref name); Release(ref src);
            objc_autoreleasePoolPop(pool);
        }
    }

    IntPtr BuildDepthStencil()
    {
        var d = New("MTLDepthStencilDescriptor");
        try
        {
            Send(d, selSetDepthCompare, CompareAlways);
            Send(d, selSetDepthWrite, (byte)0);
            var s = Send(device, selNewDss, d);
            if (s == IntPtr.Zero) Log("depth stencil state failed");
            return s;
        }
        finally { Release(ref d); }
    }

    void ReleaseAll()
    {
        foreach (var k in psos.Keys.ToList()) { var p = psos[k]; Release(ref p); }
        foreach (var (vs0, fs0) in functions.Values) { var vs = vs0; var fs = fs0; Release(ref vs); Release(ref fs); }
        psos.Clear(); functions.Clear(); errors.Clear(); psoErrors.Clear();
        Release(ref dss);
        device = IntPtr.Zero;
    }

    /// Shader compile failure (any pass), else the PSO failure for the format we draw with: stable across passes.
    public string? CompileError(ShaderSource program) =>
        errors.GetValueOrDefault(program.Hash) ?? psoErrors.GetValueOrDefault((program.Hash, MetalPassFormat.Default));

    public string? Compile(ShaderSource program)
    {
        if (device == IntPtr.Zero) return null;
        try { Pipeline(program, MetalPassFormat.Default); }
        catch (Exception ex) { LogOnce(ref loggedException, ex.ToString()); }
        return CompileError(program);
    }

    public void Dispose() => ReleaseAll();

    static void Log(string msg) => RhinoApp.WriteLine("Freppuccino Metal: " + msg);
    static void LogOnce(ref bool flag, string msg) { if (!flag) Log(msg); flag = true; }
}
