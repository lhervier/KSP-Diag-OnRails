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
            "Reads what the window of KSP Diag - On Rails shows: keepOnRails, the active vessel and onRails, " +
            "display mode, arrowScale, deepestMm, liftMm (null beyond 20 m), and the colliders the vessel stands " +
            "in with their overlaps (part, collider, depthMm, angleFromVertical), deepest first.")]
        internal static object Read()
        {
            return Mod().Read();
        }

        [McpTool("onrails_keep_on_rails",
            "Ticks or unticks Keep vessels on rails of KSP Diag - On Rails, as a click does: ticked, no vessel " +
            "leaves the rails (a save loaded afterwards included); a vessel already off stays off. Any scene. " +
            "Returns keepOnRails.")]
        internal static object KeepOnRails(bool keep)
        {
            KSPDiagOnRails.KeepOnRails = keep;
            return new Dictionary<string, object> { { "keepOnRails", KSPDiagOnRails.KeepOnRails } };
        }

        [McpTool("onrails_set_display",
            "Chooses how KSP Diag - On Rails draws, as its three modes do: 0 hidden by what stands in front " +
            "(default), 1 through everything, 2 off. Measuring goes on in every mode. Returns the mode (display).")]
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
