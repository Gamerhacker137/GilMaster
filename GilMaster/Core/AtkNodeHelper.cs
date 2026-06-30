using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Numerics;

namespace GilMaster.Core;

/// <summary>
/// Converts a native FFXIV UI node's local position/scale into absolute viewport
/// coordinates by walking the parent chain — the math needed to anchor an ImGui overlay
/// (e.g. a button) to a game window. Ported verbatim from Artisan's AtkResNodeFunctions;
/// it has no dependencies beyond FFXIVClientStructs + System.Numerics.
/// </summary>
internal static class AtkNodeHelper
{
    public static unsafe Vector2 GetNodePosition(AtkResNode* node)
    {
        var pos = new Vector2(node->X, node->Y);
        var par = node->ParentNode;
        while (par != null)
        {
            pos *= new Vector2(par->ScaleX, par->ScaleY);
            pos += new Vector2(par->X, par->Y);
            par = par->ParentNode;
        }
        return pos;
    }

    public static unsafe Vector2 GetNodeScale(AtkResNode* node)
    {
        if (node == null) return new Vector2(1, 1);
        var scale = new Vector2(node->ScaleX, node->ScaleY);
        while (node->ParentNode != null)
        {
            node = node->ParentNode;
            scale *= new Vector2(node->ScaleX, node->ScaleY);
        }
        return scale;
    }
}
