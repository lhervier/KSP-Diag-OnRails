using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace com.github.lhervier.ksp.diag.onrails
{
    /// <summary>
    /// On-rails viewer. A box of its window keeps every vessel on rails, physics never starting, until it
    /// is unticked: tick it, load a save, and the vessels stay where the save puts them. In flight, it shows
    /// how far the colliders of the parts of the active vessel stand in the colliders around them, as the
    /// physics measures it where it has them right after a physics step: each collider they stand in is drawn
    /// as KSP Diag - Colliders draws it, each collider of a part that stands in one is drawn in white where the
    /// physics has it, which is not always where the game draws the part, each overlap as an arrow from the point of the part furthest in, pointing the way out, as long as the
    /// depth times a scale chosen in the window, its colour going from yellow to red with the depth, and the
    /// depth written at its tip. The window lists the colliders stood in, each with the parts in it, their
    /// depth and the angle of the way out from the vertical, the deepest overlap, and how far the vessel
    /// has to be lifted for none to be left. By default what stands in front of the drawing hides it; the
    /// window switches between that, drawn through everything, and nothing. Nothing is written to the log.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class KSPDiagOnRails : MonoBehaviour
    {
        private const string LOG_PREFIX = "[KSPDiagOnRails] ";

        // Unity's own shader for its debug lines: unlike the unlit shaders of the game, it lets a script
        // choose the depth test.
        private const string SHADER_NAME = "Hidden/Internal-Colored";

        // How often the overlaps are measured again, in seconds of real time, always right after a physics
        // step. The drawing itself follows the vessel every frame.
        private const float REFRESH_PERIOD = 0.25f;

        // Layer the drawing is queued on: Local Scenery, the layer of the terrain and of the statics,
        // which the flight cameras draw.
        private const int DRAW_LAYER = 15;

        // How many physics steps the unpacking of vessels is held for at each call: more than one, so that
        // it holds whatever the order of this mod's updates and the game's.
        private const int HOLD_STEPS = 2;

        // Depth, in metres, at which an arrow is red; yellow at none.
        private const float RED_DEPTH = 0.1f;

        // Highest lift tried, in metres, for the lift that takes the vessel out of every overlap, and how
        // many halvings narrow it down.
        private const float MAX_LIFT = 20f;
        private const int LIFT_STEPS = 24;

        // Colours of the colliders stood in, one per collider while there are enough: those of KSP Diag -
        // Colliders.
        private static readonly Color[] PALETTE =
        {
            new Color(0.1f, 0.45f, 1f, 1f),   // blue
            new Color(1f, 0.5f, 0f, 1f),      // orange
            new Color(1f, 0f, 1f, 1f),        // magenta
            new Color(0f, 1f, 1f, 1f),        // cyan
            new Color(1f, 0.9f, 0f, 1f),      // yellow
            new Color(1f, 0.15f, 0.15f, 1f),  // red
            new Color(0.4f, 1f, 0f, 1f),      // lime
            new Color(0.6f, 0.3f, 1f, 1f),    // purple
            new Color(1f, 0.55f, 0.75f, 1f),  // pink
            new Color(0f, 0.6f, 0.45f, 1f),   // teal
        };
        private static readonly string[] PALETTE_NAMES =
        {
            "blue", "orange", "magenta", "cyan", "yellow", "red", "lime", "purple", "pink", "teal",
        };

        // Colour of the colliders of the parts that stand in another one, drawn where they were measured.
        private static readonly Color PART_LINE = Color.white;
        private const string PART_LINE_NAME = "white";

        private static readonly Color SHALLOW = new Color(1f, 1f, 0f, 1f);
        private static readonly Color DEEP = new Color(1f, 0f, 0f, 1f);

        // Drawn after everything else, the arrows last so that nothing covers them.
        private const int COLLIDER_QUEUE = 4001;
        private const int ARROW_QUEUE = 4002;

        // The window: its id, distinct from those of the other Diags, its width, and its gap from the right
        // edge of the screen; it opens half way down, under that of KSP Diag - Colliders.
        private const int WINDOW_ID = 0x47485008;
        private const float WINDOW_WIDTH = 400f;
        private const float WINDOW_MARGIN = 60f;

        // Height of the list of colliders in the window, in pixels, beyond which it scrolls.
        private const float LIST_HEIGHT = 240f;

        private static readonly string[] DISPLAY_LABELS =
        {
            "Hidden by what stands in front",
            "Through everything",
            "Off",
        };

        /// <summary>What is drawn, and how.</summary>
        private enum Display
        {
            DepthTested,
            ThroughEverything,
            Nothing,
        }

        // The arrow lengths offered, as times the depth, and their labels.
        private static readonly int[] SCALES = { 1, 10, 100 };
        private static readonly string[] SCALE_LABELS = { "x1", "x10", "x100" };

        // Static, so that the choices survive a scene change or the loading of a save.
        private static bool keepOnRails;
        private static Display display = Display.DepthTested;
        private static int scaleIndex = 1;

        private Material[] colliderLines;
        private Material partLines;
        private Material arrowLines;

        private Vessel vessel;
        private readonly Dictionary<Collider, ColliderOverlay> overlays = new Dictionary<Collider, ColliderOverlay>();

        // The colliders of the parts that stand in another one, each with where it was measured, in the frame of
        // the vessel: a part can move its collider between the physics step it was measured after and the frame
        // being drawn, and the drawing has to show the collider the physics has, not the one the game draws.
        private readonly Dictionary<Collider, ColliderOverlay> partOverlays = new Dictionary<Collider, ColliderOverlay>();
        private readonly Dictionary<Collider, Matrix4x4> partPoses = new Dictionary<Collider, Matrix4x4>();
        private readonly Dictionary<long, Overlap> overlaps = new Dictionary<long, Overlap>();

        // The colliders stood in, in the order of the window's list, each with its overlaps, deepest first.
        private readonly List<ColliderOverlay> listed = new List<ColliderOverlay>();
        private readonly Dictionary<Collider, List<Overlap>> byCollider = new Dictionary<Collider, List<Overlap>>();

        // The deepest overlap, and the lift that takes the vessel out of every one, as last measured; the
        // lift is negative when none was found below MAX_LIFT.
        private float deepest;
        private float lift;

        private float nextRefresh;

        // Scratch buffers of a refresh.
        private readonly List<Collider> partColliders = new List<Collider>();
        private readonly HashSet<long> seen = new HashSet<long>();

        private Rect windowRect;
        private Vector2 listScroll;
        private GUIStyle labelStyle;

        private void Awake()
        {
            Shader shader = Shader.Find(SHADER_NAME);
            if (shader == null)
            {
                Debug.LogError(LOG_PREFIX + "shader " + SHADER_NAME + " not found: nothing will be drawn");
                enabled = false;
                return;
            }
            colliderLines = new Material[PALETTE.Length];
            for (int i = 0; i < PALETTE.Length; i++)
            {
                colliderLines[i] = MakeMaterial(shader, PALETTE[i], COLLIDER_QUEUE);
            }
            partLines = MakeMaterial(shader, PART_LINE, COLLIDER_QUEUE);
            // White: the arrows carry their colour in their vertices.
            arrowLines = MakeMaterial(shader, Color.white, ARROW_QUEUE);
            ApplyDepthTest();
            windowRect = new Rect(Screen.width - WINDOW_WIDTH - WINDOW_MARGIN, 0.5f * Screen.height, WINDOW_WIDTH, 0f);
        }

        private void OnDestroy()
        {
            Clear();
            foreach (Material material in AllMaterials())
            {
                if (material != null)
                {
                    Destroy(material);
                }
            }
        }

        /// <summary>A transparent material of <paramref name="color"/>, drawn at
        /// <paramref name="queue"/>.</summary>
        private static Material MakeMaterial(Shader shader, Color color, int queue)
        {
            Material material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            material.SetColor("_Color", color);
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_Cull", (int)CullMode.Off);
            material.SetInt("_ZWrite", 0);
            material.renderQueue = queue;
            return material;
        }

        /// <summary>Gives every material the depth test of the current display mode.</summary>
        private void ApplyDepthTest()
        {
            CompareFunction test = display == Display.ThroughEverything
                ? CompareFunction.Always
                : CompareFunction.LessEqual;
            foreach (Material material in AllMaterials())
            {
                material.SetInt("_ZTest", (int)test);
            }
        }

        private IEnumerable<Material> AllMaterials()
        {
            yield return arrowLines;
            yield return partLines;
            if (colliderLines != null)
            {
                foreach (Material material in colliderLines)
                {
                    yield return material;
                }
            }
        }

        // Mod+F6 shows or hides the window, the same key for every KSP Diag. Static: the choice holds from one
        // flight scene to the next.
        private static readonly KeyBinding WINDOW_KEY = new KeyBinding(KeyCode.F6);
        private static bool windowVisible = true;

        /// <summary>Whether the window shows, as Mod+F6 toggles it; the measures go on either way.</summary>
        internal static bool WindowVisible
        {
            get { return windowVisible; }
            set { windowVisible = value; }
        }

        /// <summary>Where the window is on the screen, and how big.</summary>
        internal Rect WindowRect
        {
            get { return windowRect; }
            set { windowRect = value; }
        }

        /// <summary>
        /// Whether every vessel is kept on rails, as the first box of the window says. Ticked, no vessel
        /// leaves the rails, those loaded afterwards included; unticked, the game lets them go as it always
        /// does. A vessel already off the rails stays so.
        /// </summary>
        internal static bool KeepOnRails
        {
            get { return keepOnRails; }
            set { keepOnRails = value; }
        }

        private void FixedUpdate()
        {
            HoldUnpacking();
        }

        private void Start()
        {
            StartCoroutine(MeasureAfterPhysics());
        }

        private void Update()
        {
            HoldUnpacking();
            if (GameSettings.MODIFIER_KEY.GetKey() && WINDOW_KEY.GetKeyDown())
            {
                windowVisible = !windowVisible;
            }
        }

        /// <summary>Measures the overlaps again every <see cref="REFRESH_PERIOD"/>, each time right after a
        /// physics step, where the physics has the colliders.</summary>
        private IEnumerator MeasureAfterPhysics()
        {
            // A part can move its colliders between physics steps: a landing leg on rails puts its foot in
            // one place for the physics step and in another for the frame drawn, some centimetres apart.
            // Measured during Update, the result would hang on whether a physics step ran in that frame.
            WaitForFixedUpdate afterPhysics = new WaitForFixedUpdate();
            while (true)
            {
                yield return afterPhysics;
                if (Time.unscaledTime < nextRefresh)
                {
                    continue;
                }
                nextRefresh = Time.unscaledTime + REFRESH_PERIOD;
                Refresh();
            }
        }

        /// <summary>Keeps the game from taking any vessel off the rails for the next few steps, while
        /// <see cref="keepOnRails"/> asks for it.</summary>
        private static void HoldUnpacking()
        {
            // The game takes vessels off the rails in its LateUpdate, unless this hold is on; it lifts the
            // hold itself after the given number of physics steps.
            if (keepOnRails)
            {
                OrbitPhysicsManager.HoldVesselUnpack(HOLD_STEPS);
            }
        }

        private void LateUpdate()
        {
            if (display == Display.Nothing)
            {
                return;
            }
            foreach (ColliderOverlay overlay in overlays.Values)
            {
                if (overlay.Collider != null)
                {
                    overlay.Draw(colliderLines[overlay.ColorIndex], DRAW_LAYER);
                }
            }
            if (vessel != null)
            {
                Matrix4x4 frame = vessel.transform.localToWorldMatrix;
                foreach (KeyValuePair<Collider, ColliderOverlay> entry in partOverlays)
                {
                    if (partPoses.TryGetValue(entry.Key, out Matrix4x4 pose))
                    {
                        entry.Value.DrawAt(partLines, DRAW_LAYER, frame * pose);
                    }
                }
            }
            foreach (Overlap overlap in overlaps.Values)
            {
                overlap.Draw(arrowLines, DRAW_LAYER);
            }
        }

        /// <summary>
        /// Measures again every overlap of a collider of the active vessel with a collider around it, and
        /// brings the drawing and the window in line with them.
        /// </summary>
        private void Refresh()
        {
            Vessel active = FlightGlobals.ActiveVessel;
            if (active != vessel)
            {
                Clear();
                vessel = active;
            }
            if (vessel == null || !vessel.loaded)
            {
                Clear();
                return;
            }

            // The queries below read where the physics has the colliders: give it the transforms as they are.
            Physics.SyncTransforms();

            seen.Clear();
            float scale = SCALES[scaleIndex];
            foreach (Part part in vessel.parts)
            {
                part.GetComponentsInChildren(false, partColliders);
                foreach (Collider collider in partColliders)
                {
                    if (!IsSolid(collider))
                    {
                        continue;
                    }
                    Bounds bounds = collider.bounds;
                    foreach (Collider other in Physics.OverlapBox(bounds.center, bounds.extents, Quaternion.identity,
                        LayersHit(collider.gameObject.layer), QueryTriggerInteraction.Ignore))
                    {
                        if (!IsSolid(other) || IsOfVessel(other, vessel))
                        {
                            continue;
                        }
                        long key = Overlap.Key(collider, other);
                        if (!overlaps.TryGetValue(key, out Overlap overlap))
                        {
                            overlap = new Overlap(collider, part, other, vessel);
                        }
                        if (!overlap.Refresh(scale, ColorOf))
                        {
                            overlap.Destroy();
                            overlaps.Remove(key);
                            continue;
                        }
                        overlaps[key] = overlap;
                        seen.Add(key);
                    }
                }
            }
            DropUnseen();
            RefreshColliders();
            RefreshPartColliders();
            MeasureLift();
        }

        /// <summary>The colour of an arrow, and of its label, for an overlap of <paramref name="depth"/>
        /// metres.</summary>
        private static Color ColorOf(float depth)
        {
            return Color.Lerp(SHALLOW, DEEP, depth / RED_DEPTH);
        }

        /// <summary>Whether <paramref name="collider"/> can hold something up and be measured: on, in an
        /// active object, not a trigger, and not a wheel, which the physics does not measure.</summary>
        private static bool IsSolid(Collider collider)
        {
            return collider != null && collider.enabled && collider.gameObject.activeInHierarchy
                && !collider.isTrigger && !(collider is WheelCollider);
        }

        /// <summary>Whether <paramref name="collider"/> belongs to a part of <paramref name="of"/>.</summary>
        private static bool IsOfVessel(Collider collider, Vessel of)
        {
            Part part = collider.GetComponentInParent<Part>();
            return part != null && part.vessel == of;
        }

        /// <summary>The layers whose colliders the physics lets collide with one of
        /// <paramref name="layer"/>.</summary>
        private static int LayersHit(int layer)
        {
            int mask = 0;
            for (int i = 0; i < 32; i++)
            {
                if (!Physics.GetIgnoreLayerCollision(layer, i))
                {
                    mask |= 1 << i;
                }
            }
            return mask;
        }

        /// <summary>Drops the overlaps not found in this refresh.</summary>
        private void DropUnseen()
        {
            List<long> gone = null;
            foreach (KeyValuePair<long, Overlap> entry in overlaps)
            {
                if (!seen.Contains(entry.Key))
                {
                    (gone ?? (gone = new List<long>())).Add(entry.Key);
                }
            }
            if (gone == null)
            {
                return;
            }
            foreach (long key in gone)
            {
                overlaps[key].Destroy();
                overlaps.Remove(key);
            }
        }

        /// <summary>Makes <see cref="overlays"/> hold one overlay per collider stood in, and nothing else,
        /// and the window's list follow them, the deepest overlap first under each.</summary>
        private void RefreshColliders()
        {
            byCollider.Clear();
            deepest = 0f;
            foreach (Overlap overlap in overlaps.Values)
            {
                if (!byCollider.TryGetValue(overlap.Other, out List<Overlap> list))
                {
                    list = new List<Overlap>();
                    byCollider.Add(overlap.Other, list);
                }
                list.Add(overlap);
                deepest = Mathf.Max(deepest, overlap.Depth);
            }

            List<Collider> gone = null;
            foreach (Collider collider in overlays.Keys)
            {
                if (collider == null || !byCollider.ContainsKey(collider))
                {
                    (gone ?? (gone = new List<Collider>())).Add(collider);
                }
            }
            if (gone != null)
            {
                foreach (Collider collider in gone)
                {
                    overlays[collider].Destroy();
                    overlays.Remove(collider);
                }
            }
            foreach (KeyValuePair<Collider, List<Overlap>> entry in byCollider)
            {
                if (!overlays.TryGetValue(entry.Key, out ColliderOverlay overlay))
                {
                    overlay = new ColliderOverlay(entry.Key) { ColorIndex = LeastUsedColor() };
                    overlays.Add(entry.Key, overlay);
                }
                overlay.Refresh();
                entry.Value.Sort((a, b) => b.Depth.CompareTo(a.Depth));
            }

            listed.Clear();
            listed.AddRange(overlays.Values);
            listed.Sort((a, b) => string.CompareOrdinal(ShortName(a.Collider), ShortName(b.Collider)));
        }

        /// <summary>Makes <see cref="partOverlays"/> hold one overlay per collider of a part that stands in another
        /// one, and nothing else, and <see cref="partPoses"/> where each is now, in the frame of the vessel.</summary>
        private void RefreshPartColliders()
        {
            HashSet<Collider> standing = new HashSet<Collider>();
            foreach (Overlap overlap in overlaps.Values)
            {
                standing.Add(overlap.PartCollider);
            }
            List<Collider> gone = null;
            foreach (Collider collider in partOverlays.Keys)
            {
                if (collider == null || !standing.Contains(collider))
                {
                    (gone ?? (gone = new List<Collider>())).Add(collider);
                }
            }
            if (gone != null)
            {
                foreach (Collider collider in gone)
                {
                    partOverlays[collider].Destroy();
                    partOverlays.Remove(collider);
                    partPoses.Remove(collider);
                }
            }
            Matrix4x4 toVessel = vessel.transform.worldToLocalMatrix;
            foreach (Collider collider in standing)
            {
                if (!partOverlays.TryGetValue(collider, out ColliderOverlay overlay))
                {
                    overlay = new ColliderOverlay(collider);
                    partOverlays.Add(collider, overlay);
                }
                overlay.Refresh();
                partPoses[collider] = toVessel * overlay.PhysicsPose;
            }
        }

        /// <summary>Sets <see cref="lift"/> to how far the vessel has to be lifted, along the vertical, for
        /// none of the overlaps measured to be left: zero when there is none, negative when even
        /// <see cref="MAX_LIFT"/> leaves one.</summary>
        private void MeasureLift()
        {
            if (overlaps.Count == 0)
            {
                lift = 0f;
                return;
            }
            Vector3 up = Up();
            if (AnyLeft(up * MAX_LIFT))
            {
                lift = -1f;
                return;
            }
            // Halve the range between a lift that leaves an overlap and one that leaves none.
            float low = 0f;
            float high = MAX_LIFT;
            for (int i = 0; i < LIFT_STEPS; i++)
            {
                float middle = 0.5f * (low + high);
                if (AnyLeft(up * middle))
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }
            lift = high;
        }

        /// <summary>Whether any of the overlaps measured is left once the vessel is moved by
        /// <paramref name="shift"/>.</summary>
        private bool AnyLeft(Vector3 shift)
        {
            foreach (Overlap overlap in overlaps.Values)
            {
                if (overlap.PartCollider != null && overlap.Other != null
                    && Overlap.Measure(overlap.PartCollider, shift, overlap.Other, out _, out _))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The vertical at the active vessel: away from the centre of its body.</summary>
        private Vector3 Up()
        {
            return ((Vector3)((Vector3d)vessel.transform.position - vessel.mainBody.position)).normalized;
        }

        /// <summary>The colour of the palette that the fewest colliders drawn now have, the first one on a
        /// tie.</summary>
        private int LeastUsedColor()
        {
            int[] uses = new int[PALETTE.Length];
            foreach (ColliderOverlay overlay in overlays.Values)
            {
                uses[overlay.ColorIndex]++;
            }
            int least = 0;
            for (int i = 1; i < uses.Length; i++)
            {
                if (uses[i] < uses[least])
                {
                    least = i;
                }
            }
            return least;
        }

        /// <summary>Drops every overlay and overlap.</summary>
        private void Clear()
        {
            foreach (ColliderOverlay overlay in overlays.Values)
            {
                overlay.Destroy();
            }
            foreach (Overlap overlap in overlaps.Values)
            {
                overlap.Destroy();
            }
            foreach (ColliderOverlay overlay in partOverlays.Values)
            {
                overlay.Destroy();
            }
            overlays.Clear();
            overlaps.Clear();
            partOverlays.Clear();
            partPoses.Clear();
            listed.Clear();
            byCollider.Clear();
            deepest = 0f;
            lift = 0f;
        }

        // ---- The window ----

        private void OnGUI()
        {
            GUI.skin = HighLogic.Skin;
            DrawLabels();
            if (!windowVisible)
            {
                return;
            }
            windowRect = GUILayout.Window(WINDOW_ID, windowRect, DrawWindow, "KSP Diag - On Rails");
        }

        /// <summary>Writes the depth of each overlap at the tip of its arrow, unless nothing is drawn.</summary>
        private void DrawLabels()
        {
            if (display == Display.Nothing || overlaps.Count == 0)
            {
                return;
            }
            Camera camera = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (camera == null)
            {
                return;
            }
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            }
            foreach (Overlap overlap in overlaps.Values)
            {
                if (overlap.PartCollider == null)
                {
                    continue;
                }
                Vector3 screen = camera.WorldToScreenPoint(overlap.Tip);
                if (screen.z <= 0f)
                {
                    continue;
                }
                string text = Millimetres(overlap.Depth);
                Vector2 size = labelStyle.CalcSize(new GUIContent(text));
                Rect rect = new Rect(screen.x + 4f, Screen.height - screen.y - 0.5f * size.y, size.x, size.y);
                // A dark copy under the text, so that it reads on bright ground as on dark.
                labelStyle.normal.textColor = Color.black;
                GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, labelStyle);
                labelStyle.normal.textColor = ColorOf(overlap.Depth);
                GUI.Label(rect, text, labelStyle);
            }
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();
            bool keep = GUILayout.Toggle(keepOnRails, "Keep vessels on rails");
            if (keep != keepOnRails)
            {
                keepOnRails = keep;
            }
            GUILayout.Label(VesselLine());
            ChooseDisplay(GUILayout.SelectionGrid((int)display, DISPLAY_LABELS, 1));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Arrow length:", GUILayout.ExpandWidth(false));
            int chosen = GUILayout.SelectionGrid(scaleIndex, SCALE_LABELS, SCALE_LABELS.Length);
            GUILayout.EndHorizontal();
            if (chosen != scaleIndex)
            {
                ChooseScale(SCALES[chosen]);
            }
            GUILayout.Label(DeepestLine());
            GUILayout.Label(LiftLine());
            if (overlaps.Count > 0)
            {
                GUILayout.Label(PartsLine());
            }

            listScroll = GUILayout.BeginScrollView(listScroll, GUILayout.Height(LIST_HEIGHT));
            foreach (ColliderOverlay overlay in listed)
            {
                Collider collider = overlay.Collider;
                if (collider == null || !byCollider.TryGetValue(collider, out List<Overlap> list))
                {
                    continue;
                }
                GUILayout.BeginHorizontal();
                // A swatch of the colour the collider is drawn in.
                Rect swatch = GUILayoutUtility.GetRect(16f, 16f, GUILayout.Width(16f), GUILayout.Height(16f));
                Color previous = GUI.color;
                GUI.color = PALETTE[overlay.ColorIndex];
                GUI.DrawTexture(swatch, Texture2D.whiteTexture);
                GUI.color = previous;
                GUILayout.Label(ShortName(collider) + Remarks(collider));
                GUILayout.EndHorizontal();
                foreach (Overlap overlap in list)
                {
                    GUILayout.Label("    " + PartName(overlap) + ": " + Millimetres(overlap.Depth) + ", "
                        + Degrees(AngleFromUp(overlap)) + " from the vertical");
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        /// <summary>The line of the window that says which vessel is looked at, and whether it is on the
        /// rails.</summary>
        private string VesselLine()
        {
            if (vessel == null)
            {
                return "No active vessel";
            }
            return vessel.vesselName + ": " + (vessel.packed ? "on rails" : "off rails, physics running");
        }

        private string DeepestLine()
        {
            return overlaps.Count == 0
                ? "No overlap"
                : "Deepest overlap: " + Millimetres(deepest);
        }

        private string LiftLine()
        {
            if (overlaps.Count == 0)
            {
                return "";
            }
            return lift < 0f
                ? "Lifted " + MAX_LIFT.ToString("F0") + " m, an overlap is left"
                : "Lift that leaves no overlap: " + Millimetres(lift);
        }

        /// <summary>The line of the window that says how the colliders of the parts are drawn.</summary>
        private static string PartsLine()
        {
            return "Parts in " + PART_LINE_NAME + ", where the physics has them";
        }

        /// <summary>What the list of the window says after the name of a collider stood in: the level of its
        /// quad when it is ground, and whether the physics uses its hull.</summary>
        private static string Remarks(Collider collider)
        {
            PQ quad = collider.GetComponent<PQ>();
            MeshCollider meshCollider = collider as MeshCollider;
            return (quad != null ? " (ground, level " + quad.subdivision + ")" : "")
                + (meshCollider != null && meshCollider.convex ? " (convex)" : "");
        }

        /// <summary>The name of a collider stood in, as the window lists it: that of its object after that of
        /// its parent, the name of the quad alone for the ground, the title of the part before it for a part
        /// of another vessel.</summary>
        private static string ShortName(Collider collider)
        {
            if (collider == null)
            {
                return "";
            }
            if (collider.GetComponent<PQ>() != null)
            {
                return collider.name;
            }
            Part part = collider.GetComponentInParent<Part>();
            if (part != null)
            {
                return part.partInfo.title + " (" + collider.name + ")";
            }
            Transform parent = collider.transform.parent;
            return (parent != null ? parent.name + "/" : "") + collider.name;
        }

        /// <summary>The part of an overlap as the window lists it: its title, then the name of its
        /// collider.</summary>
        private static string PartName(Overlap overlap)
        {
            return overlap.Part.partInfo.title + " (" + overlap.PartCollider.name + ")";
        }

        /// <summary>The angle between the way out of <paramref name="overlap"/> and the vertical, in
        /// degrees.</summary>
        private float AngleFromUp(Overlap overlap)
        {
            return Vector3.Angle(overlap.Direction, Up());
        }

        private static string Millimetres(float metres)
        {
            return (metres * 1000f).ToString("F1") + " mm";
        }

        private static string Degrees(float degrees)
        {
            return degrees.ToString("F0") + "°";
        }

        /// <summary>A length in millimetres, rounded as the window shows it.</summary>
        private static double RoundedMillimetres(float metres)
        {
            return System.Math.Round(metres * 1000.0, 1);
        }

        // ---- What the MCP tools do ----

        /// <summary>
        /// What the window shows, ready to be written as JSON: whether vessels are kept on rails, the active
        /// vessel and whether it is on rails, the display mode, the arrow scale, the deepest overlap and the
        /// lift that leaves none (null when an overlap is left at the highest lift tried), the colour the
        /// colliders of the parts are drawn in (empty when there is no overlap), and the colliders
        /// stood in, each with its name, colour, ground level when it is ground, whether it is convex, and its
        /// overlaps: the part, its collider, the depth and the angle from the vertical, as shown.
        /// </summary>
        internal Dictionary<string, object> Read()
        {
            List<object> colliders = new List<object>();
            foreach (ColliderOverlay overlay in listed)
            {
                Collider collider = overlay.Collider;
                if (collider == null || !byCollider.TryGetValue(collider, out List<Overlap> list))
                {
                    continue;
                }
                List<object> parts = new List<object>();
                foreach (Overlap overlap in list)
                {
                    parts.Add(new Dictionary<string, object>
                    {
                        { "part", overlap.Part.partInfo.title },
                        { "collider", overlap.PartCollider.name },
                        { "depthMm", RoundedMillimetres(overlap.Depth) },
                        { "angleFromVertical", System.Math.Round((double)AngleFromUp(overlap)) }
                    });
                }
                MeshCollider meshCollider = collider as MeshCollider;
                Dictionary<string, object> line = new Dictionary<string, object>
                {
                    { "name", ShortName(collider) },
                    { "colour", PALETTE_NAMES[overlay.ColorIndex] },
                    { "convex", meshCollider != null && meshCollider.convex },
                    { "overlaps", parts }
                };
                PQ quad = collider.GetComponent<PQ>();
                if (quad != null)
                {
                    line.Add("groundLevel", quad.subdivision);
                }
                colliders.Add(line);
            }
            return new Dictionary<string, object>
            {
                { "keepOnRails", keepOnRails },
                { "vessel", vessel != null ? vessel.vesselName : "" },
                { "onRails", vessel != null && vessel.packed },
                { "display", DISPLAY_LABELS[(int)display] },
                { "arrowScale", SCALES[scaleIndex] },
                { "deepestMm", RoundedMillimetres(deepest) },
                { "liftMm", lift < 0f ? null : (object)RoundedMillimetres(lift) },
                { "partColour", overlaps.Count > 0 ? PART_LINE_NAME : "" },
                { "colliders", colliders }
            };
        }

        /// <summary>
        /// Chooses a display mode, as a click on its label in the window does: its index in the window, from
        /// the top. Returns the label of the mode now chosen.
        /// </summary>
        internal string ChooseDisplay(int index)
        {
            if (index < 0 || index >= DISPLAY_LABELS.Length)
            {
                throw new System.ArgumentOutOfRangeException(nameof(index), "0 to " + (DISPLAY_LABELS.Length - 1));
            }
            Display chosen = (Display)index;
            if (chosen != display)
            {
                display = chosen;
                ApplyDepthTest();
            }
            return DISPLAY_LABELS[index];
        }

        /// <summary>
        /// Chooses how long the arrows are, as times the depth, as a click on its label in the window does:
        /// one of 1, 10 and 100. Returns the scale now chosen.
        /// </summary>
        internal int ChooseScale(int scale)
        {
            int index = System.Array.IndexOf(SCALES, scale);
            if (index < 0)
            {
                throw new System.ArgumentOutOfRangeException(nameof(scale), "1, 10 or 100");
            }
            if (index != scaleIndex)
            {
                scaleIndex = index;
                // The arrows are rebuilt at their new length at once.
                nextRefresh = 0f;
            }
            return SCALES[scaleIndex];
        }
    }
}
