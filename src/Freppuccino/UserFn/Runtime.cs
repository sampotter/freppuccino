// Runtime for compiled FFunction code (CPU side, double precision), GLSL semantics.
// Vectors/matrices are small mutable structs; matrices are column-major (C0, C1, ...) like GLSL.
// Builtins' public static methods ARE the builtin overload set (the checker reflects over them): only GLSL-callable functions there.
namespace Freppuccino.UserFn.Runtime;

internal struct Vec2 : IEquatable<Vec2>
{
    public double X, Y;
    public Vec2(double x, double y) { X = x; Y = y; }
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator +(Vec2 a, double b) => new(a.X + b, a.Y + b);
    public static Vec2 operator +(double a, Vec2 b) => new(a + b.X, a + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a, double b) => new(a.X - b, a.Y - b);
    public static Vec2 operator -(double a, Vec2 b) => new(a - b.X, a - b.Y);
    public static Vec2 operator *(Vec2 a, Vec2 b) => new(a.X * b.X, a.Y * b.Y);
    public static Vec2 operator *(Vec2 a, double b) => new(a.X * b, a.Y * b);
    public static Vec2 operator *(double a, Vec2 b) => new(a * b.X, a * b.Y);
    public static Vec2 operator /(Vec2 a, Vec2 b) => new(a.X / b.X, a.Y / b.Y);
    public static Vec2 operator /(Vec2 a, double b) => new(a.X / b, a.Y / b);
    public static Vec2 operator /(double a, Vec2 b) => new(a / b.X, a / b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static bool operator ==(Vec2 a, Vec2 b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(Vec2 a, Vec2 b) => !(a == b);
    public readonly bool Equals(Vec2 o) => this == o;
    public override readonly bool Equals(object? o) => o is Vec2 v && this == v;
    public override readonly int GetHashCode() => HashCode.Combine(X, Y);
    public override readonly string ToString() => $"vec2({X}, {Y})";
}

internal struct Vec3 : IEquatable<Vec3>
{
    public double X, Y, Z;
    public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator +(Vec3 a, double b) => new(a.X + b, a.Y + b, a.Z + b);
    public static Vec3 operator +(double a, Vec3 b) => new(a + b.X, a + b.Y, a + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a, double b) => new(a.X - b, a.Y - b, a.Z - b);
    public static Vec3 operator -(double a, Vec3 b) => new(a - b.X, a - b.Y, a - b.Z);
    public static Vec3 operator *(Vec3 a, Vec3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    public static Vec3 operator *(Vec3 a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    public static Vec3 operator *(double a, Vec3 b) => new(a * b.X, a * b.Y, a * b.Z);
    public static Vec3 operator /(Vec3 a, Vec3 b) => new(a.X / b.X, a.Y / b.Y, a.Z / b.Z);
    public static Vec3 operator /(Vec3 a, double b) => new(a.X / b, a.Y / b, a.Z / b);
    public static Vec3 operator /(double a, Vec3 b) => new(a / b.X, a / b.Y, a / b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static bool operator ==(Vec3 a, Vec3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
    public static bool operator !=(Vec3 a, Vec3 b) => !(a == b);
    public readonly bool Equals(Vec3 o) => this == o;
    public override readonly bool Equals(object? o) => o is Vec3 v && this == v;
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z);
    public override readonly string ToString() => $"vec3({X}, {Y}, {Z})";
}

internal struct Vec4 : IEquatable<Vec4>
{
    public double X, Y, Z, W;
    public Vec4(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }
    public static Vec4 operator +(Vec4 a, Vec4 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
    public static Vec4 operator +(Vec4 a, double b) => new(a.X + b, a.Y + b, a.Z + b, a.W + b);
    public static Vec4 operator +(double a, Vec4 b) => new(a + b.X, a + b.Y, a + b.Z, a + b.W);
    public static Vec4 operator -(Vec4 a, Vec4 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);
    public static Vec4 operator -(Vec4 a, double b) => new(a.X - b, a.Y - b, a.Z - b, a.W - b);
    public static Vec4 operator -(double a, Vec4 b) => new(a - b.X, a - b.Y, a - b.Z, a - b.W);
    public static Vec4 operator *(Vec4 a, Vec4 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z, a.W * b.W);
    public static Vec4 operator *(Vec4 a, double b) => new(a.X * b, a.Y * b, a.Z * b, a.W * b);
    public static Vec4 operator *(double a, Vec4 b) => new(a * b.X, a * b.Y, a * b.Z, a * b.W);
    public static Vec4 operator /(Vec4 a, Vec4 b) => new(a.X / b.X, a.Y / b.Y, a.Z / b.Z, a.W / b.W);
    public static Vec4 operator /(Vec4 a, double b) => new(a.X / b, a.Y / b, a.Z / b, a.W / b);
    public static Vec4 operator /(double a, Vec4 b) => new(a / b.X, a / b.Y, a / b.Z, a / b.W);
    public static Vec4 operator -(Vec4 a) => new(-a.X, -a.Y, -a.Z, -a.W);
    public static bool operator ==(Vec4 a, Vec4 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.W == b.W;
    public static bool operator !=(Vec4 a, Vec4 b) => !(a == b);
    public readonly bool Equals(Vec4 o) => this == o;
    public override readonly bool Equals(object? o) => o is Vec4 v && this == v;
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z, W);
    public override readonly string ToString() => $"vec4({X}, {Y}, {Z}, {W})";
}

internal struct Mat2 : IEquatable<Mat2>
{
    public Vec2 C0, C1; // columns
    public Mat2(Vec2 c0, Vec2 c1) { C0 = c0; C1 = c1; }
    public static Mat2 operator +(Mat2 a, Mat2 b) => new(a.C0 + b.C0, a.C1 + b.C1);
    public static Mat2 operator -(Mat2 a, Mat2 b) => new(a.C0 - b.C0, a.C1 - b.C1);
    public static Mat2 operator /(Mat2 a, Mat2 b) => new(a.C0 / b.C0, a.C1 / b.C1);
    public static Mat2 operator +(Mat2 a, double b) => new(a.C0 + b, a.C1 + b);
    public static Mat2 operator +(double a, Mat2 b) => new(a + b.C0, a + b.C1);
    public static Mat2 operator -(Mat2 a, double b) => new(a.C0 - b, a.C1 - b);
    public static Mat2 operator -(double a, Mat2 b) => new(a - b.C0, a - b.C1);
    public static Mat2 operator *(Mat2 a, double b) => new(a.C0 * b, a.C1 * b);
    public static Mat2 operator *(double a, Mat2 b) => new(a * b.C0, a * b.C1);
    public static Mat2 operator /(Mat2 a, double b) => new(a.C0 / b, a.C1 / b);
    public static Mat2 operator /(double a, Mat2 b) => new(a / b.C0, a / b.C1);
    public static Mat2 operator -(Mat2 a) => new(-a.C0, -a.C1);
    public static Vec2 operator *(Mat2 m, Vec2 v) => m.C0 * v.X + m.C1 * v.Y;
    public static Vec2 operator *(Vec2 v, Mat2 m) => new(Builtins.dot(v, m.C0), Builtins.dot(v, m.C1));
    public static Mat2 operator *(Mat2 a, Mat2 b) => new(a * b.C0, a * b.C1);
    public static bool operator ==(Mat2 a, Mat2 b) => a.C0 == b.C0 && a.C1 == b.C1;
    public static bool operator !=(Mat2 a, Mat2 b) => !(a == b);
    public readonly bool Equals(Mat2 o) => this == o;
    public override readonly bool Equals(object? o) => o is Mat2 v && this == v;
    public override readonly int GetHashCode() => HashCode.Combine(C0, C1);
    public override readonly string ToString() => $"mat2({C0}, {C1})";
}

internal struct Mat3 : IEquatable<Mat3>
{
    public Vec3 C0, C1, C2; // columns
    public Mat3(Vec3 c0, Vec3 c1, Vec3 c2) { C0 = c0; C1 = c1; C2 = c2; }
    public static Mat3 operator +(Mat3 a, Mat3 b) => new(a.C0 + b.C0, a.C1 + b.C1, a.C2 + b.C2);
    public static Mat3 operator -(Mat3 a, Mat3 b) => new(a.C0 - b.C0, a.C1 - b.C1, a.C2 - b.C2);
    public static Mat3 operator /(Mat3 a, Mat3 b) => new(a.C0 / b.C0, a.C1 / b.C1, a.C2 / b.C2);
    public static Mat3 operator +(Mat3 a, double b) => new(a.C0 + b, a.C1 + b, a.C2 + b);
    public static Mat3 operator +(double a, Mat3 b) => new(a + b.C0, a + b.C1, a + b.C2);
    public static Mat3 operator -(Mat3 a, double b) => new(a.C0 - b, a.C1 - b, a.C2 - b);
    public static Mat3 operator -(double a, Mat3 b) => new(a - b.C0, a - b.C1, a - b.C2);
    public static Mat3 operator *(Mat3 a, double b) => new(a.C0 * b, a.C1 * b, a.C2 * b);
    public static Mat3 operator *(double a, Mat3 b) => new(a * b.C0, a * b.C1, a * b.C2);
    public static Mat3 operator /(Mat3 a, double b) => new(a.C0 / b, a.C1 / b, a.C2 / b);
    public static Mat3 operator /(double a, Mat3 b) => new(a / b.C0, a / b.C1, a / b.C2);
    public static Mat3 operator -(Mat3 a) => new(-a.C0, -a.C1, -a.C2);
    public static Vec3 operator *(Mat3 m, Vec3 v) => m.C0 * v.X + m.C1 * v.Y + m.C2 * v.Z;
    public static Vec3 operator *(Vec3 v, Mat3 m) => new(Builtins.dot(v, m.C0), Builtins.dot(v, m.C1), Builtins.dot(v, m.C2));
    public static Mat3 operator *(Mat3 a, Mat3 b) => new(a * b.C0, a * b.C1, a * b.C2);
    public static bool operator ==(Mat3 a, Mat3 b) => a.C0 == b.C0 && a.C1 == b.C1 && a.C2 == b.C2;
    public static bool operator !=(Mat3 a, Mat3 b) => !(a == b);
    public readonly bool Equals(Mat3 o) => this == o;
    public override readonly bool Equals(object? o) => o is Mat3 v && this == v;
    public override readonly int GetHashCode() => HashCode.Combine(C0, C1, C2);
    public override readonly string ToString() => $"mat3({C0}, {C1}, {C2})";
}

/// GLSL-callable builtins + library. Public static methods here ARE the builtin overload set (see Checker).
internal static class Builtins
{
    public static double radians(double x) => x * (Math.PI / 180.0);
    public static double degrees(double x) => x * (180.0 / Math.PI);
    public static double sin(double x) => Math.Sin(x);
    public static double cos(double x) => Math.Cos(x);
    public static double tan(double x) => Math.Tan(x);
    public static double asin(double x) => Math.Asin(x);
    public static double acos(double x) => Math.Acos(x);
    public static double atan(double x) => Math.Atan(x);
    public static double sinh(double x) => Math.Sinh(x);
    public static double cosh(double x) => Math.Cosh(x);
    public static double tanh(double x) => Math.Tanh(x);
    public static double exp(double x) => Math.Exp(x);
    public static double log(double x) => Math.Log(x);
    public static double exp2(double x) => Math.Pow(2.0, x);
    public static double log2(double x) => Math.Log2(x);
    public static double sqrt(double x) => Math.Sqrt(x);
    public static double inversesqrt(double x) => 1.0 / Math.Sqrt(x);
    public static double abs(double x) => Math.Abs(x);
    public static double sign(double x) => x > 0.0 ? 1.0 : x < 0.0 ? -1.0 : 0.0;
    public static double floor(double x) => Math.Floor(x);
    public static double ceil(double x) => Math.Ceiling(x);
    public static double fract(double x) => x - Math.Floor(x);
    public static double atan(double y, double x) => Math.Atan2(y, x);
    public static double pow(double x, double y) => Math.Pow(x, y);
    public static double mod(double x, double y) => x - y * Math.Floor(x / y);
    public static double min(double x, double y) => y < x ? y : x;
    public static double max(double x, double y) => x < y ? y : x;
    public static double step(double edge, double x) => x < edge ? 0.0 : 1.0;
    public static double clamp(double x, double minVal, double maxVal) => min(max(x, minVal), maxVal);
    public static double mix(double x, double y, double a) => x * (1.0 - a) + y * a;
    public static double smoothstep(double edge0, double edge1, double x) { double t = clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0); return t * t * (3.0 - 2.0 * t); }
    public static int abs(int x) => x < 0 ? -x : x;
    public static int sign(int x) => x > 0 ? 1 : x < 0 ? -1 : 0;
    public static int min(int x, int y) => y < x ? y : x;
    public static int max(int x, int y) => x < y ? y : x;
    public static int clamp(int x, int minVal, int maxVal) => min(max(x, minVal), maxVal);
    public static double length(double x) => Math.Abs(x);
    public static double distance(double a, double b) => Math.Abs(a - b);
    public static double dot(double a, double b) => a * b;
    public static double normalize(double x) => x / Math.Abs(x);
    public static double reflect(double i, double n) => i - 2.0 * dot(n, i) * n;

    // ---- Vec2 ----
    public static Vec2 radians(Vec2 x) => new(radians(x.X), radians(x.Y));
    public static Vec2 degrees(Vec2 x) => new(degrees(x.X), degrees(x.Y));
    public static Vec2 sin(Vec2 x) => new(sin(x.X), sin(x.Y));
    public static Vec2 cos(Vec2 x) => new(cos(x.X), cos(x.Y));
    public static Vec2 tan(Vec2 x) => new(tan(x.X), tan(x.Y));
    public static Vec2 asin(Vec2 x) => new(asin(x.X), asin(x.Y));
    public static Vec2 acos(Vec2 x) => new(acos(x.X), acos(x.Y));
    public static Vec2 atan(Vec2 x) => new(atan(x.X), atan(x.Y));
    public static Vec2 sinh(Vec2 x) => new(sinh(x.X), sinh(x.Y));
    public static Vec2 cosh(Vec2 x) => new(cosh(x.X), cosh(x.Y));
    public static Vec2 tanh(Vec2 x) => new(tanh(x.X), tanh(x.Y));
    public static Vec2 exp(Vec2 x) => new(exp(x.X), exp(x.Y));
    public static Vec2 log(Vec2 x) => new(log(x.X), log(x.Y));
    public static Vec2 exp2(Vec2 x) => new(exp2(x.X), exp2(x.Y));
    public static Vec2 log2(Vec2 x) => new(log2(x.X), log2(x.Y));
    public static Vec2 sqrt(Vec2 x) => new(sqrt(x.X), sqrt(x.Y));
    public static Vec2 inversesqrt(Vec2 x) => new(inversesqrt(x.X), inversesqrt(x.Y));
    public static Vec2 abs(Vec2 x) => new(abs(x.X), abs(x.Y));
    public static Vec2 sign(Vec2 x) => new(sign(x.X), sign(x.Y));
    public static Vec2 floor(Vec2 x) => new(floor(x.X), floor(x.Y));
    public static Vec2 ceil(Vec2 x) => new(ceil(x.X), ceil(x.Y));
    public static Vec2 fract(Vec2 x) => new(fract(x.X), fract(x.Y));
    public static Vec2 atan(Vec2 y, Vec2 x) => new(atan(y.X, x.X), atan(y.Y, x.Y));
    public static Vec2 pow(Vec2 x, Vec2 y) => new(pow(x.X, y.X), pow(x.Y, y.Y));
    public static Vec2 mod(Vec2 x, Vec2 y) => new(mod(x.X, y.X), mod(x.Y, y.Y));
    public static Vec2 min(Vec2 x, Vec2 y) => new(min(x.X, y.X), min(x.Y, y.Y));
    public static Vec2 max(Vec2 x, Vec2 y) => new(max(x.X, y.X), max(x.Y, y.Y));
    public static Vec2 step(Vec2 edge, Vec2 x) => new(step(edge.X, x.X), step(edge.Y, x.Y));
    public static Vec2 mod(Vec2 x, double y) => new(mod(x.X, y), mod(x.Y, y));
    public static Vec2 min(Vec2 x, double y) => new(min(x.X, y), min(x.Y, y));
    public static Vec2 max(Vec2 x, double y) => new(max(x.X, y), max(x.Y, y));
    public static Vec2 step(double edge, Vec2 x) => new(step(edge, x.X), step(edge, x.Y));
    public static Vec2 clamp(Vec2 x, Vec2 minVal, Vec2 maxVal) => new(clamp(x.X, minVal.X, maxVal.X), clamp(x.Y, minVal.Y, maxVal.Y));
    public static Vec2 clamp(Vec2 x, double minVal, double maxVal) => new(clamp(x.X, minVal, maxVal), clamp(x.Y, minVal, maxVal));
    public static Vec2 mix(Vec2 x, Vec2 y, Vec2 a) => new(mix(x.X, y.X, a.X), mix(x.Y, y.Y, a.Y));
    public static Vec2 mix(Vec2 x, Vec2 y, double a) => new(mix(x.X, y.X, a), mix(x.Y, y.Y, a));
    public static Vec2 smoothstep(Vec2 edge0, Vec2 edge1, Vec2 x) => new(smoothstep(edge0.X, edge1.X, x.X), smoothstep(edge0.Y, edge1.Y, x.Y));
    public static Vec2 smoothstep(double edge0, double edge1, Vec2 x) => new(smoothstep(edge0, edge1, x.X), smoothstep(edge0, edge1, x.Y));
    public static double dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
    public static double length(Vec2 x) => Math.Sqrt(dot(x, x));
    public static double distance(Vec2 a, Vec2 b) => length(a - b);
    public static Vec2 normalize(Vec2 x) => x / length(x);
    public static Vec2 reflect(Vec2 i, Vec2 n) => i - 2.0 * dot(n, i) * n;

    // ---- Vec3 ----
    public static Vec3 radians(Vec3 x) => new(radians(x.X), radians(x.Y), radians(x.Z));
    public static Vec3 degrees(Vec3 x) => new(degrees(x.X), degrees(x.Y), degrees(x.Z));
    public static Vec3 sin(Vec3 x) => new(sin(x.X), sin(x.Y), sin(x.Z));
    public static Vec3 cos(Vec3 x) => new(cos(x.X), cos(x.Y), cos(x.Z));
    public static Vec3 tan(Vec3 x) => new(tan(x.X), tan(x.Y), tan(x.Z));
    public static Vec3 asin(Vec3 x) => new(asin(x.X), asin(x.Y), asin(x.Z));
    public static Vec3 acos(Vec3 x) => new(acos(x.X), acos(x.Y), acos(x.Z));
    public static Vec3 atan(Vec3 x) => new(atan(x.X), atan(x.Y), atan(x.Z));
    public static Vec3 sinh(Vec3 x) => new(sinh(x.X), sinh(x.Y), sinh(x.Z));
    public static Vec3 cosh(Vec3 x) => new(cosh(x.X), cosh(x.Y), cosh(x.Z));
    public static Vec3 tanh(Vec3 x) => new(tanh(x.X), tanh(x.Y), tanh(x.Z));
    public static Vec3 exp(Vec3 x) => new(exp(x.X), exp(x.Y), exp(x.Z));
    public static Vec3 log(Vec3 x) => new(log(x.X), log(x.Y), log(x.Z));
    public static Vec3 exp2(Vec3 x) => new(exp2(x.X), exp2(x.Y), exp2(x.Z));
    public static Vec3 log2(Vec3 x) => new(log2(x.X), log2(x.Y), log2(x.Z));
    public static Vec3 sqrt(Vec3 x) => new(sqrt(x.X), sqrt(x.Y), sqrt(x.Z));
    public static Vec3 inversesqrt(Vec3 x) => new(inversesqrt(x.X), inversesqrt(x.Y), inversesqrt(x.Z));
    public static Vec3 abs(Vec3 x) => new(abs(x.X), abs(x.Y), abs(x.Z));
    public static Vec3 sign(Vec3 x) => new(sign(x.X), sign(x.Y), sign(x.Z));
    public static Vec3 floor(Vec3 x) => new(floor(x.X), floor(x.Y), floor(x.Z));
    public static Vec3 ceil(Vec3 x) => new(ceil(x.X), ceil(x.Y), ceil(x.Z));
    public static Vec3 fract(Vec3 x) => new(fract(x.X), fract(x.Y), fract(x.Z));
    public static Vec3 atan(Vec3 y, Vec3 x) => new(atan(y.X, x.X), atan(y.Y, x.Y), atan(y.Z, x.Z));
    public static Vec3 pow(Vec3 x, Vec3 y) => new(pow(x.X, y.X), pow(x.Y, y.Y), pow(x.Z, y.Z));
    public static Vec3 mod(Vec3 x, Vec3 y) => new(mod(x.X, y.X), mod(x.Y, y.Y), mod(x.Z, y.Z));
    public static Vec3 min(Vec3 x, Vec3 y) => new(min(x.X, y.X), min(x.Y, y.Y), min(x.Z, y.Z));
    public static Vec3 max(Vec3 x, Vec3 y) => new(max(x.X, y.X), max(x.Y, y.Y), max(x.Z, y.Z));
    public static Vec3 step(Vec3 edge, Vec3 x) => new(step(edge.X, x.X), step(edge.Y, x.Y), step(edge.Z, x.Z));
    public static Vec3 mod(Vec3 x, double y) => new(mod(x.X, y), mod(x.Y, y), mod(x.Z, y));
    public static Vec3 min(Vec3 x, double y) => new(min(x.X, y), min(x.Y, y), min(x.Z, y));
    public static Vec3 max(Vec3 x, double y) => new(max(x.X, y), max(x.Y, y), max(x.Z, y));
    public static Vec3 step(double edge, Vec3 x) => new(step(edge, x.X), step(edge, x.Y), step(edge, x.Z));
    public static Vec3 clamp(Vec3 x, Vec3 minVal, Vec3 maxVal) => new(clamp(x.X, minVal.X, maxVal.X), clamp(x.Y, minVal.Y, maxVal.Y), clamp(x.Z, minVal.Z, maxVal.Z));
    public static Vec3 clamp(Vec3 x, double minVal, double maxVal) => new(clamp(x.X, minVal, maxVal), clamp(x.Y, minVal, maxVal), clamp(x.Z, minVal, maxVal));
    public static Vec3 mix(Vec3 x, Vec3 y, Vec3 a) => new(mix(x.X, y.X, a.X), mix(x.Y, y.Y, a.Y), mix(x.Z, y.Z, a.Z));
    public static Vec3 mix(Vec3 x, Vec3 y, double a) => new(mix(x.X, y.X, a), mix(x.Y, y.Y, a), mix(x.Z, y.Z, a));
    public static Vec3 smoothstep(Vec3 edge0, Vec3 edge1, Vec3 x) => new(smoothstep(edge0.X, edge1.X, x.X), smoothstep(edge0.Y, edge1.Y, x.Y), smoothstep(edge0.Z, edge1.Z, x.Z));
    public static Vec3 smoothstep(double edge0, double edge1, Vec3 x) => new(smoothstep(edge0, edge1, x.X), smoothstep(edge0, edge1, x.Y), smoothstep(edge0, edge1, x.Z));
    public static double dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static double length(Vec3 x) => Math.Sqrt(dot(x, x));
    public static double distance(Vec3 a, Vec3 b) => length(a - b);
    public static Vec3 normalize(Vec3 x) => x / length(x);
    public static Vec3 reflect(Vec3 i, Vec3 n) => i - 2.0 * dot(n, i) * n;

    // ---- Vec4 ----
    public static Vec4 radians(Vec4 x) => new(radians(x.X), radians(x.Y), radians(x.Z), radians(x.W));
    public static Vec4 degrees(Vec4 x) => new(degrees(x.X), degrees(x.Y), degrees(x.Z), degrees(x.W));
    public static Vec4 sin(Vec4 x) => new(sin(x.X), sin(x.Y), sin(x.Z), sin(x.W));
    public static Vec4 cos(Vec4 x) => new(cos(x.X), cos(x.Y), cos(x.Z), cos(x.W));
    public static Vec4 tan(Vec4 x) => new(tan(x.X), tan(x.Y), tan(x.Z), tan(x.W));
    public static Vec4 asin(Vec4 x) => new(asin(x.X), asin(x.Y), asin(x.Z), asin(x.W));
    public static Vec4 acos(Vec4 x) => new(acos(x.X), acos(x.Y), acos(x.Z), acos(x.W));
    public static Vec4 atan(Vec4 x) => new(atan(x.X), atan(x.Y), atan(x.Z), atan(x.W));
    public static Vec4 sinh(Vec4 x) => new(sinh(x.X), sinh(x.Y), sinh(x.Z), sinh(x.W));
    public static Vec4 cosh(Vec4 x) => new(cosh(x.X), cosh(x.Y), cosh(x.Z), cosh(x.W));
    public static Vec4 tanh(Vec4 x) => new(tanh(x.X), tanh(x.Y), tanh(x.Z), tanh(x.W));
    public static Vec4 exp(Vec4 x) => new(exp(x.X), exp(x.Y), exp(x.Z), exp(x.W));
    public static Vec4 log(Vec4 x) => new(log(x.X), log(x.Y), log(x.Z), log(x.W));
    public static Vec4 exp2(Vec4 x) => new(exp2(x.X), exp2(x.Y), exp2(x.Z), exp2(x.W));
    public static Vec4 log2(Vec4 x) => new(log2(x.X), log2(x.Y), log2(x.Z), log2(x.W));
    public static Vec4 sqrt(Vec4 x) => new(sqrt(x.X), sqrt(x.Y), sqrt(x.Z), sqrt(x.W));
    public static Vec4 inversesqrt(Vec4 x) => new(inversesqrt(x.X), inversesqrt(x.Y), inversesqrt(x.Z), inversesqrt(x.W));
    public static Vec4 abs(Vec4 x) => new(abs(x.X), abs(x.Y), abs(x.Z), abs(x.W));
    public static Vec4 sign(Vec4 x) => new(sign(x.X), sign(x.Y), sign(x.Z), sign(x.W));
    public static Vec4 floor(Vec4 x) => new(floor(x.X), floor(x.Y), floor(x.Z), floor(x.W));
    public static Vec4 ceil(Vec4 x) => new(ceil(x.X), ceil(x.Y), ceil(x.Z), ceil(x.W));
    public static Vec4 fract(Vec4 x) => new(fract(x.X), fract(x.Y), fract(x.Z), fract(x.W));
    public static Vec4 atan(Vec4 y, Vec4 x) => new(atan(y.X, x.X), atan(y.Y, x.Y), atan(y.Z, x.Z), atan(y.W, x.W));
    public static Vec4 pow(Vec4 x, Vec4 y) => new(pow(x.X, y.X), pow(x.Y, y.Y), pow(x.Z, y.Z), pow(x.W, y.W));
    public static Vec4 mod(Vec4 x, Vec4 y) => new(mod(x.X, y.X), mod(x.Y, y.Y), mod(x.Z, y.Z), mod(x.W, y.W));
    public static Vec4 min(Vec4 x, Vec4 y) => new(min(x.X, y.X), min(x.Y, y.Y), min(x.Z, y.Z), min(x.W, y.W));
    public static Vec4 max(Vec4 x, Vec4 y) => new(max(x.X, y.X), max(x.Y, y.Y), max(x.Z, y.Z), max(x.W, y.W));
    public static Vec4 step(Vec4 edge, Vec4 x) => new(step(edge.X, x.X), step(edge.Y, x.Y), step(edge.Z, x.Z), step(edge.W, x.W));
    public static Vec4 mod(Vec4 x, double y) => new(mod(x.X, y), mod(x.Y, y), mod(x.Z, y), mod(x.W, y));
    public static Vec4 min(Vec4 x, double y) => new(min(x.X, y), min(x.Y, y), min(x.Z, y), min(x.W, y));
    public static Vec4 max(Vec4 x, double y) => new(max(x.X, y), max(x.Y, y), max(x.Z, y), max(x.W, y));
    public static Vec4 step(double edge, Vec4 x) => new(step(edge, x.X), step(edge, x.Y), step(edge, x.Z), step(edge, x.W));
    public static Vec4 clamp(Vec4 x, Vec4 minVal, Vec4 maxVal) => new(clamp(x.X, minVal.X, maxVal.X), clamp(x.Y, minVal.Y, maxVal.Y), clamp(x.Z, minVal.Z, maxVal.Z), clamp(x.W, minVal.W, maxVal.W));
    public static Vec4 clamp(Vec4 x, double minVal, double maxVal) => new(clamp(x.X, minVal, maxVal), clamp(x.Y, minVal, maxVal), clamp(x.Z, minVal, maxVal), clamp(x.W, minVal, maxVal));
    public static Vec4 mix(Vec4 x, Vec4 y, Vec4 a) => new(mix(x.X, y.X, a.X), mix(x.Y, y.Y, a.Y), mix(x.Z, y.Z, a.Z), mix(x.W, y.W, a.W));
    public static Vec4 mix(Vec4 x, Vec4 y, double a) => new(mix(x.X, y.X, a), mix(x.Y, y.Y, a), mix(x.Z, y.Z, a), mix(x.W, y.W, a));
    public static Vec4 smoothstep(Vec4 edge0, Vec4 edge1, Vec4 x) => new(smoothstep(edge0.X, edge1.X, x.X), smoothstep(edge0.Y, edge1.Y, x.Y), smoothstep(edge0.Z, edge1.Z, x.Z), smoothstep(edge0.W, edge1.W, x.W));
    public static Vec4 smoothstep(double edge0, double edge1, Vec4 x) => new(smoothstep(edge0, edge1, x.X), smoothstep(edge0, edge1, x.Y), smoothstep(edge0, edge1, x.Z), smoothstep(edge0, edge1, x.W));
    public static double dot(Vec4 a, Vec4 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
    public static double length(Vec4 x) => Math.Sqrt(dot(x, x));
    public static double distance(Vec4 a, Vec4 b) => length(a - b);
    public static Vec4 normalize(Vec4 x) => x / length(x);
    public static Vec4 reflect(Vec4 i, Vec4 n) => i - 2.0 * dot(n, i) * n;

    public static Vec3 cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    // ---- matrices ----
    public static Mat2 transpose(Mat2 m) => new(new(m.C0.X, m.C1.X), new(m.C0.Y, m.C1.Y));
    public static Mat3 transpose(Mat3 m) => new(new(m.C0.X, m.C1.X, m.C2.X), new(m.C0.Y, m.C1.Y, m.C2.Y), new(m.C0.Z, m.C1.Z, m.C2.Z));
    public static double determinant(Mat2 m) => m.C0.X * m.C1.Y - m.C1.X * m.C0.Y;
    public static double determinant(Mat3 m) => dot(m.C0, cross(m.C1, m.C2));
    public static Mat2 inverse(Mat2 m) => new Mat2(new(m.C1.Y, -m.C0.Y), new(-m.C1.X, m.C0.X)) / determinant(m);
    // Rows of the inverse are cross products of columns, over the determinant.
    public static Mat3 inverse(Mat3 m) => transpose(new Mat3(cross(m.C1, m.C2), cross(m.C2, m.C0), cross(m.C0, m.C1))) / determinant(m);

    // ---- library: faithful ports of UserFnLibrary.Glsl ----
    public static double smin(double a, double b, double k)
    {
        if (k <= 0.0) return min(a, b);
        double h = max(k - Math.Abs(a - b), 0.0) / k;
        return min(a, b) - h * h * k * 0.25;
    }
    public static double smax(double a, double b, double k) => -smin(-a, -b, k);
    public static double dot2(Vec2 v) => dot(v, v);
    public static double sdBox(Vec3 p, Vec3 b)
    {
        Vec3 q = abs(p) - b;
        return length(max(q, 0.0)) + min(max(q.X, max(q.Y, q.Z)), 0.0);
    }
    public static double sdTorus(Vec3 p, Vec2 t)
    {
        Vec2 q = new(length(new Vec2(p.X, p.Z)) - t.X, p.Y);
        return length(q) - t.Y;
    }
    public static double sdCappedCylinder(Vec3 p, double h, double r)
    {
        Vec2 d = abs(new Vec2(length(new Vec2(p.X, p.Z)), p.Y)) - new Vec2(r, h);
        return min(max(d.X, d.Y), 0.0) + length(max(d, 0.0));
    }
    public static double sdCappedCone(Vec3 p, double h, double r1, double r2)
    {
        Vec2 q = new(length(new Vec2(p.X, p.Z)), p.Y);
        Vec2 k1 = new(r2, h);
        Vec2 k2 = new(r2 - r1, 2.0 * h);
        Vec2 ca = new(q.X - min(q.X, (q.Y < 0.0) ? r1 : r2), Math.Abs(q.Y) - h);
        Vec2 cb = q - k1 + k2 * clamp(dot(k1 - q, k2) / dot2(k2), 0.0, 1.0);
        double s = (cb.X < 0.0 && ca.Y < 0.0) ? -1.0 : 1.0;
        return s * Math.Sqrt(min(dot2(ca), dot2(cb)));
    }
    public static double sdEllipsoid(Vec3 p, Vec3 r) => (length(p / r) - 1.0) * min(r.X, min(r.Y, r.Z));
    public static double sdPyramid(Vec3 p, double h)
    {
        double m2 = h * h + 0.25;
        p.X = Math.Abs(p.X); p.Z = Math.Abs(p.Z);
        if (p.Z > p.X) (p.X, p.Z) = (p.Z, p.X);
        p.X -= 0.5; p.Z -= 0.5;
        Vec3 q = new(p.Z, h * p.Y - 0.5 * p.X, h * p.X + 0.5 * p.Y);
        double s = max(-q.X, 0.0);
        double t = clamp((q.Y - 0.5 * p.Z) / (m2 + 0.25), 0.0, 1.0);
        double a = m2 * (q.X + s) * (q.X + s) + q.Y * q.Y;
        double b = m2 * (q.X + 0.5 * t) * (q.X + 0.5 * t) + (q.Y - m2 * t) * (q.Y - m2 * t);
        double d2 = min(q.Y, -q.X * m2 - q.Y * 0.5) > 0.0 ? 0.0 : min(a, b);
        double d = Math.Sqrt((d2 + q.Z * q.Z) / m2) * sign(max(q.Z, -p.Y));
        if (p.Y < 0.0) return length(new Vec3(max(p.X, 0.0), p.Y, max(p.Z, 0.0)));
        return max(d, -p.Y);
    }
    public static double sdPyramidScaled(Vec3 p, double h, double s) => sdPyramid(p / s, h / s) * s;
}

/// Non-GLSL helpers the compiled code calls (int ops that must not throw, indexing, matrix ctors).
internal static class Rt
{
    public static int IDiv(int a, int b) => b == 0 ? 0 : b == -1 ? unchecked(-a) : a / b;
    public static int IMod(int a, int b) => b == 0 || b == -1 ? 0 : a % b;
    public static int FtoI(double x) => double.IsNaN(x) ? 0 : x >= int.MaxValue ? int.MaxValue : x <= int.MinValue ? int.MinValue : (int)x;
    public static double Idx(Vec2 v, int i) => i <= 0 ? v.X : v.Y;
    public static double Idx(Vec3 v, int i) => i <= 0 ? v.X : i == 1 ? v.Y : v.Z;
    public static double Idx(Vec4 v, int i) => i <= 0 ? v.X : i == 1 ? v.Y : i == 2 ? v.Z : v.W;
    public static Vec2 Idx(Mat2 m, int i) => i <= 0 ? m.C0 : m.C1;
    public static Vec3 Idx(Mat3 m, int i) => i <= 0 ? m.C0 : i == 1 ? m.C1 : m.C2;
    public static Mat2 Mat2Diag(double d) => new(new(d, 0), new(0, d));
    public static Mat3 Mat3Diag(double d) => new(new(d, 0, 0), new(0, d, 0), new(0, 0, d));
    public static Mat2 Mat2From(Mat3 m) => new(new(m.C0.X, m.C0.Y), new(m.C1.X, m.C1.Y));
    public static Mat3 Mat3From(Mat2 m) => new(new(m.C0.X, m.C0.Y, 0), new(m.C1.X, m.C1.Y, 0), new(0, 0, 1));
}
