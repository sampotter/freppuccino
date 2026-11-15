using Freppuccino.Commands;
using Freppuccino.Sdf;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Ui;

/// <summary>
/// Applies one slider edit to a primitive: the SDF user text (the math) and a matching proxy brep.
/// </summary>
static class PrimitiveProxy
{
    static readonly Dictionary<string, string[]> Parameters = new(StringComparer.Ordinal)
    {
        [FSphereCommand.SdfType] = ["s"],
        [FBoxCommand.SdfType] = ["b.x", "b.y", "b.z"],
        [FCylinderCommand.SdfType] = ["h", "r"],
        [FConeCommand.SdfType] = ["h", "r1", "r2"],
        [FPyramidCommand.SdfType] = ["h", "s"],
        [FEllipsoidCommand.SdfType] = ["r.x", "r.y", "r.z"],
        [FTorusCommand.SdfType] = ["t.x", "t.y"],
        // FFunction: proxy is its bounding box. L and u.* are handled in TryEdit.
        [SdfUserText.UserType] = ["b.x", "b.y", "b.z"],
    };

    /// <summary>
    /// <paramref name="edited"/> is a copy of <paramref name="source"/> with <paramref name="name"/> set to <paramref name="value"/>.
    /// False with a null reason means the value did not change.
    /// </summary>
    public static bool TryEdit(string type, ObjectAttributes source, string name, double value, double tolerance, out ObjectAttributes? edited, out Brep? brep, out string? reason)
    {
        edited = null;
        brep = null;
        reason = null;
        bool exact = false; // L / u.*: round-trip text, always stored
        if (UserFnView.IsUser(type) && name == UserFnView.LipschitzName)
        {
            if (!double.IsFinite(value) || value <= RhinoMath.ZeroTolerance)
            {
                reason = "Lipschitz scale must be greater than zero.";
                return false;
            }
            exact = true;
        }
        else if (UserFnView.IsUser(type) && name.StartsWith(UserFnView.ParamPrefix, StringComparison.Ordinal))
        {
            if (!UserFnView.TryDefault(source, name[UserFnView.ParamPrefix.Length..], out _))
            {
                reason = $"The code doesn't declare \"{name[UserFnView.ParamPrefix.Length..]}\" (or doesn't compile).";
                return false;
            }
            if (!double.IsFinite(value))
            {
                reason = "Value must be a finite number.";
                return false;
            }
            exact = true;
        }
        else
        {
            if (!Parameters.TryGetValue(type, out var names) || !names.Contains(name))
            {
                reason = $"Unknown parameter \"{name}\".";
                return false;
            }
            // Cone radii may be zero (a pointed cone). Every other size must be positive.
            bool zeroOk = type == FConeCommand.SdfType && (name is "r1" or "r2");
            if (!double.IsFinite(value) || value < 0 || (!zeroOk && value <= RhinoMath.ZeroTolerance))
            {
                reason = zeroOk ? "Radius can't be negative." : "Size must be greater than zero.";
                return false;
            }
        }
        // Missing / unreadable key: always write (no default elision; fixes a bad SDF.L).
        if (SdfUserText.TryReadFloat(source, name, out var current) && (exact ? current == value : Math.Abs(current - value) < 1e-9))
            return false;

        edited = source.Duplicate();
        if (exact) SdfUserText.WriteFloatExact(edited, name, value);
        else SdfUserText.WriteFloat(edited, name, value);
        if (!TryBrep(type, edited, tolerance, out brep))
        {
            reason = "That value doesn't make a valid primitive.";
            edited = null;
            return false;
        }
        return true;
    }

    static bool TryBrep(string type, ObjectAttributes attributes, double tolerance, out Brep? brep)
    {
        brep = type switch
        {
            FSphereCommand.SdfType when FSphereCommand.TryRead(attributes, out var frame, out var s) && s > 0 =>
                Sphere(frame, s),
            FBoxCommand.SdfType when FBoxCommand.TryRead(attributes, out var frame, out var b) && b.X > 0 && b.Y > 0 && b.Z > 0 =>
                Box(frame, b),
            FCylinderCommand.SdfType when FCylinderCommand.TryRead(attributes, out var frame, out var h, out var r) && h > 0 && r > 0 =>
                Cylinder(frame, h, r),
            FConeCommand.SdfType when FConeCommand.TryRead(attributes, out var frame, out var h, out var r1, out var r2) &&
                                     h > 0 && r1 >= 0 && r2 >= 0 && r1 + r2 > 0 =>
                Cone(frame, h, r1, r2, tolerance),
            FPyramidCommand.SdfType when FPyramidCommand.TryRead(attributes, out var frame, out var h, out var s) && h > 0 && s > 0 =>
                FPyramidCommand.CreateBrep(frame, s, h, tolerance),
            FEllipsoidCommand.SdfType when FEllipsoidCommand.TryRead(attributes, out var frame, out var r) && r.X > 0 && r.Y > 0 && r.Z > 0 =>
                FEllipsoidCommand.CreateBrep(frame, r),
            FTorusCommand.SdfType when FTorusCommand.TryRead(attributes, out var frame, out var t) && t.X > 0 && t.Y > 0 =>
                Torus(frame, t.X, t.Y),
            SdfUserText.UserType when SdfUserText.TryReadFrame(attributes, out var frame) &&
                                     SdfUserText.TryReadVec3(attributes, "b", out var b) && b.X > 0 && b.Y > 0 && b.Z > 0 =>
                Box(frame, b),
            _ => null,
        };
        if (brep is not { IsValid: true }) return false;
        if (!SdfUserText.TryReadFrame(attributes, out var pose) || !SdfUserText.TryReadLinear(attributes, out var x, out var y, out var z))
            return true;
        // The brep is built on the parameter frame. The stored map sends those coordinates to world.
        var fromFrame = Transform.PlaneToPlane(pose, Plane.WorldXY);
        var toWorld = Transform.Identity;
        toWorld.M00 = x.X; toWorld.M01 = y.X; toWorld.M02 = z.X; toWorld.M03 = pose.Origin.X;
        toWorld.M10 = x.Y; toWorld.M11 = y.Y; toWorld.M12 = z.Y; toWorld.M13 = pose.Origin.Y;
        toWorld.M20 = x.Z; toWorld.M21 = y.Z; toWorld.M22 = z.Z; toWorld.M23 = pose.Origin.Z;
        toWorld.M33 = 1;
        brep.Transform(toWorld * fromFrame);
        return brep.IsValid;
    }

    static Brep? Sphere(Plane frame, double radius)
    {
        var sphere = new Sphere(frame, radius);
        return sphere.IsValid ? sphere.ToBrep() : null;
    }

    /// Half-extents along the frame axes; the box stays centered on the frame origin.
    static Brep? Box(Plane frame, Vector3d half)
    {
        var box = new Box(frame, new Interval(-half.X, half.X), new Interval(-half.Y, half.Y), new Interval(-half.Z, half.Z));
        return box.IsValid ? box.ToBrep() : null;
    }

    /// <paramref name="halfHeight"/> is the SDF h: the solid runs from local y = -h to y = +h.
    static Brep? Cylinder(Plane frame, double halfHeight, double radius)
    {
        var cylinder = new Cylinder(new Circle(AxisPlane(frame, -halfHeight), radius), halfHeight * 2);
        return cylinder.IsValid ? cylinder.ToBrep(true, true) : null;
    }

    /// r1 at local y = -h, r2 at y = +h. A zero radius is the pointed end.
    static Brep? Cone(Plane frame, double halfHeight, double r1, double r2, double tolerance)
    {
        if (r2 <= RhinoMath.ZeroTolerance) return PointedCone(frame, halfHeight, r1, towardPositiveY: true);
        if (r1 <= RhinoMath.ZeroTolerance) return PointedCone(frame, halfHeight, r2, towardPositiveY: false);

        var bottom = new ArcCurve(new Circle(AxisPlane(frame, -halfHeight), r1));
        var top = new ArcCurve(new Circle(AxisPlane(frame, halfHeight), r2));
        var loft = Brep.CreateFromLoft([bottom, top], Point3d.Unset, Point3d.Unset, LoftType.Straight, false);
        if (loft is not { Length: > 0 }) return null;
        return loft[0].CapPlanarHoles(tolerance);
    }

    static Brep? PointedCone(Plane frame, double halfHeight, double radius, bool towardPositiveY)
    {
        double sign = towardPositiveY ? 1 : -1;
        var plane = new Plane(frame.PointAt(0, -sign * halfHeight, 0), frame.ZAxis, sign * frame.XAxis);
        var cone = new Cone(plane, halfHeight * 2, radius);
        return cone.IsValid ? cone.ToBrep(true) : null;
    }

    /// Torus axis is local Y, matching the SDF frame.
    static Brep? Torus(Plane frame, double major, double minor)
    {
        var torus = new Torus(AxisPlane(frame, 0), major, minor);
        return torus.IsValid ? torus.ToBrep() : null;
    }

    /// Plane at local y = <paramref name="y"/> whose normal is the frame's Y axis.
    static Plane AxisPlane(Plane frame, double y) => new(frame.PointAt(0, y, 0), frame.ZAxis, frame.XAxis);
}
