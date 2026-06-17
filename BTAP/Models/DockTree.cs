using System.Text.Json;
using System.Text.Json.Serialization;

namespace BTAP.Models;

/// <summary>
/// Recursive tree describing how dockable panels are arranged in the editor body.
/// A <see cref="DockSplit"/> divides its area in two; a <see cref="DockLeaf"/>
/// holds a single panel identified by <see cref="DockLeaf.PanelId"/>.
///
/// Panel IDs are stable strings ("library", "center", "inspector") so the tree
/// can be serialised to settings.json and remain valid across version bumps.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DockSplit), "split")]
[JsonDerivedType(typeof(DockLeaf),  "leaf")]
public abstract class DockNode
{
    [JsonIgnore] public DockSplit? Parent { get; set; }
}

public enum DockOrientation
{
    /// <summary>Children side-by-side (vertical splitter line). First = left.</summary>
    Horizontal,
    /// <summary>Children stacked (horizontal splitter line). First = top.</summary>
    Vertical,
}

/// <summary>Direction a dragged panel is dropped relative to the target leaf.
/// North/South split vertically; West/East split horizontally.</summary>
public enum DropDirection { North, South, West, East }

public sealed class DockSplit : DockNode
{
    public DockOrientation Orientation { get; set; }

    /// <summary>Fraction of the area taken by <see cref="First"/>. The remaining
    /// (1-Ratio) goes to <see cref="Second"/>. Clamped to [0.05, 0.95] so a
    /// child is never reduced to zero pixels.</summary>
    public double Ratio { get; set; } = 0.5;

    public DockNode First  { get; set; } = null!;
    public DockNode Second { get; set; } = null!;
}

public sealed class DockLeaf : DockNode
{
    public string PanelId { get; set; } = string.Empty;
}

public static class DockTree
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public static string Serialize(DockNode root) =>
        JsonSerializer.Serialize(root, _json);

    /// <summary>Parses a previously serialised tree. Returns null on any error
    /// (caller substitutes a default) so corrupted settings never lock the user
    /// out of the editor.</summary>
    public static DockNode? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var root = JsonSerializer.Deserialize<DockNode>(json, _json);
            if (root is null) return null;
            FixParents(root, null);
            return root;
        }
        catch { return null; }
    }

    private static void FixParents(DockNode node, DockSplit? parent)
    {
        node.Parent = parent;
        if (node is DockSplit s)
        {
            FixParents(s.First,  s);
            FixParents(s.Second, s);
        }
    }

    /// <summary>Default tree mirroring the legacy 3-column body
    /// (library | center | inspector).</summary>
    public static DockNode DefaultTree() => Build(
        new DockSplit
        {
            Orientation = DockOrientation.Horizontal,
            Ratio       = 0.20,
            First       = new DockLeaf { PanelId = "library" },
            Second = new DockSplit
            {
                Orientation = DockOrientation.Horizontal,
                Ratio       = 0.74,
                First       = new DockLeaf { PanelId = "center" },
                Second      = new DockLeaf { PanelId = "inspector" },
            },
        });

    /// <summary>Simple preset: hides the inspector, leaves the library as a
    /// narrow strip on the left, center fills the rest.</summary>
    public static DockNode SimpleTree() => Build(
        new DockSplit
        {
            Orientation = DockOrientation.Horizontal,
            Ratio       = 0.18,
            First       = new DockLeaf { PanelId = "library" },
            Second      = new DockLeaf { PanelId = "center" },
        });

    /// <summary>Complex preset: inspector on the LEFT (transport/scopes feel),
    /// library on the right as a project panel, center wide in the middle. Mimics
    /// a Premiere-ish 3-column where Effects/Project flank the program monitor.</summary>
    public static DockNode ComplexTree() => Build(
        new DockSplit
        {
            Orientation = DockOrientation.Horizontal,
            Ratio       = 0.22,
            First       = new DockLeaf { PanelId = "inspector" },
            Second = new DockSplit
            {
                Orientation = DockOrientation.Horizontal,
                Ratio       = 0.72,
                First       = new DockLeaf { PanelId = "center" },
                Second      = new DockLeaf { PanelId = "library" },
            },
        });

    private static DockNode Build(DockNode root) { FixParents(root, null); return root; }

    /// <summary>Guarantees <paramref name="panelId"/> has a leaf in the tree. If
    /// it's missing (a previous DnD reshape happened while it was hidden),
    /// append it as a 25% strip on the right edge — the most predictable place
    /// for a re-appearing panel.</summary>
    public static DockNode EnsurePanel(DockNode root, string panelId)
    {
        if (FindLeaf(root, panelId) is not null) return root;
        var newRoot = new DockSplit
        {
            Orientation = DockOrientation.Horizontal,
            Ratio       = 0.75,
            First       = root,
            Second      = new DockLeaf { PanelId = panelId },
        };
        FixParents(newRoot, null);
        return newRoot;
    }

    /// <summary>Walks the tree and returns the first leaf whose PanelId matches.</summary>
    public static DockLeaf? FindLeaf(DockNode root, string panelId) => root switch
    {
        DockLeaf l when l.PanelId == panelId => l,
        DockSplit s => FindLeaf(s.First, panelId) ?? FindLeaf(s.Second, panelId),
        _ => null,
    };

    /// <summary>Enumerates every leaf in left-to-right, top-to-bottom order.</summary>
    public static IEnumerable<DockLeaf> Leaves(DockNode root)
    {
        switch (root)
        {
            case DockLeaf l: yield return l; break;
            case DockSplit s:
                foreach (var x in Leaves(s.First))  yield return x;
                foreach (var x in Leaves(s.Second)) yield return x;
                break;
        }
    }

    /// <summary>Moves <paramref name="sourcePanelId"/> next to <paramref name="targetPanelId"/>
    /// on the given <paramref name="side"/>. Source's old position is collapsed:
    /// its sibling takes over the parent split's slot, keeping the tree minimal.
    /// Returns the (possibly new) root.</summary>
    public static DockNode Move(DockNode root, string sourcePanelId, string targetPanelId, DropDirection side)
    {
        if (sourcePanelId == targetPanelId) return root;

        var sourceLeaf = FindLeaf(root, sourcePanelId);
        var targetLeaf = FindLeaf(root, targetPanelId);
        if (sourceLeaf is null || targetLeaf is null) return root;
        if (ReferenceEquals(sourceLeaf, targetLeaf))   return root;

        // 1. Detach source from its parent (collapse parent into source's sibling).
        root = Detach(root, sourceLeaf);

        // 2. Re-find the target leaf in the (possibly mutated) tree.
        targetLeaf = FindLeaf(root, targetPanelId);
        if (targetLeaf is null) return root;

        // 3. Wrap target in a new split with source on the requested side.
        var newSplit = new DockSplit
        {
            Orientation = (side == DropDirection.West || side == DropDirection.East)
                ? DockOrientation.Horizontal
                : DockOrientation.Vertical,
            Ratio = 0.5,
        };

        bool sourceFirst = (side == DropDirection.North) || (side == DropDirection.West);
        if (sourceFirst) { newSplit.First = sourceLeaf; newSplit.Second = targetLeaf; }
        else             { newSplit.First = targetLeaf; newSplit.Second = sourceLeaf; }

        var targetParent = targetLeaf.Parent;
        sourceLeaf.Parent = newSplit;
        targetLeaf.Parent = newSplit;
        newSplit.Parent   = targetParent;

        if (targetParent is null)
        {
            root = newSplit;
        }
        else if (ReferenceEquals(targetParent.First, targetLeaf))
        {
            targetParent.First = newSplit;
        }
        else
        {
            targetParent.Second = newSplit;
        }

        FixParents(root, null);
        return root;
    }

    /// <summary>Removes <paramref name="leaf"/> from the tree. Its parent split is
    /// collapsed into the sibling so the tree never carries empty branches.
    /// Returns the (possibly new) root.</summary>
    private static DockNode Detach(DockNode root, DockLeaf leaf)
    {
        var parent = leaf.Parent;
        if (parent is null) return root; // can't detach the only leaf

        var sibling = ReferenceEquals(parent.First, leaf) ? parent.Second : parent.First;
        sibling.Parent = parent.Parent;

        if (parent.Parent is null)
        {
            FixParents(sibling, null);
            return sibling;
        }

        if (ReferenceEquals(parent.Parent.First, parent))
            parent.Parent.First = sibling;
        else
            parent.Parent.Second = sibling;

        FixParents(root, null);
        return root;
    }
}
