using System;
using System.Collections.Generic;
using UnityEngine;
using com.github.lhervier.ksp.util.mcpserver;

namespace com.github.lhervier.ksp.diag.onrails
{
    /// <summary>
    /// What KSP-Util-MCPServer, when it is installed, offers of this mod as tools: what its window shows, each
    /// of its choices, and the moving of the window. Each tool returns no more than the window shows. Each
    /// method but the keeping on rails works on the mod loaded in the flight scene. Nothing here is needed to
    /// play by hand, and this mod runs the same without KSP-Util-MCPServer: only that server reads the attribute.
    /// </summary>
    internal static class McpTools
    {
        [McpTool("onrails_read",
            "Reads what the window of KSP Diag - On Rails shows, without writing anything: whether vessels are kept " +
                "on rails (keepOnRails), the active vessel (vessel, empty when there is none) and whether it is on " +
                "rails (onRails), the display mode, the arrow scale (arrowScale, the arrows are that many times the " +
                "depth), the deepest overlap in mm (deepestMm), the lift along the vertical that leaves no overlap in " +
                "mm (liftMm, null when even 20 m leaves one), the colour the colliders of the parts are drawn in, " +
                "where the physics has them (partColour, empty when there is no overlap), and the colliders the " +
                "active vessel stands in " +
                "(colliders), each with its name as listed (the quad's name for the ground), its colour, the level of " +
                "its quad when it is ground (groundLevel), whether it is convex, and its overlaps: the part's title " +
                "(part), the name of its collider (collider), the depth in mm (depthMm) and the angle between the way " +
                "out and the vertical in degrees (angleFromVertical), deepest first.")]
        internal static object Read()
        {
            return Mod().Read();
        }

        [McpTool("onrails_keep_on_rails",
            "Ticks or unticks the box Keep vessels on rails of KSP Diag - On Rails, as clicking it does. Ticked, no " +
            "vessel leaves the rails, those of a save loaded afterwards included, until it is unticked; a vessel " +
            "already off the rails stays so. Unticked, the game takes the vessels off the rails as it always does. " +
            "Works in any scene. Returns the state of the box (keepOnRails).")]
        internal static object KeepOnRails(bool keep)
        {
            KSPDiagOnRails.KeepOnRails = keep;
            return new Dictionary<string, object> { { "keepOnRails", KSPDiagOnRails.KeepOnRails } };
        }

        [McpTool("onrails_set_display",
            "Chooses how KSP Diag - On Rails draws the colliders stood in, the arrows and their labels, as clicking " +
            "one of the three modes of its window does: 0 Hidden by what stands in front (the default), 1 Through " +
            "everything, 2 Off. What it measures goes on in every mode. Returns the mode now chosen (display).")]
        internal static object SetDisplay(double mode)
        {
            return new Dictionary<string, object> { { "display", Mod().ChooseDisplay((int)mode) } };
        }

        [McpTool("onrails_set_arrow_scale",
            "Chooses how long the arrows of KSP Diag - On Rails are, as clicking x1, x10 or x100 in its window does: " +
            "scale 1, 10 or 100, the arrows being that many times the depth. Returns the scale now chosen " +
            "(arrowScale).")]
        internal static object SetArrowScale(double scale)
        {
            return new Dictionary<string, object> { { "arrowScale", Mod().ChooseScale((int)Math.Round(scale)) } };
        }

        [McpTool("onrails_move_window",
            "Moves the window of KSP Diag - On Rails, as dragging it does: x and y in pixels from the top left " +
            "corner of the screen. Returns its position and size (x, y, width, height).")]
        internal static object MoveWindow(double x, double y)
        {
            KSPDiagOnRails mod = Mod();
            Rect rect = mod.WindowRect;
            rect.x = (float)x;
            rect.y = (float)y;
            mod.WindowRect = rect;
            return new Dictionary<string, object>
            {
                { "x", (double)rect.x },
                { "y", (double)rect.y },
                { "width", (double)rect.width },
                { "height", (double)rect.height }
            };
        }

        [McpTool("onrails_show_window",
            "Shows or hides the window of KSP Diag - On Rails, as Mod+F6 does; what it measures goes on either way. " +
            "Returns whether it shows (visible).")]
        internal static object ShowWindow(bool visible)
        {
            KSPDiagOnRails.WindowVisible = visible;
            return new Dictionary<string, object> { { "visible", KSPDiagOnRails.WindowVisible } };
        }

        private static KSPDiagOnRails Mod()
        {
            KSPDiagOnRails mod = UnityEngine.Object.FindObjectOfType<KSPDiagOnRails>();
            if (mod == null)
            {
                throw new InvalidOperationException("KSP Diag - On Rails only runs in flight");
            }
            return mod;
        }
    }
}
