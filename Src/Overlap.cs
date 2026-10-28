using System.Collections.Generic;
using UnityEngine;

namespace com.github.lhervier.ksp.diag.onrails
{
    /// <summary>
    /// How deep one collider of a part of the vessel stands in one collider around it, as the physics gives
    /// it: the shortest move of the part's collider that takes it out, and the arrow that shows it. The arrow
    /// starts at the point of the part's collider furthest into the other one and points the way out, as
    /// long as that move times a scale.
    /// </summary>
    internal sealed class Overlap
    {
        // Size of the cross that marks the start of the arrow, and of the head of the arrow at most, in
        // metres, whatever the scale.
        private const float MARK_SIZE = 0.04f;
        private const float HEAD_SIZE = 0.2f;

        // Scratch buffers shared by every rebuild, which all happen on the main thread.
        private static readonly List<Vector3> VERTICES = new List<Vector3>();
        private static readonly List<Color32> COLORS = new List<Color32>();
        private static readonly List<int> INDICES = new List<int>();

        /// <summary>The collider of the part.</summary>
        public Collider PartCollider { get; }

        /// <summary>The part whose collider it is.</summary>
        public Part Part { get; }

        /// <summary>The collider the part's collider stands in.</summary>
        public Collider Other { get; }

        /// <summary>The way out of <see cref="Other"/>, in world space, as last measured.</summary>
        public Vector3 Direction { get; private set; }

        /// <summary>How far the part's collider has to move along <see cref="Direction"/> to come out, in
        /// metres, as last measured.</summary>
        public float Depth { get; private set; }

        /// <summary>The point of the part's collider furthest into <see cref="Other"/>, in world space, as
        /// last measured.</summary>
        public Vector3 Deepest { get; private set; }

        // The arrow, in the frame of the vessel so that it follows the vessel, and its tip in that frame,
        // where its label goes. Not in the frame of the part's collider: a part can move that collider
        // between the physics step it was measured after and the frame being drawn, as a landing leg does.
        private readonly Transform frame;
        private readonly Mesh mesh;
        private Vector3 localTip;

        /// <summary>Creates the overlap of <paramref name="partCollider"/>, of <paramref name="part"/>, in
        /// <paramref name="other"/>, with nothing measured yet; its arrow follows <paramref name="vessel"/>.</summary>
        public Overlap(Collider partCollider, Part part, Collider other, Vessel vessel)
        {
            PartCollider = partCollider;
            Part = part;
            Other = other;
            frame = vessel.transform;
            mesh = new Mesh { name = "KSPDiagOnRails arrow " + partCollider.name };
        }

        /// <summary>A key that tells the overlap of <paramref name="partCollider"/> in
        /// <paramref name="other"/> apart from every other pair.</summary>
        public static long Key(Collider partCollider, Collider other)
        {
            return ((long)partCollider.GetInstanceID() << 32) | (uint)other.GetInstanceID();
        }

        /// <summary>
        /// Tells whether the physics could measure how far <paramref name="a"/>, moved by
        /// <paramref name="shift"/>, stands in <paramref name="b"/>, both where the physics has them: false
        /// when they do not overlap, or when it cannot measure that pair (two meshes that are not convex).
        /// </summary>
        public static bool Measure(Collider a, Vector3 shift, Collider b, out Vector3 direction, out float depth)
        {
            // The physics gets the pose of a collider from its transform's position and rotation, not from
            // the matrix the game draws it with.
            Transform ta = a.transform;
            Transform tb = b.transform;
            return Physics.ComputePenetration(a, ta.position + shift, ta.rotation, b, tb.position, tb.rotation,
                out direction, out depth);
        }

        /// <summary>
        /// Measures the overlap as the colliders stand now and builds its arrow, as long as the depth times
        /// <paramref name="scale"/> and of the colour <paramref name="colorOf"/> gives the depth. Returns
        /// false when the colliders no longer overlap, or when the physics cannot measure them.
        /// </summary>
        public bool Refresh(float scale, System.Func<float, Color> colorOf)
        {
            if (!Measure(PartCollider, Vector3.zero, Other, out Vector3 direction, out float depth))
            {
                return false;
            }
            Direction = direction;
            Depth = depth;
            Deepest = Support(PartCollider, -direction);

            Vector3 start = Deepest;
            Vector3 tip = start + direction * (depth * scale);
            VERTICES.Clear();
            COLORS.Clear();
            INDICES.Clear();
            Color32 c = colorOf(depth);

            // A small cross at the start, seen even when the arrow is too short to be.
            Vector3 side1 = Vector3.Cross(direction, Mathf.Abs(direction.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 side2 = Vector3.Cross(direction, side1);
            AddSegment(frame, start - MARK_SIZE * side1, start + MARK_SIZE * side1, c);
            AddSegment(frame, start - MARK_SIZE * side2, start + MARK_SIZE * side2, c);

            // The shaft, then a head of four strokes, no longer than a quarter of the arrow.
            AddSegment(frame, start, tip, c);
            float head = Mathf.Min(HEAD_SIZE, 0.25f * depth * scale);
            Vector3 back = tip - head * direction;
            AddSegment(frame, tip, back + 0.5f * head * side1, c);
            AddSegment(frame, tip, back - 0.5f * head * side1, c);
            AddSegment(frame, tip, back + 0.5f * head * side2, c);
            AddSegment(frame, tip, back - 0.5f * head * side2, c);

            mesh.Clear();
            mesh.SetVertices(VERTICES);
            // The colour of each line is its vertices'; the material is white.
            mesh.SetColors(COLORS);
            mesh.SetIndices(INDICES.ToArray(), MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
            localTip = frame.InverseTransformPoint(tip);
            return true;
        }

        private static void AddSegment(Transform frame, Vector3 a, Vector3 b, Color32 color)
        {
            INDICES.Add(VERTICES.Count);
            VERTICES.Add(frame.InverseTransformPoint(a));
            COLORS.Add(color);
            INDICES.Add(VERTICES.Count);
            VERTICES.Add(frame.InverseTransformPoint(b));
            COLORS.Add(color);
        }

        /// <summary>
        /// The point of <paramref name="collider"/> furthest along <paramref name="toward"/>, in world space,
        /// where the physics has the collider: a vertex of a mesh, a corner of a box, a point of the surface of
        /// a sphere or capsule; a corner of its bounds for a mesh the game does not let a script read.
        /// </summary>
        private static Vector3 Support(Collider collider, Vector3 toward)
        {
            Transform frame = collider.transform;
            Vector3 local = frame.InverseTransformDirection(toward);
            Vector3 scale = frame.lossyScale;
            float maxScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            switch (collider)
            {
                case BoxCollider box:
                    return frame.TransformPoint(box.center + Vector3.Scale(0.5f * box.size, Sign(local)));
                case SphereCollider sphere:
                    return frame.TransformPoint(sphere.center) + toward.normalized * (sphere.radius * maxScale);
                case CapsuleCollider capsule:
                {
                    Vector3 axis = capsule.direction == 0 ? Vector3.right
                        : capsule.direction == 1 ? Vector3.up : Vector3.forward;
                    float half = Mathf.Max(0f, 0.5f * capsule.height - capsule.radius);
                    Vector3 end = capsule.center + (Vector3.Dot(local, axis) >= 0f ? half : -half) * axis;
                    return frame.TransformPoint(end) + toward.normalized * (capsule.radius * maxScale);
                }
                case MeshCollider meshCollider when meshCollider.sharedMesh != null:
                {
                    Mesh shape = meshCollider.sharedMesh;
                    if (!shape.isReadable)
                    {
                        return frame.TransformPoint(shape.bounds.center + Vector3.Scale(shape.bounds.extents, Sign(local)));
                    }
                    // The hull the physics uses for a convex mesh is made of some of its vertices: the
                    // furthest vertex is the furthest point either way.
                    Vector3 best = Vector3.zero;
                    float bestDot = float.NegativeInfinity;
                    foreach (Vector3 v in shape.vertices)
                    {
                        float d = Vector3.Dot(Vector3.Scale(v, scale), local);
                        if (d > bestDot)
                        {
                            bestDot = d;
                            best = v;
                        }
                    }
                    return frame.TransformPoint(best);
                }
                default:
                    return collider.bounds.center;
            }
        }

        private static Vector3 Sign(Vector3 v)
        {
            return new Vector3(v.x >= 0f ? 1f : -1f, v.y >= 0f ? 1f : -1f, v.z >= 0f ? 1f : -1f);
        }

        /// <summary>Where the tip of the arrow is now, in world space.</summary>
        public Vector3 Tip
        {
            get { return frame.TransformPoint(localTip); }
        }

        /// <summary>Queues the arrow for this frame, where the vessel is now.</summary>
        public void Draw(Material lines, int layer)
        {
            if (PartCollider == null || frame == null)
            {
                return;
            }
            Graphics.DrawMesh(mesh, frame.localToWorldMatrix, lines, layer);
        }

        /// <summary>Releases the arrow. The overlap cannot be drawn afterwards.</summary>
        public void Destroy()
        {
            Object.Destroy(mesh);
        }
    }
}
