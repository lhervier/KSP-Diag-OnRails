using System.Collections.Generic;
using UnityEngine;

namespace com.github.lhervier.ksp.diag.onrails
{
    /// <summary>
    /// The drawing of one collider, of a static or of a terrain quad, as lines: the edges of a box, three
    /// circles for a sphere, the outline of a capsule, every edge of the triangles of a mesh. It is built in
    /// the frame of the collider's own transform and drawn where the physics puts the collider, which is
    /// not always where the game draws the object (see <see cref="PhysicsMatrix"/>).
    /// </summary>
    /// <remarks>Copied from KSP Diag - Colliders, so that both draw a collider alike; <see cref="PhysicsPose"/> and
    /// <see cref="DrawAt"/> added, to draw a collider where it was measured.</remarks>
    internal sealed class ColliderOverlay
    {
        // Segments of a full circle.
        private const int CIRCLE_SEGMENTS = 32;

        // Scratch buffers shared by every rebuild, which all happen on the main thread.
        private static readonly List<Vector3> VERTICES = new List<Vector3>();
        private static readonly List<int> INDICES = new List<int>();
        private static readonly HashSet<long> EDGES = new HashSet<long>();

        /// <summary>The collider this overlay draws.</summary>
        public Collider Collider { get; }

        /// <summary>Index, in the palette of the viewer, of the colour the collider is drawn in while it is
        /// on.</summary>
        public int ColorIndex { get; set; }

        /// <summary>True when the collider is a mesh the game does not let a script read: the box of its
        /// bounds is drawn instead.</summary>
        public bool Unreadable { get; private set; }

        private readonly Mesh mesh;

        // What the drawing was built from: the shape of the collider, in the frame of its transform.
        private bool built;
        private Mesh builtMesh;
        private Vector3 builtCenter;
        private Vector3 builtSize;
        private int builtDirection;
        private Bounds builtBounds;
        private uint builtIndexCount;
        private PQS.EdgeState builtEdgeState;

        // The terrain quad whose collider this is, or null.
        private readonly PQ quad;

        // Centre of the shape in the frame of its transform: the point matched against the centre of the
        // bounds the physics gives.
        private Vector3 localCenter;

        /// <summary>
        /// The matrix that puts a shape centred on <paramref name="localCenter"/>, in the frame of
        /// <paramref name="collider"/>'s transform, where the physics has the collider, rather than where
        /// the game draws the object. For a mesh, <paramref name="localCenter"/> is the centre of the box of
        /// the vertices its triangles use (see <see cref="UsedCenter"/>).
        /// </summary>
        /// <remarks>
        /// The game draws an object through its <c>localToWorldMatrix</c>, while the physics gets its pose
        /// through the other computation of the transform, <c>position</c> and <c>rotation</c>. Under a
        /// chain of transforms that holds a vector as long as a planet radius, the two are rounded
        /// differently, by a step of float that grows with the radius, and differently at each load. The
        /// drawing is therefore moved by the gap between the centre of the bounds of the collider, which
        /// the physics keeps, and the same centre through the matrix. A collider switched off has no
        /// bounds: it is left where the game draws it.
        /// </remarks>
        internal static Matrix4x4 PhysicsMatrix(Collider collider, Vector3 localCenter)
        {
            Matrix4x4 matrix = collider.transform.localToWorldMatrix;
            if (!collider.enabled || !collider.gameObject.activeInHierarchy)
            {
                return matrix;
            }
            Vector3 gap = collider.bounds.center - matrix.MultiplyPoint3x4(localCenter);
            return Matrix4x4.Translate(gap) * matrix;
        }

        /// <summary>
        /// The centre of the box of the vertices that the triangles of <paramref name="triangles"/> use, in
        /// the frame of the mesh: the centre the physics gives the bounds of a mesh collider, which leaves out
        /// the vertices no triangle uses. The centre of the box of every vertex when there is no triangle.
        /// </summary>
        internal static Vector3 UsedCenter(Vector3[] vertices, IEnumerable<int[]> triangles)
        {
            // PhysX drops the vertices no triangle uses when it cooks a mesh, and so does its box: a terrain
            // quad beside a coarser one skips every other vertex of that edge, and the box of all of them
            // can be a metre off.
            bool any = false;
            Vector3 min = Vector3.zero;
            Vector3 max = Vector3.zero;
            foreach (int[] list in triangles)
            {
                foreach (int index in list)
                {
                    Vector3 v = vertices[index];
                    if (!any)
                    {
                        min = max = v;
                        any = true;
                    }
                    else
                    {
                        min = Vector3.Min(min, v);
                        max = Vector3.Max(max, v);
                    }
                }
            }
            if (!any)
            {
                Bounds all = GeometryUtility.CalculateBounds(vertices, Matrix4x4.identity);
                return all.center;
            }
            return 0.5f * (min + max);
        }

        /// <summary>The lists of triangles of the sub-meshes of <paramref name="shape"/> drawn as triangles.</summary>
        internal static IEnumerable<int[]> TriangleLists(Mesh shape)
        {
            for (int sub = 0; sub < shape.subMeshCount; sub++)
            {
                if (shape.GetTopology(sub) == MeshTopology.Triangles)
                {
                    yield return shape.GetTriangles(sub);
                }
            }
        }

        /// <summary>Creates the overlay of <paramref name="collider"/>, with nothing drawn yet.</summary>
        public ColliderOverlay(Collider collider)
        {
            Collider = collider;
            quad = collider.GetComponent<PQ>();
            mesh = new Mesh { name = "KSPDiagOnRails collider " + collider.name };
        }

        /// <summary>
        /// Tells whether <paramref name="collider"/> is a kind this overlay can draw, and if so, gives a
        /// sphere, in world space, that holds all of it.
        /// </summary>
        public static bool Reach(Collider collider, out Vector3 center, out float radius)
        {
            Transform frame = collider.transform;
            Vector3 scale = frame.lossyScale;
            float maxScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            switch (collider)
            {
                case BoxCollider box:
                    center = frame.TransformPoint(box.center);
                    radius = 0.5f * Vector3.Scale(box.size, scale).magnitude;
                    return true;
                case SphereCollider sphere:
                    center = frame.TransformPoint(sphere.center);
                    radius = sphere.radius * maxScale;
                    return true;
                case CapsuleCollider capsule:
                    center = frame.TransformPoint(capsule.center);
                    radius = Mathf.Max(capsule.radius, 0.5f * capsule.height) * maxScale;
                    return true;
                case MeshCollider meshCollider when meshCollider.sharedMesh != null:
                    Bounds bounds = meshCollider.sharedMesh.bounds;
                    center = frame.TransformPoint(bounds.center);
                    radius = Vector3.Scale(bounds.extents, scale).magnitude;
                    return true;
                default:
                    center = Vector3.zero;
                    radius = 0f;
                    return false;
            }
        }

        /// <summary>
        /// Makes the drawing match the shape <see cref="Collider"/> has right now. Does nothing when it
        /// already does.
        /// </summary>
        public void Refresh()
        {
            Mesh shape = null;
            Vector3 center = Vector3.zero;
            Vector3 size = Vector3.zero;
            int direction = 0;
            switch (Collider)
            {
                case BoxCollider box:
                    center = box.center;
                    size = box.size;
                    break;
                case SphereCollider sphere:
                    center = sphere.center;
                    size = new Vector3(sphere.radius, 0f, 0f);
                    break;
                case CapsuleCollider capsule:
                    center = capsule.center;
                    size = new Vector3(capsule.radius, capsule.height, 0f);
                    direction = capsule.direction;
                    break;
                case MeshCollider meshCollider:
                    shape = meshCollider.sharedMesh;
                    if (shape == null)
                    {
                        return;
                    }
                    // A mesh can be refilled in place: stock does so with the mesh of a terrain quad, built
                    // again elsewhere with as many vertices, or given other triangles where its neighbour
                    // changes level. Its vertex count, its bounds and its count of indices stand for its
                    // content, but for those other triangles: every list of them has as many indices, the
                    // triangles left out being given as degenerate ones. The quad's edge state tells them.
                    size = new Vector3(shape.vertexCount, 0f, 0f);
                    break;
                default:
                    return;
            }
            Bounds bounds = shape != null ? shape.bounds : default(Bounds);
            uint indexCount = shape != null ? IndexCount(shape) : 0;
            PQS.EdgeState edgeState = quad != null ? quad.edgeState : PQS.EdgeState.Reset;
            if (built && shape == builtMesh && center == builtCenter && size == builtSize
                && direction == builtDirection && bounds == builtBounds && indexCount == builtIndexCount
                && edgeState == builtEdgeState)
            {
                return;
            }

            VERTICES.Clear();
            INDICES.Clear();
            // A mesh that cannot be read gives the centre of its box: the vertices its triangles use are
            // out of reach.
            localCenter = shape != null ? shape.bounds.center : center;
            switch (Collider)
            {
                case BoxCollider _:
                    AddBox(center, size);
                    break;
                case SphereCollider _:
                    AddCircle(center, Vector3.right, Vector3.up, size.x, 0f, 360f);
                    AddCircle(center, Vector3.up, Vector3.forward, size.x, 0f, 360f);
                    AddCircle(center, Vector3.forward, Vector3.right, size.x, 0f, 360f);
                    break;
                case CapsuleCollider _:
                    AddCapsule(center, size.x, size.y, direction);
                    break;
                case MeshCollider _:
                    Unreadable = !shape.isReadable;
                    if (Unreadable)
                    {
                        AddBox(shape.bounds.center, shape.bounds.size);
                    }
                    else
                    {
                        AddMeshEdges(shape);
                        localCenter = UsedCenter(shape.vertices, TriangleLists(shape));
                    }
                    break;
            }

            Color32[] white = new Color32[VERTICES.Count];
            for (int i = 0; i < white.Length; i++)
            {
                white[i] = new Color32(255, 255, 255, 255);
            }
            mesh.Clear();
            // A mesh collider can have more than the 65 535 vertices of the default index format.
            mesh.indexFormat = VERTICES.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(VERTICES);
            // The colour comes from the material; the shader multiplies it by this one.
            mesh.colors32 = white;
            mesh.SetIndices(INDICES.ToArray(), MeshTopology.Lines, 0);
            mesh.RecalculateBounds();

            built = true;
            builtMesh = shape;
            builtCenter = center;
            builtSize = size;
            builtDirection = direction;
            builtBounds = bounds;
            builtIndexCount = indexCount;
            builtEdgeState = edgeState;
        }

        /// <summary>How many indices the sub-meshes of <paramref name="shape"/> hold, all together.</summary>
        private static uint IndexCount(Mesh shape)
        {
            uint count = 0;
            for (int sub = 0; sub < shape.subMeshCount; sub++)
            {
                count += shape.GetIndexCount(sub);
            }
            return count;
        }

        private static void AddSegment(Vector3 a, Vector3 b)
        {
            INDICES.Add(VERTICES.Count);
            VERTICES.Add(a);
            INDICES.Add(VERTICES.Count);
            VERTICES.Add(b);
        }

        private static void AddBox(Vector3 center, Vector3 size)
        {
            Vector3 h = 0.5f * size;
            Vector3[] corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                corners[i] = center + new Vector3(
                    (i & 1) == 0 ? -h.x : h.x,
                    (i & 2) == 0 ? -h.y : h.y,
                    (i & 4) == 0 ? -h.z : h.z);
            }
            // Two corners share an edge when their indices differ by one bit.
            for (int i = 0; i < 8; i++)
            {
                for (int bit = 1; bit < 8; bit <<= 1)
                {
                    if ((i & bit) == 0)
                    {
                        AddSegment(corners[i], corners[i | bit]);
                    }
                }
            }
        }

        /// <summary>Adds the arc of radius <paramref name="radius"/> around <paramref name="center"/> in the
        /// plane of <paramref name="u"/> and <paramref name="v"/>, from <paramref name="from"/> to
        /// <paramref name="to"/> degrees, measured from <paramref name="u"/> towards <paramref name="v"/>.</summary>
        private static void AddCircle(Vector3 center, Vector3 u, Vector3 v, float radius, float from, float to)
        {
            int segments = Mathf.Max(1, Mathf.CeilToInt(CIRCLE_SEGMENTS * (to - from) / 360f));
            Vector3 previous = Vector3.zero;
            for (int i = 0; i <= segments; i++)
            {
                float angle = Mathf.Deg2Rad * Mathf.Lerp(from, to, (float)i / segments);
                Vector3 point = center + radius * (Mathf.Cos(angle) * u + Mathf.Sin(angle) * v);
                if (i > 0)
                {
                    AddSegment(previous, point);
                }
                previous = point;
            }
        }

        private static void AddCapsule(Vector3 center, float radius, float height, int direction)
        {
            // The axis of the capsule and two directions across it.
            Vector3 axis = direction == 0 ? Vector3.right : direction == 1 ? Vector3.up : Vector3.forward;
            Vector3 across1 = direction == 0 ? Vector3.up : direction == 1 ? Vector3.forward : Vector3.right;
            Vector3 across2 = Vector3.Cross(axis, across1);

            // Centres of the two half spheres; they meet when the capsule is no longer than it is wide.
            float half = Mathf.Max(0f, 0.5f * height - radius);
            Vector3 top = center + half * axis;
            Vector3 bottom = center - half * axis;

            AddCircle(top, across1, across2, radius, 0f, 360f);
            AddCircle(bottom, across1, across2, radius, 0f, 360f);
            AddSegment(top + radius * across1, bottom + radius * across1);
            AddSegment(top - radius * across1, bottom - radius * across1);
            AddSegment(top + radius * across2, bottom + radius * across2);
            AddSegment(top - radius * across2, bottom - radius * across2);
            AddCircle(top, across1, axis, radius, 0f, 180f);
            AddCircle(top, across2, axis, radius, 0f, 180f);
            AddCircle(bottom, across1, -axis, radius, 0f, 180f);
            AddCircle(bottom, across2, -axis, radius, 0f, 180f);
        }

        private static void AddMeshEdges(Mesh shape)
        {
            Vector3[] vertices = shape.vertices;
            int first = VERTICES.Count;
            VERTICES.AddRange(vertices);
            EDGES.Clear();
            for (int sub = 0; sub < shape.subMeshCount; sub++)
            {
                if (shape.GetTopology(sub) != MeshTopology.Triangles)
                {
                    continue;
                }
                int[] triangles = shape.GetTriangles(sub);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    AddEdge(first, triangles[i], triangles[i + 1]);
                    AddEdge(first, triangles[i + 1], triangles[i + 2]);
                    AddEdge(first, triangles[i + 2], triangles[i]);
                }
            }
        }

        /// <summary>Adds the edge between two vertices of a mesh, unless a triangle already gave it.</summary>
        private static void AddEdge(int first, int a, int b)
        {
            if (a == b)
            {
                return;
            }
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (!EDGES.Add(key))
            {
                return;
            }
            INDICES.Add(first + a);
            INDICES.Add(first + b);
        }

        /// <summary>Queues the drawing for this frame, where the physics has the collider now.</summary>
        public void Draw(Material lines, int layer)
        {
            if (Collider == null || !built)
            {
                return;
            }
            Graphics.DrawMesh(mesh, PhysicsMatrix(Collider, localCenter), lines, layer);
        }

        /// <summary>The matrix that puts the drawing where the physics has the collider now.</summary>
        public Matrix4x4 PhysicsPose
        {
            get { return PhysicsMatrix(Collider, localCenter); }
        }

        /// <summary>Queues the drawing for this frame through <paramref name="matrix"/>, wherever the collider is
        /// now.</summary>
        public void DrawAt(Material lines, int layer, Matrix4x4 matrix)
        {
            if (Collider == null || !built)
            {
                return;
            }
            Graphics.DrawMesh(mesh, matrix, lines, layer);
        }

        /// <summary>Releases the mesh. The overlay cannot be drawn afterwards.</summary>
        public void Destroy()
        {
            Object.Destroy(mesh);
        }
    }
}
