using Freppuccino.Shaders;
using Rhino.Display;

namespace Freppuccino.Gpu;

/// Draws a program on Rhino's own device, inside Rhino's current pass. Must not end/flush Rhino's pass or leak state Rhino relies on.
public interface IGpuBackend : IDisposable
{
    /// Draws program.VertexCount vertices (triangle list). `uniforms` (std140, <= Uniforms.MaxBytes) bound to both stages; may be empty.
    /// False if nothing was drawn (unsupported pass, compile failure already logged, etc.). Never throws.
    bool Draw(DisplayPipeline pipeline, ShaderSource program, ReadOnlySpan<byte> uniforms);

    /// Why <paramref name="program"/> can't be drawn (shader / pipeline compile failure, never retried), or null if it
    /// hasn't failed (yet). Lets the caller tell a broken program from a pass it just can't draw in.
    string? CompileError(ShaderSource program);

    /// Builds <paramref name="program"/> on the device last drawn with (cached like Draw; no drawing), then
    /// <see cref="CompileError"/>. Null if it builds, or if no device has been seen yet.
    string? Compile(ShaderSource program);

    bool Building(ShaderSource program) => false;
}

public static class GpuBackends
{
    static IGpuBackend? metal, d3d11, gl;

#if RHINO9
    public static IGpuBackend? For(DisplayPipeline p) => p.Technology switch
    {
        DisplayTechnology.Metal => metal ??= new Metal.MetalBackend(),
        DisplayTechnology.Direct3d11 => d3d11 ??= new D3D11.D3D11Backend(),
        DisplayTechnology.OpenGL when OperatingSystem.IsWindows() => gl ??= new OpenGL.GlBackend(),
        _ => null,
    };
#else
    public static IGpuBackend? For(DisplayPipeline p) =>
        OperatingSystem.IsMacOS() ? metal ??= new Metal.MetalBackend()
        : OperatingSystem.IsWindows() && p.IsOpenGL ? gl ??= new OpenGL.GlBackend()
        : null;
#endif

    /// Build error of <paramref name="program"/> on whichever backend exists (null: builds, or nothing has drawn yet).
    public static string? CompileError(ShaderSource program) => metal?.Compile(program) ?? d3d11?.Compile(program) ?? gl?.Compile(program);

    public static void DisposeAll() { metal?.Dispose(); d3d11?.Dispose(); gl?.Dispose(); metal = d3d11 = gl = null; }
}
