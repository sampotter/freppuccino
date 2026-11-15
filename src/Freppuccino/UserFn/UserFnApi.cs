// FFunction: user-written SDF code (GLSL subset) compiled to GLSL (viewport) + a C# delegate (mesher).
// SKELETON: the contract between agents. Implementation lives in the other UserFn/*.cs files.
// No Rhino dependency allowed in UserFn/ (tested standalone).
using System.Collections.Immutable;

namespace Freppuccino.UserFn;

/// 1-based line/column into the source.
public sealed record UserFnDiagnostic(int Line, int Column, string Message)
{
    public override string ToString() => $"{Line}:{Column}: {Message}";
}

/// `param float name = default;` Order = declaration order = index into the values array.
public sealed record UserFnParam(string Name, double Default);

/// CPU evaluation in the function's local frame. u = param values in declaration order. Thread-safe.
public delegate double UserFnEval(double x, double y, double z, double[] u);

public sealed class UserFnProgram
{
    public required string Source { get; init; }
    /// Stable short hex of the emitted GLSL; equal code => equal hash (shader dedup + cache key).
    public required string Hash { get; init; }
    public required ImmutableArray<UserFnParam> Params { get; init; }
    public required UserFnEval Cpu { get; init; }
    /// GLSL defining `float {name}(vec3 p[, float u0, float u1, ...])` plus mangled helpers ({name}_*).
    /// Calls only builtins + <see cref="UserFnLibrary.Glsl"/>. Splice after the library in the fragment shader.
    public required Func<string, string> Glsl { get; init; }
}

public sealed record UserFnResult(UserFnProgram? Program, ImmutableArray<UserFnDiagnostic> Diagnostics)
{
    public bool Ok => Program != null;
}

public static partial class UserFnCompiler
{
    /// Parse + check + emit. Never throws on bad input; errors come back as diagnostics. Cached by source.
    public static partial UserFnResult Compile(string source);
}

/// Per-object instance data (read from SDF.Code / SDF.L / SDF.u.*). Program null => code didn't compile (node is dropped).
public sealed record UserFnInstance(string Code, UserFnResult Result, ImmutableArray<double> Values, double L)
{
    public UserFnProgram? Program => Result.Program;
}
