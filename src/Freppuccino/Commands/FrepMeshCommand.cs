using System.Diagnostics;
using System.Runtime.InteropServices;
using Freppuccino.Csg;
using Freppuccino.Meshing;
using Freppuccino.Scene;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;

namespace Freppuccino.Commands;

/// <summary>
/// Bakes every visible F-rep in the document into one mesh: the same primitives and CSG tree the
/// Freppuccino display mode shows, meshed with <see cref="DualContour"/>. Resolution sets
/// the finest cell size; the simplification tolerance merges cells where the surface is flat or gently
/// curved, so triangle density adapts to the shape. Optionally the result is passed through Rhino's
/// QuadRemesh for an even quad layout.
/// The F-rep objects are left in place.
/// </summary>
[Guid("3f6b1c82-9a4e-4d57-b1c3-7e2a5d9f0c46")]
public class FrepMeshCommand : Command
{
    public const int MinResolution = 16, MaxResolution = 384;

    // Remembered between runs, like Rhino's own commands.
    static int resolution = 128;
    static double simplify = 0.03;
    static int refine = 0;
    static bool quads;
    // Progress lines pump Rhino's message loop (RhinoApp.Wait) so they paint during the run; this keeps a second
    // FrepMesh from starting inside the first while it does.
    static bool running;

    public override string EnglishName => "FrepMesh";

    public const string BakedKey = "Freppuccino.Baked";

    public static bool IsBaked(Rhino.DocObjects.RhinoObject? o) =>
        o is Rhino.DocObjects.MeshObject && (o.Attributes.GetUserString(BakedKey) != null || o.Attributes.Name == "FrepMesh");

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (running)
        {
            RhinoApp.WriteLine("FrepMesh: already running; wait for it to finish.");
            return Result.Nothing;
        }
        running = true;
        try { return Run(doc); }
        finally { running = false; }
    }

    Result Run(RhinoDoc doc)
    {
        var scene = FrepScene.Get(doc);
        if (scene.Count == 0)
        {
            RhinoApp.WriteLine("FrepMesh: no F-rep objects in the document (create some with FSphere, FBox, ...).");
            return Result.Nothing;
        }
        // Only the selected blends (roots), snapshotted now, before the new mesh becomes the selection. Nothing selected,
        // or only shapes inside blends: pick blends. Never the whole scene.
        var roots = Selection.FrepSelection.Roots(doc);
        if (roots.Count == 0)
        {
            var picked = PickRoots(doc);
            if (picked == null) return Result.Cancel;
            roots = picked;
        }
        if (roots.Count == 0)
        {
            RhinoApp.WriteLine("FrepMesh: no blend selected; nothing baked.");
            return Result.Nothing;
        }
        var field = FrepFieldBuilder.Build(scene, roots, out var built);
        if (!field.HasRoots)
        {
            RhinoApp.WriteLine("FrepMesh: nothing to mesh (the selected blends are hidden or cancelled out by the CSG tree).");
            return Result.Nothing;
        }

        var rc = RhinoGet.GetInteger("Mesh resolution (cells along the longest side)", true, ref resolution, MinResolution, MaxResolution);
        if (rc != Result.Success) return rc;
        rc = RhinoGet.GetNumber("Simplification tolerance in cells (0 = uniform density)", true, ref simplify, 0, 1);
        if (rc != Result.Success) return rc;
        rc = RhinoGet.GetInteger("Refine: extra levels for thin features and sharp creases (0 = off, fastest; 1-3 = slower, for models that mesh badly)", true, ref refine, 0, 3);
        if (rc != Result.Success) return rc;
        rc = RhinoGet.GetBool("Remesh to quads with Rhino's QuadRemesh (slower, and it reshapes the surface slightly)", true, "No", "Yes", ref quads);
        if (rc != Result.Success) return rc;

        var baked = Bake(doc, field, resolution, simplify, refine, quads, "FrepMesh", out var error);
        if (baked == null)
        {
            RhinoApp.WriteLine($"FrepMesh: {error}.");
            return Result.Failure;
        }

        // The Freppuccino display mode draws the F-reps over unselected objects; selecting the mesh shows it.
        doc.Objects.UnselectAll();
        doc.Objects.Select(baked.Id);
        doc.Views.Redraw();
        var result = baked.Mesh;
        RhinoApp.WriteLine(
            $"FrepMesh: {built.Count} blend(s) -> {baked.Faces}, cell size {result.CellSize:0.####} {doc.ModelUnitSystem}, " +
            $"{baked.Quality}, {baked.Milliseconds} ms.");
        if (refine > 0)
        {
            string lowered = result.Refine < refine ? $" (lowered from {refine}: budget or document tolerance)" : "";
            RhinoApp.WriteLine(
                $"FrepMesh: refine {result.Refine}{lowered}: accuracy cell {result.AccuracyCellSize:0.####} {doc.ModelUnitSystem}, {result.RefinedCells} cells refined" +
                (result.DeepCells > 0 ? $", {result.DeepCells} finer cells sampled {result.ExtraDepth} level(s) deeper" : "") +
                $"; smallest cell sampled {result.MinCellSize:0.#####} {doc.ModelUnitSystem}" +
                (result.Unresolved > 0 ? $"; {result.Unresolved} finest vertices ended at their fallback position" : "") + ".");
            if (result.DeepPassesRejected > 0)
                RhinoApp.WriteLine($"FrepMesh: {result.DeepPassesRejected} deeper pass(es) were discarded because they left more self-intersections; " +
                    (result.DeepCells > 0 ? "the mesh kept is an earlier deep pass, with the depth reported above." : "the mesh kept has no extra depth."));
            if (result.Contradictory > 0)
                RhinoApp.WriteLine($"FrepMesh: {result.Contradictory} finest vertices still hold opposing surface normals in one sheet.");
            if (result.ProbeFailures > 0)
                RhinoApp.WriteLine($"FrepMesh: {result.ProbeFailures} triangle(s) at coarse cells are still more than {0.25 * result.AccuracyCellSize:0.####} " +
                    $"{doc.ModelUnitSystem} from the surface by the field's estimate (refinement passes ran out).");
        }
        if (result.Fragments > 0)
            RhinoApp.WriteLine($"FrepMesh: {result.Fragments} small disconnected component(s), at most {4 * result.AccuracyCellSize:0.####} {doc.ModelUnitSystem} " +
                "across, kept as meshed. Some may be parts of features thinner than the finest cell; a higher resolution or refine level can join those.");
        if (result.UnsafeSamples > 0)
            RhinoApp.WriteLine($"FrepMesh: {result.UnsafeSamples} sample(s) changed sign when evaluated again: the field changes faster " +
                "than its Lipschitz bound there, so skipping samples was unsafe. Those cells were refined.");
        return Result.Success;
    }

    public sealed record BakeResult(Guid Id, string Faces, int FaceCount, DualContour.Result Mesh, bool Quads, bool Closed, int BadEdges, int BadVertices,
        long Milliseconds)
    {
        public double CellSize => Mesh.CellSize;
        public int Crossings => Mesh.Crossings;
        public bool Manifold => BadEdges + BadVertices == 0;
        // The mesher removes triangles that pass through each other by stepping vertices back to safer positions; if
        // it ran out of steps, say so rather than present the mesh as clean. (Counted on the triangle mesh, before
        // any QuadRemesh, which makes its own surface.)
        public string Quality => $"{(Closed ? "closed" : "OPEN")}, " +
            (Manifold ? "manifold" : $"NON-MANIFOLD ({BadEdges} bad edges, {BadVertices} pinched vertices; try a higher resolution)") +
            (Crossings > 0
                ? $", {Crossings} SELF-INTERSECTING triangle pair(s){(Quads ? " before QuadRemesh" : "")} ({(Mesh.Refine < 3 ? $"try Refine {Mesh.Refine + 1} or a higher resolution" : "try a higher resolution")})"
                : "");
    }

    public static BakeResult? Bake(RhinoDoc doc, FrepField field, int resolution, double simplify, int refineLevels, bool quads, string name, out string error)
    {
        error = "";
        var watch = Stopwatch.StartNew();
        // Stage lines while it runs, timed from the start of this run; RhinoApp.Wait paints each one as it is written.
        void Stage(string what)
        {
            RhinoApp.WriteLine($"FrepMesh: [{watch.Elapsed.TotalSeconds:0.0} s] {what}");
            RhinoApp.Wait();
        }
        // The finest cell stays well above the document's tolerance. The field and roots are snapshots taken above,
        // so document changes while the lines paint do not reach the mesher.
        var result = DualContour.Mesh(field, resolution, simplify, refineLevels, 16 * doc.ModelAbsoluteTolerance, Stage);
        if (result.Triangles.Count == 0)
        {
            error = "the F-reps produced no surface at this resolution";
            return null;
        }

        var mesh = new Mesh();
        mesh.Vertices.UseDoublePrecisionVertices = true;
        foreach (var p in result.Vertices) mesh.Vertices.Add(p.X, p.Y, p.Z);
        var t = result.Triangles;
        for (int i = 0; i < t.Count; i += 3) mesh.Faces.AddFace(t[i], t[i + 1], t[i + 2]);
        mesh.Faces.CullDegenerateFaces();
        mesh.Compact();
        mesh.Normals.ComputeNormals();
        // Split vertex normals across sharp edges so creases shade crisp, as Rhino's own meshes do.
        mesh.Unweld(RhinoMath.ToRadians(30), true);

        // Optional second pass: Rhino's QuadRemesh lays out even quads over the triangles, keeping the
        // creases we just unwelded as hard edges. It works from the triangle mesh, so it cannot be more
        // accurate than it (volumes moved by up to 1.3% in testing), and it decides its own topology.
        string faces = $"{mesh.Faces.Count} triangles";
        if (quads)
        {
            Stage("remeshing to quads with QuadRemesh");
            var parameters = new QuadRemeshParameters
            {
                TargetQuadCount = Math.Max(200, mesh.Faces.Count / 2),
                AdaptiveQuadCount = true,
                AdaptiveSize = 50,
                DetectHardEdges = true,
            };
            var remeshed = mesh.QuadRemesh(parameters);
            if (remeshed != null && remeshed.Faces.Count > 0)
            {
                faces += $" -> {remeshed.Faces.Count} faces ({remeshed.Faces.QuadCount} quads) after QuadRemesh";
                mesh = remeshed;
            }
            else RhinoApp.WriteLine("FrepMesh: QuadRemesh produced nothing; keeping the triangle mesh.");
        }

        Stage("checking the mesh and adding it to the document");
        // Our own check on the mesh being added, whichever pass produced it: it also catches pinched
        // vertices, which an edge-based test such as Mesh.IsManifold passes.
        bool closed = mesh.IsClosed;
        var (badEdges, badVertices) = Defects(mesh);

        var attributes = doc.CreateDefaultAttributes();
        attributes.Name = name;
        attributes.SetUserString(BakedKey, "1");
        var id = doc.Objects.AddMesh(mesh, attributes);
        if (id == Guid.Empty)
        {
            error = "unable to add the mesh to the document";
            return null;
        }
        return new BakeResult(id, faces, mesh.Faces.Count, result, quads, closed, badEdges, badVertices, watch.ElapsedMilliseconds);
    }

    /// Click blends (an F-rep selection session: the picker acts during this prompt, adding each clicked root); Enter ends.
    /// Null if cancelled.
    static List<Csg.FrepOperand>? PickRoots(RhinoDoc doc)
    {
        doc.Objects.UnselectAll(); // leaves only: start the blend selection from scratch
        doc.Views.Redraw();
        using var session = Selection.FrepPicker.BeginSession();
        var gp = new Rhino.Input.Custom.GetPoint();
        gp.SetCommandPrompt("Click the blends to bake, Enter when done");
        gp.AcceptNothing(true);
        while (true)
        {
            switch (gp.Get())
            {
                case GetResult.Point: // a click on no blend: ignore
                    continue;
                case GetResult.Nothing:
                    return Selection.FrepSelection.Roots(doc);
                default:
                    return null;
            }
        }
    }

    /// <see cref="DualContour.Defects"/> for a Rhino mesh: faces are read through the mesh's topology, so
    /// vertices that were unwelded for shading count as one, and quads are split in two.
    static (int Edges, int Vertices) Defects(Mesh mesh)
    {
        var topology = mesh.TopologyVertices;
        var vertices = new List<V3>(topology.Count);
        for (int i = 0; i < topology.Count; i++) vertices.Add(new V3(topology[i].X, topology[i].Y, topology[i].Z));
        var triangles = new List<int>(mesh.Faces.Count * 3);
        foreach (var face in mesh.Faces)
        {
            int a = topology.TopologyVertexIndex(face.A), b = topology.TopologyVertexIndex(face.B), c = topology.TopologyVertexIndex(face.C);
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
            if (!face.IsQuad) continue;
            triangles.Add(a); triangles.Add(c); triangles.Add(topology.TopologyVertexIndex(face.D));
        }
        return DualContour.Defects(new DualContour.Result(vertices, triangles, 0, 0, 0, 0));
    }

    static V3 V(Point3d p) => new(p.X, p.Y, p.Z);
    static V3 V(Vector3d v) => new(v.X, v.Y, v.Z);
}
