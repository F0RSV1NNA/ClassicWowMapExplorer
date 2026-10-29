using System.Numerics;

namespace MapExtract;

/// Places where you can get from A to B without walking the ground between them.
///
/// A navmesh describes surface, and some connections are not surface. Walking into the
/// Darnassus portal on map 1 puts you in Darnassus with nothing walkable in between, so the
/// flood sees two pieces and a router sees no route - see document.md 40 and 41.
///
/// Detour has off-mesh connections for exactly this. They are marked in the viewer and
/// stored with the rest of the hand edits (MapEdits); this is only the shape the bake takes.
public static class OffMeshLinks
{
    public readonly record struct Link(int Map, Vector3 From, Vector3 To, float Radius, bool Bidirectional,
                                       string Mode = "walk", int UserId = 0);

    /// Every link marked for one map.
    public static List<Link> Load(int mapId) => MapEdits.Links(mapId);
}
