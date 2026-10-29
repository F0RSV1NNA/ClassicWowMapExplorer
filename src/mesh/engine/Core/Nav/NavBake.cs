using System.Numerics;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

namespace MapExtract;

/// Bakes a triangle soup into Detour navmesh tiles.
///
/// The bake is tiled, and has to be: Recast stores polymesh vertex indices as 16 bit,
/// so one mesh cannot exceed 65,535 vertices. A single dense ADT (Stormwind) reaches
/// ~52k on its own, so baking a whole tile as one mesh sits just under a hard wall and
/// anything larger overflows. Tiles are indexed on a global grid derived from the map
/// origin, so tiles baked from different ADTs still line up.
///
/// Recast is Y-up and our world is Z-up, so geometry is rotated on the way in and the
/// debug mesh back out. Per-triangle areas make water swimmable and magma/slime a
/// distinct hazard rather than collapsing into walkable-or-not.
public static class NavBake
{
    // Area ids keep TrinityCore's meanings for ground / water / magma so existing
    // consumers keep working, and add ocean and slime, which the extractor already
    // tells apart. Detour stores the area in 6 bits, so 0..63 are all available.
    //
    //   11 ground  0x01     TC
    //   10 —       0x02     TC's steep ground; we do not emit it
    //    9 water   0x04     TC   fresh water: lakes, rivers, pools
    //    8 magma   0x08     TC
    //    7 slime   0x10     new
    //    6 ocean   0x20     new
    //
    // Suggested filter: ground | water | ocean = 0x25, leaving magma and slime out
    // so a path never routes through them. The old TC mask 0x0D excludes ocean, so
    // a consumer using it will refuse to swim the sea until it is widened.
    public const int AreaNull = 0;
    public const int AreaGround = 11;
    public const int AreaWater = 9;
    public const int AreaMagma = 8;
    public const int AreaSlime = 7;
    public const int AreaOcean = 6;

    /// Surface the bake found inside the ground and disowned.
    ///
    /// An area of its own rather than simply clearing the flags, because clearing them is
    /// not durable: NavPrune re-derives every polygon's flags from its area afterwards, to
    /// undo an earlier `drop`, and that handed these their flags straight back. The whole
    /// map still had every buried polygon routable while a single-tile bake looked fixed -
    /// the prune only runs over an assembled map. Carrying it in the area means the fact
    /// survives the round trip and anything that recomputes flags reaches the same answer.
    public const int AreaBuried = 5;
    public const int AreaBlocked = 4;
    public const int AreaTransport = 3;
    /// Rasterisation-only marker: this span came from a doodad. Cleared to AreaNull before
    /// the compact heightfield is built, so nothing downstream ever sees it - it exists just
    /// long enough to tell a tree from a ceiling. See DoodadRoofSpans.
    // Recast merges overlapping solid spans with max(area) when their tops are
    // within climb height. This temporary marker must outrank every walkable
    // area; otherwise ground wins the merge and a blocked fence becomes a
    // raised ground span before filtering even starts. Cleared before compact.
    public const int AreaDoodadSolid = 63;
    // Explicitly rejected WMO trim, not ordinary steep stair risers. Outranks
    // walkable WMO provenance during climb-bounded raster span merging.
    internal const int AreaWmoSolid = 60;
    // Raster-only underside provenance. Unlike rejected trim, an underside must
    // lose an overlapping merge to genuine terrain/WMO footing (areas >= 6).
    // A standalone ceiling must never be promoted as a low-hanging obstacle.
    internal const int AreaWmoUnderside = 2;

    /// The floor of the swimmable volume: ground with water over it too deep to stand in.
    ///
    /// Depth decides this and nothing else. Deeper than SwimDepth under a liquid surface
    /// and you are swimming, so the mesh under you is swim floor whether it is the flat bed
    /// of a lake or the bank running down into it. Shallower and it is a ford: ordinary
    /// ground you wade across on foot.
    ///
    /// Assigned in ToDetour, after the polygon mesh is built, so it is a label and changes
    /// no geometry. An earlier cut made it an area at rasterisation time instead, which
    /// split Recast's regions along it and cut long straight seams across the lake bed.
    public const int AreaSwimFloor = 10;

    /// Ground that is road, from the painted terrain textures and the viewer's road brush.
    ///
    /// An AREA, not a flag: it carries FlagGround like any ground, so a consumer filtering on
    /// flags sees no difference and the tile format does not change, while a path query can
    /// give road polygons a lower cost than the rest. Set on the compact heightfield before
    /// regions are built, so Recast cuts polygons along the road's edge and a road polygon
    /// is road all the way across.
    public const int AreaRoad = 12;

    public const int FlagGround = 0x01;
    public const int FlagWater = 0x04;
    public const int FlagMagma = 0x08;
    public const int FlagSlime = 0x10;
    public const int FlagOcean = 0x20;

    // A lake bottom is ordinary ground geometry and Recast bakes it like any other floor,
    // so without these a path crosses the seabed exactly as if it were a road — the
    // opposite of being unable to reach it. These mark ground that lies under a liquid
    // surface, so a consumer can require submerged movement deliberately instead of
    // wandering into it. Detour poly flags are 16 bit; 0x40 and 0x80 are free.
    public const int FlagSubmerged = 0x40;
    public const int FlagUnderHazard = 0x80;

    /// Everything a swimmer may enter; hazards deliberately absent.
    /// Crossing this polygon means swimming, not walking.
    ///
    /// Set on every liquid surface, and on any floor lying deeper than SwimDepth beneath
    /// one. A consumer that wants to stay dry excludes it and still walks shallow water,
    /// because the ground under shallow water is a separate, unflagged polygon; a consumer
    /// going after something on a lake bed includes it.
    public const int FlagSwim = 0x200;

    /// Reachable from nowhere. Not a surface type but a verdict about connectivity, which
    /// can only be reached once the whole map is assembled - so it is set by NavPrune after
    /// a bake, never during one. Kept out of FlagsWalkable so a query filter can exclude it
    /// without excluding anything else.
    public const int FlagStranded = 0x100;

    /// How far above a liquid surface a polygon may sit and still count as part of that
    /// body of water for reachability. Roughly a body's height: the shore shelf you wade in
    /// from belongs to the lake, a bridge deck over it does not.
    public const float FlagStrandedSurfaceSlack = 2.2f;

    /// Every flag that says "this is some kind of surface". Clearing all of them leaves a
    /// polygon no include mask can match, which is what NavPrune's drop option does.
    public const int FlagsAnySurface = FlagGround | FlagWater | FlagOcean | FlagMagma | FlagSlime;

    public const int FlagsWalkable = FlagGround | FlagWater | FlagOcean;
    public const int FlagsHazard = FlagMagma | FlagSlime;

    public const float AdtSize = 533.33333f;

    /// Recast X/Z origin for the whole world. Tile indices in the dtMeshHeader are
    /// measured from here, which is what dtNavMesh::getTileAt resolves against.
    public const float WorldOrigin = -32f * AdtSize;

    public sealed class Settings
    {
        // Defaults match the staged TrinityCore tiles exactly: cell 0.2666,
        // radius 0.5333, height 2.133, climb 1.067. The nav DLL's edge-margin
        // logic is tuned against that erosion, so changing it changes steering.
        public float CellSize = 0.26666665f;
        /// Vertical quantum. Every span top, and so every detail height, is a multiple of
        /// this, which is why the mesh sits above the collision it came from and why two
        /// tiles meeting on a flat floor can land one step apart.
        ///
        /// Half of CellSize, not equal to it. The step where tiles meet IS this number: on
        /// the WMO floor at (2120.3, 533.3) it measured 0.27, 0.14 and 0.06 yd at quanta of
        /// 0.267, 0.133 and 0.067, and the mesh sat 0.19, 0.08 and 0.04 yd above the 708.81
        /// collision it came from. Nothing else moves - polygon count, stranding, fan count
        /// and longest edge are flat across all three.
        ///
        /// AND IT IS NOT SHIPPED, because the seam is not what it costs. Halving reopened
        /// the two ramp holes at (2011.8, 579.0) and (2019.5, 574.7) that 34 had just
        /// closed - they erode away and `FillPinholes` no longer reaches them - took
        /// railing and trim 1,122 -> 1,427 yd2 and bare columns 312 -> 500, for 7 MB.
        /// Raising the pinhole limit to 25 yd2 does not buy them back: it restores one of
        /// them at the wrong level and costs another point of stranding.
        ///
        /// Over the whole map the seam was never quantisation-limited anyway - the median
        /// step is 0.02 yd at this setting - so what halving buys is 83 samples in the tail
        /// against holes in mesh someone reported. CELL_H is there to revisit it, ideally
        /// alongside a fix for whatever makes those ramp cells erode in the first place.
        public float CellHeight =
            float.TryParse(Environment.GetEnvironmentVariable("CELL_H"), out float chh) ? chh : 0.26666665f;
        public float AgentHeight = 2.1333332f;
        public float AgentRadius = 0.5333333f;
        public float AgentMaxClimb = 1.0666666f;

        /// How deep the water has to be before the floor stops being walkable.
        ///
        /// 2.03128 is the standard humanoid's collision height, confirmed against this
        /// build's own CASC: of 989 CreatureModelData rows with a collision height the
        /// median is 2.0313 and 687 carry exactly 2.03128. It is also the constant
        /// TrinityCore's TerrainMgr::GetLiquidStatus compares against - delta above it is
        /// UNDER_WATER, delta above zero is IN_WATER, delta above -0.1 is WATER_WALK.
        ///
        /// So this is the depth at which the water closes over your head, and that is a
        /// slightly different question from where the client makes you start swimming,
        /// which happens earlier - around chest height, by eye. That threshold is NOT in
        /// the client data: CreatureModelData carries collision height, hover height and
        /// mount height but nothing about swimming, and LiquidType's only depth field is
        /// MaxDarkenDepth, which is a fog setting. It lives in the client binary.
        ///
        /// 2.03128 is the humanoid CollisionHeight, and it was the wrong number: it is the
        /// depth at which a player is fully submerged, not the depth at which they start
        /// to swim. The difference is a whole chest, and it showed up on rivers, which are
        /// shallower than lakes - the stream through Elwynn is swum in game and came out
        /// as wadeable ground along its whole length.
        ///
        /// Measured by walking the stream with `--waterline`, which reports the water, the
        /// channel bottom beside the line rather than under it - a straight line across a
        /// meandering stream spends half its length on the bank - and what the bake called
        /// each. The channel runs 1.26 to 2.54 yd deep along its whole length and is swum
        /// throughout in game.
        ///
        /// Three quarters of the collision height, 1.5235, left eight stretches of bed
        /// still called ground, all of them where the channel measures between 1.26 and
        /// 1.47. Three fifths, 1.2188, leaves none: every part of the bed comes out as
        /// swim floor, which is what the water does.
        ///
        /// It stays a fraction of the one citable number rather than a figure of its own,
        /// and it costs nothing where water is deep - Zephras Isle moves 54 polygons out
        /// of 130,000 between the two, and its stranding not at all, because a lake bed is
        /// far past either.
        public float SwimDepth = 1.2188f;

        /// The climb allowed when deciding whether two SUBMERGED cells are neighbours.
        ///
        /// BuildCompactHeightfield joins two adjacent cells only when their floors are
        /// within walkableClimb of each other, which at 0.27 yd per cell caps the
        /// steepness of any connected surface at atan(climb / cellSize) - 76 degrees with
        /// the walking climb. Past that every cell is an island, and erosion, which treats
        /// a cell with fewer than four connections as a boundary, takes the lot. That is a
        /// third and separate way steep ground vanishes, after the slope test and
        /// FilterLedgeSpans, and it is the one that survives both of those being fixed.
        ///
        /// 4 yd lifts the ceiling to 86 degrees. It is applied ONLY between two cells that
        /// are both swim floor, so nothing gains a way to climb out of the water: the link
        /// from the top of a submerged wall to the dry bank above it is still judged at
        /// AgentMaxClimb.
        ///
        /// Handing the same number to Recast for every cell was tried first and is not
        /// safe - the regions stop being simply connected and BuildContours throws
        /// "Multiple outlines for region", losing a whole ADT. BuildTileMesh falls back to
        /// the plain build if that happens anyway.
        /// 2 yd, not 4, and the difference is not subtle. Joining cells across a big step
        /// lets a region wrap around onto two spans in the same column, which Recast calls
        /// "overlapping regions" and resolves by discarding the ambiguous spans. At 4 yd
        /// that cost tile [26,29] 36,000 yd2 of navmesh - a band 107 by 518 yd - while
        /// gaining a few hundred elsewhere. At 2 yd there are no warnings, no loss, and the
        /// wall it was added for still meshes to within 4.3 yd of the waterline.
        ///
        ///     lift   warnings   tile [26,29]   the cliff at (2742, 574)
        ///     0             0    270,444 yd2   no mesh
        ///     1.5           0    270,612       643.8
        ///     2             0    270,657       643.8
        ///     3             1    270,699       643.8
        ///     4             2    234,472       642.8
        public float SwimConnectClimb = 2f;

        /// How far under a liquid surface the swim floor is allowed to finish.
        ///
        /// Where rock breaks the surface its cell is cut down to this far below the water
        /// rather than thrown away, so the mesh runs to the shore instead of stopping a
        /// cell's worth of height short of it. Roughly one cell: enough that the detail
        /// mesh, which interpolates between samples and is free to overshoot a little, does
        /// not surface. Zero reaches the edge exactly and then pokes through it.
        public float SwimSurfaceMargin = 0.3f;

        /// How far under a liquid surface erosion is not applied to the swim floor.
        ///
        /// Erosion exists to keep an agent's width off a drop, and above the top of a
        /// submerged wall is dry cliff - null area - so it trims the agent radius off the
        /// top of the swim floor. On a steep bank that is the visible difference between
        /// mesh that reaches the shore and mesh that stops a few yards under it.
        ///
        /// Only this band, not the whole bed: restoring everything erosion took restores
        /// the ragged edge of every region as well, and that measured ten times the
        /// polygons poking above the water for no gain at the shore.
        public float SwimShoreBand = 3f;

        /// How far under the ADT heightmap a polygon may sit before it is disowned.
        ///
        /// ZERO, meaning off, and that is a retreat from a bad idea rather than a setting
        /// nobody got round to.
        ///
        /// Recast does leave polygons inside cliffs - one measured 23 yd inside a 64 degree
        /// face that was itself unwalkable - and disowning them looked like the fix. It is
        /// not, because "under the heightmap" is not the same question as "inside rock".
        /// Asking instead whether anything holds the polygon up:
        ///
        ///     tile      under the heightmap   in a cave   on a WMO/doodad   unsupported
        ///     [27,30]                   302           2               292             8
        ///     [25,29]                 2,239       1,573               558           108
        ///     [26,30]                 1,003         145               677           181
        ///
        /// A bridge deck, a rock shelf, a building floor: all below the heightmap, all
        /// carrying navmesh exactly where it belongs. Disowning by roof alone threw those
        /// away, several hundred per tile, which across a map is missing mesh everywhere.
        /// Genuinely unsupported mesh is a fifth of a percent of a tile.
        ///
        /// Kept because --buried needs it to measure, and because a consumer that would
        /// rather lose ground than trust it can turn it on. 2.5 is the value to use if so:
        /// between 0.5 and 2.5 the amount caught does not move at all - 75 detail triangles
        /// on [27,30] at every setting - while polygons disowned fall from 426 to 60,
        /// because the ground grid is sampled per yard and on a 50 degree slope a corner
        /// sits 0.6 yd under the sampled height while resting on it. Past 4 yd it starts
        /// letting real offenders through.
        public float BuriedMargin = 0f;

        /// Put the liquid surfaces into the heightfield alongside the terrain.
        ///
        /// OFF, and this is the shoreline ring. A water surface rasterises as a span, and
        /// Recast's FilterWalkableLowHeightSpans nulls any walkable span with less than
        /// walkableHeight of clear air above it - so the water reads as a CEILING and the
        /// lake bed under it is deleted for being underwater. Measured over one pool,
        /// counting columns whose floor survives against the depth of water above them:
        ///
        ///     depth (yd)     columns   floor gone, liquid in   floor gone, liquid out
        ///     0.00-0.27       29,502                     934                    1,029
        ///     0.27-0.55       20,944                  20,944                       47
        ///     0.55-1.07        3,640                   3,640                       24
        ///     1.07-1.60        5,881                   5,881                       61
        ///     1.60-2.13        2,008                   2,008                       59
        ///     2.13-3.00        2,478                     685                      121
        ///
        /// Total destruction between 0.27 and 2.13 yd of depth, and nothing above it -
        /// 2.13 being walkableHeight exactly. Below a cell the water merges into the ground
        /// span and survives; above a body's height there is room to stand. In between, the
        /// whole shelf goes, which on a gentle bank is a ring several yards wide. It is also
        /// why deep water looks fine: past 2.13 yd the floor was never in danger.
        ///
        /// Nothing is lost by leaving the water out. It was a duplicate of the .mliq layer,
        /// and a surface polygon cannot express swimming anyway - you can only be exactly ON
        /// a polygon, never ten yards below it. What comes back is the lake bed: ground you
        /// can wade across, stand on, and path over, which is the floor of the volume a
        /// swimmer moves through.
        public bool RasterizeLiquid = false;


        /// How far below the liquid surface a floor has to be before it counts as
        /// submerged. Slack, not a wade depth.
        ///
        /// FlagSubmerged means "this ground is under liquid" — any liquid, any depth — and
        /// consumers add it to the include mask to allow underwater objectives. So the only
        /// job of this number is to stop a shoreline floor sitting at the same height as the
        /// water sheet from reading as submerged through float error.
        ///
        /// It was briefly raised to a "wade depth" of 0.65 x agent height on the reasoning
        /// that you walk until the water is chest high. That reasoning was invented, and
        /// TrinityCore does not work that way: TerrainMgr::GetLiquidStatus compares
        /// liquidLevel - z against 0 for IN_WATER and against collisionHeight (default
        /// 2.03128, near enough our agent height) for UNDER_WATER, with no wade constant
        /// anywhere between. Raising it also quietly redefined the flag to mean "deep water
        /// only", which is not what anything reading 0x40 expects.
        ///
        /// If a genuine walk-versus-swim distinction is wanted, it belongs in its own flag
        /// at collisionHeight, not in this one.
        public float SubmergedSlack = 0.5f;
        public float AgentMaxSlope = 50f;
        public float MinRegionArea = 2f;
        public float MergeRegionArea = 8f;
        /// Yards; a contour edge longer than this is subdivided, which is the controlled
        /// way to make polygons smaller - unlike EdgeMaxError, which makes them smaller by
        /// letting the outline wander and costs stranding for it.
        public float EdgeMaxLen =
            float.TryParse(Environment.GetEnvironmentVariable("EDGE_LEN"), out float el) ? el : 12f;
        /// Yards a simplified contour may stray from the region boundary. Every yard of
        /// slack is a yard the polygon can bulge out over the wall beside the floor, and
        /// the detail mesh then has to draw the face between the two.
        public float EdgeMaxError =
            float.TryParse(Environment.GetEnvironmentVariable("EDGE_ERR"), out float ee) ? ee : 1.3f;
        public int VertsPerPoly = 6;

        /// Cap on how much ground one region may cover, in yards square. 0 disables.
        ///
        /// Not a quality knob so much as a workaround for a hard limit: BuildPolyDetail
        /// will not put more than 127 vertices in a polygon however large it is, so a big
        /// polygon cannot be made to follow the ground no matter how finely
        /// DetailSampleDist samples. Capping the region caps the polygons cut from it.
        ///
        /// 10 yd, from a sweep on tile [25,29], where the largest polygon before any of
        /// this covered 33,860 yd2 and its biggest detail triangle 1,296:
        ///
        ///     grid   polys    largest polygon   largest detail triangle
        ///     off    3,928         33,860 yd2               1,296 yd2
        ///     16     5,224          3,937                     616
        ///     10     7,113          2,336                     370
        ///      6    14,974          1,265                     202
        ///
        /// 6 nearly triples the polygon count for less than 10 gives, and the returns keep
        /// shrinking because below about this size the detail budget stops being what
        /// limits anything.
        ///
        /// 7 since interiors were looked at. That sweep was run on a terrain tile, where
        /// the 127-vertex cap is what bites; indoors nothing is near the cap and the
        /// polygons are simply large - on tile [28,30] the p90 footprint was 74 yd2 and the
        /// largest 118. Re-measured over the whole map, 7 is not the trade the table above
        /// implies:
        ///
        ///     grid   polys     largest poly   stranded   coverage
        ///     10    135,912        118 yd2      28.0%    187,046 yd2
        ///      7    186,560         59          26.8%    186,937
        ///      5    243,444         40          26.2%    186,800
        ///
        /// Coverage is identical to four digits, so this is subdivision and not new or lost
        /// surface, and stranding FALLS - smaller regions join up better than big ones. At
        /// 7 the longest polygon edge goes 16 -> 13 yd, thin polygons 2.2% -> 1.6%, railing
        /// and trim 1,045 -> 1,023 yd2, with no change in bake time. The cost is size:
        /// 27.3 -> 34.7 MB. 5 costs 43.1 MB for much less.
        public float RegionSplit =
            float.TryParse(Environment.GetEnvironmentVariable("REGION_SPLIT"), out float rs) ? rs : 7f;

        /// Bypass everything in BuildTileMesh and call RcBuilder.Build, for bisecting.
        public bool PlainBuild;
        /// How often the detail mesh samples the heightfield, in cells.
        ///
        /// 6 is Recast's usual figure and at this cell size it means a sample every 1.6 yd,
        /// so any ridge narrower than that is simply not seen and the terrain pokes up
        /// through the navmesh. Lowering it buys detail vertices, not polygons - the
        /// polygon count is identical at every setting. Over the whole of Zephras Isle:
        ///
        ///     6   1.60 yd apart   14.5 MB   49 s
        ///     4   1.07 yd apart   15.3 MB   69 s
        ///     3   0.80 yd apart   15.9 MB   97 s
        ///
        /// 2, which is as far as it is worth going. Measured against the raw terrain with
        /// --pokethrough, the navmesh already sits ABOVE the ground almost everywhere - the
        /// median gap is 0.40 yd, the cell quantisation - and terrain breaks through at
        /// only 0.2% of sampled columns. Tightening the sampling barely touches that:
        /// points more than a yard proud go 69 (at 3) to 59 (at 2) to 58 (at 1.5), while
        /// the bake goes 7.6 s to 11 s to 25 s per tile. What is left is mostly the rim of
        /// the mesh, where a boundary polygon's straight edge cuts across curved ground,
        /// and no sampling rate reaches that.
        public float DetailSampleDist =
            float.TryParse(Environment.GetEnvironmentVariable("DETAIL_DIST"), out float dd) ? dd : 2f;
        public float DetailSampleMaxError =
            // Explorer: one full height cell allowed the detail surface to cut
            // through shallow terrain ridges. Half a cell retains those samples
            // without the excessive triangulation of a tenth-cell tolerance.
            float.TryParse(Environment.GetEnvironmentVariable("DETAIL_ERR"), out float de) ? de : 0.5f;

        /// How far a detail vertex may sit from any span in its own column before it is
        /// treated as misplaced and snapped back. 0 disables.
        ///
        /// 1 yd, which is about four times the sampler's own error budget, so nothing it
        /// does on purpose is affected. See FlattenDetailSpikes.
        public float DetailSpikeSnap = 1f;

        /// Enables zero-footprint detail cleanup. Zero disables; positive-area
        /// ground triangles are always preserved, regardless of this legacy value.
        public float DetailBladeArea = 0.02f;

        /// Rebuild each polygon's detail triangulation with Delaunay. See
        /// RetriangulateDetail - this is what removes the fans rather than hiding them.
        public bool RetriangulateDetail = true;

        /// How far apart to lay points along a hull edge that has none. See
        /// RetriangulateDetail - this is what fixes a tile seam, where the contour is
        /// collinear and Recast leaves the whole edge bare.
        /// 1 yd. Swept on tile [26,29], watching the tilt of the needles against the
        /// tilt of everything around them - that gap, not the needle count, is what makes
        /// a crease visible:
        ///
        ///     spacing   file    detail area   needles   at the seam   needle vs local tilt
        ///     off      1,239      269,096         816      40%          29.2 vs 14.1 deg
        ///     3        1,451      269,840         635      47%          14.4 vs 15.8
        ///     2        1,789      269,959         569      41%          22.4 vs 16.5
        ///     1        2,990      270,207         744      12%          14.5 vs 14.5
        ///
        /// OFF, and it is worth knowing why it was ever on.
        ///
        /// Seeding was added to fix tile seams, where a contour clipped to the boundary is
        /// collinear and leaves one side of the hull bare. It did: the needles there went
        /// from 40% to 12% and came to lie at the angle of the ground around them.
        ///
        /// It also breaks the mesh. Two polygons sharing an edge each seed it from their
        /// own side, and any disagreement leaves a T-JUNCTION - one side carrying a vertex
        /// the other does not. The triangles then no longer share a complete edge, which
        /// is a hairline crack you can see through and shading that stops dead at the
        /// line, because there is no shared vertex to average a normal across. That is the
        /// seam with faceted ground on one side and smooth on the other.
        ///
        /// Making the point set a pure function of the edge's endpoints did not save it,
        /// because the budget is still per polygon: one can afford to seed where its
        /// neighbour cannot. On tile [28,29] that went the wrong way, 563 T-junctions to
        /// 3,597. Across the map, against the same bake with it off:
        ///
        ///                        off        on
        ///     T-junctions      2,542   218,829
        ///     edges over 40 yd   105       155
        ///     needles         17,899    41,987
        ///     detail fans        133       366
        ///     file              25.8 MB   44.6 MB
        ///
        /// Worse on every count. The seam problem it was aimed at is now mostly handled by
        /// splitting border regions - see SplitOversizeRegions - which was the real cause.
        /// Doing this properly would mean seeding each polygon EDGE once, globally, and
        /// handing the same points to both polygons that share it; per-submesh cannot work.
        public float DetailSeedSpacing;

        /// Largest enclosed hole, in yards square, to fill in rather than contour around.
        /// See FillPinholes. 0 disables.
        /// Yards squared; a hole erosion left in open floor, bounded entirely by cells that
        /// are still walkable, is put back if it is no bigger than this. 0 disables.
        public float ErodedPinholeFill =
            float.TryParse(Environment.GetEnvironmentVariable("ERODE_FILL"), out float ef) ? ef : 0f;
        public float PinholeFill =
            float.TryParse(Environment.GetEnvironmentVariable("PINHOLE"), out float ph) ? ph : 4f;

        /// Longest contour edge, in yards, before vertices are inserted along it.
        /// See DensifyContours. OFF, and the reason is worth keeping.
        ///
        /// It works on what it was aimed at. Ear clipping fans when the vertices it is
        /// handed are lopsided, and spacing them evenly along every contour edge - the
        /// region-against-region ones too, which Recast's own MaxEdgeLen skips - took the
        /// fans from 111 to 22 and the stranded polygons down with them.
        ///
        /// It also pulls the navmesh under the ground. A contour edge is a CHORD: over
        /// curved ground the straight line between two corners sags below the surface,
        /// and every vertex added along it sits on that chord. Measured on tile [26,29],
        /// sample points where terrain stands above the navmesh:
        ///
        ///                    points   over 0.25 yd   over 1 yd
        ///     off               323             26           0
        ///     2 yd            2,937            726          89
        ///
        /// Ten times as much, and it reads as the ground showing through the mesh in thin
        /// slivers along every triangle edge.
        ///
        /// Lifting those vertices onto the surface does not rescue it, and that is the
        /// part that makes this a dead end rather than a tuning problem. BuildPolyMesh
        /// matches vertices by x, y AND z; two regions meeting along a boundary must agree
        /// on all three or their polygons stop sharing an edge and the mesh comes apart.
        /// Recast's own corner heights agree because one rule computes them from the cells
        /// round the corner. Snapping to the nearest span under an interpolated guess is
        /// per-region and disagrees - that took terrain-above-navmesh to 2.4%. Taking the
        /// highest span at the corner is position-only and ought to agree, and it measured
        /// 2.4% as well, because a corner height chosen without reference to the region
        /// belongs to neither of them.
        ///
        /// So a polygon vertex has to lie on the contour, the contour is a chord, and the
        /// chord is under the hill. Fixing the fans properly means replacing the
        /// triangulation, not feeding it more vertices.
        public float ContourEdgeMax;
        /// Cells of erosion under a roof. Recast's own radius is used outdoors; a
        /// negative value keeps Recast's erosion everywhere.
        /// Doodads carry no walkable surface at all. They stay solid either way.
        /// Yards a detail vertex may sit outside its own polygon's height range.
        public float DetailBandMargin =
            float.TryParse(Environment.GetEnvironmentVariable("DETAIL_BAND"), out float db) ? db : 2f;
        /// Optional narrow-strip deletion; disabled by default to preserve landings.
        public float MinIndoorStripWidth =
            float.TryParse(Environment.GetEnvironmentVariable("STRIP_WIDTH"), out float sw) ? sw : 0f;
        /// Cells of morphological opening applied indoors. Clears strips about twice
        /// this wide without shrinking anything wider.
        public int IndoorOpenCells =
            int.TryParse(Environment.GetEnvironmentVariable("INDOOR_OPEN"), out int io) ? io : 0;
        /// Yards a detail vertex may sit BELOW its polygon's corners under a roof.
        public float IndoorFloorDrop =
            float.TryParse(Environment.GetEnvironmentVariable("INDOOR_DROP"), out float idp) ? idp : 0.3f;
        /// Yards; how far the indoor opening may step up while dilating. Above this
        /// it would climb onto a railing cap and put it back.
        public float LedgeMinDrop =
            float.TryParse(Environment.GetEnvironmentVariable("OPEN_STEP"), out float lmn) ? lmn : 0.35f;
        /// Yards; how far below a narrow level patch to look for the floor it is a
        /// railing over.
        public float RailingReach =
            float.TryParse(Environment.GetEnvironmentVariable("RAIL_REACH"), out float rr3) ? rr3 : 5f;
        /// Yards; indoors, a neighbour further than this above or below is a different
        /// surface, not a continuation of this one. **Off by default.**
        ///
        /// It works on what it was built for - the railing caps at (2114.5, 541.7) and
        /// (2116.0, 539.3) go from 29 and 33 cells to 6 and 12 - but it does not move the
        /// number that matters: over the whole map, railing and trim goes 987 -> 984 yd2
        /// for 1,390 extra polygons. Off because it does not pay, not because it breaks.
        ///
        /// It used to break. Severing made `BuildContours` throw "Multiple outlines for
        /// region N" and drop a tile through the whole fallback chain to the plain unsplit
        /// build, whose polygons run to 207 yd. That was never the merging fault this
        /// comment used to blame - the regions were connected, single-area and single-layer,
        /// and every check written to catch a broken one passed. It was the winding test in
        /// `RcContours`; see NavContours.cs, which vendors the fix. Severing is safe now.
        /// Yards squared; an indoor island smaller than this, that nothing in the tile can
        /// reach and that has mesh below it, is furniture. 0 disables the check.
        ///
        /// 100 is where the curve turns. It takes the furniture stack over (2118.6, 565.2)
        /// from three surfaces to one and railing and trim from 1,205 to 1,168 yd2, for
        /// three mainland polygons. 250 costs 92 mainland polygons and buys 6 yd2, so the
        /// gates stop describing furniture somewhere between the two. They are unreachable
        /// by construction either way; the tile-edge gate is what makes that true rather
        /// than an artefact of where the tile was cut.
        /// Yards; a linked neighbour this much higher or lower counts as an edge for
        /// erosion, so the surface is trimmed back from it. 0 = Recast's behaviour.
        ///
        /// Disabled by default: linked legal treads and terrain thresholds must
        /// remain connected. Source collision supplies final surface heights.
        /// Declared connections that are not surface - portals and the like. Loaded by the
        /// caller from offmesh.txt, because the bake should not be reading files.
        public List<OffMeshLinks.Link> OffMeshLinks = [];
        public float ErodeStepBreak =
            float.TryParse(Environment.GetEnvironmentVariable("ERODE_STEP"), out float es) ? es : 0f;
        /// Off, because it is not safe next to ErodeStepBreak. With erosion trimming back
        /// from steps, a real storey can be left narrow enough to come apart, and then this
        /// pass removes it: at 100 yd2 the whole 708.9 floor of the building at
        /// (2107.1, 548.2) disappeared. The furniture it was aimed at is worth far less
        /// than a floor, so it stays off until the two can tell each other apart.
        public float IndoorIslandArea =
            float.TryParse(Environment.GetEnvironmentVariable("ISLAND_AREA"), out float ia) ? ia : 0f;
        public float IndoorStepCut =
            float.TryParse(Environment.GetEnvironmentVariable("STEP_CUT"), out float sc3) ? sc3 : 0f;
        /// Cells; a region smaller than this after the splitting passes cannot be
        /// contoured and is removed. 0 disables the check.
        public int MinRegionCells =
            int.TryParse(Environment.GetEnvironmentVariable("MIN_REGION"), out int mr) ? mr : 6;
        public bool BlockDoodads = Environment.GetEnvironmentVariable("DOODAD_BLOCK") != "0";
        /// Preserve narrow indoor treads/thresholds; solid walls retain body clearance.
        public int IndoorErodeCells =
            int.TryParse(Environment.GetEnvironmentVariable("INDOOR_ERODE"), out int ie) ? ie : 0;
        /// Diagnostic: force every triangle to this area id. 0 = off.
        public int DebugForceArea;
        /// Diagnostic: tally heightfield spans by area after rasterization.
        public bool DebugSpanTally;

        /// Cells across one ADT. One Detour tile per ADT is what the loader expects.
        public int TileSizeCells => (int)MathF.Ceiling(AdtSize / CellSize);
    }

    /// Row/Col are the ADT indices the nav DLL derives from a world position and uses
    /// to build the filename. GridX/GridZ are the Detour grid indices stored inside the
    /// tile header, measured from WorldOrigin. They are deliberately different numbers.
    public sealed record NavTile(int Row, int Col, int GridX, int GridZ,
                                 DtMeshData Data, int Polys, int Bytes, int MeshVerts);

    public sealed class Result
    {
        public bool Ok;
        public string Message = "";
        public double RasterizeMs, BuildMs, DetourMs;
        public int InputTris, PolyCount, TileCount, FailedTiles;
        /// Recast's polymesh index is 16 bit; anything near 65,535 is about to overflow.
        public int MaxMeshVerts;
        /// Why the first failing tile failed — otherwise a blown tile is silent.
        public string FailMessage = "";
        /// Polygons per Recast area id, so the liquid split is visible not assumed.
        public readonly Dictionary<int, int> PolysByArea = [];
        /// Submerged spans the filters tried to delete and that were put back, and how many
        /// there were to begin with. A large first number is the point of the exercise.
        public int SwimFloorKept, SwimFloorSpans, SwimFloorDemoted, SwimFloorClamped;
        /// Of the demoted, how many lost the mark because the ceiling had no water there.
        public int SwimDemotedNoWater;
        /// Cell joins added between two submerged cells too steep for the walking climb.
        public int SwimConnectionsLifted;
        /// Pieces produced by chopping regions too large for a detail mesh to describe.
        public int RegionsSplit;
        /// Tiles where the chop broke contouring and the unsplit build was used instead.
        public int SplitGaveUp;
        /// Tiles that abandoned splitting because region ids ran out.
        public int SplitOutOfIds;
        /// Tiles that had to widen the split grid to fit inside the region id field.
        public int SplitCoarsened;
        /// Pieces made by splitting regions that wound over their own footprint.
        public int RegionsUnwound;
        /// Cells dropped by the indoor opening - trim too thin to stand on.
        public int TrimCells;
        /// Cells removed with regions too small to contour.
        public int TinyRegionCells;
        public int AreaIslandCellsRestored;
        /// Indoor cell links cut because the step was too high to be one surface.
        public int SeveredSteps;
        public int OneWayLinks;
        /// Off-mesh connections written into tiles.
        public int OffMeshLinks;
        /// Compact cells relabelled as road.
        public int RoadCells;
        /// Compact cells removed by the erase brush.
        public int ErasedCells;
        public int IslandCells;
        public int ErodedPinholes;
        /// Cells dropped as railing caps and trim - thin indoor strips of their own.
        public int RailingCells;
        /// Detail vertices pulled back inside the height their polygon covers.
        public int DetailOutOfBand;
        /// Doodad triangles rasterised as solid but never walkable.
        public int DoodadTrisBlocked;
        public int DoodadSpansReblocked;
        public int SourceSurfaceVertices;
        public int PlanarDetailVerticesRemoved;
        public int SourceWmoCapsBlocked, SolidWallCells;
        public int RaisedWmoCapsBlocked;
        public int CoveredWmoSpansRestored;
        /// Compact cells judged to be under a roof, which erode less.
        public int IndoorCells;
        /// Cells erosion removed.
        public int ErodedCells;
        /// Detail triangles that left the bake with an edge over 40 yd.
        public int LongDetailEdges;
        /// Tiles that kept a chopped build only by nudging the grid off the asked-for size.
        public int SplitNudged;
        /// Yards; the grid the last nudged tile settled on.
        public float NudgedTo;
        /// Yards; the widest split grid any tile settled on.
        public float WidestSplitGrid;
        /// Tiles that needed a coarser contour density than asked for.
        public int CoarserContours;
        /// Detail vertices that had been placed off the ground and were snapped back.
        public int DetailSpikes;
        /// Detail submeshes rebuilt with Delaunay instead of Recast's strip walk.
        public int DetailRetriangulated;
        /// Hull points laid along bare edges - tile seams, mostly - before re-triangulating.
        public int DetailSeeded;
        /// Enclosed holes in the walkable area filled, and the largest of them in cells.
        public int Pinholes, BiggestPinhole;
        /// Walkable cells in the compact heightfield - the grid the polygons are cut from.
        public int WalkableCells;
        /// Vertices inserted along contour edges so ear clipping has an even spread.
        public int ContourVertsAdded;
        /// Vertical-blade detail triangles removed.
        public int DetailBlades;
        /// Polygons found under the ground with no roof over them, stripped of their flags.
        public int BuriedPolys;
        /// Swim-floor cells within the shore band put back after erosion.
        public int SwimShoreRestored;
        /// Tiles where both partitions threw and Recast's plain build was used instead.
        public int SwimBuildFellBack;
        /// Why the first attempt threw, for the tiles that end up on the plain build.
        public string FallbackWhy = "";
        /// Tiles that needed monotone partitioning because watershed would not contour.
        public int MonotoneTiles;
        /// Triangle pieces produced by cutting terrain along a water surface.
        public int WaterlineCuts;
        /// Polygons that can only be crossed by swimming.
        public int SwimPolys;
        /// Walkable ground that lies under a liquid surface — seabeds, lake floors.
        public int SubmergedPolys, UnderHazardPolys;
        public string SpanTally = "";
        public long NavDataBytes;
        public List<NavTile> Tiles = [];
        public DtNavMeshParams MeshParams;

        public List<Vector3> DebugVerts = [];
        public List<int> DebugIndices = [];
        public List<int> DebugAreas = [];
        public double TotalMs => RasterizeMs + BuildMs + DetourMs;
    }

    /// World (Z-up) -> Recast, matching the nav DLL's wowToRc: a pure cyclic swap with
    /// no negation. Verified against the staged tiles: ADT[25,30] covers wowX 3200..3733
    /// and wowY 533..1067, and its header reads bmin (533.3, _, 3200.0).
    static RcVec3f ToRc(Vector3 v) => new(v.Y, v.Z, v.X);
    static Vector3 FromRc(float x, float y, float z) => new(z, x, y);

    public static int RowOf(float worldX) => (int)MathF.Floor(32f - worldX / AdtSize);
    public static int ColOf(float worldY) => (int)MathF.Floor(32f - worldY / AdtSize);

    static int AreaOfLiquid(LiquidClass c) => c switch
    {
        LiquidClass.Ocean => AreaOcean,
        LiquidClass.Magma => AreaMagma,
        LiquidClass.Slime => AreaSlime,
        _ => AreaWater,                   // Water, and Unknown treated as water
    };

    public static int FlagOfArea(int area) => area switch
    {
        AreaSwimFloor => FlagGround,
        AreaWater => FlagWater,
        AreaOcean => FlagOcean,
        AreaMagma => FlagMagma,
        AreaSlime => FlagSlime,
        AreaNull or AreaBuried or AreaBlocked or AreaTransport => 0,
        _ => FlagGround,
    };

    public static string AreaName(int area) => area switch
    {
        AreaGround => "ground", AreaSwimFloor => "swim floor", AreaBuried => "buried",
        AreaRoad => "road",
        AreaWater => "water", AreaOcean => "ocean",
        AreaMagma => "magma", AreaSlime => "slime", _ => "none",
    };

    /// World-space (Z-up) rectangle a bake is allowed to emit tiles for.
    public readonly record struct Clip(float MinX, float MinY, float MaxX, float MaxY)
    {
        /// The world rect of one ADT tile.
        public static Clip ForAdt(int tileX, int tileY)
        {
            const float T = 533.33333f;
            return new Clip((32 - tileX - 1) * T, (32 - tileY - 1) * T, (32 - tileX) * T, (32 - tileY) * T);
        }
    }

    public static Result Bake(Extractor.Soup soup, Settings s, CancellationToken ct = default,
                              Clip? clip = null, RoadMask? roads = null)
    {
        var r = new Result();
        int solidTris = soup.TriCount;
        int liquidTris = s.RasterizeLiquid ? soup.LiquidTris : 0;
        r.InputTris = solidTris + liquidTris;
        if (r.InputTris == 0) { r.Message = "no geometry"; return r; }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // From the SOUP, not from what was rasterised. FlagSubmerged has to keep working
        // whether or not the water went into the heightfield - knowing a floor is underwater
        // is independent of whether Recast was allowed to see the water.
        var ceiling = soup.LiquidTris > 0 ? new LiquidCeiling(soup) : null;
        var ground = new GroundLevel(soup);
        // A source patch's size/shape does not establish traversability. Preserve
        // collision support; reject obstacles from the compact walking graph.
        var sourceCaps = WmoCapPatches.Detect(soup,s,localObstaclesOnly:true);
        r.SourceWmoCapsBlocked=sourceCaps.Count;
        var surfaceFit = new SourceSurfaceFit(soup, s, clip,sourceCaps);
        var bodyClearance = new SourceBodyClearance(soup,s,sourceCaps);

        // ---- decide every solid triangle's area, splitting at the waterline -----
        //
        // Slope decides walking. Under water it decides nothing: you are swimming down
        // there, so there is no footing to lose and nowhere to slide to, and the bank of a
        // lake is as crossable as its bottom. No second angle to configure - a cosine of
        // zero accepts any face pointing upwards at all.
        //
        // Wading, though, needs somewhere to stand, and a wall denies that however shallow
        // the water is. So a submerged face is swim floor when the water is over your head
        // OR when it is too steep to stand on, and ordinary ground - a ford - only when it
        // is both shallow and walkable.
        //
        // The awkward case is a triangle that STRADDLES the surface, and on a cliff running
        // into a lake almost every triangle does. Its area has to be one value, so:
        //
        //   - judging it by its centre painted the whole thing swim floor, and a
        //     near-vertical triangle spans yards, so purple climbed twelve yards of cliff;
        //   - demanding it be wholly submerged threw it away instead, which left a cliff
        //     with no mesh at all from the waterline down to SwimDepth - the swim floor
        //     only began once the water was deep enough to qualify on depth alone.
        //
        // Neither is necessary. The triangle is cut along the surface and each piece is
        // classified on its own, so the submerged half is swim floor right up to the
        // waterline and the dry half is judged as dry land.
        float slopeCos = MathF.Cos(s.AgentMaxSlope * MathF.PI / 180f);
        var solidArea = new int[solidTris];
        var cutVerts = new List<Vector3>();
        var cutTris = new List<(int A, int B, int C, int Area)>();
        int cutBase = soup.Verts.Count + soup.LiquidVerts.Count;
        var below = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        var above = new List<(Vector3 A, Vector3 B, Vector3 C)>();

        int AddCutVert(Vector3 v) { cutVerts.Add(v); return cutBase + cutVerts.Count - 1; }

        // Submerged and walkable, for a piece already known to be under the surface.
        // Depth is judged at the middle of the piece, not at its shallowest corner.
        //
        // The shallowest corner was a deliberate bias, to stop swim floor being claimed
        // where it might surface. It is the wrong bias for THIS test: a triangle sloping
        // through three yards of water gets called a ford because one corner of it is
        // wadeable, so blue walkable ground appears well out of its depth. That is the
        // dangerous direction - something keeping its feet dry will walk into a lake -
        // while over-claiming swim only costs a ford that gets swum instead of waded.
        //
        // Nothing is needed to keep it under the surface, because every piece reaching
        // here is already wholly submerged: whole triangles by the nBelow == 3 test, cut
        // pieces by construction. The waterline itself is handled by the span clamp.
        int WetArea(float top, float midZ, bool walk) =>
            top - midZ > s.SwimDepth || !walk ? AreaSwimFloor : AreaGround;

        for (int t = 0; t < solidTris; t++)
        {
            var va = soup.Verts[soup.Indices[t * 3]];
            var vb = soup.Verts[soup.Indices[t * 3 + 1]];
            var vc = soup.Verts[soup.Indices[t * 3 + 2]];

            // A doodad is a prop, and nothing walks on a prop. Crates, rocks, barrels,
            // carts: a bot routed onto one gets stuck on it, so a doodad must carry no
            // navmesh at all - not stranded mesh, not flagged mesh, none.
            //
            // The triangle is still rasterised, with a null area. That matters: it stays
            // SOLID, so it goes on blocking, it goes on casting the cut-out that erosion
            // widens round it, and it goes on acting as a ceiling for anything beneath.
            // Only its top stops being ground.
            if (s.BlockDoodads && t < soup.TriKind.Count && soup.TriKind[t] == Extractor.Soup.KindDoodad)
            {
                solidArea[t] = AreaDoodadSolid;
                r.DoodadTrisBlocked++;
                continue;
            }

            if(sourceCaps.Contains(t)){solidArea[t]=AreaWmoSolid;continue;}
            if(t<soup.TriKind.Count&&soup.TriKind[t]==Extractor.Soup.KindWmo&&Vector3.Cross(vb-va,vc-va).Z<0){
                solidArea[t]=AreaWmoUnderside;continue;
            }
            bool walk = SlopeOkWorld(va, vb, vc, slopeCos);
            int dry = walk ? AreaGround : AreaNull;
            solidArea[t] = dry;

            if (ceiling is not { Any: true }) continue;
            var mid = (va + vb + vc) / 3f;
            if (ceiling.SurfaceZ(mid.X, mid.Y) is not { } top) continue;

            int nBelow = (va.Z < top ? 1 : 0) + (vb.Z < top ? 1 : 0) + (vc.Z < top ? 1 : 0);
            if (nBelow == 0) continue;                       // dry, judged as dry
            if (nBelow == 3)
            {
                // A sheer face carries no span for a top-down rasteriser anyway, so it is
                // the one thing still rejected outright.
                if (!walk && !SlopeOkWorld(va, vb, vc, 0f)) continue;
                solidArea[t] = WetArea(top, mid.Z, walk);
                continue;
            }

            below.Clear();
            above.Clear();
            ClipAtZ(va, vb, vc, top, below, above);
            if (below.Count == 0) continue;

            solidArea[t] = AreaNull;                         // replaced by its pieces
            foreach (var (pa, pb, pc) in below)
            {
                if (!walk && !SlopeOkWorld(pa, pb, pc, 0f)) continue;
                cutTris.Add((AddCutVert(pa), AddCutVert(pb), AddCutVert(pc),
                             WetArea(top, (pa.Z + pb.Z + pc.Z) / 3f, walk)));
            }
            // Null dry pieces still supply solid wall/riser collision.
            foreach (var (pa, pb, pc) in above)
                cutTris.Add((AddCutVert(pa), AddCutVert(pb), AddCutVert(pc), dry));
        }
        r.WaterlineCuts = cutTris.Count;

        // Keep dry WMO-top provenance through raster span merging. Unlike props,
        // these remain walkable unless the local cap test proves a raised shelf.
        for(int t=0;t<solidTris;t++)
            if(solidArea[t]==AreaGround&&t<soup.TriKind.Count&&soup.TriKind[t]==Extractor.Soup.KindWmo)
                {
                var a=soup.Verts[soup.Indices[t*3]];var b=soup.Verts[soup.Indices[t*3+1]];var c=soup.Verts[soup.Indices[t*3+2]];
                var n=Vector3.Cross(b-a,c-a);float span=MathF.Max(a.Z,MathF.Max(b.Z,c.Z))-MathF.Min(a.Z,MathF.Min(b.Z,c.Z));
                solidArea[t]=n.Z<n.Length()*.98f&&span<=s.CellHeight*2.01f?SurfaceClearance.WmoBevelTop:SurfaceClearance.WmoTop;
            }

        // ---- flatten to Recast arrays, in Y-up ------------------------------
        r.InputTris = solidTris + liquidTris + cutTris.Count;
        int nv = soup.Verts.Count + soup.LiquidVerts.Count + cutVerts.Count;
        var verts = new float[nv * 3];
        int w = 0;
        foreach (var v in soup.Verts) { var p = ToRc(v); verts[w++] = p.X; verts[w++] = p.Y; verts[w++] = p.Z; }
        foreach (var v in soup.LiquidVerts) { var p = ToRc(v); verts[w++] = p.X; verts[w++] = p.Y; verts[w++] = p.Z; }
        foreach (var v in cutVerts) { var p = ToRc(v); verts[w++] = p.X; verts[w++] = p.Y; verts[w++] = p.Z; }

        var tris = new int[r.InputTris * 3];
        var areas = new int[r.InputTris];
        for (int i = 0; i < solidTris * 3; i++) tris[i] = soup.Indices[i];
        for (int t = 0; t < solidTris; t++) areas[t] = solidArea[t];

        int liquidBase = soup.Verts.Count;
        for (int i = 0; i < liquidTris * 3; i++) tris[solidTris * 3 + i] = soup.LiquidIndices[i] + liquidBase;
        for (int t = 0; t < liquidTris; t++)
            areas[solidTris + t] = AreaOfLiquid(soup.LiquidTriClass[t]);

        int cutAt = solidTris + liquidTris;
        for (int j = 0; j < cutTris.Count; j++)
        {
            tris[(cutAt + j) * 3] = cutTris[j].A;
            tris[(cutAt + j) * 3 + 1] = cutTris[j].B;
            tris[(cutAt + j) * 3 + 2] = cutTris[j].C;
            areas[cutAt + j] = cutTris[j].Area;
        }

        if (s.DebugForceArea != 0)
            for (int t = 0; t < areas.Length; t++) areas[t] = s.DebugForceArea;

        var bmin = new RcVec3f(float.MaxValue, float.MaxValue, float.MaxValue);
        var bmax = new RcVec3f(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < nv; i++)
        {
            bmin = new RcVec3f(MathF.Min(bmin.X, verts[i * 3]), MathF.Min(bmin.Y, verts[i * 3 + 1]), MathF.Min(bmin.Z, verts[i * 3 + 2]));
            bmax = new RcVec3f(MathF.Max(bmax.X, verts[i * 3]), MathF.Max(bmax.Y, verts[i * 3 + 1]), MathF.Max(bmax.Z, verts[i * 3 + 2]));
        }
        r.RasterizeMs = sw.Elapsed.TotalMilliseconds;

        // ---- config -----------------------------------------------------------
        int border = RcConfig.CalcBorder(s.AgentRadius, s.CellSize);
        var cfg = new RcConfig(
            true, s.TileSizeCells, s.TileSizeCells, border, RcPartition.WATERSHED,
            s.CellSize, s.CellHeight, s.AgentMaxSlope, s.AgentHeight, s.AgentRadius, s.AgentMaxClimb,
            s.MinRegionArea, s.MergeRegionArea, s.EdgeMaxLen, s.EdgeMaxError, s.VertsPerPoly,
            s.DetailSampleDist, s.DetailSampleMaxError,
            // Recast's own filter pass is switched OFF and run by hand below, so that the
            // swim floor can be put back between the stages. See FilterKeepingSwimFloor.
            false, false, false,
            new RcAreaModification(AreaGround), true);

        // One Detour tile per ADT, on the grid the nav DLL resolves against.
        const float tws = AdtSize;
        int tx0 = (int)MathF.Floor((bmin.X - WorldOrigin) / tws);
        int tx1 = (int)MathF.Floor((bmax.X - WorldOrigin) / tws);
        int tz0 = (int)MathF.Floor((bmin.Z - WorldOrigin) / tws);
        int tz1 = (int)MathF.Floor((bmax.Z - WorldOrigin) / tws);

        r.MeshParams = new DtNavMeshParams
        {
            orig = new RcVec3f(WorldOrigin, 0, WorldOrigin),
            tileWidth = tws,
            tileHeight = tws,
            maxTiles = 1 << 14,
            maxPolys = 1 << 16,
        };

        var geom = new SoupGeom(verts, tris, bmin, bmax);
        var ctx = new RcContext();
        var builder = new RcBuilder();

        // ---- per tile ----------------------------------------------------------
        sw.Restart();
        double detourMs = 0;

        for (int tz = tz0; tz <= tz1; tz++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                if (ct.IsCancellationRequested) { r.Message = "cancelled"; return r; }

                // When baking one ADT at a time we still load its neighbours so the
                // border ring has geometry, but only emit tiles centred on this ADT —
                // otherwise adjacent bakes would fight over the same tile index.
                if (clip is { } c)
                {
                    // tile centre back in world (Z-up): rc.X is wowY, rc.Z is wowX
                    float wy = WorldOrigin + (tx + 0.5f) * tws;
                    float wx = WorldOrigin + (tz + 0.5f) * tws;
                    if (wx < c.MinX || wx >= c.MaxX || wy < c.MinY || wy >= c.MaxY) continue;
                }

                float minX = WorldOrigin + tx * tws, minZ = WorldOrigin + tz * tws;

                // The vertical grid has to be GLOBAL, exactly as the horizontal one is.
                //
                // X and Z start at WorldOrigin plus a whole number of tiles, so every tile
                // rasterises onto the same columns. Y used to start at bmin.Y - the lowest
                // vertex in THIS ADT's soup, an arbitrary number that is different for
                // every ADT. Span heights are an integer count of cells from there, so two
                // tiles rasterising the same piece of ground rounded it onto two vertical
                // lattices offset by some fraction of a cell, and every height derived from
                // those spans inherited the offset - contour, polygon, detail vertex.
                //
                // That is the seam. Four tiles meet at (3200.0, 2133.3) and each put its
                // corner vertex at a different height - 835.1661, 835.2410, 835.2566,
                // 835.2656, a spread of 0.0995 yd against a cell height of 0.2667 - which
                // reads as a step you can see through from the side. Nothing horizontal was
                // ever wrong, which is why the tiles line up in plan and only the heights
                // disagree.
                //
                // Flooring to a whole number of cells from zero puts every tile on one
                // lattice. It only ever lowers the floor, by less than a cell, so no span
                // moves out of range.
                float minY = MathF.Floor(bmin.Y / s.CellHeight) * s.CellHeight;
                var tmin = new RcVec3f(minX, minY, minZ);
                var tmax = new RcVec3f(minX + tws, bmax.Y, minZ + tws);

                // the border ring lets neighbouring tiles agree on their shared edge
                float pad = border * s.CellSize;
                var hfMin = new RcVec3f(tmin.X - pad, tmin.Y, tmin.Z - pad);
                var hfMax = new RcVec3f(tmax.X + pad, tmax.Y, tmax.Z + pad);
                int hw = s.TileSizeCells + border * 2, hh = s.TileSizeCells + border * 2;

                var solid = new RcHeightfield(hw, hh, hfMin, hfMax, s.CellSize, s.CellHeight, border);

                // Rasterize every triangle; Recast clips to the heightfield bounds.
                //
                // Do NOT route this through RcTriMesh.GetChunksOverlappingRect: the chunky
                // mesh reorders triangles, so node.i does not index our per-triangle area
                // array and every area comes out wrong. Ground dominates, so the damage
                // was invisible — it just silently erased every water and lava surface.
                // With one tile per ADT there is nothing to cull anyway.
                int rasterized = r.InputTris;
                int realClimb = (int)MathF.Ceiling(s.AgentMaxClimb / s.CellHeight);
                // Ordinary low risers may merge with walkable footing as in
                // Recast. Explicit cap/prop markers outrank every walkable area,
                // so that allowance cannot resurrect rejected fences or trim.
                RcRasterizations.RasterizeTriangles(ctx, verts, tris, areas, r.InputTris, solid, realClimb);
                if (rasterized == 0) continue;
                SolidObserver?.Invoke("rasterized",solid);
                r.CoveredWmoSpansRestored += RestoreCoveredWmoSpans(ctx,solid,verts,tris,areas,r.InputTris,realClimb,surfaceFit);

                // A tree is not a roof.
                //
                // "Indoors" means solid geometry overhead, and a branch 20 yd up satisfies
                // that as readily as a ceiling - so the ground under a tree was indoor
                // ground, and the indoor trim pass ate it in the shape of the branches. The
                // hole at (2092.1, 650.1) is a canopy at z 690.9 over terrain at 670.2,
                // with the agent 2.1 yd tall and 20 yd of clear air between.
                //
                // The kind is known at rasterisation and nowhere after it, so it is
                // recorded here and the spans put back to AreaNull immediately, before
                // anything - filters, the compact build - can mistake a marked span for
                // walkable ground. Only the roof test in ErodeIndoorAware reads it.
                DoodadRoofSpans = null;
                var nonWalkableWmo = new HashSet<long>();
                var rejectedWmoCaps=new HashSet<long>();
                var wmoBevels=new HashSet<long>();
                var wmoStructural=new HashSet<long>();
                for(int ci=0;ci<solid.spans.Length;ci++)
                    for(var sp=solid.spans[ci];sp!=null;sp=sp.next)
                        if(sp.area is AreaWmoSolid or AreaWmoUnderside){
                            long key=((long)ci<<20)|(uint)sp.smin;nonWalkableWmo.Add(key);
                            if(sp.area==AreaWmoSolid)rejectedWmoCaps.Add(key);sp.area=AreaNull;
                        }
                        else if(sp.area is SurfaceClearance.WmoTop or SurfaceClearance.WmoBevelTop)
                        {
                            long key=((long)ci<<20)|(uint)sp.smin;
                            if(sp.area==SurfaceClearance.WmoBevelTop)wmoBevels.Add(key);else wmoStructural.Add(key);
                            sp.area=AreaGround;
                        }
                if (s.BlockDoodads)
                {
                    var roof = new HashSet<long>();
                    for (int ci = 0; ci < solid.spans.Length; ci++)
                        for (var sp = solid.spans[ci]; sp != null; sp = sp.next)
                            if (sp.area == AreaDoodadSolid)
                            {
                                roof.Add(((long)ci << 20) | (uint)sp.smin);
                                sp.area = AreaNull;
                            }
                    DoodadRoofSpans = roof;
                }

                // A narrow exterior footing can be coplanar with a broad room
                // floor. Keep the room; reject only locally exposed wall bases.
                var localFootings=new List<(int Column,RcSpan Span,long Key)>();
                foreach(long key in wmoStructural){
                    int ci=(int)(key>>20);
                    for(var sp=solid.spans[ci];sp!=null;sp=sp.next){
                        if(sp.smin!=(int)(key&0xfffff)||sp.area!=AreaGround)continue;
                        float wx=solid.bmin.Z+(ci/solid.width+.5f)*solid.cs,wy=solid.bmin.X+(ci%solid.width+.5f)*solid.cs;
                        if(!surfaceFit.IsWallFooting(wx,wy,solid.bmin.Y+sp.smax*solid.ch))continue;
                        localFootings.Add((ci,sp,key));
                    }
                }
                // Decide against the original source before applying exclusions,
                // so neighbouring footing decisions do not depend on iteration order.
                foreach(var (ci,sp,key) in localFootings){
                    surfaceFit.BlockCap(solid,ci,sp);sp.area=AreaNull;
                    nonWalkableWmo.Add(key);rejectedWmoCaps.Add(key);r.RaisedWmoCapsBlocked++;
                }
                SolidObserver?.Invoke("source exclusions",solid);
                FilterKeepingSwimFloor(ctx, cfg, realClimb, s.SwimSurfaceMargin, solid, ceiling, r, nonWalkableWmo);
                // Flat WMO floor near an exterior drop is not a trim cap.
                // Whole source patches already classify raised flat shelves;
                // the voxel supplement may only reject source-labelled bevels.
                // Do not delete whole step/bevel strips before testing their
                // physical connectivity and body clearance.

                if (s.DebugSpanTally)
                {
                    var tally = new Dictionary<int, int>();
                    for (int i = 0; i < solid.spans.Length; i++)
                        for (var sp = solid.spans[i]; sp != null; sp = sp.next)
                            tally[sp.area] = tally.GetValueOrDefault(sp.area) + 1;
                    r.SpanTally = string.Join("  ", tally.OrderByDescending(k => k.Value)
                        .Select(k => $"{AreaName(k.Key)}({k.Key}) {k.Value:N0}"));
                }

                RcBuilderResult built;
                // A way to bisect: run Recast's own build and none of the work below, so a
                // defect can be pinned on this file or cleared of it without guessing.
                if (s.PlainBuild)
                {
                    try { built = builder.Build(ctx, tx, tz, geom, cfg, solid, true); }
                    catch (Exception e)
                    {
                        r.FailedTiles++;
                        if (r.FailMessage.Length == 0) r.FailMessage = $"{e.GetType().Name}: {e.Message}";
                        continue;
                    }
                    goto haveMesh;
                }

                // Watershed first, then monotone, then Recast's own build untouched.
                //
                // Both additions can leave a region that BuildContours refuses - "Multiple
                // outlines for region" - because a watershed region that has been thickened
                // back up or joined across a wall need not stay simply connected. Monotone
                // builds its regions as runs along rows and cannot produce that, at the cost
                // of longer, thinner polygons, so it is worth trying before giving the
                // submerged ground up altogether.
                built = null!;
                // Watershed with the region split, then watershed WITHOUT it, and only
                // then monotone.
                //
                // Order matters and the middle rung was missing. Chopping regions can
                // leave one that BuildContours will not trace, and dropping straight to
                // monotone for the whole tile costs more than the split gains: on tile
                // [26,31] it lost about 2,500 square yards of ground, which reads as a
                // rectangle of missing mesh because the chop is on a grid. Falling back to
                // exactly the build that worked before the split is strictly better than
                // changing the partitioner underneath the whole ADT.
                // Densifying the contours is what removes the fans, so a tile that cannot
                // take it at full density is offered a coarser one before anything else is
                // given up. Nine ADTs used to fall straight through to the plain build and
                // come out with no densification at all - which is why the fans survived in
                // patches, and the patches were the busiest tiles.
                // When a chopped piece is one BuildContours will not trace it throws
                // "Bad outline for region N" and takes the whole tile's build with it,
                // and the next rung used to be no split at all. That is a bad trade: the
                // offending piece is one region out of sixteen thousand, and giving up
                // the chop restores every long polygon in the ADT. Tile [24,28] is the
                // example - it fell back unsplit and came out holding 61 yd slivers over
                // a lake bed. Nudging the grid moves every piece boundary, which is
                // usually enough for the piece that would not trace to not exist, so the
                // tile keeps a chopped build. Only if none of the grids work does it go
                // out unsplit.
                //
                // The rungs are deduplicated because with densification off the first
                // three used to be the same build three times over.
                var rungs = (( int Part, float Split, float Dense )[])[
                    (cfg.Partition, s.RegionSplit, s.ContourEdgeMax),
                    (cfg.Partition, s.RegionSplit, s.ContourEdgeMax * 2),
                    (cfg.Partition, s.RegionSplit, s.ContourEdgeMax * 4),
                    (cfg.Partition, s.RegionSplit, 0f),
                    (cfg.Partition, s.RegionSplit * 1.4f, 0f),
                    (cfg.Partition, s.RegionSplit * 0.7f, 0f),
                    (cfg.Partition, s.RegionSplit * 2.2f, 0f),
                    // Dense multi-storey tiles can still sit just above the 16-bit
                    // vertex ceiling at 2.2x. Retain bounded regions before trying
                    // an entirely unsplit mesh; voxel/detail resolution stays fixed.
                    (cfg.Partition, s.RegionSplit * 3f, 0f),
                    (cfg.Partition, 0f, 0f),
                    ((int)RcPartition.MONOTONE, s.RegionSplit, 0f)];

                foreach (var (part, split, dense) in rungs.Distinct())
                {
                    try
                    {
                        built = BuildTileMesh(ctx, tx, tz, cfg, solid, ceiling, s.SwimShoreBand,
                                              split, dense, s, r, part, roads, ground,bodyClearance,rejectedWmoCaps,DoodadRoofSpans,surfaceFit);

                        // Producing nothing counts as failing. Chopping regions can shred
                        // a tile that only ever held a scrap of navmesh - [25,26] holds
                        // two polygons and the split left none - and without this the
                        // chain never retried, because nothing had thrown. A tile that is
                        // genuinely empty costs one wasted rebuild and still ends empty.
                        if (built.Mesh == null || built.Mesh.npolys == 0) { built = null!; continue; }

                        if (dense != s.ContourEdgeMax) r.CoarserContours++;
                        if (split == 0f && s.RegionSplit > 0) r.SplitGaveUp++;
                        else if (split != s.RegionSplit) { r.SplitNudged++; r.NudgedTo = split; }
                        if (part != cfg.Partition) r.MonotoneTiles++;
                        break;
                    }
                    catch (Exception ex)
                    {
                        built = null!;
                        if (Environment.GetEnvironmentVariable("EXPLORER_MESH_PROGRESS") == "1")
                            Console.Error.WriteLine($"Custom contour attempt (partition={part}, split={split}, dense={dense}) failed: {ex.Message}");
                        if (r.FallbackWhy.Length == 0)
                            r.FallbackWhy = $"{ex.GetType().Name}: {ex.Message} @ " +
                                            (ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "?");
                    }
                }
                if (built == null)
                {
                    r.SwimBuildFellBack++;
                    try { built = builder.Build(ctx, tx, tz, geom, cfg, solid, true); }
                    catch (Exception e)
                    {
                        r.FailedTiles++;
                        if (r.FailMessage.Length == 0) r.FailMessage = $"{e.GetType().Name}: {e.Message}";
                        continue;
                    }
                }

                haveMesh:

                var pmesh = built.Mesh;
                var dmesh = built.MeshDetail;
                if (pmesh == null || pmesh.npolys == 0) continue;

                // Tidy the detail mesh HERE, not inside BuildTileMesh, so that every path
                // gets it - including the plain RcBuilder.Build fallback. Tucked inside,
                // a tile that fell back came out with Recast's raw fans while its
                // neighbours were clean, which is a seam nobody would think to look for.
                if (built.CompactHeightfield is { } dchf && dmesh != null)
                {
                    if (s.RetriangulateDetail)
                    {
                        r.DetailRetriangulated +=
                            RetriangulateDetail(ctx, dmesh, dchf, s.DetailSeedSpacing, out int seeded);
                        r.DetailSeeded += seeded;
                    }
                    r.DetailSpikes += FlattenDetailSpikes(dmesh, dchf, s.DetailSpikeSnap);
                    r.DetailBlades += CullDetailBlades(dmesh, s.DetailBladeArea, tmin, tmax);
                    // Last, so nothing after it can put a vertex back outside the band.
                    r.DetailOutOfBand += ClampDetailToPolygon(pmesh, dmesh, dchf, solid,
                                                              s.DetailBandMargin, s.IndoorFloorDrop,surfaceFit);
                }

                if (dmesh != null) r.LongDetailEdges += CountLongDetailEdges(dmesh, 40f);

                var t0 = System.Diagnostics.Stopwatch.StartNew();
                var data = ToDetour(pmesh, dmesh, s, tx, tz, tmin, tmax, ceiling, ground, r);
                detourMs += t0.Elapsed.TotalMilliseconds;
                if (data == null) { r.FailedTiles++; continue; }
                r.SourceSurfaceVertices += surfaceFit.Apply(data);
                r.PlanarDetailVerticesRemoved += PlanarDetailReducer.Reduce(data);
                surfaceFit.ObserveCoverage(data);

                // filename indices the nav DLL derives from a world position
                float wyc = WorldOrigin + (tx + 0.5f) * tws;
                float wxc = WorldOrigin + (tz + 0.5f) * tws;
                int row = RowOf(wxc), col = ColOf(wyc);

                int bytes = EstimateBytes(data);
                r.Tiles.Add(new NavTile(row, col, tx, tz, data, data.header.polyCount, bytes, data.header.vertCount));
                r.PolyCount += data.header.polyCount;
                r.MaxMeshVerts = Math.Max(r.MaxMeshVerts, data.header.vertCount);
                // The tally is kept by ToDetour, off the areas it WRITES. Reading
                // pmesh.areas here counted the rasteriser's provisional marks instead, so
                // the report disagreed with the file on disk.
                r.NavDataBytes += bytes;
                AppendDebug(data, r);
            }

        r.BuildMs = sw.Elapsed.TotalMilliseconds - detourMs;
        r.DetourMs = detourMs;
        r.TileCount = r.Tiles.Count;

        if (r.TileCount == 0)
        {
            r.Message = r.FailMessage.Length > 0
                ? $"{r.FailedTiles} tile(s) failed — {r.FailMessage}"
                : "no walkable polygons";
            return r;
        }
        r.Ok = true;
        r.Message = $"{r.TileCount} tiles, {r.PolyCount:N0} polys";
        if (r.SubmergedPolys > 0)
            r.Message += $", {r.SubmergedPolys:N0} submerged" +
                         (r.UnderHazardPolys > 0 ? $" ({r.UnderHazardPolys:N0} under hazard)" : "");
        if (r.SwimPolys > 0) r.Message += $", {r.SwimPolys:N0} swim";
        return r;
    }

    /// Everything RcBuilder.Build does, so that the submerged parts can be spared.
    ///
    /// Build is a single call from a heightfield to a polygon mesh, and two of the stages
    /// inside it delete submerged ground for reasons that only apply to walking:
    ///
    ///   - BuildCompactHeightfield joins two adjacent cells only when their floors are
    ///     within walkableClimb, which caps a connected surface at 76 degrees. A wall
    ///     dropping into a lake is steeper than that, so its cells end up islands.
    ///   - ErodeWalkableArea then removes them, because a cell with fewer than four
    ///     connections counts as a boundary and the whole strip is boundary.
    ///
    /// Neither is a hazard to something swimming. The stages below are Recast's own, in
    /// Recast's order, with two additions: swim-floor cells are connected using a larger
    /// climb, and the swim floor is restored after erosion.
    ///
    /// The connection lift is taken from a SECOND compact heightfield built over the same
    /// spans with the generous climb, rather than computed here - same input, same order,
    /// so the span indices line up and the answer is Recast's own. It is applied only when
    /// both ends are swim floor, so nothing gains a way to climb out of the water.
    ///
    /// If any of it throws - and BuildContours does throw "Multiple outlines for region"
    /// when regions stop being simply connected - the tile is rebuilt the plain way rather
    /// than lost. That failure mode is real: handing every cell the generous climb caused
    /// it, and Bake catches per tile, so an entire ADT silently produced nothing.
    /// Recast's own direction order for compact-heightfield neighbours, which the packed
    /// `con` field is indexed by. Inlined because DotRecast keeps its copy internal.
    static readonly int[] DirX = [-1, 0, 1, 0];
    static readonly int[] DirY = [0, 1, 0, -1];

    static RcBuilderResult BuildTileMesh(RcContext ctx, int tx, int tz, RcConfig cfg,
                                         RcHeightfield solid, LiquidCeiling? ceiling, float shoreBand,
                                         float regionSplit, float contourMax, Settings s, Result r,
                                         int partition, RoadMask? roads = null, GroundLevel? ground = null,
                                         SourceBodyClearance? bodyClearance=null,IReadOnlySet<long>? rejectedCaps=null,IReadOnlySet<long>? blockedProps=null,SourceSurfaceFit? surfaceFit=null)
    {
        int swimClimb = (int)MathF.Ceiling(s.SwimConnectClimb / s.CellHeight);

        ProbeSolid("after rasterise + filters", solid);
        var chf = RcCompacts.BuildCompactHeightfield(ctx, cfg.WalkableHeight, cfg.WalkableClimb, solid);
        var protectedClearance=new bool[chf.spanCount];
        ProbeCompact("after BuildCompactHeightfield", chf);
        ProbeWidths(chf, solid);
        chf.borderSize = cfg.BorderSize;

        if (swimClimb > cfg.WalkableClimb && Environment.GetEnvironmentVariable("NO_LIFT") != "1")
        {
            var loose = RcCompacts.BuildCompactHeightfield(ctx, cfg.WalkableHeight, swimClimb, solid);
            int lifted = 0;
            for (int cz = 0; cz < chf.height; cz++)
                for (int cx = 0; cx < chf.width; cx++)
                {
                    ref var c = ref chf.cells[cx + cz * chf.width];
                    for (int i = c.index; i < c.index + c.count; i++)
                    {
                        if (chf.areas[i] != AreaSwimFloor) continue;
                        int con = chf.spans[i].con;
                        for (int d = 0; d < 4; d++)
                        {
                            if (((chf.spans[i].con >> (d * 6)) & 0x3f) != 0x3f) continue;   // already joined
                            int lc = (loose.spans[i].con >> (d * 6)) & 0x3f;
                            if (lc == 0x3f) continue;                                       // not joined either

                            int nx = cx + DirX[d], nz = cz + DirY[d];
                            if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) continue;
                            int ni = chf.cells[nx + nz * chf.width].index + lc;
                            if (ni < 0 || ni >= chf.spanCount || chf.areas[ni] != AreaSwimFloor) continue;

                            con = (con & ~(0x3f << (d * 6))) | (lc << (d * 6));
                            lifted++;
                        }
                        if (con == chf.spans[i].con) continue;
                        var nb = RcCompactSpanBuilder.NewBuilder(ref chf.spans[i]);
                        nb.con = con;
                        chf.spans[i] = nb.Build();
                    }
                }
            r.SwimConnectionsLifted += lifted;
        }

        // Erosion is left alone.
        //
        // Sparing the swim floor from it was the obvious companion to the connection lift
        // and it is a bad trade, measured over the map: polygons with a vertex above the
        // water went from 70 to 691, the tiles needing monotone went from 3 to 7, and the
        // wall this was all for ended up 4.9 yd short of the waterline instead of 3.5. The
        // restored cells are the ragged edge of the region by definition, so their detail
        // vertices are the ones most likely to wander above the surface, and thickening a
        // region back up after Recast has thinned it is what stops it being simply
        // connected. Agent radius is a real clearance even swimming.
        // Erosion is kept, except in the band just under the water.
        //
        // The cell above the top of a submerged wall is dry cliff, which is null area, so
        // erosion eats the agent radius off the top of the swim floor - two cells, and at
        // 79 degrees two cells is 2.8 yd of height. That is why the mesh still stopped a
        // few yards under the surface after the connection lift fixed the wall itself.
        //
        // Sparing the WHOLE swim floor from erosion was tried and is a bad trade: polygons
        // with a vertex above the water went from 70 to 691 and the tiles needing monotone
        // from 3 to 7, because restoring every eroded cell restores the ragged edge of
        // every region. Restoring only what lies within SwimShoreBand of the surface keeps
        // the part that shows - the shoreline - and leaves the rest of the bed alone.
        var before = (int[])chf.areas.Clone();
        if (s.IndoorErodeCells >= 0 && s.IndoorErodeCells < cfg.WalkableRadius)
        {
            // Two ways of handling indoors, and the choice is a real trade, not a bug to
            // be fixed. The default keeps every floor reachable and leaves navmesh on the
            // railings; BRIDGE erodes indoors like everywhere else, which clears the
            // railings, and puts back a path across whatever that severed. Measured on
            // the test building: railings at (2129.8, 560.6) go from 41 cells to 14 and
            // at (2107.5, 560.3) to 6, while the spiral drops from five levels to four
            // and the mainland loses about 3,600 polygons.
            var preErode = (int[])chf.areas.Clone();
            // Full erosion followed by a shortest bridge leaves threadlike
            // doorways/stairs. Preserve the configured indoor clearance directly.
            bool bridge = Environment.GetEnvironmentVariable("INDOOR_BRIDGE") == "1";
            r.ErodedCells += ErodeIndoorAware(chf, solid, cfg.WalkableRadius,
                                              bridge ? cfg.WalkableRadius : s.IndoorErodeCells,
                                              out int indoors, out var indoorCells, s.ErodeStepBreak);
            r.IndoorCells += indoors;
            ProbeCompact("after radius erosion",chf);
            var afterRadius=(int[])chf.areas.Clone();
            int solidWallCells=ErodeSolidIndoorWalls(chf,solid,indoorCells,preErode,s,bodyClearance,protectedClearance,rejectedCaps,blockedProps);
            r.TrimCells+=solidWallCells;r.SolidWallCells+=solidWallCells;
            ProbeCompact("after solid wall clearance",chf);
            if (bridge) r.TrimCells += RestoreErodedBridges(chf, preErode, indoorCells);
            else
            {
                r.TrimCells += DropNarrowIndoorStrips(chf, indoorCells, s.MinIndoorStripWidth);
            ProbeCompact("after narrow strips",chf);
                r.TrimCells += OpenIndoorSurface(chf, indoorCells, s.IndoorOpenCells, s.LedgeMinDrop);
            }

            // Whatever is left that is a railing by the patch test, whichever route got
            // here. See DropRailingPatches.
            // Cut first, then sweep up. Severing leaves a railing cap as an island of
            // its own - which is the point, it is no longer part of the floor - but an
            // island left lying there is still navmesh, and still counts against
            // reachability. Dropping separate narrow pieces afterwards is what actually
            // removes it.
            ProbeCompact("after surface opening",chf);
            r.SeveredSteps += SeverIndoorSteps(chf, indoorCells, s.IndoorStepCut>0?s.IndoorStepCut:MathF.Min(s.AgentMaxClimb,2*s.CellHeight+.0001f));
            ProbeCompact("after sever steps",chf);
            r.OneWayLinks += SymmetriseLinks(chf);
            r.ErodedPinholes += FillErodedPinholes(chf, preErode, s.ErodedPinholeFill);
            r.RailingCells += DropNarrowIndoorStrips(chf, indoorCells, s.MinIndoorStripWidth);
            ProbeCompact("after second narrow strips",chf);
            r.RailingCells += DropRailingPatches(chf, indoorCells, s.MinIndoorStripWidth,
                                                 s.LedgeMinDrop, 0.5f,
                                                 MathF.Min(s.RailingReach,s.AgentMaxClimb+s.CellHeight));
            ProbeCompact("after railing patches",chf);
            r.IslandCells += DropIndoorIslands(chf, indoorCells, s.IndoorIslandArea);
            ProbeCompact("after tiny islands",chf);
            r.RaisedWmoCapsBlocked += IndoorSupportGraph.RemoveRaisedIslands(chf,indoorCells,s,surfaceFit);
            ProbeCompact("after raised islands",chf);
            // Later hole filling must not undo deliberate wall/trim rejection.
            for(int i=0;i<chf.spanCount;i++)
                if(afterRadius[i]!=AreaNull&&chf.areas[i]==AreaNull)protectedClearance[i]=true;
            ProbeWidths(chf, solid);
            ProbeCells("cells after erosion", chf, preErode);
        }
        else RcAreas.ErodeWalkableArea(ctx, cfg.WalkableRadius, chf);
        ProbeCompact("after ErodeWalkableArea", chf);


        int restored = 0;
        if (ceiling is { Any: true } && shoreBand > 0)
            for (int cz = 0; cz < chf.height; cz++)
                for (int cx = 0; cx < chf.width; cx++)
                {
                    float wy = chf.bmin.X + (cx + 0.5f) * chf.cs;
                    float wx = chf.bmin.Z + (cz + 0.5f) * chf.cs;
                    if (ceiling.SurfaceZ(wx, wy) is not { } top) continue;
                    ref var c = ref chf.cells[cx + cz * chf.width];
                    for (int i = c.index; i < c.index + c.count; i++)
                    {
                        if (before[i] != AreaSwimFloor || chf.areas[i] == AreaSwimFloor) continue;
                        if (top - (chf.bmin.Y + chf.spans[i].y * chf.ch) > shoreBand) continue;
                        chf.areas[i] = AreaSwimFloor;
                        restored++;
                    }
                }
        r.SwimShoreRestored += restored;

        // Before the regions are built, not after.
        //
        // This fills a hole by writing chf.areas and nothing else. Run after BuildRegions,
        // as it was, the cells it fills have no region - and BuildContours skips any span
        // whose reg is 0, so they produce no contour, no polygon and no detail triangle.
        // The pass whose job is to close a hole was opening one exactly its own size, which
        // is what the pier at (2033.1, 910.8) and the ramp at (2011.8, 579.0) were: the
        // trace shows the cell erode away, come back at "after unwinding self-overlap",
        // and come back marked (noreg).
        //
        // Run first and region building simply covers the filled cells, which also gets
        // their connectivity right rather than grafting an id on afterwards.
        r.Pinholes += FillPinholes(chf, s.PinholeFill, out int biggest,protectedClearance);
        r.BiggestPinhole = Math.Max(r.BiggestPinhole, biggest);

        // After the pinhole fill, so an erase stroke is not filled straight back in as a
        // "pinhole", and a filled cell gets its road label like any other.
        if (roads != null && ground != null)
        {
            r.RoadCells += MarkRoads(chf, roads, ground, out int erased);
            r.ErasedCells += erased;
        }

        if (partition == (int)RcPartition.MONOTONE)
            RcRegions.BuildRegionsMonotone(ctx, chf, cfg.MinRegionArea, cfg.MergeRegionArea);
        else if (partition == (int)RcPartition.LAYERS)
            RcRegions.BuildLayerRegions(ctx, chf, cfg.MinRegionArea);
        else
        {
            RcRegions.BuildDistanceField(ctx, chf);
            RcRegions.BuildRegions(ctx, chf, cfg.MinRegionArea, cfg.MergeRegionArea);
        }

        r.AreaIslandCellsRestored += RestoreConnectedFloorAreaIslands(chf);
        ProbeCompact("after BuildRegions", chf, true);
        for (int i = 0; i < chf.spanCount; i++) if (chf.areas[i] != AreaNull) r.WalkableCells++;


        OutOfRegionIds = false;
        SplitCoarsened = 0f;
        r.RegionsSplit += SplitOversizeRegions(chf, regionSplit);
        if (OutOfRegionIds) r.SplitOutOfIds++;
        if (SplitCoarsened > 0f)
        {
            r.SplitCoarsened++;
            r.WidestSplitGrid = Math.Max(r.WidestSplitGrid, SplitCoarsened);
        }

        r.RegionsUnwound += SplitDisconnectedRegions(chf);
        r.RegionsUnwound += SplitRegionsOverThemselves(chf);
        ProbeCompact("after unwinding self-overlap", chf, true);
        r.TinyRegionCells += DropTinyRegions(chf, s.MinRegionCells);
        if (Environment.GetEnvironmentVariable("CHECK_REGIONS") == "1") CheckRegions(chf, "before contours");
        var cset = NavContours.BuildContours(ctx, chf, cfg.MaxSimplificationError, cfg.MaxEdgeLen,
                                            RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES,
                                            surfaceFit==null?null:(x,z,id)=>surfaceFit.HasShallowWmoSupport(chf.bmin.Z+(z+.5f)*chf.cs,chf.bmin.X+(x+.5f)*chf.cs,chf.bmin.Y+chf.spans[id].y*chf.ch,chf.ch*1.15f));
        r.ContourVertsAdded += DensifyContours(cset, contourMax, cfg.Cs, chf);
        ContoursObserver?.Invoke(cset);

        var pmesh = RcMeshs.BuildPolyMesh(ctx, cset, cfg.MaxVertsPerPoly);
        var dmesh = cfg.BuildMeshDetail
            ? RcMeshDetails.BuildPolyMeshDetail(ctx, pmesh, chf, cfg.DetailSampleDist, cfg.DetailSampleMaxError)
            : null;
        return new RcBuilderResult(tx, tz, solid, chf, cset, pmesh, dmesh, ctx);
    }

    /// Re-triangulate each polygon's detail mesh with Delaunay instead of Recast's strip.
    ///
    /// This is the fans, and the needles that radiate from them, at the root.
    ///
    /// BuildPolyDetail only runs a Delaunay when it has added interior sample points. When
    /// it has not - and that is most polygons - it falls back to triangulateHull, which
    /// walks the hull taking whichever of the two ends gives the shorter perimeter. On a
    /// hull with vertices spread evenly that is fine. On one with many vertices down a
    /// bumpy interior edge and almost none along a straight one, it takes the same end
    /// every time and degenerates into a FAN about a single vertex, every triangle of it a
    /// needle. Tile borders are the worst case, because the border edge is dead straight
    /// and carries no samples at all: measured at the boundary x = 3200, 100% of the detail
    /// triangles within eight yards were needles.
    ///
    /// The vertices are not wrong - the surface they describe is the right shape - so
    /// nothing needs moving or deleting. Only the triangulation is bad, and Recast's own
    /// DelaunayHull is right there, maximising the minimum angle, which is precisely the
    /// property a fan lacks. The hull is recovered from the triangulation that is already
    /// there: an edge used by one triangle is on the boundary, and those edges chain into
    /// the loop.
    ///
    /// Anything that does not come back cleanly - an unclosed boundary, a Delaunay that
    /// returns fewer triangles than it was given points to cover - keeps what Recast built.
    /// Detail triangles with an edge longer than the limit, measured on the Recast mesh
    /// before it is packed into a Detour tile. A polygon is at most the split grid across,
    /// so a detail triangle far longer than that is a vertex in the wrong place, and this
    /// says whether the bake made it or the packing did.
    static int CountLongDetailEdges(RcPolyMeshDetail dmesh, float limit)
    {
        if (dmesh.meshes == null || dmesh.tris == null || dmesh.verts == null) return 0;
        int bad = 0;
        for (int m = 0; m < dmesh.nmeshes; m++)
        {
            int vb = dmesh.meshes[m * 4], tb = dmesh.meshes[m * 4 + 2], tc = dmesh.meshes[m * 4 + 3];
            for (int k = 0; k < tc; k++)
            {
                int at = (tb + k) * 4;
                for (int j = 0; j < 3; j++)
                {
                    int q0 = vb + dmesh.tris[at + j], q1 = vb + dmesh.tris[at + (j + 1) % 3];
                    float ex = dmesh.verts[q0 * 3] - dmesh.verts[q1 * 3];
                    float ey = dmesh.verts[q0 * 3 + 1] - dmesh.verts[q1 * 3 + 1];
                    float ez = dmesh.verts[q0 * 3 + 2] - dmesh.verts[q1 * 3 + 2];
                    if (ex * ex + ey * ey + ez * ez > limit * limit) { bad++; j = 3; }
                }
            }
        }
        return bad;
    }

    static int RetriangulateDetail(RcContext ctx, RcPolyMeshDetail dmesh,
                                   RcCompactHeightfield? chf, float seedSpacing, out int added)
    {
        added = 0;
        if (dmesh.meshes == null || dmesh.tris == null || dmesh.verts == null) return 0;

        var kept = new List<int>(dmesh.tris.Length);
        var newVerts = new List<float>(dmesh.verts.Length);
        var pts = new List<float>();
        var hull = new List<int>();
        var outTris = new List<int>();
        var use = new Dictionary<(int, int), int>();
        var next = new Dictionary<int, int>();
        int redone = 0, rejected = 0;

        for (int m = 0; m < dmesh.nmeshes; m++)
        {
            int vertBase = dmesh.meshes[m * 4], vertCount = dmesh.meshes[m * 4 + 1];
            int origVertCount = vertCount;
            int triBase = dmesh.meshes[m * 4 + 2], triCount = dmesh.meshes[m * 4 + 3];
            int newBase = kept.Count / 4;
            int newCount = 0;

            bool ok = vertCount >= 3 && triCount >= 1;
            if (ok)
            {
                // Boundary edges: used once. Directed, so the loop keeps its winding.
                use.Clear();
                next.Clear();
                for (int k = 0; k < triCount && ok; k++)
                {
                    int at = (triBase + k) * 4;
                    int a = dmesh.tris[at], b = dmesh.tris[at + 1], c = dmesh.tris[at + 2];
                    foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                    {
                        var key = u < v ? (u, v) : (v, u);
                        use[key] = use.GetValueOrDefault(key) + 1;
                    }
                }
                for (int k = 0; k < triCount && ok; k++)
                {
                    int at = (triBase + k) * 4;
                    int a = dmesh.tris[at], b = dmesh.tris[at + 1], c = dmesh.tris[at + 2];
                    foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                    {
                        var key = u < v ? (u, v) : (v, u);
                        if (use[key] != 1) continue;
                        if (next.ContainsKey(u)) { ok = false; break; }   // not a simple loop
                        next[u] = v;
                    }
                }

                if (ok && next.Count >= 3)
                {
                    hull.Clear();
                    int start = next.Keys.Min(), cur = start;
                    for (int guard = 0; guard <= next.Count; guard++)
                    {
                        hull.Add(cur);
                        if (!next.TryGetValue(cur, out cur)) { ok = false; break; }
                        if (cur == start) break;
                    }
                    if (hull.Count != next.Count) ok = false;
                }
                else ok = false;
            }

            if (ok)
            {
                pts.Clear();
                for (int v = 0; v < vertCount; v++)
                {
                    pts.Add(dmesh.verts[(vertBase + v) * 3]);
                    pts.Add(dmesh.verts[(vertBase + v) * 3 + 1]);
                    pts.Add(dmesh.verts[(vertBase + v) * 3 + 2]);
                }

                // SEED THE BARE EDGES.
                //
                // Delaunay alone does not fix a tile seam, because the problem there is
                // not which triangles are chosen but that there is nothing to choose
                // between: a contour clipped to the tile boundary is collinear, simplifies
                // to two endpoints, and leaves one side of the hull with no vertices at
                // all. Any triangulation of that spans the polygon in needles.
                //
                // So put vertices on it. Points are laid along any hull edge longer than
                // the spacing, at the height the heightfield gives for that column, which
                // is the same source BuildPolyDetail samples from - so this adds detail
                // where Recast declined to, rather than inventing a surface.
                //
                // Both sides of a shared edge must seed it IDENTICALLY, or the surfaces
                // come apart.
                //
                // Where two polygons meet, each one seeds that edge from its own side. If
                // they disagree by a single point the result is a T-junction: one side has
                // a vertex the other does not, the triangles no longer share a complete
                // edge, and you get a hairline crack you can see through plus shading that
                // stops dead at the line, because there is no shared vertex to average a
                // normal across. Measured on tile [28,29], seeding took T-junctions from
                // 17 to 563 - the hard seam with faceted ground one side and smooth the
                // other, and the gaps.
                //
                // So the point set is a pure function of the edge's two ENDPOINTS: the
                // count comes from the length alone, never from how much budget this
                // polygon happens to have left, and the parameter runs from whichever end
                // sorts first so both sides walk it the same way. Height comes from the
                // heightfield at the position, which is the same function either side.
                //
                // The budget is checked up front for the whole submesh rather than spent
                // edge by edge, so a polygon either seeds every edge or none - a partial
                // seed is the one outcome guaranteed to disagree with a neighbour.
                var seeded = new List<int>(hull.Count * 2);
                int wanted = 0;
                for (int h = 0; h < hull.Count; h++)
                {
                    int ia = hull[h], ib = hull[(h + 1) % hull.Count];
                    wanted += SeedCount(pts[ia * 3], pts[ia * 3 + 2], pts[ib * 3], pts[ib * 3 + 2], seedSpacing);
                }
                bool seedThis = chf != null && wanted > 0 && vertCount + wanted <= MaxDetailVerts;

                for (int h = 0; h < hull.Count; h++)
                {
                    int ia = hull[h], ib = hull[(h + 1) % hull.Count];
                    seeded.Add(ia);
                    if (!seedThis) continue;

                    float axf = pts[ia * 3], ayf = pts[ia * 3 + 1], azf = pts[ia * 3 + 2];
                    float bxf = pts[ib * 3], byf = pts[ib * 3 + 1], bzf = pts[ib * 3 + 2];
                    int add = SeedCount(axf, azf, bxf, bzf, seedSpacing);

                    // Walk from the end that sorts first, so the neighbour - which sees
                    // this edge reversed - lands on the same points in the same places.
                    bool flip = (bxf, bzf).CompareTo((axf, azf)) < 0;
                    for (int q = 1; q <= add; q++)
                    {
                        float t = q / (float)(add + 1);
                        if (flip) t = 1f - t;
                        float sx = axf + (bxf - axf) * t, sz = azf + (bzf - azf) * t;
                        float sy = SpanHeightAt(chf!, sx, sz, ayf + (byf - ayf) * t);
                        if (float.IsNaN(sy)) sy = ayf + (byf - ayf) * t;
                        seeded.Add(pts.Count / 3);
                        pts.Add(sx); pts.Add(sy); pts.Add(sz);
                        added++;
                    }
                }
                if (seeded.Count > hull.Count) { hull.Clear(); hull.AddRange(seeded); }
                vertCount = pts.Count / 3;

                outTris.Clear();
                try { RcMeshDetails.DelaunayHull(ctx, vertCount, [.. pts], hull.Count, [.. hull], outTris); }
                catch { ok = false; }
                // Delaunay can discard dangling faces and leaves temporary -1 flags.
                // Accept only a complete disk with exactly this boundary; rebuild
                // Detour's boundary bits rather than exporting those temporary values.
                if (ok) ok = ValidateDetailCoverage(pts, hull, outTris);

                // Nothing may come out bigger than what went in.
                //
                // DelaunayHull does not clip to a concave boundary - it assumes the hull it
                // is handed is convex, which Recast's own polygons are. A hull recovered
                // from a triangulation is not guaranteed to be, and when it is not the
                // Delaunay spans the concavity and emits a sheet: this tripled the
                // triangles with an edge over 40 yd, 141 to 407, some of them 200 yd
                // across. The polygon's own extent is the ceiling, and anything past it
                // means the hull was wrong, so Recast's triangulation is kept instead.
                if (ok)
                {
                    float spanX = 0, spanZ = 0;
                    for (int v = 0; v < vertCount; v++)
                        for (int w = v + 1; w < vertCount; w++)
                        {
                            spanX = MathF.Max(spanX, MathF.Abs(pts[v * 3] - pts[w * 3]));
                            spanZ = MathF.Max(spanZ, MathF.Abs(pts[v * 3 + 2] - pts[w * 3 + 2]));
                        }
                    // Never longer than what Recast already had here. The polygon's own
                    // diagonal is too generous a ceiling: on a long polygon a Delaunay
                    // will happily span it end to end where the strip walk made a dozen
                    // short triangles, and that doubled the map's triangles with an edge
                    // over 40 yd. Holding it to the incoming maximum makes this change
                    // incapable of making the longest edge worse.
                    float had = 0;
                    for (int k = 0; k < triCount; k++)
                    {
                        int at = (triBase + k) * 4;
                        for (int j = 0; j < 3; j++)
                        {
                            int q0 = dmesh.tris[at + j], q1 = dmesh.tris[at + (j + 1) % 3];
                            float ex0 = dmesh.verts[(vertBase + q0) * 3] - dmesh.verts[(vertBase + q1) * 3];
                            float ez0 = dmesh.verts[(vertBase + q0) * 3 + 2] - dmesh.verts[(vertBase + q1) * 3 + 2];
                            had = MathF.Max(had, MathF.Sqrt(ex0 * ex0 + ez0 * ez0));
                        }
                    }
                    float cap = MathF.Min(MathF.Sqrt(spanX * spanX + spanZ * spanZ) * 1.01f,
                                          had * 1.05f) + 0.01f;
                    for (int k = 0; k < outTris.Count && ok; k += 4)
                        for (int j = 0; j < 3 && ok; j++)
                        {
                            int p0 = outTris[k + j], p1 = outTris[k + (j + 1) % 3];
                            float ex = pts[p0 * 3] - pts[p1 * 3], ez = pts[p0 * 3 + 2] - pts[p1 * 3 + 2];
                            if (MathF.Sqrt(ex * ex + ez * ez) > cap) ok = false;
                        }
                    if (!ok) rejected++;
                }

                // Both counts go into single bytes in the tile Detour reads, so a submesh
                // that overshoots would be written truncated and silently wrong.
                if (ok && outTris.Count >= 12 && outTris.Count % 4 == 0
                    && outTris.Count / 4 <= 255 && vertCount <= 255)
                {
                    int vb = newVerts.Count / 3;
                    newVerts.AddRange(pts);
                    for (int k = 0; k < outTris.Count; k += 4)
                    {
                        kept.Add(outTris[k]); kept.Add(outTris[k + 1]);
                        kept.Add(outTris[k + 2]); kept.Add(outTris[k + 3]);
                        newCount++;
                    }
                    redone++;
                    dmesh.meshes[m * 4] = vb;
                    dmesh.meshes[m * 4 + 1] = vertCount;
                    dmesh.meshes[m * 4 + 2] = newBase;
                    dmesh.meshes[m * 4 + 3] = newCount;
                    continue;
                }
            }

            // Keep Recast's own triangulation for this one, and its own vertices with it.
            int keepBase = newVerts.Count / 3;
            for (int v = 0; v < origVertCount; v++)
            {
                newVerts.Add(dmesh.verts[(vertBase + v) * 3]);
                newVerts.Add(dmesh.verts[(vertBase + v) * 3 + 1]);
                newVerts.Add(dmesh.verts[(vertBase + v) * 3 + 2]);
            }
            for (int k = 0; k < triCount; k++)
            {
                int at = (triBase + k) * 4;
                kept.Add(dmesh.tris[at]); kept.Add(dmesh.tris[at + 1]);
                kept.Add(dmesh.tris[at + 2]); kept.Add(dmesh.tris[at + 3]);
                newCount++;
            }
            dmesh.meshes[m * 4] = keepBase;
            dmesh.meshes[m * 4 + 1] = origVertCount;
            dmesh.meshes[m * 4 + 2] = newBase;
            dmesh.meshes[m * 4 + 3] = newCount;
        }

        _ = rejected;
        dmesh.verts = [.. newVerts];
        dmesh.nverts = newVerts.Count / 3;
        dmesh.tris = [.. kept];
        dmesh.ntris = kept.Count / 4;
        return redone;
    }

    internal static bool ValidateDetailCoverage(IReadOnlyList<float> verts, IReadOnlyList<int> hull, List<int> tris)
    {
        int count = verts.Count / 3;
        if (hull.Count < 3 || tris.Count < 4 || tris.Count % 4 != 0) return false;
        static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);
        var boundary = new HashSet<(int, int)>();
        var edges = new Dictionary<(int, int), (int Count, int Balance)>();
        double hullArea = 0, triangleArea = 0;
        int origin = hull[0];
        double Cross(int a, int b, int c) =>
            ((double)verts[b*3]-verts[a*3]) * ((double)verts[c*3+2]-verts[a*3+2]) -
            ((double)verts[c*3]-verts[a*3]) * ((double)verts[b*3+2]-verts[a*3+2]);
        for (int i=0; i<hull.Count; i++)
        {
            int a=hull[i], b=hull[(i+1)%hull.Count];
            if ((uint)a>=count || (uint)b>=count || a==b || !boundary.Add(Edge(a,b))) return false;
            hullArea += Cross(origin,a,b);
        }
        if (hullArea == 0 || !double.IsFinite(hullArea)) return false;
        int sign = Math.Sign(hullArea);
        for (int t=0; t<tris.Count; t+=4)
        {
            int a=tris[t], b=tris[t+1], c=tris[t+2];
            if ((uint)a>=count || (uint)b>=count || (uint)c>=count || a==b || b==c || c==a) return false;
            double area=Cross(a,b,c);
            if (!double.IsFinite(area) || area*sign<=0) return false;
            triangleArea += area;
            for (int j=0; j<3; j++)
            {
                int u=tris[t+j], v=tris[t+(j+1)%3]; var key=Edge(u,v);
                var old=edges.GetValueOrDefault(key);
                edges[key]=(old.Count+1,old.Balance+(u<v?1:-1));
            }
        }
        if (Math.Abs(triangleArea-hullArea)>Math.Max(1e-7,Math.Abs(hullArea)*1e-5)) return false;
        foreach (var (edge, use) in edges)
            if (boundary.Contains(edge) ? use.Count!=1 : use.Count!=2 || use.Balance!=0) return false;
        if (boundary.Any(edge=>!edges.ContainsKey(edge))) return false;
        for (int t=0; t<tris.Count; t+=4)
        {
            int flags=0;
            for (int j=0; j<3; j++)
                if (boundary.Contains(Edge(tris[t+j],tris[t+(j+1)%3]))) flags |= 1 << (2*j);
            tris[t+3]=flags;
        }
        return true;
    }

    /// How many points go on an edge: from its length alone, so both polygons sharing it
    /// agree without having to know about each other.
    static int SeedCount(float ax, float az, float bx, float bz, float spacing)
    {
        if (spacing <= 0) return 0;
        float run = MathF.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
        return Math.Clamp((int)(run / spacing) - 1, 0, 24);
    }

    /// Recast's own ceiling on a detail submesh, and Detour stores the count in a byte.
    /// 100 leaves room for the triangles the extra points generate.
    const int MaxDetailVerts = 100;

    /// The heightfield's own surface at a column, nearest a height we expect. Seeded hull
    /// points take their height from here rather than from a straight line between the
    /// corners, so a seeded edge follows the ground instead of cutting across it.
    static float SpanHeightAt(RcCompactHeightfield chf, float x, float z, float near)
    {
        int cx = (int)MathF.Floor((x - chf.bmin.X) / chf.cs);
        int cz = (int)MathF.Floor((z - chf.bmin.Z) / chf.cs);
        if (cx < 0 || cz < 0 || cx >= chf.width || cz >= chf.height) return float.NaN;
        ref var c = ref chf.cells[cx + cz * chf.width];
        float best = float.NaN, gap = float.MaxValue;
        for (int i = c.index; i < c.index + c.count; i++)
        {
            if (chf.areas[i] == AreaNull) continue;
            float y = chf.bmin.Y + chf.spans[i].y * chf.ch;
            if (MathF.Abs(y - near) >= gap) continue;
            gap = MathF.Abs(y - near);
            best = y;
        }
        return best;
    }

    /// Remove only triangles with exactly zero horizontal footprint.
    /// Small or steep positive-area triangles still cover ground; deleting them
    /// creates holes. Their heights are repaired by the source-surface fit.
    static int CullDetailBlades(RcPolyMeshDetail dmesh, float minArea, RcVec3f tmin, RcVec3f tmax)
    {
        if (minArea <= 0 || dmesh.meshes == null || dmesh.tris == null) return 0;

        var kept = new List<int>(dmesh.tris.Length);
        int dropped = 0;

        for (int m = 0; m < dmesh.nmeshes; m++)
        {
            int vertBase = dmesh.meshes[m * 4];
            int triBase = dmesh.meshes[m * 4 + 2];
            int triCount = dmesh.meshes[m * 4 + 3];
            int newBase = kept.Count / 4;
            int newCount = 0;

            for (int k = 0; k < triCount; k++)
            {
                int at = (triBase + k) * 4;
                int a = dmesh.tris[at], b = dmesh.tris[at + 1], c = dmesh.tris[at + 2];

                var va = new Vector3(dmesh.verts[(vertBase + a) * 3], dmesh.verts[(vertBase + a) * 3 + 1],
                                     dmesh.verts[(vertBase + a) * 3 + 2]);
                var vb = new Vector3(dmesh.verts[(vertBase + b) * 3], dmesh.verts[(vertBase + b) * 3 + 1],
                                     dmesh.verts[(vertBase + b) * 3 + 2]);
                var vc = new Vector3(dmesh.verts[(vertBase + c) * 3], dmesh.verts[(vertBase + c) * 3 + 1],
                                     dmesh.verts[(vertBase + c) * 3 + 2]);

                // No area seen from above. That is what a blade IS - a triangle standing
                // on edge covers no ground and carries no height a query could want - and
                // it is the only test that selects them without also selecting the steep
                // ground they stand next to.
                // Float products can cancel to zero for a thin but positive
                // footprint. Only a genuinely collapsed triangle may be culled.
                double area = Math.Abs(((double)vb.X - va.X) * ((double)vc.Z - va.Z) -
                                       ((double)vc.X - va.X) * ((double)vb.Z - va.Z)) * 0.5;
                if (area == 0) { dropped++; continue; }

                // Every positive footprint contributes real coverage. Removing a
                // small or steep triangle tears a hole; repair its heights instead.

                kept.Add(a); kept.Add(b); kept.Add(c); kept.Add(dmesh.tris[at + 3]);
                newCount++;
            }

            dmesh.meshes[m * 4 + 2] = newBase;
            dmesh.meshes[m * 4 + 3] = newCount;
        }

        dmesh.tris = [.. kept];
        dmesh.ntris = kept.Count / 4;
        return dropped;

    }

    /// Pull detail vertices that ended up nowhere near the ground back onto it.
    ///
    /// BuildPolyMeshDetail reads its heights from a patch flood-filled over the polygon's
    /// OWN region. A polygon can cover cells belonging to a neighbouring region - merging
    /// and contour simplification both arrange that, and chopping regions makes it more
    /// common - and those cells come back RC_UNSET_HEIGHT. Recast then spirals outward
    /// looking for any height at all and takes the first one it finds, which on broken
    /// ground can be metres away vertically.
    ///
    /// One vertex placed like that drags every triangle touching it, and since it sits in
    /// the middle of a fan the result is a star of long needles standing above and below
    /// the surface: the "crumpled paper" this shows up as. The vertex is wrong, not the
    /// triangulation, so the fix is to put the vertex back.
    ///
    /// The compact heightfield is the authority - a detail vertex should be at a span in
    /// its own column, because that is the thing the detail mesh exists to describe. Only
    /// vertices further out than DetailSpikeSnap are touched, which is far beyond the
    /// sampler's own error budget, so ordinary interpolation is left alone.
    /// Yards; the largest height correction a spike snap is allowed to make.
    const float SpikeSnapMax = 4f;

    static int FlattenDetailSpikes(RcPolyMeshDetail dmesh, RcCompactHeightfield chf, float snap)
    {
        if (snap <= 0 || dmesh.verts == null) return 0;

        int fixedUp = 0;
        for (int v = 0; v < dmesh.nverts; v++)
        {
            float vx = dmesh.verts[v * 3], vy = dmesh.verts[v * 3 + 1], vz = dmesh.verts[v * 3 + 2];
            int cx = (int)MathF.Floor((vx - chf.bmin.X) / chf.cs);
            int cz = (int)MathF.Floor((vz - chf.bmin.Z) / chf.cs);
            if (cx < 0 || cz < 0 || cx >= chf.width || cz >= chf.height) continue;

            ref var c = ref chf.cells[cx + cz * chf.width];
            if (c.count == 0) continue;

            // A vertex outside the heightfield's own vertical range is not a spike, it is
            // garbage, and it has to go wherever the ground is however far that is.
            //
            // BuildPolyMeshDetail produces these on its own: tile [28,31] came out of the
            // bake holding 17 detail triangles with edges of 278,809 yd, on vertices at
            // z 93,740 and 186,675 against a tile that spans a few hundred. Their
            // polygons are ordinary - 15 yd across - so it is the vertex that is wrong,
            // and Recast's height patch failing to resolve a column is the likely source.
            // One of them is a sheet across the whole map, so nothing may be left to
            // chance here.
            bool absurd = vy < chf.bmin.Y - 50f || vy > chf.bmax.Y + 50f;

            float best = float.NaN, bestGap = float.MaxValue;
            for (int i = c.index; i < c.index + c.count; i++)
            {
                if (chf.areas[i] == AreaNull && !absurd) continue;   // not a surface to stand on
                float sy = chf.bmin.Y + chf.spans[i].y * chf.ch;
                float gap = MathF.Abs(sy - vy);
                if (gap < bestGap) { bestGap = gap; best = sy; }
            }
            if (float.IsNaN(best))
            {
                // Nothing in the column at all. Better the floor of the tile than a
                // number that draws a triangle across the world.
                if (!absurd) continue;
                dmesh.verts[v * 3 + 1] = chf.bmin.Y;
                fixedUp++;
                continue;
            }
            if (bestGap <= snap) continue;

            // A spike is a vertex a little off the ground under it. A vertex a hundred
            // yards off is not a spike - it is a vertex over a column whose only spans
            // belong to some other surface entirely, a cave floor or a roof, and the
            // nearest one is simply the least wrong of several wrong answers. Snapping
            // to it does not flatten anything, it drags one corner of a triangle through
            // the world: on tile [26,30] this alone put 81 detail triangles with an edge
            // over 40 yd into a mesh that had none, the longest of them 136 yd, and
            // every one of those is a sheet you can see from underneath the map.
            //
            // So there is a ceiling on the correction, and past it the vertex is left
            // where Recast put it. Recast's height came from sampling the polygon's own
            // height patch, which is the better answer here by definition.
            if (bestGap > SpikeSnapMax && !absurd) continue;

            dmesh.verts[v * 3 + 1] = best;
            fixedUp++;
        }
        return fixedUp;
    }

    /// Put vertices along every contour edge, so the polygon mesh has something to work
    /// with. This is where the fans are born.
    ///
    /// BuildPolyMesh ear-clips each contour and then merges the triangles into convex
    /// polygons. Ear clipping is only as good as the vertices it is given, and a
    /// cell-quantised contour is lopsided: the side that runs along broken ground carries
    /// a vertex every cell or two, and the side that runs straight - a tile border, the
    /// flank of a strip, the far side of a gap between two doodads - simplifies down to
    /// its two endpoints. Clipping ears off that produces a FAN, every triangle a sliver
    /// reaching across the polygon to one corner, and merging cannot undo it because
    /// merging two slivers only makes a wider sliver. Measured before this ran: 7.0% of
    /// polygons thinner than 20:1, and 111 points on the map with eight or more of them
    /// meeting - the stars.
    ///
    /// Recast has the same idea in MaxEdgeLen, but only applies it to WALL edges, where a
    /// region meets null. The edges that matter here are region against region, which it
    /// leaves alone - lowering EdgeMaxLen to 3 yd moved the fan count from 115 to 103 for
    /// 34,000 extra polygons, which is why that lever looked useless.
    ///
    /// Subdividing a shared boundary is only safe if BOTH regions do it identically, or
    /// their polygons stop meeting edge to edge and Detour loses the adjacency it builds
    /// portals from. Contour vertices are integer cell coordinates, so the split points
    /// can be made exactly equal rather than nearly: the count comes from the endpoints
    /// alone, and the walk starts from whichever end sorts first, so a region tracing the
    /// boundary one way lands on the same integers as its neighbour tracing it the other.
    /// Vertices one contour may end up with. Recast indexes its polygon-building
    /// arrays with shorts and sizes them from this count; past a few hundred it
    /// throws from inside, and the tile silently falls back to an undensified build.
    const int MaxContourVerts = 400;

    /// How far the ground may stray from a contour edge's chord, in yards, before
    /// that edge is left alone. A vertex placed on a chord that does not follow the
    /// surface puts the mesh under the terrain.
    static readonly float ChordTolerance =
        float.TryParse(Environment.GetEnvironmentVariable("CHORD_TOL"), out var ct) ? ct : 0.15f;

    static int DensifyContours(RcContourSet cset, float maxEdgeYards, float cs,
                               RcCompactHeightfield? chf)
    {
        if (maxEdgeYards <= 0 || cset.conts == null) return 0;
        int step = Math.Max(1, (int)MathF.Round(maxEdgeYards / cs));
        int added = 0;

        // Budget the WHOLE TILE, before touching anything.
        //
        // rcBuildPolyMesh keeps its vertex indices in shorts and refuses past 65,535 -
        // "Too many vertices 79460" was one tile - and several others threw
        // IndexOutOfRange from inside the same overflow. Every one of those was caught by
        // the fallback and rebuilt the plain way, so eleven ADTs came out with none of
        // this applied and their fans intact. Patchy results, and the patches were exactly
        // the tiles that needed it most, because busy tiles are the ones that overflow.
        //
        // Widening the step for one contour at a time cannot see that coming: the limit is
        // the sum over the tile. So the sum is computed first and the step doubled until
        // it fits, which trades a little density on a crowded tile for that tile getting
        // densified at all.
        const int TileVertBudget = 55_000;
        while (step < 4096)
        {
            long total = 0;
            foreach (var c0 in cset.conts)
            {
                if (c0.nverts < 3) continue;
                total += c0.nverts;
                for (int i = 0; i < c0.nverts; i++)
                {
                    int j = (i + 1) % c0.nverts;
                    int ddx = c0.verts[j * 4] - c0.verts[i * 4];
                    int ddz = c0.verts[j * 4 + 2] - c0.verts[i * 4 + 2];
                    total += Math.Max(0, (int)(MathF.Sqrt(ddx * ddx + ddz * ddz) / step) - 1);
                }
            }
            if (total <= TileVertBudget) break;
            step *= 2;
        }

        foreach (var c in cset.conts)
        {
            if (c.nverts < 3) continue;

            // A contour has a ceiling. BuildPolyMesh sizes its working arrays from the
            // vertex count and indexes them with shorts, and pushing a big contour past
            // what it expects throws IndexOutOfRange from inside Recast - which the
            // fallback then catches, quietly rebuilding that whole ADT the plain way with
            // none of this applied. Eleven tiles were doing that, which is why fans
            // survived in patches: those tiles had never been densified at all.
            //
            // So the step widens for this contour until the result fits, rather than the
            // contour being handed over at a density that breaks it.
            int useStep = step;
            while (true)
            {
                int want = c.nverts;
                for (int i = 0; i < c.nverts; i++)
                {
                    int j = (i + 1) % c.nverts;
                    int ddx = c.verts[j * 4] - c.verts[i * 4];
                    int ddz = c.verts[j * 4 + 2] - c.verts[i * 4 + 2];
                    want += Math.Max(0, (int)(MathF.Sqrt(ddx * ddx + ddz * ddz) / useStep) - 1);
                }
                if (want <= MaxContourVerts || useStep > 4096) break;
                useStep *= 2;
            }

            var outV = new List<int>(c.nverts * 8);

            for (int i = 0; i < c.nverts; i++)
            {
                int j = (i + 1) % c.nverts;
                int ax = c.verts[i * 4], ay = c.verts[i * 4 + 1], az = c.verts[i * 4 + 2], aw = c.verts[i * 4 + 3];
                int bx = c.verts[j * 4], by = c.verts[j * 4 + 1], bz = c.verts[j * 4 + 2];

                outV.Add(ax); outV.Add(ay); outV.Add(az); outV.Add(aw);

                int dx = bx - ax, dz = bz - az;
                int pieces = (int)(MathF.Sqrt(dx * dx + dz * dz) / useStep);
                if (pieces < 2) continue;

                // Only split an edge whose CHORD already follows the ground.
                //
                // This is the whole reason densifying failed the first time. A contour
                // edge is a straight line; over curved ground it sags below the surface,
                // and every vertex put on it sits in that sag, which is ground showing
                // through the mesh. Splitting every edge took terrain-above-navmesh from
                // 0.1% of samples to 1.1%.
                //
                // But the edges that cause the fans are long STRAIGHT runs, and they are
                // straight because contour simplification found the ground under them
                // planar - exactly the case where the chord and the surface agree. So
                // measure before splitting: walk the edge, compare the heightfield to the
                // chord, and leave the edge alone if they part company. Nothing is added
                // where adding would sink the mesh.
                if (chf != null)
                {
                    float worst = 0;
                    for (int q = 1; q < pieces && worst <= ChordTolerance; q++)
                    {
                        float t2 = q / (float)pieces;
                        int sx2 = ax + (int)MathF.Round(dx * t2);
                        int sz2 = az + (int)MathF.Round(dz * t2);
                        int chord = ay + (int)MathF.Round((by - ay) * t2);

                        // Contours have had the border removed; compact columns
                        // still include it. Add it back before sampling.
                        int hx = sx2 + chf.borderSize, hz = sz2 + chf.borderSize;
                        if (hx < 0 || hz < 0 || hx >= chf.width || hz >= chf.height) continue;
                        ref var hc = ref chf.cells[hx + hz * chf.width];
                        int near = int.MinValue, gap = int.MaxValue;
                        for (int w = hc.index; w < hc.index + hc.count; w++)
                        {
                            if (chf.areas[w] == AreaNull) continue;
                            int d3 = Math.Abs(chf.spans[w].y - chord);
                            if (d3 >= gap) continue;
                            gap = d3; near = chf.spans[w].y;
                        }
                        if (near == int.MinValue) continue;
                        worst = MathF.Max(worst, MathF.Abs(near - chord) * chf.ch);
                    }
                    if (worst > ChordTolerance) continue;
                }

                // Canonical direction: the endpoint that sorts first is always the origin,
                // whichever way round this contour happens to be walking the edge.
                bool flip = bx < ax || (bx == ax && bz < az);
                for (int k = 1; k < pieces; k++)
                {
                    int kk = flip ? pieces - k : k;
                    float t = kk / (float)pieces;
                    // From the canonical origin, so both sides round identically.
                    int ox = flip ? bx : ax, oz = flip ? bz : az, oy = flip ? by : ay;
                    int ex = flip ? ax : bx, ez = flip ? az : bz, ey = flip ? ay : by;
                    int px = ox + (int)MathF.Round((ex - ox) * t);
                    int pz = oz + (int)MathF.Round((ez - oz) * t);
                    // The height has to be a pure function of POSITION, like the x and z.
                    //
                    // This is what was leaving gaps. Two regions meeting along a boundary
                    // each trace their own contour, and BuildPolyMesh matches vertices by
                    // (x, y, z) - all three. Recast's own corner heights agree across
                    // regions because they come from one rule applied to the cells round
                    // the corner. An interpolated height does not: each side starts from
                    // its own endpoint heights, so the two sides can land a few cells
                    // apart in y, the vertices fail to merge, the polygons stop sharing an
                    // edge, and the mesh comes apart along it. Snapping to "the span
                    // nearest my interpolated guess" is no better - the guess differs per
                    // side, so the answer does too, and it made terrain-above-navmesh go
                    // from 0.2% of samples to 2.4%.
                    //
                    // Highest span among the four cells touching the corner, which is
                    // Recast's own rule and depends on nothing but where the corner is.
                    int py = oy + (int)MathF.Round((ey - oy) * t);

                    // No repeats. Split points land on integer cells, so two of them - or
                    // one of them and an endpoint - can round onto the same cell. A
                    // contour with a repeated vertex is degenerate, Triangulate walks off
                    // the end of its array, and Recast throws IndexOutOfRange from inside;
                    // the tile then falls back and loses all of this. Six ADTs were doing
                    // exactly that.
                    int last = outV.Count - 4;
                    if (px == outV[last] && pz == outV[last + 2]) continue;
                    if (px == bx && pz == bz) continue;

                    outV.Add(px); outV.Add(py); outV.Add(pz); outV.Add(aw);
                    added++;
                }
            }

            c.verts = [.. outV];
            c.nverts = outV.Count / 4;
        }
        return added;
    }

    /// Fill pinholes in the walkable area — the footprints of small objects.
    ///
    /// A doodad or a small WMO punches a hole in the walkable surface, and erosion widens
    /// it by an agent radius. Recast cannot contour a region with a hole directly: it
    /// merges each hole into the outer contour with a BRIDGE, a zero-width slit running
    /// out to the hole and back. Two sides of that slit are the same line, so whatever
    /// triangulates the polygon fans around it - which is why 47% of the fans on this map
    /// sit within 4 yd of an object, median 1.7 yd. Seeding hull points cannot help,
    /// because there is no width to seed across.
    ///
    /// Removing the hole removes the bridge. That is only honest for holes an agent could
    /// not use anyway: after erosion a hole is already the obstacle plus a radius, so one
    /// still smaller than the agent is a hole nothing can stand in and nothing can path
    /// around - it exists only to make a slit.
    ///
    /// Holes touching the edge of the heightfield are left alone: they are not pinholes,
    /// they are the outside world arriving at the tile border.

    /// Put back cells erosion hollowed out of open floor.
    ///
    /// `FillPinholes` cannot reach these. It floods every null cell, and the clearance band
    /// erosion leaves around obstacles is one connected null region that runs to the tile
    /// border, so every hole inside it counts as "open" and is never filled. Raising its
    /// limit from 4 to 25 yd2 changed nothing, which is what put the fault in the flood
    /// rather than the threshold.
    ///
    /// Looking only at cells that erosion ITSELF removed separates the two cases. A hole in
    /// the middle of open floor is bounded entirely by cells that are still walkable, and is
    /// an artefact - the terrain at (2133.8, 598.9) and the 673 floor at (2032.1, 535.3)
    /// both are. A clearance band touches a cell that was never walkable, which is the
    /// obstacle it is keeping the agent clear of, and that one has to stay.
    static int FillErodedPinholes(RcCompactHeightfield chf, int[] preErode, float maxYards2)
    {
        if (maxYards2 <= 0) return 0;

        int limit = (int)(maxYards2 / (chf.cs * chf.cs));
        var seen = new bool[chf.spanCount];
        var piece = new List<int>();
        var stack = new Stack<int>();
        int filled = 0;

        for (int i0 = 0; i0 < chf.spanCount; i0++)
        {
            if (seen[i0] || chf.areas[i0] != AreaNull || preErode[i0] == AreaNull) continue;

            piece.Clear();
            stack.Clear();
            stack.Push(i0);
            seen[i0] = true;
            bool keep = true;

            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                piece.Add(cur);
                int sx = CellXOf(chf, cur), sz = CellZOf(chf, cur);
                if (sx == 0 || sz == 0 || sx == chf.width - 1 || sz == chf.height - 1) keep = false;

                for (int dir = 0; dir < 4; dir++)
                {
                    int con = (chf.spans[cur].con >> (dir * 6)) & 0x3f;
                    if (con == 0x3f) continue;
                    int nx = sx + DirX[dir], nz = sz + DirY[dir];
                    if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) { keep = false; continue; }
                    int ni = chf.cells[nx + nz * chf.width].index + con;
                    if (ni < 0 || ni >= chf.spanCount) continue;

                    if (chf.areas[ni] != AreaNull) continue;        // still walkable: a wall of the hole
                    if (preErode[ni] == AreaNull) { keep = false; continue; }   // a real obstacle
                    if (seen[ni]) continue;
                    seen[ni] = true;
                    stack.Push(ni);
                }
            }

            if (!keep || piece.Count > limit) continue;
            foreach (int i in piece) chf.areas[i] = preErode[i];
            filled += piece.Count;
        }

        return filled;
    }

    /// Apply the road paint and the hand edits to the heightfield.
    ///
    /// Painted road texture counts only on spans sitting ON the terrain - within a yard of
    /// its height - because the paint is on the terrain: a bridge over a painted road is not
    /// road just because the texture runs underneath it.
    ///
    /// Brush strokes are not limited to terrain. A stroke carries the height it was placed
    /// at and acts on whatever surface is there - which is what makes painting a bridge
    /// work, and what the first version got wrong by applying strokes only to terrain, so a
    /// bridge painted as road baked as plain ground. Erase removes the surface outright.
    ///
    /// Before regions are built, deliberately: Recast never puts two areas in one region, so
    /// the road's edge becomes a polygon edge and road polygons follow the road.
    /// ROADS=0 bakes without road areas, to measure what they cost.
    static readonly bool NoRoads = Environment.GetEnvironmentVariable("ROADS") == "0";

    static int MarkRoads(RcCompactHeightfield chf, RoadMask roads, GroundLevel ground, out int erased)
    {
        int marked = 0;
        erased = 0;
        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                if (c.count == 0) continue;
                float wy = chf.bmin.X + (cx + 0.5f) * chf.cs;
                float wx = chf.bmin.Z + (cz + 0.5f) * chf.cs;
                float? terrain = ground.TerrainZ(wx, wy);
                bool? painted = null;

                for (int i = c.index; i < c.index + c.count; i++)
                {
                    int area = chf.areas[i];
                    if (area == AreaNull) continue;
                    float z = chf.bmin.Y + chf.spans[i].y * chf.ch;
                    var stroke = roads.StrokeAt(wx, wy, z);

                    if (stroke == MapEdits.StrokeKind.EraseMesh)
                    {
                        chf.areas[i] = AreaNull;
                        erased++;
                        continue;
                    }
                    if(stroke==MapEdits.StrokeKind.Block){chf.areas[i]=AreaBlocked;continue;}
                    if (area is not (AreaGround or AreaRoad)) continue;     // swim floor stays

                    bool road = !NoRoads && (stroke switch
                    {
                        MapEdits.StrokeKind.PaintRoad => true,
                        MapEdits.StrokeKind.RemoveRoad => false,
                        _ => terrain is { } tz && MathF.Abs(z - tz) <= 1f &&
                             (painted ??= roads.Painted(wx, wy)),
                    });
                    if (road && area == AreaGround) { chf.areas[i] = AreaRoad; marked++; }
                    else if (!road && area == AreaRoad) chf.areas[i] = AreaGround;
                }
            }
        return marked;
    }

    internal static int FillPinholes(RcCompactHeightfield chf, float maxYards2, out int biggestFilled,bool[]? protectedClearance=null)
    {
        biggestFilled = 0;
        if (maxYards2 <= 0) return 0;

        int limit = (int)(maxYards2 / (chf.cs * chf.cs));
        var seen = new bool[chf.spanCount];
        var piece = new List<int>();
        var stack = new Stack<int>();
        int filled = 0;

        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c0 = ref chf.cells[cx + cz * chf.width];
                for (int i0 = c0.index; i0 < c0.index + c0.count; i0++)
                {
                    if (seen[i0] || chf.areas[i0] != AreaNull) continue;

                    piece.Clear();
                    stack.Clear();
                    stack.Push(i0);
                    seen[i0] = true;
                    bool open = false;          // reaches the edge of the heightfield

                    while (stack.Count > 0)
                    {
                        int cur = stack.Pop();
                        piece.Add(cur);
                        if(protectedClearance?[cur]==true)open=true;
                        int sx = CellXOf(chf, cur), sz = CellZOf(chf, cur);
                        if (sx == 0 || sz == 0 || sx == chf.width - 1 || sz == chf.height - 1) open = true;

                        for (int dir = 0; dir < 4; dir++)
                        {
                            int con = (chf.spans[cur].con >> (dir * 6)) & 0x3f;
                            // An absent neighbour is an open/obstacle boundary,
                            // not proof of a small hole enclosed by walkable floor.
                            if (con == 0x3f) {open=true;continue;}
                            int nx = sx + DirX[dir], nz = sz + DirY[dir];
                            if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) { open = true; continue; }
                            int ni = chf.cells[nx + nz * chf.width].index + con;
                            if (ni < 0 || ni >= chf.spanCount || seen[ni]) continue;
                            if (chf.areas[ni] != AreaNull) continue;
                            seen[ni] = true;
                            stack.Push(ni);
                        }
                    }

                    if (open || piece.Count > limit) continue;

                    // Take the area its walkable neighbours have, so a pinhole in a lake
                    // bed fills with swim floor and one in a field fills with ground.
                    var vote = new Dictionary<int, int>();
                    foreach (int cur in piece)
                    {
                        int sx = CellXOf(chf, cur), sz = CellZOf(chf, cur);
                        for (int dir = 0; dir < 4; dir++)
                        {
                            int con = (chf.spans[cur].con >> (dir * 6)) & 0x3f;
                            if (con == 0x3f) continue;
                            int nx = sx + DirX[dir], nz = sz + DirY[dir];
                            if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) continue;
                            int ni = chf.cells[nx + nz * chf.width].index + con;
                            if (ni < 0 || ni >= chf.spanCount) continue;
                            int na = chf.areas[ni];
                            if (na == AreaNull) continue;
                            vote[na] = vote.GetValueOrDefault(na) + 1;
                        }
                    }
                    if (vote.Count == 0) continue;

                    int win = vote.OrderByDescending(kv => kv.Value).First().Key;
                    foreach (int cur in piece) chf.areas[cur] = win;
                    filled++;
                    biggestFilled = Math.Max(biggestFilled, piece.Count);
                }
            }

        return filled;
    }

    /// Chop regions that are too big for the detail mesh to describe.
    ///
    /// A polygon's detail mesh is capped at 127 vertices, whatever its size - that limit is
    /// inside Recast's BuildPolyDetail and it is not configurable. So a large polygon is
    /// not merely coarse, it is UNREFINABLE: DetailSampleDist can be set as fine as you
    /// like and the sampler still stops after 127 points. Measured on tile [25,29] before
    /// this ran, the largest polygon covered 33,860 yd2 with 121 detail vertices - one per
    /// 280 yd2, about 17 yd apart - and the six largest were all pinned at 121-123. On a
    /// lake bed that is a handful of enormous planes with the ground poking through them,
    /// which is exactly what it looked like.
    ///
    /// Nothing downstream can fix that, and the levers that look like they should all
    /// failed to: EdgeMaxError, EdgeMaxLen and DetailSampleMaxError each made more
    /// triangles without moving the largest polygon at all. VertsPerPoly would, by merging
    /// less, but Detour's C-compatible tile layout fixes dtPoly at six vertices and
    /// anything else writes a tile that will not read back.
    ///
    /// So the size has to be capped earlier, at the region, because a region's contour is
    /// what the polygons are cut from. Each oversize region is intersected with a grid and
    /// each piece flood-filled separately, so a region that leaves a grid square and comes
    /// back does not end up as one disconnected region - BuildContours rejects those with
    /// "Multiple outlines for region".
    ///
    /// Region ids must stay under RC_BORDER_REG, which is a flag bit in the same field, so
    /// splitting stops rather than overflowing into it.
    /// Cells; below this a piece tends not to survive contouring.
    const int Viable = 24;

    /// Set when a tile could not split even at the widest grid and went out unsplit.
    [ThreadStatic] static bool OutOfRegionIds;

    /// The grid a tile actually settled on, when it is not the one that was asked for.
    [ThreadStatic] static float SplitCoarsened;

    /// Splitting at the asked-for grid can want more region ids than the field has room
    /// for, because ids share their 16-bit field with RC_BORDER_REG. Abandoning the tile
    /// was the wrong answer to that: 16 of this map's 29 tiles hit the ceiling and went
    /// out unsplit, which is where the 221 yd polygons came from - and a polygon that
    /// long cannot bend inside its 127-vertex detail budget, so it tents over the ground
    /// and that tent is the ridge. A coarser grid still bounds polygon length. No grid at
    /// all does not. So widen until the pieces fit, and only a tile that cannot fit even
    /// at eight times the asked-for grid goes out unsplit.
    /// A window on the world, in world X/Y, that the bake reports its own progress inside.
    ///
    /// When the navmesh is missing somewhere the useful question is not "is it missing" but
    /// "which stage threw it away", and every stage here is capable of it: the slope rule,
    /// the filters, erosion round an obstacle, the minimum region area, the region chop.
    /// Counting the surviving cells over this window after each stage names the culprit
    /// instead of leaving it to be guessed at.
    [ThreadStatic] public static (float X0, float Y0, float X1, float Y1)? ProbeWindow;
    [ThreadStatic] public static Action<string,RcCompactHeightfield>? StageObserver;
    [ThreadStatic] internal static Action<string,RcHeightfield>? SolidObserver;
    [ThreadStatic] internal static Action<RcContourSet>? ContoursObserver;
    [ThreadStatic] public static List<string>? ProbeLog;

    static bool InProbe(float wx, float wy) =>
        ProbeWindow is { } w && wx >= w.X0 && wx <= w.X1 && wy >= w.Y0 && wy <= w.Y1;

    /// The column at the middle of the window, listed at every stage. A window is a
    /// top-down thing and a building is not: inside one, the question is which of the
    /// surfaces stacked in a column survived, and a count cannot say.
    [ThreadStatic] public static (float X, float Y)? ProbeColumn;

    /// Walkable spans of the solid heightfield inside the window.
    static void ProbeSolid(string stage, RcHeightfield hf)
    {
        if (ProbeWindow == null) return;
        int walkable = 0, total = 0;
        for (int cz = 0; cz < hf.height; cz++)
            for (int cx = 0; cx < hf.width; cx++)
            {
                // Recast X is world Y, Recast Z is world X.
                float wy = hf.bmin.X + (cx + 0.5f) * hf.cs;
                float wx = hf.bmin.Z + (cz + 0.5f) * hf.cs;
                if (!InProbe(wx, wy)) continue;
                for (var sp = hf.spans[cx + cz * hf.width]; sp != null; sp = sp.next)
                {
                    total++;
                    if (sp.area != AreaNull) walkable++;
                }
            }
        ProbeLog?.Add($"{stage,-34} {walkable,7:N0} walkable of {total,7:N0} spans" + SolidColumn(hf));
    }

    /// Walkable span tops in the probe column of a solid heightfield.
    static string SolidColumn(RcHeightfield hf)
    {
        if (ProbeColumn is not { } q) return "";
        int cx = (int)MathF.Floor((q.Y - hf.bmin.X) / hf.cs);
        int cz = (int)MathF.Floor((q.X - hf.bmin.Z) / hf.cs);
        if (cx < 0 || cz < 0 || cx >= hf.width || cz >= hf.height) return "   [column outside]";
        var zs = new List<string>();
        for (var sp = hf.spans[cx + cz * hf.width]; sp != null; sp = sp.next)
            if (sp.area != AreaNull) zs.Add((hf.bmin.Y + sp.smax * hf.ch).ToString("F1"));
        return $"   column: {(zs.Count == 0 ? "empty" : string.Join(" ", zs))}";
    }

    /// Walkable span heights in the probe column of a compact heightfield.
    static string CompactColumn(RcCompactHeightfield chf)
    {
        if (ProbeColumn is not { } q) return "";
        int cx = (int)MathF.Floor((q.Y - chf.bmin.X) / chf.cs);
        int cz = (int)MathF.Floor((q.X - chf.bmin.Z) / chf.cs);
        if (cx < 0 || cz < 0 || cx >= chf.width || cz >= chf.height) return "   [column outside]";
        var zs = new List<string>();
        ref var c = ref chf.cells[cx + cz * chf.width];
        for (int i = c.index; i < c.index + c.count; i++)
            if (chf.areas[i] != AreaNull)
                zs.Add((chf.bmin.Y + chf.spans[i].y * chf.ch).ToString("F1") +
                       (chf.spans[i].reg != 0 ? "" : "(noreg)"));
        return $"   column: {(zs.Count == 0 ? "empty" : string.Join(" ", zs))}";
    }

    /// Cells of the compact heightfield inside the window, by what is still true of them.
    /// Draw the walkable cells in the probe window, and say what happened to the rest.
    ///
    /// Every hole chased this session came down to "erosion removed it", which the stage
    /// counts say but cannot explain - erosion only removes a cell because of its
    /// NEIGHBOURS, so the answer is always in the shape around it, and a count has no
    /// shape. This draws it: what is still walkable, what erosion took, and what was never
    /// walkable to begin with, which is the thing erosion was reacting to.
    static void ProbeCells(string stage, RcCompactHeightfield chf, int[]? preErode)
    {
        if (ProbeWindow == null || ProbeLog == null) return;

        int cx0 = int.MaxValue, cx1 = int.MinValue, cz0 = int.MaxValue, cz1 = int.MinValue;
        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                float wy0 = chf.bmin.X + (cx + 0.5f) * chf.cs;
                float wx0 = chf.bmin.Z + (cz + 0.5f) * chf.cs;
                if (!InProbe(wx0, wy0)) continue;
                cx0 = Math.Min(cx0, cx); cx1 = Math.Max(cx1, cx);
                cz0 = Math.Min(cz0, cz); cz1 = Math.Max(cz1, cz);
            }
        if (cx0 > cx1) return;

        ProbeLog.Add($"{stage}: '#' walkable  '+' eroded away  '.' span, never walkable  ' ' no span");
        for (int cx = cx1; cx >= cx0; cx--)
        {
            var sb = new System.Text.StringBuilder();
            for (int cz = cz0; cz <= cz1; cz++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                char ch = ' ';
                for (int i = c.index; i < c.index + c.count; i++)
                {
                    if (chf.areas[i] != AreaNull) { ch = '#'; break; }
                    if (preErode != null && preErode[i] != AreaNull) ch = '+';
                    else if (ch != '+') ch = '.';
                }
                sb.Append(ch);
            }
            ProbeLog.Add($"  {chf.bmin.X + (cx + 0.5f) * chf.cs,8:F1} {sb}");
        }

        // Erosion makes a boundary wherever a link is MISSING, whether or not the
        // neighbour is walkable, so this is the map of what it was reacting to.
        ProbeLog.Add($"{stage}: links missing per cell (0-4), '-' no walkable span");
        for (int cx = cx1; cx >= cx0; cx--)
        {
            var sb = new System.Text.StringBuilder();
            for (int cz = cz0; cz <= cz1; cz++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                char ch = '-';
                for (int i = c.index; i < c.index + c.count; i++)
                {
                    bool live = chf.areas[i] != AreaNull ||
                                (preErode != null && preErode[i] != AreaNull);
                    if (!live) continue;
                    int missing = 0;
                    for (int dir = 0; dir < 4; dir++)
                        if (((chf.spans[i].con >> (dir * 6)) & 0x3f) == 0x3f) missing++;
                    ch = (char)('0' + missing);
                    break;
                }
                sb.Append(ch);
            }
            ProbeLog.Add($"  {chf.bmin.X + (cx + 0.5f) * chf.cs,8:F1} {sb}");
        }
    }

    static void ProbeCompact(string stage, RcCompactHeightfield chf, bool regions = false)
    {
        StageObserver?.Invoke(stage,chf);
        if (ProbeWindow == null) return;
        int walkable = 0, total = 0, withReg = 0;
        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                float wy = chf.bmin.X + (cx + 0.5f) * chf.cs;
                float wx = chf.bmin.Z + (cz + 0.5f) * chf.cs;
                if (!InProbe(wx, wy)) continue;
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i = c.index; i < c.index + c.count; i++)
                {
                    total++;
                    if (chf.areas[i] != AreaNull) walkable++;
                    if (chf.spans[i].reg != 0) withReg++;
                }
            }
        ProbeLog?.Add($"{stage,-34} {walkable,7:N0} walkable of {total,7:N0} cells" +
                      (regions ? $", {withReg:N0} in a region" : "") + CompactColumn(chf));

        if (!regions) return;

        // A region that occupies the same column twice cannot be traced. BuildContours
        // walks the cell grid, so a helix - a spiral ramp is one region winding upward
        // over its own footprint - comes back to a column it has already left, and the
        // outline crosses itself. Recast says "Multiple outlines for region" or
        // "Bad triangulation" and the polygons for that whole region are lost.
        var seen = new Dictionary<int, HashSet<int>>();
        var doubled = new Dictionary<int, int>();
        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i = c.index; i < c.index + c.count; i++)
                {
                    int reg = chf.spans[i].reg & 0x7fff;
                    if (reg == 0) continue;
                    if (!seen.TryGetValue(reg, out var cols)) seen[reg] = cols = [];
                    if (!cols.Add(cx + cz * chf.width)) doubled[reg] = doubled.GetValueOrDefault(reg) + 1;
                }
            }
        ProbeLog?.Add($"{"  of those, regions over themselves",-34} {doubled.Count,7:N0} of {seen.Count:N0} " +
                      $"regions revisit a column ({doubled.Values.Sum():N0} spans) - these cannot be contoured");
    }

    /// Erode the walkable area, but not as hard indoors.
    ///
    /// Recast pulls the walkable area back by the agent's radius everywhere, so that a
    /// polygon's surface is somewhere the agent's whole body fits. Outdoors that is right
    /// and cheap - the ground is wide. Indoors it is neither: a WMO corridor, a landing or
    /// a spiral ramp is built to human width, and pulling 0.53 yd off each side of a ramp
    /// barely a yard and a half wide leaves nothing. The ramp then vanishes in the middle,
    /// and everything above it becomes unreachable - which is the whole floor of a
    /// building, with a quest NPC on it.
    ///
    /// Measured in the building at (2109.7, 538.3), following one column through the bake:
    /// five walkable surfaces come out of rasterisation at 694.4, 704.8, 728.0, 741.1 and
    /// 809.9, and erosion alone deletes 728.0. 761 of 2,147 cells in a 6 yd window go with
    /// it.
    ///
    /// "Indoors" is asked of the geometry rather than of where the triangle came from: a
    /// cell is indoors when there is solid geometry over it within `RoofReach`. That is
    /// the condition that actually matters - a space with a roof on it is a space built to
    /// fit through - and it catches caves and overhangs on the same argument, without
    /// needing to trust which file a triangle arrived in.
    ///
    /// This is Recast's own erosion otherwise: the same chamfer distance transform, 2 for
    /// an orthogonal step and 3 for a diagonal, thresholded at twice the radius. Only the
    /// threshold varies per cell.
    // Indoor stair preservation must not disable clearance from actual body-height
    // solid walls. Missing links and legal treads are not walls; inspect the solid
    // field instead of applying general boundary erosion to the whole staircase.
    internal static int ErodeSolidIndoorWalls(RcCompactHeightfield chf,RcHeightfield solid,bool[] indoor,int[] preErode,Settings settings,
                                            SourceBodyClearance? source=null,bool[]? protectedClearance=null,
                                            IReadOnlySet<long>? rejectedCaps=null,IReadOnlySet<long>? blockedProps=null)
    {
        int reach=(int)MathF.Ceiling(settings.AgentRadius/chf.cs),removed=0;
        float radius2=settings.AgentRadius*settings.AgentRadius;
        var blocked=new List<int>();
        for(int z=0;z<chf.height;z++)for(int x=0;x<chf.width;x++){
            ref var cell=ref chf.cells[x+z*chf.width];
            for(int i=cell.index;i<cell.index+cell.count;i++){
                if(!indoor[i]||chf.areas[i]==AreaSwimFloor)continue;
                float floor=chf.bmin.Y+chf.spans[i].y*chf.ch;
                bool hit=false,deliberate=false;
                for(int dz=-reach;dz<=reach&&!deliberate;dz++)for(int dx=-reach;dx<=reach&&!deliberate;dx++){
                    int nx=x+dx,nz=z+dz;if(nx<0||nz<0||nx>=solid.width||nz>=solid.height)continue;
                    float sx=MathF.Max(0,MathF.Abs(dx)-.5f)*chf.cs,sz=MathF.Max(0,MathF.Abs(dz)-.5f)*chf.cs;
                    if(sx*sx+sz*sz>=radius2)continue;
                    for(var span=solid.spans[nx+nz*solid.width];span!=null;span=span.next){
                        // A legally climbable top is a tread, not a body-height wall.
                        float top=solid.bmin.Y+span.smax*solid.ch,bottom=solid.bmin.Y+span.smin*solid.ch;
                        long key=((long)(nx+nz*solid.width)<<20)|(uint)span.smin;
                        bool rejected=(rejectedCaps?.Contains(key)??false)||(blockedProps?.Contains(key)??false);
                        if(rejected&&span.area==AreaNull&&top>floor+chf.ch*.5f&&bottom<floor+settings.AgentHeight){hit=true;deliberate=true;break;}
                        if(top<=floor+settings.AgentMaxClimb+chf.ch||bottom>=floor+settings.AgentHeight)continue;
                        if(span.area==AreaNull){hit=true;break;}
                    }
                }
                float wx=chf.bmin.Z+(z+.5f)*chf.cs,wy=chf.bmin.X+(x+.5f)*chf.cs;
                if((deliberate&&(source==null||source.HitsRejected(wx,wy,floor)))||
                   (hit&&(source==null||source.Hits(wx,wy,floor))))blocked.Add(i);
            }
        }
        foreach(int i in blocked){
            if(chf.areas[i]!=AreaNull)removed++;
            chf.areas[i]=AreaNull;preErode[i]=AreaNull;
            if(protectedClearance!=null)protectedClearance[i]=true;
        }
        return removed;
    }

    static int ErodeIndoorAware(RcCompactHeightfield chf, RcHeightfield solid,
                                int radiusCells, int indoorRadiusCells, out int indoorCount,
                                out bool[] indoorOut, float stepBreak = 0f)
    {
        // 60, not 30, because 30 is shorter than the rooms.
        //
        // Every railing pass here is gated on "indoors", and at 30 yd the three worst
        // railing runs on the map - (1497.6, 1702.8), (3873.1, 357.9), (3138.0, 612.8) -
        // all sit under ceilings 35 to 41 yd up, so none of them counted as indoors and
        // none of the machinery aimed at them ever looked. Raising it to 60 takes railing
        // and trim from 1,512 to 1,215 yd2 and GROWS the mainland by 96 polys, which is
        // the shape of a fix rather than a trade. 120 does no better than 60.
        float RoofReach = float.TryParse(Environment.GetEnvironmentVariable("ROOF_REACH"),
                                         out float rr) ? rr : 60f;
        var indoor = new bool[chf.spanCount];
        indoorOut = indoor;
        indoorCount = 0;

        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                if (c.count == 0) continue;
                for (int i = c.index; i < c.index + c.count; i++)
                {
                    if (chf.areas[i] == AreaNull) continue;
                    float z = chf.bmin.Y + chf.spans[i].y * chf.ch;
                    int col = cx + cz * solid.width;
                    for (var sp = solid.spans[col]; sp != null; sp = sp.next)
                    {
                        float bottom = solid.bmin.Y + sp.smin * solid.ch;
                        if (bottom <= z + chf.ch) continue;          // itself, or below
                        if (bottom - z > RoofReach) break;           // spans are ordered
                        if (DoodadRoofSpans != null &&
                            DoodadRoofSpans.Contains(((long)col << 20) | (uint)sp.smin)) continue;
                        indoor[i] = true;
                        indoorCount++;
                        break;
                    }
                }
            }

        // Recast's distance transform, verbatim apart from the threshold.
        var dist = new byte[chf.spanCount];
        Array.Fill(dist, (byte)0xff);

        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i = c.index; i < c.index + c.count; i++)
                {
                    if (chf.areas[i] == AreaNull) { dist[i] = 0; continue; }
                    var sp = chf.spans[i];
                    float myz = chf.bmin.Y + sp.y * chf.ch;
                    int open = 0;
                    for (int dir = 0; dir < 4; dir++)
                    {
                        int con = (sp.con >> (dir * 6)) & 0x3f;
                        if (con == 0x3f) break;
                        int nx = cx + DirX[dir], nz = cz + DirY[dir];
                        int ni = chf.cells[nx + nz * chf.width].index + con;
                        if (chf.areas[ni] == AreaNull) break;
                        // A step up is an edge, even though it is linked.
                        //
                        // Recast counts a neighbour as open whenever the link exists, and
                        // the link exists up to AgentMaxClimb. That is what keeps the top
                        // of a railing alive: it is one cell wide, but the floor beside it
                        // is walkable and linked, so the distance transform reads it as
                        // deep inside a wide open blob and erosion never trims it. The
                        // floor polygon then takes a corner up at rail height - poly 7433
                        // at (2115.6, 545.6) spreads 1.33 yd across its own corners - and
                        // the detail mesh draws the diagonal between.
                        //
                        // Counting a step as an edge trims the surface back from it,
                        // WITHOUT cutting the link. That distinction is the whole point:
                        // severing disconnects a staircase into one island per tread,
                        // while this only keeps the agent's width clear of a drop, which
                        // is what erosion is for in the first place.
                        // Indoors only.
                        //
                        // A step is an edge for erosion because of railings, and railings
                        // are an interior problem. Applied outdoors it severs paths: the
                        // harbour walkway at (-8574.1, 1167.5) crosses an 0.84 yd step,
                        // well inside AgentMaxClimb, and trimming 0.53 yd back from each
                        // side opens a gap the agent cannot cross - the walkway to the
                        // boats became a 73-polygon island cut off from the mainland.
                        //
                        // A threshold cannot tell the two apart: the railing that this was
                        // built for climbs in ~0.7 yd steps and this one is 0.84. What does
                        // tell them apart is that a railing has a roof over it and a harbour
                        // does not.
                        if (stepBreak > 0f && indoor[i] &&
                            MathF.Abs(chf.bmin.Y + chf.spans[ni].y * chf.ch - myz) > stepBreak) break;
                        open++;
                    }
                    if (open != 4) dist[i] = 0;
                }
            }

        Sweep(true);
        Sweep(false);

        int removed = 0;
        for (int i = 0; i < chf.spanCount; i++)
        {
            if (chf.areas[i] == AreaNull) continue;
            int thr = (indoor[i] ? indoorRadiusCells : radiusCells) * 2;
            if (dist[i] >= thr) continue;
            chf.areas[i] = AreaNull;
            removed++;
        }
        return removed;

        // One pass of the chamfer sweep. Forward visits dir 0 and 3, backward 2 and 1,
        // each with the diagonal reached through it.
        void Sweep(bool forward)
        {
            for (int zi = 0; zi < chf.height; zi++)
                for (int xi = 0; xi < chf.width; xi++)
                {
                    int cz = forward ? zi : chf.height - 1 - zi;
                    int cx = forward ? xi : chf.width - 1 - xi;
                    ref var c = ref chf.cells[cx + cz * chf.width];
                    for (int i = c.index; i < c.index + c.count; i++)
                    {
                        var sp = chf.spans[i];
                        foreach (int dir in forward ? new[] { 0, 3 } : [2, 1])
                        {
                            int con = (sp.con >> (dir * 6)) & 0x3f;
                            if (con == 0x3f) continue;
                            int nx = cx + DirX[dir], nz = cz + DirY[dir];
                            int ni = chf.cells[nx + nz * chf.width].index + con;
                            if (dist[ni] + 2 < dist[i]) dist[i] = (byte)(dist[ni] + 2);

                            int side = forward ? (dir + 1) & 0x3 : (dir + 3) & 0x3;
                            int con2 = (chf.spans[ni].con >> (side * 6)) & 0x3f;
                            if (con2 == 0x3f) continue;
                            int mx = nx + DirX[side], mz = nz + DirY[side];
                            int mi = chf.cells[mx + mz * chf.width].index + con2;
                            if (dist[mi] + 3 < dist[i]) dist[i] = (byte)(dist[mi] + 3);
                        }
                    }
                }
        }
    }

    /// Split any region that winds over its own footprint.
    ///
    /// `BuildContours` walks the cell grid, so a region can only be traced if it covers
    /// each column at most once. A spiral ramp is one region winding upward over the same
    /// footprint: its outline returns to columns it has already left and crosses itself,
    /// Recast reports "Multiple outlines for region" or "Bad triangulation", and the
    /// polygons for that whole region are dropped. That is a staircase of navmesh gone,
    /// not a sliver.
    ///
    /// The grid chop hides most of it by accident, because two turns of a WIDE spiral land
    /// in different grid squares. It can do nothing for a tight one, whose every turn sits
    /// inside a single square - and the tighter the spiral, the more certainly it breaks.
    ///
    /// So the fill takes one turn at a time: expand while the column is still free, stop
    /// when it comes back over itself. Each piece then covers every column once.
    ///
    /// **Order matters, and on its own this does nothing.** Tried first while erosion was
    /// still deleting the middle of the ramp, it took self-overlap from 3 regions to 0 and
    /// brought back not one missing level, because the surface it was rescuing had already
    /// been thrown away two stages earlier. It only pays once the ramp survives erosion.
    /// Keep every detail vertex inside the height its own polygon covers.
    ///
    /// The detail mesh exists to refine a polygon's surface, so a detail vertex belongs
    /// somewhere between that polygon's corners give or take the sampler's error. It has
    /// no business ten yards below them.
    ///
    /// It gets there because `BuildPolyDetail` reads heights from a patch flood-filled
    /// over the polygon's own region, and where that patch has no answer it spirals
    /// outward and takes the first height it finds. In a column with one surface that is
    /// a good guess. Inside a spiral ramp, where the same column carries a turn every ten
    /// yards, it is a coin flip - and when it loses, one corner of a triangle drops to the
    /// turn below and the triangle becomes a vertical sheet hanging between the two. A
    /// stack of them reads exactly like a waterfall, which is what it looked like in the
    /// viewer.
    ///
    /// `FlattenDetailSpikes` cannot help: the correction needed is the whole gap between
    /// two turns, and its ceiling is deliberately 4 yd so that a vertex over a cave does
    /// not get dragged to the cave floor. The bound that works here is not a distance but
    /// the polygon itself, which knows what height it is at.
    static int ClampDetailToPolygon(RcPolyMesh pmesh, RcPolyMeshDetail dmesh,
                                    RcCompactHeightfield chf, RcHeightfield solid,
                                    float margin, float indoorDrop,SourceSurfaceFit? source=null)
    {
        if (dmesh.meshes == null || dmesh.verts == null || pmesh.polys == null) return 0;

        int moved = 0;
        int nvp = pmesh.nvp;
        for (int m = 0; m < dmesh.nmeshes && m < pmesh.npolys; m++)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            for (int j = 0; j < nvp; j++)
            {
                int v = pmesh.polys[m * nvp * 2 + j];
                if (v == 0xffff) break;                   // RC_MESH_NULL_IDX
                float y = pmesh.bmin.Y + pmesh.verts[v * 3 + 1] * pmesh.ch;
                lo = MathF.Min(lo, y); hi = MathF.Max(hi, y);
            }
            if (lo > hi) continue;
            float hullLo = lo;
            lo -= margin; hi += margin;

            int vertBase = dmesh.meshes[m * 4], vertCount = dmesh.meshes[m * 4 + 1];
            for (int v = 0; v < vertCount; v++)
            {
                int at = (vertBase + v) * 3;
                float vy = dmesh.verts[at + 1];
                float vx = dmesh.verts[at], vz = dmesh.verts[at + 2];
                // A roof does not make a floor flat: ramps, stair transitions
                // and recessed floors may legitimately dip below hull corners.
                // Preserve collision-verified detail within this polygon's
                // storey band rather than lifting it to an invented flat floor.
                float verified=source?.Height(new(vz,vx,vy))??float.NaN;
                if(float.IsFinite(verified)&&verified>=lo&&verified<=hi&&MathF.Abs(verified-vy)<=chf.ch*2.01f)continue;
                int cx = (int)MathF.Floor((vx - chf.bmin.X) / chf.cs);
                int cz = (int)MathF.Floor((vz - chf.bmin.Z) / chf.cs);

                // A floor is flat and a detail vertex below it has sunk through it - that
                // is the mesh you see hanging from the ceiling of the storey below. Open
                // ground is different: a polygon over a dip legitimately sags below its
                // own corners, so the slack downward stays generous outdoors and is cut
                // to `indoorDrop` under a roof. Measured in the building at
                // (2108.3, 537.7): 8 columns of 5,649 sat up to 0.95 yd under their own
                // floor, which the 2 yd band let through.
                float floorLo = lo;
                if (Roofed(cx, cz, vy)) floorLo = MathF.Max(lo, hullLo - indoorDrop);
                if (vy >= floorLo && vy <= hi) continue;
                float best = vy < floorLo ? floorLo : hi, bestGap = float.MaxValue;
                if (cx >= 0 && cz >= 0 && cx < chf.width && cz < chf.height)
                {
                    ref var c = ref chf.cells[cx + cz * chf.width];
                    for (int i = c.index; i < c.index + c.count; i++)
                    {
                        if (chf.areas[i] == AreaNull) continue;
                        float sy = chf.bmin.Y + chf.spans[i].y * chf.ch;
                        if (sy < floorLo || sy > hi) continue;
                        float gap = MathF.Abs(sy - vy);
                        if (gap < bestGap) { bestGap = gap; best = sy; }
                    }
                }
                dmesh.verts[at + 1] = best;
                moved++;
            }
        }
        return moved;

        // Is there solid geometry over this spot? The same question the erosion asks, and
        // the answer means the same thing: a space with a roof on it is built, and a built
        // floor is flat.
        bool Roofed(int cx, int cz, float z)
        {
            if (cx < 0 || cz < 0 || cx >= solid.width || cz >= solid.height) return false;
            for (var sp = solid.spans[cx + cz * solid.width]; sp != null; sp = sp.next)
            {
                float bottom = solid.bmin.Y + sp.smin * solid.ch;
                if (bottom <= z + chf.ch) continue;
                return bottom - z <= 30f;
            }
            return false;
        }
    }

    /// Drop a thin indoor strip that nothing walks onto.
    ///
    /// Not eroding indoors is what saves the ramps, and it also leaves the navmesh sitting
    /// on every railing cap and every band of trim round a room, because those have flat
    /// tops at a walkable angle and Recast has no opinion about what a thing is for. A bot
    /// routed along a handrail is worse than one that cannot use the stairs.
    ///
    /// What separates a railing from a bump in the floor is not its width, it is whether
    /// you can step onto it. A bump the agent can climb is connected to the floor through
    /// the compact heightfield and is part of the floor's own piece, however narrow it
    /// looks. A railing cap stands a clear step above everything around it, so it is a
    /// piece of its own - and a long, thin one. So: take the connected pieces of indoor
    /// walkable cells, and drop the ones that are both separate and too narrow to stand
    /// on. Mean width is 2 x area / perimeter, which is the strip width for anything long.
    ///
    /// A ramp survives because it is not separate: it meets the floor at the bottom.
    /// Cells to search sideways for the floor a strip of trim stands over. Ground under a
    /// tree has nothing under it at any radius, so reaching further only recovers railings.
    static readonly int TrimFloorReach =
        int.TryParse(Environment.GetEnvironmentVariable("TRIM_REACH"), out int tr) ? tr : 2;

    static int DropNarrowIndoorStrips(RcCompactHeightfield chf, bool[] indoor, float minWidthYards)
    {
        if (minWidthYards <= 0) return 0;

        var seen = new bool[chf.spanCount];
        var pieceOf = new int[chf.spanCount];
        var piece = new List<int>();
        var stack = new Stack<int>();
        int dropped = 0, pieceId = 0;

        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i0 = c.index; i0 < c.index + c.count; i0++)
                {
                    if (seen[i0] || chf.areas[i0] == AreaNull || !indoor[i0]) continue;

                    piece.Clear();
                    stack.Clear();
                    stack.Push(i0);
                    seen[i0] = true;
                    pieceId++;
                    pieceOf[i0] = pieceId;
                    int edges = 0;
                    bool joined = false;        // touches walkable ground outside the roof

                    while (stack.Count > 0)
                    {
                        int cur = stack.Pop();
                        piece.Add(cur);
                        int sx = CellXOf(chf, cur), sz = CellZOf(chf, cur);
                        for (int dir = 0; dir < 4; dir++)
                        {
                            int con = (chf.spans[cur].con >> (dir * 6)) & 0x3f;
                            if (con == 0x3f) { edges++; continue; }
                            int nx = sx + DirX[dir], nz = sz + DirY[dir];
                            if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) { edges++; continue; }
                            int ni = chf.cells[nx + nz * chf.width].index + con;
                            if (ni < 0 || ni >= chf.spanCount) { edges++; continue; }
                            if (chf.areas[ni] == AreaNull) { edges++; continue; }
                            // Leaving the indoor set is a boundary for this purpose, but
                            // it also means the strip is attached to something outside,
                            // so it is not a railing - bail out and keep the piece.
                            //
                            // That is what this comment has always said and what the code
                            // never did: it counted the edge and carried on, so a patch
                            // joined to open ground was dropped like a railing cap. The
                            // indoor set's boundary is wherever the roof happens to end,
                            // which is arbitrary ground, and a sliver cut out by it has a
                            // long perimeter for its area - so 2A/P calls it narrow and it
                            // goes. On terrain under a tall structure that is a diagonal
                            // slit through open hillside, which is the hole reported at
                            // (2134.6, 599.7): erosion leaves the window solid, and this
                            // pass cuts the crack straight afterwards.
                            //
                            // Requiring the outside neighbour to be LEVEL with this cell,
                            // so that "joined" means the same surface carrying on rather
                            // than anything linked out there, was tried and is inert -
                            // identical polygon count and railing area. Every join that
                            // matters here is already level.
                            if (!indoor[ni]) { edges++; joined = true; continue; }
                            if (seen[ni]) continue;
                            seen[ni] = true;
                            pieceOf[ni] = pieceId;
                            stack.Push(ni);
                        }
                    }

                    if (edges == 0 || joined) continue;
                    float area = piece.Count * chf.cs * chf.cs;
                    float width = 2f * area / (edges * chf.cs);
                    if (width >= minWidthYards) continue;

                    // Width alone also describes a narrow stair/corridor. Only
                    // delete a level raised shelf over nearby lower floor.
                    int low=piece.Min(i=>chf.spans[i].y), high=piece.Max(i=>chf.spans[i].y);
                    if((high-low)*chf.ch>.5f)continue;
                    int supported=0;
                    foreach(int i in piece)
                    {
                        int sx=CellXOf(chf,i),sz=CellZOf(chf,i);bool over=false;
                        for(int dz=-2;dz<=2&&!over;dz++)for(int dx=-2;dx<=2&&!over;dx++)
                        {
                            int nx=sx+dx,nz=sz+dz;
                            if(nx<0||nz<0||nx>=chf.width||nz>=chf.height)continue;
                            ref var nc=ref chf.cells[nx+nz*chf.width];
                            for(int j=nc.index;j<nc.index+nc.count;j++)
                            {
                                if(chf.areas[j]==AreaNull||pieceOf[j]==pieceId)continue;
                                float drop=(chf.spans[i].y-chf.spans[j].y)*chf.ch;
                                if(drop>=.35f&&drop<=1.5f){over=true;break;}
                            }
                        }
                        if(over)supported++;
                    }
                    if(supported*4<piece.Count*3)continue;

                    foreach (int cur in piece) chf.areas[cur] = AreaNull;
                    dropped += piece.Count;
                }
            }
        return dropped;
    }

    /// How wide is the walkable surface here, cell by cell?
    ///
    /// Erosion and the trim cull both turn on width, and picking either threshold by eye
    /// is how a ramp gets deleted. This reports the distribution directly, indoors and
    /// out, using the same chamfer distance the erosion uses: 2 per orthogonal step, 3
    /// per diagonal, so a distance of d is a width of about d yards / 1.5 cell units.
    static void ProbeWidths(RcCompactHeightfield chf, RcHeightfield solid)
    {
        if (ProbeWindow == null) return;

        var dist = new byte[chf.spanCount];
        Array.Fill(dist, (byte)0xff);
        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i = c.index; i < c.index + c.count; i++)
                {
                    if (chf.areas[i] == AreaNull) { dist[i] = 0; continue; }
                    int open = 0;
                    for (int dir = 0; dir < 4; dir++)
                    {
                        int con = (chf.spans[i].con >> (dir * 6)) & 0x3f;
                        if (con == 0x3f) break;
                        int nx = cx + DirX[dir], nz = cz + DirY[dir];
                        if (chf.areas[chf.cells[nx + nz * chf.width].index + con] == AreaNull) break;
                        open++;
                    }
                    if (open != 4) dist[i] = 0;
                }
            }
        for (int pass = 0; pass < 2; pass++)
            for (int zi = 0; zi < chf.height; zi++)
                for (int xi = 0; xi < chf.width; xi++)
                {
                    int cz = pass == 0 ? zi : chf.height - 1 - zi;
                    int cx = pass == 0 ? xi : chf.width - 1 - xi;
                    ref var c = ref chf.cells[cx + cz * chf.width];
                    for (int i = c.index; i < c.index + c.count; i++)
                        foreach (int dir in pass == 0 ? new[] { 0, 3 } : [2, 1])
                        {
                            int con = (chf.spans[i].con >> (dir * 6)) & 0x3f;
                            if (con == 0x3f) continue;
                            int nx = cx + DirX[dir], nz = cz + DirY[dir];
                            int ni = chf.cells[nx + nz * chf.width].index + con;
                            if (dist[ni] + 2 < dist[i]) dist[i] = (byte)(dist[ni] + 2);
                            int side = pass == 0 ? (dir + 1) & 0x3 : (dir + 3) & 0x3;
                            int con2 = (chf.spans[ni].con >> (side * 6)) & 0x3f;
                            if (con2 == 0x3f) continue;
                            int mi = chf.cells[nx + DirX[side] + (nz + DirY[side]) * chf.width].index + con2;
                            if (dist[mi] + 3 < dist[i]) dist[i] = (byte)(dist[mi] + 3);
                        }
                }

        var inW = new List<float>();
        var outW = new List<float>();
        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                float wy = chf.bmin.X + (cx + 0.5f) * chf.cs;
                float wx = chf.bmin.Z + (cz + 0.5f) * chf.cs;
                if (!InProbe(wx, wy)) continue;
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i = c.index; i < c.index + c.count; i++)
                {
                    if (chf.areas[i] == AreaNull) continue;
                    // Half-width in cells is dist/2 with the 2/3 chamfer; times 2 for the
                    // full width, times the cell size for yards.
                    float w = dist[i] / 2f * chf.cs * 2f;
                    bool roofed = false;
                    float z = chf.bmin.Y + chf.spans[i].y * chf.ch;
                    for (var sp = solid.spans[cx + cz * solid.width]; sp != null; sp = sp.next)
                    {
                        float bottom = solid.bmin.Y + sp.smin * solid.ch;
                        if (bottom <= z + chf.ch) continue;
                        if (bottom - z > 30f) break;
                        roofed = true; break;
                    }
                    (roofed ? inW : outW).Add(w);
                }
            }
        Report("indoor", inW);
        Report("outdoor", outW);

        // Cells that LOOK like a railing cap - narrow, and standing over walkable
        // surface within a yard - split by whether the roof test called them indoors.
        // If the survivors are outdoors, the rule is not firing rather than not working.
        int narrowIn = 0, narrowOut = 0;
        var dist2 = ChamferDistance(chf);
        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                float wy = chf.bmin.X + (cx + 0.5f) * chf.cs;
                float wx = chf.bmin.Z + (cz + 0.5f) * chf.cs;
                if (!InProbe(wx, wy)) continue;
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i = c.index; i < c.index + c.count; i++)
                {
                    if (chf.areas[i] == AreaNull) continue;
                    if (dist2[i] * chf.cs >= 1.07f) continue;
                    float z = chf.bmin.Y + chf.spans[i].y * chf.ch;
                    bool over = false;
                    for (int dz = -3; dz <= 3 && !over; dz++)
                        for (int dx = -3; dx <= 3 && !over; dx++)
                        {
                            int nx = cx + dx, nz = cz + dz;
                            if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) continue;
                            ref var nc = ref chf.cells[nx + nz * chf.width];
                            for (int j = nc.index; j < nc.index + nc.count; j++)
                            {
                                if (chf.areas[j] == AreaNull) continue;
                                float drop = z - (chf.bmin.Y + chf.spans[j].y * chf.ch);
                                if (drop >= 0.35f && drop <= 3f) { over = true; break; }
                            }
                        }
                    if (!over) continue;
                    bool roofed = false;
                    for (var sp = solid.spans[cx + cz * solid.width]; sp != null; sp = sp.next)
                    {
                        float bottom = solid.bmin.Y + sp.smin * solid.ch;
                        if (bottom <= z + chf.ch) continue;
                        roofed = bottom - z <= 30f; break;
                    }
                    if (roofed) narrowIn++; else narrowOut++;
                }
            }
        ProbeLog?.Add($"  railing-shaped cells still here: {narrowIn:N0} indoor, {narrowOut:N0} outdoor");

        void Report(string what, List<float> v)
        {
            if (v.Count == 0) { ProbeLog?.Add($"  width {what,-8} none"); return; }
            v.Sort();
            ProbeLog?.Add($"  width {what,-8} {v.Count,6:N0} cells: p10 {v[v.Count / 10]:F2}, " +
                          $"median {v[v.Count / 2]:F2}, p90 {v[v.Count * 9 / 10]:F2}, max {v[^1]:F2} yd" +
                          $"   under 0.5: {v.Count(x => x < 0.5f) * 100.0 / v.Count:F0}%" +
                          $"   under 1.07: {v.Count(x => x < 1.07f) * 100.0 / v.Count:F0}%");
        }
    }

    /// Remove thin indoor detail without shrinking anything wide - a morphological
    /// opening rather than an erosion.
    ///
    /// Dropping separate thin pieces catches a handrail cap, because a cap you cannot step
    /// onto is an island. It does nothing about the trim round the bottom of a wall, the
    /// lip of a hearth or the ledge along a stair stringer, because those are only an inch
    /// or two proud and the agent can climb them, so they are CONNECTED to the floor and
    /// come out as fingers on the floor's own piece. Measured in the building at
    /// (2108.3, 537.7): indoor cells have a median width of 1.60 yd, and 17% of them sit
    /// in strips under half a yard wide. That 17% is the trim.
    ///
    /// Erosion would take them, and take the ramps with them - which is the whole reason
    /// erosion is off indoors. An opening does not: erode by the radius, then dilate back
    /// by the same, and everything wider than the element returns exactly as it was while
    /// everything narrower is gone for good. At one cell it clears strips under about half
    /// a yard and leaves a 1.6 yd ramp untouched.
    static int OpenIndoorSurface(RcCompactHeightfield chf, bool[] indoor, int radiusCells,
                                 float stepLimit)
    {
        if (radiusCells <= 0) return 0;

        var dist = new byte[chf.spanCount];
        Array.Fill(dist, (byte)0xff);
        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i = c.index; i < c.index + c.count; i++)
                {
                    if (chf.areas[i] == AreaNull) { dist[i] = 0; continue; }
                    int open = 0;
                    for (int dir = 0; dir < 4; dir++)
                    {
                        int con = (chf.spans[i].con >> (dir * 6)) & 0x3f;
                        if (con == 0x3f) break;
                        int nx = cx + DirX[dir], nz = cz + DirY[dir];
                        if (chf.areas[chf.cells[nx + nz * chf.width].index + con] == AreaNull) break;
                        open++;
                    }
                    if (open != 4) dist[i] = 0;
                }
            }
        for (int pass = 0; pass < 2; pass++)
            for (int zi = 0; zi < chf.height; zi++)
                for (int xi = 0; xi < chf.width; xi++)
                {
                    int cz = pass == 0 ? zi : chf.height - 1 - zi;
                    int cx = pass == 0 ? xi : chf.width - 1 - xi;
                    ref var c = ref chf.cells[cx + cz * chf.width];
                    for (int i = c.index; i < c.index + c.count; i++)
                        foreach (int dir in pass == 0 ? new[] { 0, 3 } : [2, 1])
                        {
                            int con = (chf.spans[i].con >> (dir * 6)) & 0x3f;
                            if (con == 0x3f) continue;
                            int nx = cx + DirX[dir], nz = cz + DirY[dir];
                            int ni = chf.cells[nx + nz * chf.width].index + con;
                            if (dist[ni] + 2 < dist[i]) dist[i] = (byte)(dist[ni] + 2);
                            int side = pass == 0 ? (dir + 1) & 0x3 : (dir + 3) & 0x3;
                            int con2 = (chf.spans[ni].con >> (side * 6)) & 0x3f;
                            if (con2 == 0x3f) continue;
                            int mi = chf.cells[nx + DirX[side] + (nz + DirY[side]) * chf.width].index + con2;
                            if (dist[mi] + 3 < dist[i]) dist[i] = (byte)(dist[mi] + 3);
                        }
                }

        // The core is what survives the erosion. Everything else indoors has to earn its
        // place back by being within the radius of some core cell.
        var keep = new bool[chf.spanCount];
        var queue = new Queue<(int Span, int Left)>();
        for (int i = 0; i < chf.spanCount; i++)
        {
            if (chf.areas[i] == AreaNull) continue;
            if (!indoor[i] || dist[i] >= radiusCells * 2) { keep[i] = true; queue.Enqueue((i, radiusCells)); }
        }
        while (queue.Count > 0)
        {
            var (cur, left) = queue.Dequeue();
            if (left == 0) continue;
            int sx = CellXOf(chf, cur), sz = CellZOf(chf, cur);
            for (int dir = 0; dir < 4; dir++)
            {
                int con = (chf.spans[cur].con >> (dir * 6)) & 0x3f;
                if (con == 0x3f) continue;
                int nx = sx + DirX[dir], nz = sz + DirY[dir];
                if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) continue;
                int ni = chf.cells[nx + nz * chf.width].index + con;
                if (ni < 0 || ni >= chf.spanCount || keep[ni] || chf.areas[ni] == AreaNull) continue;
                // The dilation may not climb.
                //
                // This is why the opening did nothing to railings. A cap about a yard
                // high is climbable, so the compact heightfield links it to the floor
                // beside it, and the floor's core - which is wide and survives the
                // erosion - simply dilated up onto the cap and put it back. Refusing to
                // step up more than `stepLimit` while dilating leaves the cap where the
                // erosion left it, without touching the floor's own edge, which is what
                // separates this from eroding indoors.
                float za = chf.bmin.Y + chf.spans[cur].y * chf.ch;
                float zb = chf.bmin.Y + chf.spans[ni].y * chf.ch;
                if (MathF.Abs(zb - za) > stepLimit) continue;
                keep[ni] = true;
                queue.Enqueue((ni, left - 1));
            }
        }

        int dropped = 0;
        for (int i = 0; i < chf.spanCount; i++)
        {
            if (chf.areas[i] == AreaNull || keep[i]) continue;
            chf.areas[i] = AreaNull;
            dropped++;
        }
        return dropped;
    }

    /// Erode indoors like everywhere else, then put back only what erosion CUT.
    ///
    /// Width cannot separate a railing from a ramp - both are narrow, and five attempts to
    /// find a shape that tells them apart all failed. Purpose can. A ramp or a narrow
    /// corridor is the only way to get somewhere: erode it and the floor above becomes an
    /// island. A railing cap is a dead end: erode it and nothing else changes. So erode at
    /// the full radius, which removes both, then restore a path across anything erosion
    /// severed and leave the dead ends dead.
    ///
    /// Measured on the building: full erosion takes the handrail at (2129.8, 560.6) from
    /// 41 cells to 1, and takes the spiral ramp from five levels to three. The two have to
    /// be separated by something, and this is the only thing that does.
    ///
    /// The restore is a meeting-in-the-middle search. Every surviving cell is a source
    /// labelled with its own piece; the search spreads through the cells erosion removed;
    /// where two labels meet and the pieces differ, the two paths back to their sources
    /// are a bridge, so they are restored and the pieces joined. A dead end is only ever
    /// reached by one label, so nothing about it ever meets anything and it stays gone.
    /// Bridges are widened by one cell afterwards, because a path one cell wide contours
    /// badly and an agent has to fit.
    static int RestoreErodedBridges(RcCompactHeightfield chf, int[] preErode, bool[] indoor)
    {
        int n = chf.spanCount;
        var label = new int[n];         // 0 = unlabelled
        var parent = new int[n];
        Array.Fill(parent, -1);

        // Pieces of what survived.
        var piece = new int[n];
        int pieces = 0;
        var stack = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            if (chf.areas[i] == AreaNull || piece[i] != 0) continue;
            int id = ++pieces;
            stack.Clear();
            stack.Push(i);
            piece[i] = id;
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                Neighbours(cur, (ni, _) =>
                {
                    if (chf.areas[ni] == AreaNull || piece[ni] != 0) return;
                    piece[ni] = id;
                    stack.Push(ni);
                });
            }
        }
        if (pieces < 2) return 0;

        var uf = new int[pieces + 1];
        for (int i = 0; i <= pieces; i++) uf[i] = i;

        // Spread each piece's label through the cells erosion took.
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
            if (chf.areas[i] != AreaNull) { label[i] = piece[i]; queue.Enqueue(i); }
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            Neighbours(cur, (ni, _) =>
            {
                if (label[ni] != 0 || chf.areas[ni] != AreaNull) return;
                if (preErode[ni] == AreaNull || !indoor[ni]) return;
                label[ni] = label[cur];
                parent[ni] = cur;
                queue.Enqueue(ni);
            });
        }

        // Where two labels touch and their pieces differ, walk both paths home.
        var restored = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (label[i] == 0) continue;
            int a = Find(label[i]);
            Neighbours(i, (ni, _) =>
            {
                if (label[ni] == 0) return;
                int b = Find(label[ni]);
                if (a == b) return;
                var first=Path(i);var second=Path(ni);
                if(first==null||second==null)return;
                uf[b] = a;
                foreach(int at in first.Concat(second))
                    if(chf.areas[at]==AreaNull){chf.areas[at]=preErode[at];restored.Add(at);}
                a = Find(label[i]);
            });
        }

        // One cell of width, so the bridge can be contoured and stood on. A path one cell
        // wide often does not survive contouring at all, which is a ramp with a hole in
        // it - reported at (2109.7, 536.7).
        //
        // This was off for a while because widening brought the railings back. It no
        // longer can: a path that is mostly over floor is now rejected whole, so the only
        // paths left to widen are ways through.
        foreach (int i in restored.ToList())
            Neighbours(i, (ni, _) =>
            {
                if (chf.areas[ni] != AreaNull || preErode[ni] == AreaNull || !indoor[ni]) return;
                chf.areas[ni] = preErode[ni];
                restored.Add(ni);
            });

        return restored.Count;

        // A bridge is restored unless the bridge ITSELF is a railing. Without this a
        // handrail running between two landings reads as a connector - it does join them
        // - and comes straight back.
        //
        // The test is on the whole path, not on each cell. A ramp passes over the floor
        // near the bottom of its run, so refusing cells one at a time breaks the ramp
        // exactly where it matters while a handrail, which is over floor along its entire
        // length, loses only the odd cell. Most of a railing is over floor; only the end
        // of a ramp is.
        List<int>? Path(int from)
        {
            var path = new List<int>();
            for (int at = from; at >= 0; at = parent[at])
            {
                if (chf.areas[at] != AreaNull) break;
                path.Add(at);
            }
            int over = path.Count(OverFloor);
            return over*2>path.Count?null:path;
        }

        bool OverFloor(int span)
        {
            int sx = CellXOf(chf, span), sz = CellZOf(chf, span);
            float z = chf.bmin.Y + chf.spans[span].y * chf.ch;
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int nx = sx + dx, nz = sz + dz;
                    if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) continue;
                    ref var nc = ref chf.cells[nx + nz * chf.width];
                    for (int j = nc.index; j < nc.index + nc.count; j++)
                    {
                        if (preErode[j] == AreaNull || j == span) continue;
                        float drop = z - (chf.bmin.Y + chf.spans[j].y * chf.ch);
                        if (drop >= 0.35f && drop <= 8f) return true;
                    }
                }
            return false;
        }

        int Find(int x) { while (uf[x] != x) x = uf[x] = uf[uf[x]]; return x; }

        void Neighbours(int span, Action<int, int> f)
        {
            int sx = CellXOf(chf, span), sz = CellZOf(chf, span);
            for (int dir = 0; dir < 4; dir++)
            {
                int con = (chf.spans[span].con >> (dir * 6)) & 0x3f;
                if (con == 0x3f) continue;
                int nx = sx + DirX[dir], nz = sz + DirY[dir];
                if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) continue;
                int ni = chf.cells[nx + nz * chf.width].index + con;
                if (ni >= 0 && ni < chf.spanCount) f(ni, dir);
            }
        }
    }

    /// Recast's chamfer distance to the edge of the walkable area: 2 per orthogonal step,
    /// 3 per diagonal. Shared by everything here that has to ask how wide something is.
    static byte[] ChamferDistance(RcCompactHeightfield chf)
    {
        var dist = new byte[chf.spanCount];
        Array.Fill(dist, (byte)0xff);
        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i = c.index; i < c.index + c.count; i++)
                {
                    if (chf.areas[i] == AreaNull) { dist[i] = 0; continue; }
                    int open = 0;
                    for (int dir = 0; dir < 4; dir++)
                    {
                        int con = (chf.spans[i].con >> (dir * 6)) & 0x3f;
                        if (con == 0x3f) break;
                        int nx = cx + DirX[dir], nz = cz + DirY[dir];
                        if (chf.areas[chf.cells[nx + nz * chf.width].index + con] == AreaNull) break;
                        open++;
                    }
                    if (open != 4) dist[i] = 0;
                }
            }
        for (int pass = 0; pass < 2; pass++)
            for (int zi = 0; zi < chf.height; zi++)
                for (int xi = 0; xi < chf.width; xi++)
                {
                    int cz = pass == 0 ? zi : chf.height - 1 - zi;
                    int cx = pass == 0 ? xi : chf.width - 1 - xi;
                    ref var c = ref chf.cells[cx + cz * chf.width];
                    for (int i = c.index; i < c.index + c.count; i++)
                        foreach (int dir in pass == 0 ? new[] { 0, 3 } : [2, 1])
                        {
                            int con = (chf.spans[i].con >> (dir * 6)) & 0x3f;
                            if (con == 0x3f) continue;
                            int nx = cx + DirX[dir], nz = cz + DirY[dir];
                            int ni = chf.cells[nx + nz * chf.width].index + con;
                            if (dist[ni] + 2 < dist[i]) dist[i] = (byte)(dist[ni] + 2);
                            int side = pass == 0 ? (dir + 1) & 0x3 : (dir + 3) & 0x3;
                            int con2 = (chf.spans[ni].con >> (side * 6)) & 0x3f;
                            if (con2 == 0x3f) continue;
                            int mi = chf.cells[nx + DirX[side] + (nz + DirY[side]) * chf.width].index + con2;
                            if (dist[mi] + 3 < dist[i]) dist[i] = (byte)(dist[mi] + 3);
                        }
                }
        return dist;
    }

    /// Drop a railing: a narrow, level patch of walkable cells standing over floor.
    ///
    /// The measure that finally works is the connected PATCH, not the polygon and not the
    /// distance to a boundary - both of those are small for the edge band of any floor,
    /// which is why five earlier attempts either did nothing or ate the rooms. A patch is
    /// what you could walk across without climbing, so the flood refuses to cross a step
    /// bigger than `stepBreak`: that separates a railing cap from the floor it is bolted
    /// to, which is the thing every previous attempt failed to do, because the compact
    /// heightfield links them whenever the cap is climbable and a railing is exactly
    /// climbable.
    ///
    /// Then a railing is the conjunction of three things, none of which means anything
    /// alone: the patch is narrower than the agent, it is level along its length, and
    /// there is floor a short step below it. A ramp fails the second, a floor the first,
    /// a balcony over a hall the third.
    /// Stop indoor cells linking to things a step too high, so nothing spans from a floor
    /// onto what is standing on it.
    ///
    /// This is the cause behind both the railings and the furniture, and every rule before
    /// it was chasing the symptom. The compact heightfield joins two neighbouring cells
    /// whenever their tops are within `AgentMaxClimb`, which is 1.0667 yd - and a railing
    /// cap, a bench top and a window sill are all about that far above the floor. Once
    /// joined they are one region, so one contour, so one polygon whose detail surface
    /// interpolates from the floor up onto the thing: the bridge of mesh running up the
    /// object to the wall, and the fins climbing the railing. Deleting the triangles
    /// afterwards is deleting evidence.
    ///
    /// Severing the link instead leaves the cap as a region of its own, which the patch
    /// rules can then see and drop, and leaves no polygon spanning the two heights, so
    /// there is no ramp to build in the first place. Measured against the climb itself:
    /// taking `AgentMaxClimb` down to 0.8 halves the steep triangles at the railing, 14 to
    /// 7, which is what named the cause - but the climb is what the agent can step up
    /// everywhere, outdoors included, and it is not the thing that should move.
    ///
    /// Stairs are unaffected: a riser is 0.3 to 0.5 yd and this cuts at 0.8.
    static int SeverIndoorSteps(RcCompactHeightfield chf, bool[] indoor, float maxStep)
    {
        if (maxStep <= 0) return 0;

        // Both ends, always.
        //
        // Cutting one direction and leaving the other makes the neighbour graph directed,
        // and nothing downstream expects that. Recast's contour walk reads an edge where
        // this cell says there is none and the neighbour still says there is, and comes
        // back with "Multiple outlines for region N" over a region that a one-directional
        // flood - including the check written to catch exactly this - calls perfectly
        // connected. On tile [27,29] that failure dropped the tile through the whole
        // fallback chain to the plain unsplit build and its polygons ran to 207 yd.
        var pairs = new List<(int A, int Dir, int B, int Back)>();
        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i = c.index; i < c.index + c.count; i++)
                {
                    if (chf.areas[i] == AreaNull || !indoor[i]) continue;
                    float z = chf.bmin.Y + chf.spans[i].y * chf.ch;
                    for (int dir = 0; dir < 4; dir++)
                    {
                        int con = (chf.spans[i].con >> (dir * 6)) & 0x3f;
                        if (con == 0x3f) continue;
                        int nx = cx + DirX[dir], nz = cz + DirY[dir];
                        if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) continue;
                        int ni = chf.cells[nx + nz * chf.width].index + con;
                        if (ni < 0 || ni >= chf.spanCount) continue;
                        if (MathF.Abs(chf.bmin.Y + chf.spans[ni].y * chf.ch - z) <= maxStep) continue;
                        pairs.Add((i, dir, ni, (dir + 2) & 0x3));
                    }
                }
            }

        foreach (var (a2, dir, b2, back) in pairs)
        {
            Cut(a2, dir);
            if (Neighbour(b2, back) == a2) Cut(b2, back);
        }
        return pairs.Count;

        void Cut(int span, int dir)
        {
            var rb = RcCompactSpanBuilder.NewBuilder(ref chf.spans[span]);
            rb.con = rb.con | (0x3f << (dir * 6));
            chf.spans[span] = rb.Build();
        }

        int Neighbour(int span, int dir)
        {
            int con = (chf.spans[span].con >> (dir * 6)) & 0x3f;
            if (con == 0x3f) return -1;
            int nx = CellXOf(chf, span) + DirX[dir], nz = CellZOf(chf, span) + DirY[dir];
            if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) return -1;
            int ni = chf.cells[nx + nz * chf.width].index + con;
            return ni >= 0 && ni < chf.spanCount ? ni : -1;
        }
    }

    /// Drop every neighbour link whose other end does not point back.
    ///
    /// A span's con field holds an INDEX into the neighbouring column, not an edge, so
    /// "A links to B" does not imply "B links to A" - with stacked spans B's back link can
    /// resolve to a different span in A's column. Recast's contour walk assumes it can step
    /// out along a link and step back in along the reverse, and a one-way link makes it trace
    /// a second outline over a region everything else agrees is one connected piece, which
    /// surfaces as "Multiple outlines for region N". Cheap to enforce, and it only removes
    /// links that were already lying about the neighbourhood.
    static int SymmetriseLinks(RcCompactHeightfield chf)
    {
        var con = new int[chf.spanCount];
        for (int i = 0; i < chf.spanCount; i++) con[i] = chf.spans[i].con;

        int Neighbour(int span, int dir)
        {
            int c = (con[span] >> (dir * 6)) & 0x3f;
            if (c == 0x3f) return -1;
            int nx = CellXOf(chf, span) + DirX[dir], nz = CellZOf(chf, span) + DirY[dir];
            if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) return -1;
            int ni = chf.cells[nx + nz * chf.width].index + c;
            return ni >= 0 && ni < chf.spanCount ? ni : -1;
        }

        int cut = 0;
        for (int i = 0; i < chf.spanCount; i++)
        {
            int keep = con[i];
            for (int dir = 0; dir < 4; dir++)
            {
                int ni = Neighbour(i, dir);
                if (ni < 0) continue;
                if (Neighbour(ni, (dir + 2) & 0x3) == i) continue;
                keep |= 0x3f << (dir * 6);
                cut++;
            }
            if (keep == con[i]) continue;
            var rb = RcCompactSpanBuilder.NewBuilder(ref chf.spans[i]);
            rb.con = keep;
            chf.spans[i] = rb.Build();
        }
        return cut;
    }


    /// Drop small indoor islands that nothing indoors can reach.
    ///
    /// This is the furniture, and it is the one shape every flat-patch rule here misses.
    /// `DropRailingPatches` spares anything whose cells are not level, and the mesh on a
    /// crate is not level: it climbs the side. The island over the object at
    /// (2124.0, 573.3) runs from z 698 to 705, so a "level patch" gate lets seven yards of
    /// vertical mesh through on the grounds that it is not flat.
    ///
    /// Connectivity answers it directly. The con links already encode what the agent can
    /// climb, so a component of them IS what can be walked onto from inside this tile. A
    /// small one with walkable mesh underneath it is a thing standing on the floor -
    /// furniture, a beam, a railing cap - and the mesh on top of it was never reachable;
    /// it just was not shaped like the rules were looking for.
    ///
    /// Three gates keep it off real geometry. A component touching the tile's edge is not
    /// judged at all, because its other half is in the next tile and this tile cannot see
    /// it. It must be indoors, so an outcrop on a hillside is not furniture. And something
    /// must be walkable below it, so an upper floor reached by a stair that this tile
    /// happens to cut is not mistaken for a crate.
    static int DropIndoorIslands(RcCompactHeightfield chf, bool[] indoor, float maxArea)
    {
        if (maxArea <= 0) return 0;

        int maxCells = (int)MathF.Ceiling(maxArea / (chf.cs * chf.cs));
        var comp = new int[chf.spanCount];
        var stack = new Stack<int>();
        var mine = new List<int>();
        int dropped = 0, id = 0;

        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i0 = c.index; i0 < c.index + c.count; i0++)
                {
                    if (comp[i0] != 0 || chf.areas[i0] == AreaNull) continue;

                    id++;
                    mine.Clear();
                    stack.Clear();
                    stack.Push(i0);
                    comp[i0] = id;
                    bool onEdge = false, anyIndoor = false;
                    bool tooBig = false;

                    while (stack.Count > 0)
                    {
                        int cur = stack.Pop();
                        if (!tooBig) mine.Add(cur);
                        if (mine.Count > maxCells) tooBig = true;
                        int sx = CellXOf(chf, cur), sz = CellZOf(chf, cur);
                        if (sx == 0 || sz == 0 || sx == chf.width - 1 || sz == chf.height - 1)
                            onEdge = true;
                        if (indoor[cur]) anyIndoor = true;

                        for (int dir = 0; dir < 4; dir++)
                        {
                            int con = (chf.spans[cur].con >> (dir * 6)) & 0x3f;
                            if (con == 0x3f) continue;
                            int nx = sx + DirX[dir], nz = sz + DirY[dir];
                            if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) continue;
                            int ni = chf.cells[nx + nz * chf.width].index + con;
                            if (ni < 0 || ni >= chf.spanCount) continue;
                            if (comp[ni] != 0 || chf.areas[ni] == AreaNull) continue;
                            comp[ni] = id;
                            stack.Push(ni);
                        }
                    }

                    if (tooBig || onEdge || !anyIndoor) continue;

                    // Something walkable below, in a component that is not this one.
                    bool overFloor = false;
                    foreach (int i in mine)
                    {
                        if (overFloor) break;
                        int sx = CellXOf(chf, i), sz = CellZOf(chf, i);
                        float z = chf.bmin.Y + chf.spans[i].y * chf.ch;
                        ref var nc = ref chf.cells[sx + sz * chf.width];
                        for (int j = nc.index; j < nc.index + nc.count; j++)
                        {
                            if (chf.areas[j] == AreaNull || comp[j] == id) continue;
                            if (z - (chf.bmin.Y + chf.spans[j].y * chf.ch) > 0.5f) { overFloor = true; break; }
                        }
                    }
                    if (!overFloor) continue;

                    foreach (int i in mine) chf.areas[i] = AreaNull;
                    dropped += mine.Count;
                }
            }

        return dropped;
    }

    static int DropRailingPatches(RcCompactHeightfield chf, bool[] indoor, float maxWidth,
                                  float stepBreak, float minDrop, float maxDrop)
    {
        if (maxWidth <= 0) return 0;

        var patch = new int[chf.spanCount];
        var stack = new Stack<int>();
        var members = new List<List<int>>();
        var edges = new List<int>();

        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i0 = c.index; i0 < c.index + c.count; i0++)
                {
                    if (patch[i0] != 0 || chf.areas[i0] == AreaNull || !indoor[i0]) continue;

                    int id = members.Count + 1;
                    var mine = new List<int>();
                    int open = 0;
                    members.Add(mine);
                    stack.Clear();
                    stack.Push(i0);
                    patch[i0] = id;

                    while (stack.Count > 0)
                    {
                        int cur = stack.Pop();
                        mine.Add(cur);
                        int sx = CellXOf(chf, cur), sz = CellZOf(chf, cur);
                        float z = chf.bmin.Y + chf.spans[cur].y * chf.ch;
                        for (int dir = 0; dir < 4; dir++)
                        {
                            int con = (chf.spans[cur].con >> (dir * 6)) & 0x3f;
                            if (con == 0x3f) { open++; continue; }
                            int nx = sx + DirX[dir], nz = sz + DirY[dir];
                            if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) { open++; continue; }
                            int ni = chf.cells[nx + nz * chf.width].index + con;
                            if (ni < 0 || ni >= chf.spanCount) { open++; continue; }
                            if (chf.areas[ni] == AreaNull) { open++; continue; }
                            // A step you have to climb is the edge of this patch.
                            float nzh = chf.bmin.Y + chf.spans[ni].y * chf.ch;
                            if (MathF.Abs(nzh - z) > stepBreak) { open++; continue; }
                            if (patch[ni] != 0) continue;
                            patch[ni] = id;
                            stack.Push(ni);
                        }
                    }
                    edges.Add(open);
                }
            }

        int dropped = 0;
        for (int p = 0; p < members.Count; p++)
        {
            var mine = members[p];
            if (edges[p] == 0) continue;
            float area = mine.Count * chf.cs * chf.cs;
            float width = 2f * area / (edges[p] * chf.cs);

            // Narrow OR small. A handrail is narrow; the top of a bench, a crate or a
            // table is not - the one at (2107.1, 548.2) measures 1.2 yd across and 3.7
            // long, so it walks straight past a width gate set at the agent's diameter -
            // but it is only four and a half square yards, and a floor is hundreds. Both
            // are furniture in the sense that matters: a surface the size of a table,
            // sitting on top of one you can already stand on.
            // Small area alone does not make furniture: real stair landings
            // and doorway thresholds can also be a few square yards.
            if (width >= maxWidth) continue;

            float lo = float.MaxValue, hi = float.MinValue;
            foreach (int i in mine)
            {
                float z = chf.bmin.Y + chf.spans[i].y * chf.ch;
                lo = MathF.Min(lo, z); hi = MathF.Max(hi, z);
            }
            if (hi - lo > 0.5f) continue;                 // a ramp goes somewhere

            int supported=0;bool ascending=false,descending=false;
            foreach (int i in mine)
            {
                bool overFloor=false;
                int sx = CellXOf(chf, i), sz = CellZOf(chf, i);
                float z = chf.bmin.Y + chf.spans[i].y * chf.ch;
                for(int dir=0;dir<4;dir++)
                {
                    int con=(chf.spans[i].con>>(dir*6))&0x3f;
                    if(con==0x3f)continue;
                    int nx=sx+DirX[dir],nz=sz+DirY[dir];
                    if(nx<0||nz<0||nx>=chf.width||nz>=chf.height)continue;
                    int ni=chf.cells[nx+nz*chf.width].index+con;
                    if(chf.areas[ni]==AreaNull||patch[ni]==p+1)continue;
                    float delta=(chf.spans[ni].y-chf.spans[i].y)*chf.ch;
                    if(delta>stepBreak)ascending=true;
                    if(delta < -stepBreak)descending=true;
                }
                for (int dz = -2; dz <= 2 && !overFloor; dz++)
                    for (int dx = -2; dx <= 2 && !overFloor; dx++)
                    {
                        int nx = sx + dx, nz = sz + dz;
                        if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) continue;
                        ref var nc = ref chf.cells[nx + nz * chf.width];
                        for (int j = nc.index; j < nc.index + nc.count; j++)
                        {
                            if (chf.areas[j] == AreaNull || patch[j] == p + 1) continue;
                            float drop = z - (chf.bmin.Y + chf.spans[j].y * chf.ch);
                            if (drop >= minDrop && drop <= maxDrop) { overFloor = true; break; }
                        }
                    }
                if(overFloor)supported++;
            }
            // Treads have a continuation up and down. A single nearby lower
            // cell is not enough evidence to delete an entire passage.
            if((ascending&&descending)||supported*4<mine.Count*3)continue;

            foreach (int i in mine) chf.areas[i] = AreaNull;
            dropped += mine.Count;
        }
        return dropped;
    }

    /// Give every disconnected piece of a region its own id.
    ///
    /// `BuildContours` traces one outline per region and refuses a region that is in two
    /// places at once - "Multiple outlines for region N" - and the whole tile's build fails
    /// with it, falling through the chain to the plain unsplit build, whose polygons run to
    /// 207 yd. Regions come out of the watershed connected, but they do not stay that way:
    /// the small-region merging folds a fragment into a neighbour it does not touch, and
    /// severing indoor steps makes many more fragments for it to fold.
    ///
    /// Splitting them back apart is cheap and removes a whole class of tile failure.
    /// Check, right before contouring, the two things BuildContours needs of a region:
    /// that it is in one piece, and that it covers each column once. Anything it reports
    /// is a region that will fail, named before Recast names it.
    /// Remove regions too small to contour.
    ///
    /// `BuildRegions` applies `MinRegionArea` and then everything here runs AFTER it -
    /// the grid chop, unwinding helices, splitting disconnected pieces - and each of them
    /// mints new regions with no floor on their size. A region of one or two cells has no
    /// outline worth the name, and `BuildContours` can come back with "Multiple outlines
    /// for region N" over the degenerate result, which fails the tile and drops it through
    /// the whole fallback chain to the plain unsplit build.
    ///
    /// Recast's own minimum is applied at a point these passes never see, so it has to be
    /// applied again after them.
    internal static int RestoreConnectedFloorAreaIslands(RcCompactHeightfield chf)
    {
        // Region filtering can discard a tiny ground island surrounded by road
        // (or the reverse), although the walking surface is fully connected.
        // Recover only spans linked to a retained walking region. Keep their
        // own area label and leave null, blocked and isolated surfaces alone.
        bool Floor(int i)=>chf.areas[i] is AreaGround or AreaRoad;
        int next=chf.maxRegions+1,restored=0;
        var pending=new Queue<(int Span,int Column)>();
        int Neighbour(int i,int column,int d,out int nc){
            int con=(chf.spans[i].con>>(d*6))&63,x=column%chf.width+DirX[d],z=column/chf.width+DirY[d];nc=-1;
            if(con==63||x<0||x>=chf.width||z<0||z>=chf.height)return -1;
            nc=x+z*chf.width;var cell=chf.cells[nc];return con<cell.count?cell.index+con:-1;
        }
        void Recover(int seed,int column){
            if(!Floor(seed)||chf.spans[seed].reg!=0)return;
            if(next>=0x8000)throw new InvalidOperationException("Floor area recovery exhausted region ids");
            int region=next++,area=chf.areas[seed];var queue=new Queue<(int Span,int Column)>();
            void Claim(int i,int c){var b=RcCompactSpanBuilder.NewBuilder(ref chf.spans[i]);b.reg=region;chf.spans[i]=b.Build();restored++;queue.Enqueue((i,c));}
            Claim(seed,column);
            while(queue.TryDequeue(out var at))for(int d=0;d<4;d++){
                int ni=Neighbour(at.Span,at.Column,d,out int nc);if(ni<0||!Floor(ni)||chf.spans[ni].reg!=0)continue;
                if(chf.areas[ni]==area)Claim(ni,nc);else pending.Enqueue((ni,nc));
            }
        }
        for(int column=0;column<chf.cells.Length;column++){
            var c=chf.cells[column];for(int i=c.index;i<c.index+c.count;i++){
                if(!Floor(i)||chf.spans[i].reg!=0)continue;
                for(int d=0;d<4;d++){
                    int ni=Neighbour(i,column,d,out _);if(ni>=0&&Floor(ni)&&chf.spans[ni].reg!=0){Recover(i,column);break;}
                }
                while(pending.TryDequeue(out var at))Recover(at.Span,at.Column);
            }
        }
        if(restored>0)chf.maxRegions=next;
        return restored;
    }

    static int DropTinyRegions(RcCompactHeightfield chf, int minCells)
    {
        if (minCells <= 0) return 0;

        var count = new Dictionary<int, int>();
        for (int i = 0; i < chf.spanCount; i++)
        {
            int reg = chf.spans[i].reg;
            if (reg != 0) count[reg] = count.GetValueOrDefault(reg) + 1;
        }
        var tiny = count.Where(kv => kv.Value < minCells).Select(kv => kv.Key).ToHashSet();
        if (tiny.Count == 0) return 0;

        // Splitting a large surface can leave a tiny boundary fragment. Its
        // size does not make it disposable: deleting it opens a hole in an
        // otherwise connected floor. Only isolated small regions are noise.
        for(int z=0;z<chf.height;z++)for(int x=0;x<chf.width;x++)
        {
            ref var cell=ref chf.cells[x+z*chf.width];
            for(int i=cell.index;i<cell.index+cell.count;i++)
            {
                int region=chf.spans[i].reg;
                if(chf.areas[i]==AreaNull||!tiny.Contains(region))continue;
                for(int d=0;d<4;d++)
                {
                    int con=(chf.spans[i].con>>(d*6))&0x3f;
                    if(con==0x3f)continue;
                    int nx=x+DirX[d],nz=z+DirY[d];
                    if(nx<0||nz<0||nx>=chf.width||nz>=chf.height)continue;
                    int ni=chf.cells[nx+nz*chf.width].index+con;
                    if(chf.areas[ni]!=AreaNull&&chf.spans[ni].reg!=0&&chf.spans[ni].reg!=region)
                    {tiny.Remove(region);break;}
                }
            }
        }

        int dropped = 0;
        for (int i = 0; i < chf.spanCount; i++)
        {
            if (!tiny.Contains(chf.spans[i].reg)) continue;
            var rb = RcCompactSpanBuilder.NewBuilder(ref chf.spans[i]);
            rb.reg = 0;
            chf.spans[i] = rb.Build();
            chf.areas[i] = AreaNull;
            dropped++;
        }
        return dropped;
    }

    static void CheckRegions(RcCompactHeightfield chf, string when)
    {
        var comp = new Dictionary<int, int>();
        var areasOf = new Dictionary<int, HashSet<int>>();
        var cols = new Dictionary<int, HashSet<int>>();
        var twice = new HashSet<int>();
        var seen = new int[chf.spanCount];
        var stack = new Stack<int>();

        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i = c.index; i < c.index + c.count; i++)
                {
                    int reg = chf.spans[i].reg;
                    if (reg == 0) continue;
                    if (!cols.TryGetValue(reg, out var used)) cols[reg] = used = [];
                    if (!used.Add(cx + cz * chf.width)) twice.Add(reg);
                    if (!areasOf.TryGetValue(reg, out var ar)) areasOf[reg] = ar = [];
                    ar.Add(chf.areas[i]);
                    if (seen[i] != 0) continue;

                    // Partition the way RcContours does, on region AND area together.
                    // Its boundary test is neighbour.reg == reg && neighbour.area == area, so a
                    // region holding two area ids traces one outline per area and trips
                    // "Multiple outlines" while every flood that looks at reg alone calls it
                    // a single connected piece.
                    int area = chf.areas[i];
                    comp[reg] = comp.GetValueOrDefault(reg) + 1;
                    stack.Clear();
                    stack.Push(i);
                    seen[i] = 1;
                    while (stack.Count > 0)
                    {
                        int cur = stack.Pop();
                        int sx = CellXOf(chf, cur), sz = CellZOf(chf, cur);
                        for (int dir = 0; dir < 4; dir++)
                        {
                            int con = (chf.spans[cur].con >> (dir * 6)) & 0x3f;
                            if (con == 0x3f) continue;
                            int nx = sx + DirX[dir], nz = sz + DirY[dir];
                            if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) continue;
                            int ni = chf.cells[nx + nz * chf.width].index + con;
                            if (ni < 0 || ni >= chf.spanCount) continue;
                            if (seen[ni] != 0 || chf.spans[ni].reg != reg || chf.areas[ni] != area) continue;
                            seen[ni] = 1;
                            stack.Push(ni);
                        }
                    }
                }
            }

        int split = comp.Count(kv => kv.Value > 1);
        int mixed = areasOf.Count(kv => kv.Value.Count > 1);
        int maxPlain = comp.Keys.Where(r => (r & 0x8000) == 0).DefaultIfEmpty(0).Max();
        int maxAny = comp.Keys.DefaultIfEmpty(0).Max();
        Console.WriteLine($"      {when}: {comp.Count:N0} regions, {split:N0} in more than one piece, " +
                          $"{twice.Count:N0} covering a column twice, {mixed:N0} holding more than one area; " +
                          $"highest id {maxPlain} (with border bit {maxAny}), maxRegions {chf.maxRegions}" +
                          (maxPlain >= chf.maxRegions ? "  <-- id AT OR ABOVE maxRegions" : ""));
        foreach (var kv in comp.Where(k => k.Value > 1).OrderByDescending(k => k.Value).Take(4))
            Console.WriteLine($"         region {kv.Key} is in {kv.Value} pieces, areas " +
                              string.Join('/', areasOf[kv.Key].OrderBy(x => x)));
    }

    static int SplitDisconnectedRegions(RcCompactHeightfield chf)
    {
        const int BorderReg = 0x8000;
        var seen = new int[chf.spanCount];
        var stack = new Stack<int>();
        var extra = new List<(int Span, int Id)>();
        var pieces = new Dictionary<int, int>();          // region -> pieces seen so far
        int next = chf.maxRegions + 1;
        int split = 0;

        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i0 = c.index; i0 < c.index + c.count; i0++)
                {
                    int reg = chf.spans[i0].reg;
                    if (reg == 0 || seen[i0] != 0) continue;

                    int nth = pieces.GetValueOrDefault(reg) + 1;
                    pieces[reg] = nth;
                    // The first piece keeps the id; the rest need new ones.
                    int id = reg;
                    if (nth > 1)
                    {
                        if (next >= BorderReg - 64) return split;
                        id = next++ | (reg & BorderReg);
                        split++;
                    }

                    stack.Clear();
                    stack.Push(i0);
                    seen[i0] = 1;
                    while (stack.Count > 0)
                    {
                        int cur = stack.Pop();
                        if (id != reg) extra.Add((cur, id));
                        int sx = CellXOf(chf, cur), sz = CellZOf(chf, cur);
                        for (int dir = 0; dir < 4; dir++)
                        {
                            int con = (chf.spans[cur].con >> (dir * 6)) & 0x3f;
                            if (con == 0x3f) continue;
                            int nx = sx + DirX[dir], nz = sz + DirY[dir];
                            if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) continue;
                            int ni = chf.cells[nx + nz * chf.width].index + con;
                            if (ni < 0 || ni >= chf.spanCount) continue;
                            if (seen[ni] != 0 || chf.spans[ni].reg != reg) continue;
                            seen[ni] = 1;
                            stack.Push(ni);
                        }
                    }
                }
            }

        foreach (var (span, id) in extra)
        {
            var rb = RcCompactSpanBuilder.NewBuilder(ref chf.spans[span]);
            rb.reg = id;
            chf.spans[span] = rb.Build();
        }
        chf.maxRegions = next;
        return split;
    }

    static int SplitRegionsOverThemselves(RcCompactHeightfield chf)
    {
        const int BorderReg = 0x8000;

        var cols = new Dictionary<int, HashSet<int>>();
        var bad = new HashSet<int>();
        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c0 = ref chf.cells[cx + cz * chf.width];
                for (int i = c0.index; i < c0.index + c0.count; i++)
                {
                    int reg = chf.spans[i].reg;
                    if (reg == 0) continue;
                    if (!cols.TryGetValue(reg, out var used)) cols[reg] = used = [];
                    if (!used.Add(cx + cz * chf.width)) bad.Add(reg);
                }
            }
        if (bad.Count == 0) return 0;

        var assigned = new int[chf.spanCount];
        var members = new Dictionary<int, List<int>>();
        var origin = new Dictionary<int, int>();
        var stack = new Stack<int>();
        int nextPiece = 1;

        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i = c.index; i < c.index + c.count; i++)
                {
                    int reg = chf.spans[i].reg;
                    if (assigned[i] != 0 || !bad.Contains(reg)) continue;

                    int id = nextPiece++;
                    var mine = new List<int>();
                    var taken = new HashSet<int> { cx + cz * chf.width };
                    members[id] = mine;
                    origin[id] = reg;
                    stack.Clear();
                    stack.Push(i);
                    assigned[i] = id;

                    while (stack.Count > 0)
                    {
                        int cur = stack.Pop();
                        mine.Add(cur);
                        int sx = CellXOf(chf, cur), sz = CellZOf(chf, cur);
                        for (int dir = 0; dir < 4; dir++)
                        {
                            int con = (chf.spans[cur].con >> (dir * 6)) & 0x3f;
                            if (con == 0x3f) continue;
                            int nx = sx + DirX[dir], nz = sz + DirY[dir];
                            if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) continue;
                            int ni = chf.cells[nx + nz * chf.width].index + con;
                            if (ni < 0 || ni >= chf.spanCount) continue;
                            if (assigned[ni] != 0 || chf.spans[ni].reg != reg) continue;
                            if (!taken.Add(nx + nz * chf.width)) continue;   // already over it
                            assigned[ni] = id;
                            stack.Push(ni);
                        }
                    }
                }
            }

        int next = chf.maxRegions + 1;
        if (next + members.Count >= BorderReg - 64) return 0;

        foreach (var (piece, mine) in members)
        {
            int id = next++ | (origin[piece] & BorderReg);
            foreach (int cur in mine)
            {
                var rb = RcCompactSpanBuilder.NewBuilder(ref chf.spans[cur]);
                rb.reg = id;
                chf.spans[cur] = rb.Build();
            }
        }
        chf.maxRegions = next;
        return members.Count;
    }

    static int SplitOversizeRegions(RcCompactHeightfield chf, float gridYards)
    {
        if (gridYards <= 0) return 0;

        // Every id the split hands out has to fit under RC_BORDER_REG, and it starts
        // counting from chf.maxRegions - which is the highest id the watershed ever
        // reached, not the number of regions that survived it. Those two are far apart:
        // the watershed floods, merges and abandons ids as it goes, so a tile holding a
        // few thousand regions can easily report maxRegions in the tens of thousands and
        // leave the split almost no room to work in. Renumbering the survivors densely
        // costs one pass and is the difference between splitting a tile and not.
        RenumberRegionsDensely(chf);

        foreach (float scale in new[] { 1f, 1.5f, 2f, 3f, 4f, 6f, 8f })
        {
            int made = SplitAtGrid(chf, gridYards * scale);
            if (made < 0) continue;                   // ids ran out; nothing was written
            if (scale > 1f) SplitCoarsened = gridYards * scale;
            return made;
        }
        OutOfRegionIds = true;
        return 0;
    }

    /// Pack the surviving region ids down to 1..n, keeping each one's border flag, and
    /// set maxRegions to what is actually in use. Contours only ever compare region ids
    /// for equality and test the border bit, so the numbering itself carries no meaning
    /// and this is safe to do between BuildRegions and BuildContours.
    static void RenumberRegionsDensely(RcCompactHeightfield chf)
    {
        const int BorderReg = 0x8000;
        var dense = new Dictionary<int, int>();
        int next = 1;

        for (int i = 0; i < chf.spanCount; i++)
        {
            int reg = chf.spans[i].reg;
            if (reg == 0) continue;
            if (!dense.TryGetValue(reg, out int packed))
                dense[reg] = packed = next++ | (reg & BorderReg);
            if (packed == reg) continue;
            var rb = RcCompactSpanBuilder.NewBuilder(ref chf.spans[i]);
            rb.reg = packed;
            chf.spans[i] = rb.Build();
        }
        chf.maxRegions = next;

    }

    /// One attempt at one grid. -1 means it ran out of region ids, and nothing has been
    /// written in that case, so the caller is free to try again wider.
    static int SplitAtGrid(RcCompactHeightfield chf, float gridYards)
    {
        int grid = Math.Max(1, (int)MathF.Round(gridYards / chf.cs));
        int limit = grid * grid;                      // cells one grid square holds
        const int BorderReg = 0x8000;

        // The chop grid is anchored to the WORLD, not to the tile.
        //
        // Dividing the tile-local cell index by the grid puts the lines at a different
        // offset in every tile, because a tile's heightfield starts at its own bmin and
        // carries a border pad. Two tiles then chop the ground either side of a shared
        // border differently, their contours put vertices at different places along that
        // border, each vertex takes the span height of whatever column it landed in, and
        // where the surface falls near a cell boundary the two sides round opposite ways.
        // That is a step of exactly one cell height between them.
        //
        // Measured at (2133.3, 1008.2): the two tiles' border vertices sat at 1008.27 and
        // 1008.35 along the seam, at 670.9334 and 671.2000 - both on the height lattice,
        // one cell apart - for a visible step of 0.20 to 0.29 yd. Turning the chop off
        // took the same seam to 0.002.
        //
        // Anchoring to WorldOrigin makes neighbouring tiles agree on where the lines are,
        // so the chop stops inventing disagreements at the borders. It is the same rule
        // the X/Z cell grid and the height lattice already follow.
        float originY = chf.bmin.X - WorldOrigin, originX = chf.bmin.Z - WorldOrigin;
        int GridX(int i) => (int)MathF.Floor((originY + (i + 0.5f) * chf.cs) / gridYards);
        int GridZ(int i) => (int)MathF.Floor((originX + (i + 0.5f) * chf.cs) / gridYards);

        // Too big means too WIDE, not just too many cells.
        //
        // Counting cells is counting area, and area misses the shape that matters most.
        // A region 250 yd long and a third of a yard wide holds only about 90 yd2, so it
        // slipped under a 100 yd2 threshold and was never split - and it is exactly the
        // region that produces a polygon 250 yd long, 0.36 yd wide, dropping 51 yd down a
        // slope. Those are the sheets: at (3206, 1266) a fan of them, each one ten yards
        // longer than the last, thinness up to 701:1. Recast produces them on its own; the
        // split is what is supposed to stop them, and by area it never looked.
        var count = new Dictionary<int, int>();
        var box = new Dictionary<int, (int X0, int Z0, int X1, int Z1)>();
        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var cc = ref chf.cells[cx + cz * chf.width];
                for (int i = cc.index; i < cc.index + cc.count; i++)
                {
                    int reg = chf.spans[i].reg;
                    if (reg == 0) continue;
                    count[reg] = count.GetValueOrDefault(reg) + 1;
                    box[reg] = box.TryGetValue(reg, out var b)
                        ? (Math.Min(b.X0, cx), Math.Min(b.Z0, cz), Math.Max(b.X1, cx), Math.Max(b.Z1, cz))
                        : (cx, cz, cx, cz);
                }
            }
        // Wide enough to want cutting, and big enough to survive it. Without the second
        // clause the extent test fires on regions that are long but tiny - a handful of
        // cells strung out - and the pieces are too small to contour, which cost a whole
        // ADT: 29 tiles became 28, the missing one shredded rather than failed.
        const int SurvivesCutting = 4 * Viable;
        var oversize = count.Where(kv => kv.Value > limit
                                      || (kv.Value > SurvivesCutting
                                          && (box[kv.Key].X1 - box[kv.Key].X0 > grid
                                           || box[kv.Key].Z1 - box[kv.Key].Z0 > grid)))
                            .Select(kv => kv.Key).ToHashSet();
        if (oversize.Count == 0) return 0;

        // Work out the pieces first and write them afterwards, so the small ones can be
        // folded back before anything downstream sees them. Emitting them cost a whole
        // grid square of navmesh on two tiles: a one-cell sliver against a grid line has
        // a contour Recast will not build a polygon from, so it is dropped silently, and
        // because the chop is on a grid the hole it leaves is a neat rectangle.
        // Pieces are numbered with plain component indices here, not region ids, and the
        // ids are minted at the very end from whatever survives. That matters: on tile
        // [28,30] an 80 yd grid over 1,350 regions wanted 31,353 pieces, which looks like
        // a tile that cannot be split - but nearly all of them are the lace of one and
        // two-cell fragments that terrain around rocks leaves behind, and every one of
        // them is folded back into a neighbour a few lines below. Spending a region id on
        // a fragment before knowing whether it lives is what made the budget look full.
        var assigned = new int[chf.spanCount];        // 0 = not yet visited
        var members = new Dictionary<int, List<int>>();
        var origin = new Dictionary<int, int>();      // piece -> region it came from
        var stack = new Stack<int>();
        int nextPiece = 1;

        for (int cz = 0; cz < chf.height; cz++)
            for (int cx = 0; cx < chf.width; cx++)
            {
                ref var c = ref chf.cells[cx + cz * chf.width];
                for (int i = c.index; i < c.index + c.count; i++)
                {
                    int reg = chf.spans[i].reg;
                    if (assigned[i] != 0 || !oversize.Contains(reg)) continue;
                    // One connected piece of this region inside one grid square.
                    int gx = GridX(cx), gz = GridZ(cz), id = nextPiece++;
                    var mine = new List<int>();
                    members[id] = mine;
                    origin[id] = reg;
                    stack.Clear();
                    stack.Push(i);
                    assigned[i] = id;
                    while (stack.Count > 0)
                    {
                        int cur = stack.Pop();
                        mine.Add(cur);

                        int sx = CellXOf(chf, cur), sz = CellZOf(chf, cur);
                        for (int dir = 0; dir < 4; dir++)
                        {
                            int con = (chf.spans[cur].con >> (dir * 6)) & 0x3f;
                            if (con == 0x3f) continue;
                            int nx = sx + DirX[dir], nz = sz + DirY[dir];
                            if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) continue;
                            if (GridX(nx) != gx || GridZ(nz) != gz) continue;   // other square
                            int ni = chf.cells[nx + nz * chf.width].index + con;
                            if (ni < 0 || ni >= chf.spanCount) continue;
                            if (assigned[ni] != 0 || chf.spans[ni].reg != reg) continue;
                            assigned[ni] = id;
                            stack.Push(ni);
                        }
                    }
                }
            }

        // Fold any piece too small to survive contouring into a neighbour of the same
        // original region. Repeated, because absorbing one sliver can make the next one
        // viable, and a piece with no neighbour at all simply keeps the id it had.
        for (int pass = 0; pass < 3; pass++)
        {
            bool moved = false;
            foreach (var (id, mine) in members.Where(kv => kv.Value.Count is > 0 and < Viable).ToList())
            {
                int host = 0;
                foreach (int cur in mine)
                {
                    int sx = CellXOf(chf, cur), sz = CellZOf(chf, cur);
                    for (int dir = 0; dir < 4 && host == 0; dir++)
                    {
                        int con = (chf.spans[cur].con >> (dir * 6)) & 0x3f;
                        if (con == 0x3f) continue;
                        int nx = sx + DirX[dir], nz = sz + DirY[dir];
                        if (nx < 0 || nz < 0 || nx >= chf.width || nz >= chf.height) continue;
                        int ni = chf.cells[nx + nz * chf.width].index + con;
                        if (ni < 0 || ni >= chf.spanCount) continue;
                        int other = assigned[ni];
                        if (other == 0 || other == id) continue;
                        if (origin[other] != origin[id]) continue;      // different region
                        if (members[other].Count < Viable) continue;   // also a sliver
                        host = other;
                    }
                    if (host != 0) break;
                }
                if (host == 0) continue;
                foreach (int cur in mine) assigned[cur] = host;
                members[host].AddRange(mine);
                members.Remove(id);
                moved = true;
            }
            if (!moved) break;
        }

        // Only now is it known how many pieces there really are, so only now is it worth
        // asking whether they fit. Ids share their field with RC_BORDER_REG, and one that
        // runs past 0x8000 reads as a border region and the mesh around it goes missing -
        // tile [27,28] lost about 2,500 square yards to exactly that. Nothing has been
        // written yet, so the caller just retries at a wider grid.
        int next = chf.maxRegions + 1;
        if (next + members.Count >= BorderReg - 64)
        {
            return -1;
        }

        foreach (var (piece, mine) in members)
        {
            // Keep the border flag with the piece. Border regions are split too: the
            // tile's border zone leaves a column of walkable cells one cell wide running
            // the whole length of the tile, and skipping it left polygons 250 yd long
            // and 0.36 yd wide - the sheets.
            int id = next++ | (origin[piece] & BorderReg);
            foreach (int cur in mine)
            {
                // RcCompactSpan is a readonly struct; the builder is the way in.
                var rb = RcCompactSpanBuilder.NewBuilder(ref chf.spans[cur]);
                rb.reg = id;
                chf.spans[cur] = rb.Build();
            }
        }

        chf.maxRegions = next;
        return members.Count;
    }

    /// Which column a compact span belongs to. The span array is grouped by cell, so this
    /// is a binary search over the cell starts rather than anything stored per span.
    static int CellXOf(RcCompactHeightfield chf, int span) => CellOf(chf, span) % chf.width;
    static int CellZOf(RcCompactHeightfield chf, int span) => CellOf(chf, span) / chf.width;

    /// This used to be a binary search for the last cell whose index is at or below the
    /// span, which reads as obviously correct and is not: it assumes every cell's index
    /// is a running total, and an empty cell does not carry one. Where a run of empty
    /// cells follows a real one, the search walks past the owner and answers with an
    /// empty cell further along the row. On tile [28,30] it was wrong for 929,393 of
    /// 2,685,389 spans - more than a third.
    ///
    /// Everything that walks neighbours by cell coordinate was reading those answers:
    /// the flood fill in SplitOversizeRegions took a span's position from here, tested
    /// its neighbours against the wrong grid square and refused to cross to them, so
    /// regions that are all but perfectly connected - 17 lonely spans out of 2.7 million
    /// - shattered into 312,821 pieces with a median size of one cell. That looked like
    /// a tile too fragmented to split, the split abandoned it, and unsplit regions are
    /// what produced the 221 yd polygons. FillPinholes was reading the same answers.
    ///
    /// A span-to-cell table costs one pass and one int per span and is simply right.
    /// Spans of the heightfield being built that came from a doodad, keyed by
    /// (column << 20) | smin. Set during rasterisation, read only by the roof test.
    [ThreadStatic] static HashSet<long>? DoodadRoofSpans;

    [ThreadStatic] static RcCompactHeightfield? CellLookupFor;
    [ThreadStatic] static int[]? CellLookup;

    static int CellOf(RcCompactHeightfield chf, int span)
    {
        if (!ReferenceEquals(CellLookupFor, chf) || CellLookup!.Length != chf.spanCount)
        {
            var map = new int[chf.spanCount];
            for (int c = 0; c < chf.cells.Length; c++)
            {
                ref var cc = ref chf.cells[c];
                for (int i = cc.index; i < cc.index + cc.count; i++) map[i] = c;
            }
            CellLookup = map;
            CellLookupFor = chf;
        }
        return CellLookup![span];
    }

    /// Recast's three filters, with the swim floor put back after each one.
    ///
    /// Terrain under water is reachable, full stop. A swimmer is not standing on it, so
    /// nothing the filters are protecting against applies: FilterLedgeSpans exists to stop
    /// an agent walking off a drop it would fall down, and FilterWalkableLowHeightSpans to
    /// stop it wedging under an overhang. Neither is a hazard you can swim into.
    ///
    /// The ledge filter is the one that matters. Relaxing the slope test for submerged faces
    /// let the bank of a lake into the heightfield, and then the ledge filter took most of
    /// it straight back out - a bank steep enough to need the relaxation is, by definition,
    /// dropping more than walkableClimb per cell. That is why slopes under water still came
    /// out with no mesh on them after the slope rule was already fixed.
    ///
    /// Done this way, rather than by reimplementing the build, because RcConfig can simply
    /// be told not to filter: the stages below run in Recast's own order with Recast's own
    /// code, and the only addition is restoring what was marked AreaSwimFloor. Wadeable
    /// ground is NOT restored - shallow water is walked through, so it keeps every rule dry
    /// land has.
    static void FilterKeepingSwimFloor(RcContext ctx, RcConfig cfg, int realClimb, float surfaceMargin,
                                       RcHeightfield solid, LiquidCeiling? ceiling, Result r, IReadOnlySet<long>? nonWalkableWmo=null)
    {
        // The spans themselves, not their indices: the filters rewrite `area` in place and
        // never move or relink a span, so holding the objects is enough to find them again.
        // First, demote any swim floor whose TOP is not actually under the water.
        //
        // Rasterisation is per cell, and a cell is 0.27 yd across. Where a cliff drops into
        // a lake, one cell column holds the whole face: the submerged part and the dry part
        // land in the SAME cell and merge into one span, whose top is the clifftop. Recast
        // then takes that top as the walkable height, so a polygon marked swim floor for
        // its submerged half comes out sitting metres up a dry cliff. Cutting the input
        // triangles at the waterline does not help - both halves still fall in one cell.
        //
        // A span whose top is above the surface is not submerged, whatever is underneath
        // it, so it loses the mark. Null rather than ground: the only reason it was ever
        // considered was being under water, and it is not.
        var swim = new List<RcSpan>();
        int demoted = 0, clamped = 0, noWater = 0;
        for (int i = 0; i < solid.spans.Length; i++)
        {
            int cx = i % solid.width, cz = i / solid.width;
            // ToRc sends world (x, y, z) to (y, z, x), so it comes back this way round.
            float wy = solid.bmin.X + (cx + 0.5f) * solid.cs;
            float wx = solid.bmin.Z + (cz + 0.5f) * solid.cs;
            float? top = ceiling?.SurfaceZ(wx, wy);

            for (var sp = solid.spans[i]; sp != null; sp = sp.next)
            {
                if (sp.area != AreaSwimFloor) continue;
                if (top is null) { sp.area = AreaNull; demoted++; noWater++; continue; }

                // Clamp to the water line rather than throw the span away.
                //
                // Deleting it was the first attempt and it is too blunt. A cell is 0.27 yd
                // across, so on a near-vertical wall a single span covers everything from
                // the lake bed to the clifftop - fifteen yards at 89 degrees - and killing
                // it for topping out above the water kills the submerged part with it. The
                // purple then stopped yards BELOW the waterline, which is the same bug as
                // before wearing the other face.
                //
                // Cut it off at the surface instead. What is left is the rock from the bed
                // up to water level, which is exactly the swim floor, and its top now sits
                // at the waterline where it belongs. Lowering smax cannot collide with the
                // span above it, so the column stays ordered.
                // A cell whose rock breaks the surface is cut down to just under it.
                //
                // Dropping the whole span instead leaves the mesh short of the shore by
                // however much height one cell covers, which on a steep wall is yards -
                // the swim floor stopped 3.5 yd below the waterline for exactly this
                // reason. Cutting is sound rather than a fudge: the rock face genuinely
                // passes through the clamped height somewhere inside the cell, so the
                // surface is being placed ON the rock, just at a different point within
                // the 0.27 yd square than its centre.
                //
                // The margin is the part that matters. Clamped flush to the surface the
                // mesh reaches the shore and then detail vertices wander a few inches over
                // it, which is the one error that is obvious on screen. A margin of about
                // a cell gives that wander somewhere to go.
                float spanTop = solid.bmin.Y + sp.smax * solid.ch;
                if (spanTop > top.Value - surfaceMargin)
                {
                    int lid = (int)MathF.Floor((top.Value - surfaceMargin - solid.bmin.Y) / solid.ch);
                    if (lid <= sp.smin) { sp.area = AreaNull; demoted++; continue; }
                    sp.smax = lid;
                    clamped++;
                }
                swim.Add(sp);
            }
        }
        r.SwimFloorDemoted += demoted;
        r.SwimDemotedNoWater += noWater;
        r.SwimFloorClamped += clamped;

        int Restore()
        {
            int n = 0;
            foreach (var sp in swim)
                if (sp.area != AreaSwimFloor) { sp.area = AreaSwimFloor; n++; }
            return n;
        }

        r.DoodadSpansReblocked += FilterLowObstaclesKeepingBlockedProps(ctx, realClimb, solid, DoodadRoofSpans, nonWalkableWmo);
        r.SwimFloorKept += Restore();
        SolidObserver?.Invoke("low obstacles",solid);
        RcFilters.FilterLedgeSpans(ctx, cfg.WalkableHeight, realClimb, solid);
        r.SwimFloorKept += Restore();
        SolidObserver?.Invoke("ledges",solid);
        RcFilters.FilterWalkableLowHeightSpans(ctx, cfg.WalkableHeight, solid);
        r.SwimFloorKept += Restore();
        SolidObserver?.Invoke("headroom",solid);
        r.SwimFloorSpans += swim.Count;
    }

    // A rejected support below a deck can contaminate a merged span's area:
    // Recast's climb-bounded max(area) merge does not retain the marker's
    // original top. Re-rasterize rejected WMO geometry alone to recover that
    // evidence. Only an independently supported, higher WMO surface may win;
    // a cap at the actual top remains blocked, including coplanar overlaps.
    static int RestoreCoveredWmoSpans(RcContext ctx,RcHeightfield solid,float[] verts,int[] tris,int[] areas,int count,int climb,SourceSurfaceFit source)
    {
        var blockedTris=new List<int>();
        for(int t=0;t<count;t++)if(areas[t]==AreaWmoSolid){blockedTris.Add(tris[t*3]);blockedTris.Add(tris[t*3+1]);blockedTris.Add(tris[t*3+2]);}
        if(blockedTris.Count==0)return 0;
        var blocked=new RcHeightfield(solid.width,solid.height,solid.bmin,solid.bmax,solid.cs,solid.ch,solid.borderSize);
        RcRasterizations.RasterizeTriangles(ctx,verts,blockedTris.ToArray(),Enumerable.Repeat(AreaWmoSolid,blockedTris.Count/3).ToArray(),blockedTris.Count/3,blocked,climb);
        return RestoreCoveredWmoSpans(solid,blocked,source);
    }
    internal static int RestoreCoveredWmoSpans(RcHeightfield solid,RcHeightfield blocked,SourceSurfaceFit source)
    {
        int restored=0;
        for(int ci=0;ci<solid.spans.Length;ci++)for(var span=solid.spans[ci];span!=null;span=span.next){
            if(span.area!=AreaWmoSolid)continue;
            bool buried=false,exposed=false;
            for(var cap=blocked.spans[ci];cap!=null;cap=cap.next){
                if(cap.smax<span.smin||cap.smin>span.smax)continue;
                buried=true;
                // Keep a full voxel of separation: co-quantized/touching
                // obstacle tops are not proof of burial below the floor.
                if(cap.smax>=span.smax-1){exposed=true;break;}
            }
            if(!buried||exposed)continue;
            float x=solid.bmin.Z+(ci/solid.width+.5f)*solid.cs,y=solid.bmin.X+(ci%solid.width+.5f)*solid.cs;
            float top=solid.bmin.Y+span.smax*solid.ch;
            if(!source.HasWmoCellSupport(x,y,top,solid.cs,solid.ch))continue;
            span.area=SurfaceClearance.WmoTop;restored++;
        }
        return restored;
    }

    internal static int FilterLowObstaclesKeepingBlockedProps(RcContext ctx, int climb,
                                                              RcHeightfield solid, IReadOnlySet<long>? blocked, IReadOnlySet<long>? structuralBlocked=null)
    {
        RcFilters.FilterLowHangingWalkableObstacles(ctx, climb, solid);
        // Recast deliberately promotes a short unwalkable span above walkable
        // ground (useful for rough stairs). A prop explicitly blocked by source
        // provenance must not be promoted this way. Filters mutate only area,
        // so the original column/span-bottom identity still names the same solid.
        int restored = 0;
        if (blocked != null)
            foreach (long key in blocked)
                for (var span = solid.spans[(int)(key >> 20)]; span != null; span = span.next)
                    if (span.smin == (int)(key & 0xfffff) && span.area != AreaNull)
                    {
                        span.area = AreaNull;
                        restored++;
                    }
        if(structuralBlocked!=null)
            foreach(long key in structuralBlocked)
                for(var span=solid.spans[(int)(key>>20)];span!=null;span=span.next)
                    if(span.smin==(int)(key&0xfffff))span.area=AreaNull;
        return restored;
    }

    static DtMeshData? ToDetour(RcPolyMesh pmesh, RcPolyMeshDetail? dmesh, Settings s,
                                int tx, int tz, RcVec3f tmin, RcVec3f tmax,
                                LiquidCeiling? ceiling, GroundLevel ground, Result r)
    {
        var flags = new int[pmesh.npolys];
        var centre = new Vector3[pmesh.npolys];
        for (int i = 0; i < pmesh.npolys; i++) centre[i] = PolyCentre(pmesh, i, s);

        // Areas are REMAPPED here rather than passed through, so that "you must swim here"
        // is decided once, on depth, after the geometry is settled. Doing it earlier makes
        // it an input to region building, and Recast then cuts its regions along the line -
        // which showed up as long straight seams across a lake bed that has no such edge.
        // The areas are the rasteriser's, unchanged. What is added here is only the flags
        // that depend on the liquid ABOVE a polygon rather than on the polygon itself.
        var areas = pmesh.areas;

        for (int i = 0; i < pmesh.npolys; i++)
        {
            int area = areas[i];
            flags[i] = FlagOfArea(area);

            // A liquid surface is always swum across. Only reachable when RasterizeLiquid
            // is switched back on; the shipped bake leaves the water out of the mesh.
            if (area is AreaWater or AreaOcean or AreaMagma or AreaSlime)
            { flags[i] |= FlagSwim; r.SwimPolys++; continue; }

            // Out of your depth: standing is not an option, so this is swim floor whether
            // it is the flat bottom of a lake or the bank running down into it. Decided
            // per triangle back at rasterisation, so the polygon boundary already follows
            // the depth contour and there is nothing to re-litigate from a centre point.
            if (area == AreaSwimFloor) { flags[i] |= FlagSwim; r.SwimPolys++; }

            // Mesh inside a mountain is not mesh. It keeps its shape so the viewer can
            // show it, and loses every flag so nothing can path to it.
            //
            // Every corner as well as the centre, because the shape that causes this is a
            // polygon reaching in under a cliff: the centre can still be out in the open
            // while the far edge is twenty yards inside rock. Testing the centre alone
            // caught 89 of the 300 buried triangles on one tile.
            if (s.BuriedMargin > 0 && ground.Any && BuriedPoly(pmesh, i, s, ground))
            {
                areas[i] = AreaBuried;
                flags[i] = 0;
                r.BuriedPolys++;
                continue;
            }

            if (ceiling is not { Any: true } || area is not (AreaGround or AreaSwimFloor or AreaRoad)) continue;

            var mid = centre[i];
            var (under, hazard) = ceiling.Query(mid.X, mid.Y, mid.Z, s.SubmergedSlack);
            if (!under && area != AreaSwimFloor) continue;
            flags[i] |= FlagSubmerged;
            r.SubmergedPolys++;
            if (hazard) { flags[i] |= FlagUnderHazard; r.UnderHazardPolys++; }
        }

        for (int i = 0; i < pmesh.npolys; i++)
            r.PolysByArea[areas[i]] = r.PolysByArea.GetValueOrDefault(areas[i]) + 1;

        var option = new DtNavMeshCreateParams
        {
            verts = pmesh.verts, vertCount = pmesh.nverts,
            polys = pmesh.polys, polyAreas = areas, polyFlags = flags,
            polyCount = pmesh.npolys, nvp = pmesh.nvp,
            detailMeshes = dmesh?.meshes, detailVerts = dmesh?.verts,
            detailVertsCount = dmesh?.nverts ?? 0,
            detailTris = dmesh?.tris, detailTriCount = dmesh?.ntris ?? 0,
            walkableHeight = s.AgentHeight, walkableRadius = s.AgentRadius, walkableClimb = s.AgentMaxClimb,
            bmin = pmesh.bmin, bmax = pmesh.bmax,
            cs = s.CellSize, ch = s.CellHeight,
            tileX = tx, tileZ = tz, tileLayer = 0,
            buildBvTree = true,
        };

        // Attached to the tile holding the link's START, which is how Detour expects them:
        // the other end is resolved against whatever tile holds it once both are loaded, so
        // a link may cross tiles. Recast's X is the world's Y and its Z the world's X, hence
        // ToRc on the way in.
        //
        // Nothing declared means nothing changes - offMeshConCount stays 0, as it has been
        // since this was written.
        var links = s.OffMeshLinks;
        if (links.Count > 0)
        {
            var mine = new List<OffMeshLinks.Link>();
            foreach (var l in links)
            {
                // World bounds of this tile: Recast X is world Y, Recast Z is world X.
                if (l.From.X < tmin.Z || l.From.X >= tmax.Z) continue;
                if (l.From.Y < tmin.X || l.From.Y >= tmax.X) continue;
                mine.Add(l);
            }

            if (mine.Count > 0)
            {
                var verts = new float[mine.Count * 6];
                var rad = new float[mine.Count];
                var dir = new int[mine.Count];
                var areasOf = new int[mine.Count];
                var flagsOf = new int[mine.Count];
                var ids = new int[mine.Count];
                for (int i = 0; i < mine.Count; i++)
                {
                    var a2 = ToRc(mine[i].From);
                    var b2 = ToRc(mine[i].To);
                    verts[i * 6 + 0] = a2.X; verts[i * 6 + 1] = a2.Y; verts[i * 6 + 2] = a2.Z;
                    verts[i * 6 + 3] = b2.X; verts[i * 6 + 4] = b2.Y; verts[i * 6 + 5] = b2.Z;
                    rad[i] = mine[i].Radius;
                    dir[i] = mine[i].Bidirectional ? 1 : 0;
                    areasOf[i] = mine[i].Mode=="walk"?AreaGround:AreaTransport;
                    flagsOf[i] = mine[i].Mode=="walk"?FlagGround:0;
                    ids[i] = mine[i].UserId>0?mine[i].UserId:0x1000+i;
                }
                option.offMeshConVerts = verts;
                option.offMeshConRad = rad;
                option.offMeshConDir = dir;
                option.offMeshConAreas = areasOf;
                option.offMeshConFlags = flagsOf;
                option.offMeshConUserID = ids;
                option.offMeshConCount = mine.Count;
                r.OffMeshLinks += mine.Count;
            }
        }

        try
        {
            return DtNavMeshBuilder.CreateNavMeshData(option)
                ?? throw new InvalidDataException($"Detour rejected tile {tx},{tz} ({pmesh.npolys} polygons).");
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Detour conversion failed for tile {tx},{tz}: {ex.Message}", ex);
        }
    }

    /// Why is there no navmesh here? Runs the rasteriser and each filter in turn over one
    /// tile and reports, along a line through the spot, what every stage did to the ground.
    ///
    /// Every explanation of the shoreline band so far has been inference. This is the only
    /// thing that can settle it: the stages are Recast's own, called in Recast's order, and
    /// the column is sampled after each one.
    public static void BandReport(Extractor.Soup soup, Settings s, Clip clip, float qx, float qy)
    {
        var ctx = new RcContext();
        int nv = soup.Verts.Count + soup.LiquidVerts.Count;
        var verts = new float[nv * 3];
        int w0 = 0;
        foreach (var v in soup.Verts) { var p = ToRc(v); verts[w0++] = p.X; verts[w0++] = p.Y; verts[w0++] = p.Z; }
        foreach (var v in soup.LiquidVerts) { var p = ToRc(v); verts[w0++] = p.X; verts[w0++] = p.Y; verts[w0++] = p.Z; }

        int solidTris = soup.TriCount;
        int liquidTris = s.RasterizeLiquid ? soup.LiquidTris : 0;
        int total = solidTris + liquidTris;
        var tris = new int[total * 3];
        var areas = new int[total];
        for (int i = 0; i < solidTris * 3; i++) tris[i] = soup.Indices[i];
        int liquidBase = soup.Verts.Count;
        for (int i = 0; i < liquidTris * 3; i++) tris[solidTris * 3 + i] = soup.LiquidIndices[i] + liquidBase;

        float slopeCos = MathF.Cos(s.AgentMaxSlope * MathF.PI / 180f);
        int steep = 0;
        for (int t = 0; t < solidTris; t++)
        {
            bool ok = SlopeOk(verts, tris, t, slopeCos);
            areas[t] = ok ? AreaGround : AreaNull;
            if (!ok) steep++;
        }
        for (int t = 0; t < liquidTris; t++) areas[solidTris + t] = AreaOfLiquid(soup.LiquidTriClass[t]);
        Console.WriteLine($"triangles: {solidTris:N0} solid ({steep:N0} too steep at {s.AgentMaxSlope} deg), {liquidTris:N0} liquid");

        // the Detour tile containing the point, built exactly as the bake builds it
        const float tws = AdtSize;
        int tz = (int)MathF.Floor((qx - WorldOrigin) / tws);
        int tx = (int)MathF.Floor((qy - WorldOrigin) / tws);
        float minX = WorldOrigin + tx * tws, minZ = WorldOrigin + tz * tws;

        var bmin = new RcVec3f(float.MaxValue, float.MaxValue, float.MaxValue);
        var bmax = new RcVec3f(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < nv; i++)
        {
            bmin.X = MathF.Min(bmin.X, verts[i * 3]); bmax.X = MathF.Max(bmax.X, verts[i * 3]);
            bmin.Y = MathF.Min(bmin.Y, verts[i * 3 + 1]); bmax.Y = MathF.Max(bmax.Y, verts[i * 3 + 1]);
            bmin.Z = MathF.Min(bmin.Z, verts[i * 3 + 2]); bmax.Z = MathF.Max(bmax.Z, verts[i * 3 + 2]);
        }

        int border = (int)MathF.Ceiling(s.AgentRadius / s.CellSize) + 3;
        float pad = border * s.CellSize;
        var hfMin = new RcVec3f(minX - pad, bmin.Y, minZ - pad);
        var hfMax = new RcVec3f(minX + tws + pad, bmax.Y, minZ + tws + pad);
        int hw = s.TileSizeCells + border * 2, hh = s.TileSizeCells + border * 2;

        var hf = new RcHeightfield(hw, hh, hfMin, hfMax, s.CellSize, s.CellHeight, border);
        RcRasterizations.RasterizeTriangles(ctx, verts, tris, areas, total, hf, (int)MathF.Ceiling(s.AgentMaxClimb / s.CellHeight));

        // A window of cells around the spot, printed as a picture. A single line can run
        // parallel to a shore and show nothing but water, which is exactly what the first
        // attempt did.
        int cx = (int)((qy - hfMin.X) / s.CellSize);        // rc X is wow Y
        int cz = (int)((qx - hfMin.Z) / s.CellSize);        // rc Z is wow X
        const int RadX = 150, RadZ = 150, Step = 10;

        static char Mark(int area) => area == AreaNull ? 'x'
                                    : area == AreaGround ? 'G'
                                    : area is AreaWater or AreaOcean ? 'W'
                                    : area is AreaMagma or AreaSlime ? 'L' : '?';

        void Picture(string label, Func<int, int, char> at)
        {
            Console.WriteLine();
            Console.WriteLine(label);
            for (int dz = -RadZ; dz <= RadZ; dz += Step)
            {
                var sb = new System.Text.StringBuilder("  ");
                for (int dx = -RadX; dx <= RadX; dx += Step) sb.Append(at(cx + dx, cz + dz));
                Console.WriteLine(sb.ToString());
            }
        }

        char Hf(int x, int z)
        {
            if (x < 0 || x >= hf.width || z < 0 || z >= hf.height) return '?';
            RcSpan top = null!;
            for (var sp = hf.spans[x + z * hf.width]; sp != null; sp = sp.next) top = sp;
            return top == null ? '.' : Mark(top.area);
        }

        // The GROUND under the water, which the top-span view hides completely: over a lake
        // every column's top span is the surface sheet, so the floor beneath it - and any
        // unwalkable wall between that floor and the shore - is invisible from above.
        char Floor(int x, int z)
        {
            if (x < 0 || x >= hf.width || z < 0 || z >= hf.height) return '?';
            RcSpan best = null!;
            for (var sp = hf.spans[x + z * hf.width]; sp != null; sp = sp.next)
                if (sp.area is not (AreaWater or AreaOcean or AreaMagma or AreaSlime)) best = sp;
            return best == null ? '.' : Mark(best.area);
        }

        Console.WriteLine();
        Console.WriteLine($"window {RadX * 2 * s.CellSize:F0} x {RadZ * 2 * s.CellSize:F0} yd, " +
                          $"one character per {Step * s.CellSize:F1} yd, centre = the spot.  " +
                          ". none  x null  G ground  W water");

        // A snapshot of the ground height before the filters run, so the depth table below
        // can still ask "how deep was the water here" for a column the filters emptied.
        var raw0 = new RcSpan[hf.spans.Length];
        for (int i = 0; i < hf.spans.Length; i++) raw0[i] = hf.spans[i];

        Picture("rasterised, GROUND under the water:", Floor);

        // Where the LIQUID actually is, from the same soup, so the two can be compared. If
        // the heightfield has no water span where the liquid layer says there is water, the
        // liquid never reached the rasteriser and every conclusion drawn from its absence
        // is about the wrong thing.
        var liqT = LiquidMap.Build(soup, clip);
        Picture("liquid per the .mliq layer (W = wet):", (x, z) =>
        {
            if (x < 0 || x >= hf.width || z < 0 || z >= hf.height) return '?';
            float wy = hfMin.X + (x + 0.5f) * s.CellSize;      // rc X is wow Y
            float wx = hfMin.Z + (z + 0.5f) * s.CellSize;      // rc Z is wow X
            return liqT != null && !float.IsNaN(liqT.SurfaceAt(wx, wy)) ? 'W' : '.';
        });

        var tally = new Dictionary<int, int>();
        int cols = 0, colsWithWater = 0;
        for (int i = 0; i < hf.spans.Length; i++)
        {
            bool any = false, wet = false;
            for (var sp = hf.spans[i]; sp != null; sp = sp.next)
            {
                tally[sp.area] = tally.GetValueOrDefault(sp.area) + 1;
                any = true;
                if (sp.area is AreaWater or AreaOcean or AreaMagma or AreaSlime) wet = true;
            }
            if (any) cols++;
            if (wet) colsWithWater++;
        }
        Console.WriteLine();
        Console.WriteLine($"whole heightfield: {cols:N0} columns with any span, {colsWithWater:N0} with a liquid span");
        Console.WriteLine("  spans by area: " + string.Join("  ",
            tally.OrderByDescending(k => k.Value).Select(k => $"{AreaName(k.Key)}({k.Key})={k.Value:N0}")));
        Console.WriteLine($"  liquid triangles offered to the rasteriser: {liquidTris:N0}");

        Picture("liquid SPANS in the heightfield (W = a water span exists):", (x, z) =>
        {
            if (x < 0 || x >= hf.width || z < 0 || z >= hf.height) return '?';
            for (var sp = hf.spans[x + z * hf.width]; sp != null; sp = sp.next)
                if (sp.area is AreaWater or AreaOcean or AreaMagma or AreaSlime) return 'W';
            return '.';
        });

        int wh = (int)MathF.Ceiling(s.AgentHeight / s.CellHeight);
        int wc = (int)MathF.Ceiling(s.AgentMaxClimb / s.CellHeight);
        RcFilters.FilterLowHangingWalkableObstacles(ctx, wc, hf);
        Picture("1. after FilterLowHangingWalkableObstacles, GROUND:", Floor);
        RcFilters.FilterLedgeSpans(ctx, wh, wc, hf);
        Picture("2. after FilterLedgeSpans, GROUND:", Floor);
        // What the low-height filter is actually seeing: the clear space above each
        // ground span, in cells, before it runs.
        Console.WriteLine();
        Console.WriteLine($"clear space above the ground span, in cells (walkableHeight = {wh} cells " +
                          $"= {wh * s.CellHeight:F2} yd). '#' = nothing above, digits = cells, + = 10 or more");
        for (int dz = -RadZ; dz <= RadZ; dz += Step)
        {
            var sb = new System.Text.StringBuilder("  ");
            for (int dx = -RadX; dx <= RadX; dx += Step)
            {
                int x = cx + dx, z = cz + dz;
                if (x < 0 || x >= hf.width || z < 0 || z >= hf.height) { sb.Append('?'); continue; }
                RcSpan ground = null!;
                for (var sp = hf.spans[x + z * hf.width]; sp != null; sp = sp.next)
                    if (sp.area is not (AreaWater or AreaOcean or AreaMagma or AreaSlime)) ground = sp;
                if (ground == null) { sb.Append('.'); continue; }
                if (ground.next == null) { sb.Append('#'); continue; }
                int gap = ground.next.smin - ground.smax;
                sb.Append(gap >= 10 ? '+' : (char)('0' + gap));
            }
            Console.WriteLine(sb.ToString());
        }

        RcFilters.FilterWalkableLowHeightSpans(ctx, wh, hf);
        Picture("3. after FilterWalkableLowHeightSpans, GROUND:", Floor);

        var chf = RcCompacts.BuildCompactHeightfield(ctx, (int)MathF.Ceiling(s.AgentHeight / s.CellHeight),
                                                     (int)MathF.Ceiling(s.AgentMaxClimb / s.CellHeight), hf);
        char Ch(int x, int z)
        {
            if (x < 0 || x >= chf.width || z < 0 || z >= chf.height) return '?';
            ref var c = ref chf.cells[x + z * chf.width];
            if (c.count == 0) return '.';
            return Mark(chf.areas[c.index + c.count - 1]);
        }

        char CFloor(int x, int z)
        {
            if (x < 0 || x >= chf.width || z < 0 || z >= chf.height) return '?';
            ref var c = ref chf.cells[x + z * chf.width];
            if (c.count == 0) return '.';
            char best = '.';
            for (int i = c.index; i < c.index + c.count; i++)
            {
                int a = chf.areas[i];
                if (a is AreaWater or AreaOcean or AreaMagma or AreaSlime) continue;
                best = Mark(a);
            }
            return best;
        }
        Picture("compact heightfield (top):", Ch);
        Picture("compact heightfield, GROUND under the water:", CFloor);
        RcAreas.ErodeWalkableArea(ctx, (int)MathF.Ceiling(s.AgentRadius / s.CellSize), chf);
        Picture("AFTER EROSION, top:", Ch);
        Picture("AFTER EROSION, GROUND under the water - the band lives here if anywhere:", CFloor);

        // One number for the whole window, so a sweep can be read at a glance instead of
        // eyeballing rows - which is how two earlier sweeps managed to sample rows the band
        // did not cross and conclude nothing was happening.
        int walkable = 0, blank = 0;
        for (int dz = -RadZ; dz <= RadZ; dz++)
            for (int dx = -RadX; dx <= RadX; dx++)
            {
                char c = Ch(cx + dx, cz + dz);
                if (c == 'G' || c == 'W' || c == 'L') walkable++;
                else if (c == '.' || c == 'x') blank++;
            }
        Console.WriteLine();
        Console.WriteLine($"WINDOW AFTER EROSION: walkable {walkable:N0}, not walkable {blank:N0} " +
                          $"({100.0 * blank / MathF.Max(1, walkable + blank):F1}%)");

        // The question the pictures cannot answer: does whether a column survives depend on
        // how DEEP the water over it is? If the ring is the water acting as a ceiling, then
        // columns die in a band of depths and live outside it, and that shows up here as a
        // hole in the middle of the table rather than at one end.
        Console.WriteLine();
        Console.WriteLine($"water depth over each column vs whether it ends up walkable " +
                          $"(walkableHeight = {(int)MathF.Ceiling(s.AgentHeight / s.CellHeight)} cells " +
                          $"= {s.AgentHeight:F2} yd, walkableClimb = " +
                          $"{(int)MathF.Ceiling(s.AgentMaxClimb / s.CellHeight)} cells)");
        Console.WriteLine($"{"depth (yd)",-14} {"columns",9} {"floor ok",9} {"floor GONE",10}");

        var buckets = new (float Lo, float Hi)[]
            { (0, 0.27f), (0.27f, 0.55f), (0.55f, 1.07f), (1.07f, 1.6f),
              (1.6f, 2.13f), (2.13f, 3f), (3f, 6f), (6f, 1e9f) };
        var nCol = new int[buckets.Length];
        var nWalk = new int[buckets.Length];

        for (int dz = -RadZ; dz <= RadZ; dz++)
            for (int dx = -RadX; dx <= RadX; dx++)
            {
                int x = cx + dx, z = cz + dz;
                if (x < 0 || x >= hf.width || z < 0 || z >= hf.height) continue;
                float wy = hfMin.X + (x + 0.5f) * s.CellSize;
                float wx = hfMin.Z + (z + 0.5f) * s.CellSize;
                if (liqT == null) continue;
                float top = liqT.SurfaceAt(wx, wy);
                if (float.IsNaN(top)) continue;              // dry: not part of this question

                // the ground under it, from the raw rasterised field kept aside earlier
                float groundZ = float.NaN;
                for (var sp = raw0[x + z * hf.width]; sp != null; sp = sp.next)
                    if (sp.area is not (AreaWater or AreaOcean or AreaMagma or AreaSlime))
                        groundZ = hfMin.Y + sp.smax * s.CellHeight;
                if (float.IsNaN(groundZ)) continue;

                float depth = top - groundZ;
                int b = 0;
                while (b < buckets.Length - 1 && depth >= buckets[b].Hi) b++;
                nCol[b]++;
                // The GROUND, not whatever is on top. Over water the top span is the water
                // surface, so asking Ch() here answers "is there anything" and always says
                // yes - which hides exactly the thing being looked for.
                if (CFloor(x, z) == 'G') nWalk[b]++;
            }

        for (int b = 0; b < buckets.Length; b++)
        {
            if (nCol[b] == 0) continue;
            string range = buckets[b].Hi > 1e8f ? $"{buckets[b].Lo:F2}+" : $"{buckets[b].Lo:F2}-{buckets[b].Hi:F2}";
            Console.WriteLine($"{range,-14} {nCol[b],9:N0} {nWalk[b],9:N0} {nCol[b] - nWalk[b],9:N0}");
        }
        Console.WriteLine();

        Console.WriteLine($"\nerosion radius {(int)MathF.Ceiling(s.AgentRadius / s.CellSize)} cells " +
                          $"= {MathF.Ceiling(s.AgentRadius / s.CellSize) * s.CellSize:F2} yd, cell {s.CellSize:F3} yd");
    }

    /// Polygon centre in world space (Z-up). pmesh vertices are cell indices from bmin.
    /// Is any part of this polygon inside the ground?
    ///
    /// Its corners and its centre. A polygon is convex, so if none of those is buried the
    /// only way its interior can be is for the ground to bulge up through the middle of
    /// it, which is the detail mesh's business rather than a misplaced polygon.
    static bool BuriedPoly(RcPolyMesh pmesh, int poly, Settings s, GroundLevel ground)
    {
        int nvp = pmesh.nvp;
        float sx = 0, sy = 0, sz = 0;
        int n = 0;
        for (int j = 0; j < nvp; j++)
        {
            int v = pmesh.polys[poly * nvp * 2 + j];
            if (v == 0xffff) break;
            float rx = pmesh.bmin.X + pmesh.verts[v * 3] * s.CellSize;
            float ry = pmesh.bmin.Y + pmesh.verts[v * 3 + 1] * s.CellHeight;
            float rz = pmesh.bmin.Z + pmesh.verts[v * 3 + 2] * s.CellSize;
            sx += rx; sy += ry; sz += rz; n++;

            var w = FromRc(rx, ry, rz);
            if (ground.Buried(w.X, w.Y, w.Z, s.BuriedMargin)) return true;
        }
        if (n == 0) return false;
        var mid = FromRc(sx / n, sy / n, sz / n);
        return ground.Buried(mid.X, mid.Y, mid.Z, s.BuriedMargin);
    }

    static Vector3 PolyCentre(RcPolyMesh pmesh, int poly, Settings s)
    {
        int nvp = pmesh.nvp;
        float x = 0, y = 0, z = 0; int n = 0;
        for (int j = 0; j < nvp; j++)
        {
            int v = pmesh.polys[poly * nvp * 2 + j];
            if (v == 0xffff) break;                       // RC_MESH_NULL_IDX
            x += pmesh.bmin.X + pmesh.verts[v * 3] * s.CellSize;
            y += pmesh.bmin.Y + pmesh.verts[v * 3 + 1] * s.CellHeight;
            z += pmesh.bmin.Z + pmesh.verts[v * 3 + 2] * s.CellSize;
            n++;
        }
        return n == 0 ? Vector3.Zero : FromRc(x / n, y / n, z / n);
    }

    /// The same slope test as SlopeOk, on world-space corners. World Z is up, and the
    /// world-to-Recast swap is a cyclic permutation, so the normal's Z here is the normal's
    /// Y there and the comparison is identical.
    static bool SlopeOkWorld(Vector3 a, Vector3 b, Vector3 c, float slopeCos)
    {
        var n = Vector3.Cross(b - a, c - a);
        float len = n.Length();
        return len >= 1e-9f && n.Z / len > slopeCos;
    }

    /// Cut a triangle along a horizontal plane, keeping the winding.
    ///
    /// The odd corner out is rotated to the front, which turns four cases into two and
    /// keeps every piece wound the same way as the original - a flipped piece would face
    /// downwards and the slope test would then reject the ground it came from.
    static void ClipAtZ(Vector3 a, Vector3 b, Vector3 c, float z,
                        List<(Vector3 A, Vector3 B, Vector3 C)> below,
                        List<(Vector3 A, Vector3 B, Vector3 C)> above)
    {
        Span<Vector3> v = [a, b, c];
        int nBelow = 0;
        for (int i = 0; i < 3; i++) if (v[i].Z < z) nBelow++;
        if (nBelow is 0 or 3) return;

        int k = 0;
        for (int i = 0; i < 3; i++)
            if (nBelow == 1 ? v[i].Z < z : v[i].Z >= z) k = i;

        Vector3 p0 = v[k], p1 = v[(k + 1) % 3], p2 = v[(k + 2) % 3];
        Vector3 i01 = AtZ(p0, p1, z), i02 = AtZ(p0, p2, z);

        if (nBelow == 1)
        {
            below.Add((p0, i01, i02));
            above.Add((i01, p1, p2));
            above.Add((i01, p2, i02));
        }
        else
        {
            above.Add((p0, i01, i02));
            below.Add((i01, p1, p2));
            below.Add((i01, p2, i02));
        }
    }

    static Vector3 AtZ(Vector3 p, Vector3 q, float z)
    {
        float d = q.Z - p.Z;
        return MathF.Abs(d) < 1e-9f ? p : Vector3.Lerp(p, q, (z - p.Z) / d);
    }

    static bool SlopeOk(float[] verts, int[] tris, int t, float slopeCos)
    {
        int a = tris[t * 3] * 3, b = tris[t * 3 + 1] * 3, c = tris[t * 3 + 2] * 3;
        float e0x = verts[b] - verts[a], e0y = verts[b + 1] - verts[a + 1], e0z = verts[b + 2] - verts[a + 2];
        float e1x = verts[c] - verts[a], e1y = verts[c + 1] - verts[a + 1], e1z = verts[c + 2] - verts[a + 2];
        float nx = e0y * e1z - e0z * e1y, ny = e0z * e1x - e0x * e1z, nz = e0x * e1y - e0y * e1x;
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-9f) return false;
        return ny / len > slopeCos;     // Y is up inside Recast
    }

    static int EstimateBytes(DtMeshData d) =>
        (d.verts?.Length ?? 0) * 4 + (d.polys?.Length ?? 0) * 32 +
        (d.detailVerts?.Length ?? 0) * 4 + (d.detailTris?.Length ?? 0) +
        (d.bvTree?.Length ?? 0) * 16;

    static void AppendDebug(DtMeshData data, Result r)
    {
        for (int p = 0; p < data.header.polyCount; p++)
        {
            var poly = data.polys[p];
            if (poly.GetPolyType() != 0) continue;
            void Vertex(int index, int detailBase)
            {
                bool hull = index < poly.vertCount;
                var verts = hull ? data.verts : data.detailVerts;
                int at = (hull ? poly.verts[index] : detailBase + index - poly.vertCount) * 3;
                r.DebugVerts.Add(FromRc(verts[at], verts[at + 1], verts[at + 2]));
            }
            void Triangle(int a, int b, int c, int detailBase)
            {
                int first = r.DebugVerts.Count;
                Vertex(a, detailBase); Vertex(b, detailBase); Vertex(c, detailBase);
                r.DebugIndices.Add(first); r.DebugIndices.Add(first + 1); r.DebugIndices.Add(first + 2);
                r.DebugAreas.Add(poly.GetArea());
            }
            if (data.detailMeshes == null || p >= data.detailMeshes.Length)
            {
                for (int i = 2; i < poly.vertCount; i++) Triangle(0, i - 1, i, 0);
                continue;
            }
            var detail = data.detailMeshes[p];
            for (int i = 0; i < detail.triCount; i++)
            {
                int at = (detail.triBase + i) * 4;
                Triangle(data.detailTris[at], data.detailTris[at + 1], data.detailTris[at + 2], detail.vertBase);
            }
        }
    }

    /// Highest liquid surface over each patch of world, and whether it is a hazard.
    ///
    /// Liquid in the client files is a surface, never a volume, so nothing in the geometry
    /// says "this floor is underwater" — the seabed rasterizes as plain walkable ground.
    /// This is the only thing that can tell them apart afterwards.
    /// Exposed for the wall probe, which asks what the raw terrain looks like.
    public static LiquidCeiling CeilingFor(Extractor.Soup soup) => new(soup);


    /// The ADT heightmap, and whatever solid sits above it, sampled on a grid.
    ///
    /// Recast will happily put a polygon under a cliff. The contour of a region at the
    /// cliff's foot is simplified, the polygon built from it reaches in under the rock, and
    /// BuildPolyMeshDetail then fills that overhang with detail triangles whose heights come
    /// from wherever the height patch has data. Measured on Zephras Isle that leaves a few
    /// hundred triangles per ADT below the ground - one of them 93 yd below it - and they
    /// are not harmless: they are ordinary walkable polygons, so a consumer will happily
    /// route onto mesh buried inside a mountain.
    ///
    /// This is baseline Recast, not something the swim work introduced; a plain
    /// RcBuilder.Build over the same tile produces the identical set.
    ///
    /// Below the heightmap is nowhere. Below the heightmap but under a WMO, a doodad or a
    /// cave mouth is somewhere real, and on one tile alone that is 1,573 legitimate
    /// triangles, so the two have to be told apart rather than both discarded. Hence two
    /// grids: the terrain's top, and the highest non-terrain surface over it.
    public sealed class GroundLevel
    {
        const float Cell = 1f;
        readonly Dictionary<(int, int), float> _terrain = [];
        readonly Dictionary<(int, int), float> _cover = [];

        public bool Any => _terrain.Count > 0;

        public GroundLevel(Extractor.Soup soup)
        {
            for (int t = 0; t < soup.TriCount; t++)
            {
                var a = soup.Verts[soup.Indices[t * 3]];
                var b = soup.Verts[soup.Indices[t * 3 + 1]];
                var c = soup.Verts[soup.Indices[t * 3 + 2]];
                bool terrain = t < soup.TriTerrain.Count && soup.TriTerrain[t];
                var grid = terrain ? _terrain : _cover;
                float zlo = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
                float zhi = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));

                // The plane, evaluated per cell - NOT the triangle's highest corner.
                //
                // Taking the corner and smearing it over the footprint reads a steep
                // triangle as a wall of its own top height, so ground on a hillside looks
                // buried under itself. At the first attempt that disowned a quarter of a
                // tile.
                var n = Vector3.Cross(b - a, c - a);
                bool flat = MathF.Abs(n.Z) < 1e-6f;

                int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Cell);
                int x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Cell);
                int y0 = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) / Cell);
                int y1 = (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) / Cell);

                // A single triangle spanning half a tile is a WMO wall seen edge-on; it
                // would smear its top height over everything it shadows.
                if ((x1 - x0) > 64 || (y1 - y0) > 64) continue;

                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                    {
                        float px = (x + 0.5f) * Cell, py = (y + 0.5f) * Cell;
                        float z = flat ? zhi
                            : Math.Clamp(a.Z - (n.X * (px - a.X) + n.Y * (py - a.Y)) / n.Z, zlo, zhi);
                        var key = (x, y);
                        if (!grid.TryGetValue(key, out float cur) || z > cur) grid[key] = z;
                    }
            }
        }

        /// Is this point buried: under the terrain, with nothing solid between it and the
        /// sky to justify being there?
        ///
        /// The cover test is deliberately generous - any non-terrain surface higher than
        /// the point counts as a roof, without checking it is directly overhead. Throwing
        /// away a real cave floor is far worse than keeping a few buried triangles, so
        /// where the answer is unclear the polygon stays.
        /// The terrain's height at a position, or null off the terrain.
        public float? TerrainZ(float worldX, float worldY) =>
            _terrain.TryGetValue(((int)MathF.Floor(worldX / Cell), (int)MathF.Floor(worldY / Cell)), out float z)
                ? z : null;

        public bool Buried(float worldX, float worldY, float worldZ, float margin)
        {
            var key = ((int)MathF.Floor(worldX / Cell), (int)MathF.Floor(worldY / Cell));
            if (!_terrain.TryGetValue(key, out float ground)) return false;
            if (worldZ >= ground - margin) return false;
            return !_cover.TryGetValue(key, out float roof) || roof <= worldZ + 0.1f;
        }
    }

    public sealed class LiquidCeiling
    {
        /// Half a yard, not two. This grid decides which floor is swim floor, and with the
        /// bounding-box fill replaced by a containment test the cell size IS the error: at
        /// 2 yd the kept region still overhangs the sheet by up to a yard on every side.
        const float Cell = 0.5f;
        readonly Dictionary<(int, int), (float Z, bool Hazard)> _top = [];

        public bool Any => _top.Count > 0;

        public LiquidCeiling(Extractor.Soup soup)
        {
            for (int t = 0; t < soup.LiquidIndices.Count / 3; t++)
            {
                var a = soup.LiquidVerts[soup.LiquidIndices[t * 3]];
                var b = soup.LiquidVerts[soup.LiquidIndices[t * 3 + 1]];
                var c = soup.LiquidVerts[soup.LiquidIndices[t * 3 + 2]];
                var cls = t < soup.LiquidTriClass.Count ? soup.LiquidTriClass[t] : LiquidClass.Water;
                bool hazard = cls is LiquidClass.Magma or LiquidClass.Slime;
                float z = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));

                // liquid quads are ~4 yd across, so a triangle covers a handful of cells
                int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Cell);
                int x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Cell);
                int y0 = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) / Cell);
                int y1 = (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) / Cell);

                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                    {
                        if (!CoversCentre((x + 0.5f) * Cell, (y + 0.5f) * Cell, a, b, c)) continue;
                        var key = (x, y);
                        if (!_top.TryGetValue(key, out var cur) || z > cur.Z) _top[key] = (z, hazard);
                        else if (hazard && MathF.Abs(z - cur.Z) < 1f) _top[key] = (cur.Z, true);
                    }
            }
        }


        /// Is the cell centre actually inside the triangle, seen from above?
        ///
        /// Both liquid grids used to fill each triangle's axis-aligned bounding box. A liquid
        /// triangle is half of a quad, so its box is the WHOLE quad - and a WMO sheet is often
        /// rotated, which makes the box larger than the quad again. That is the water spreading
        /// past its pool: measured at (2167.7, 1009.2) the sheet reads 11.5 yd square over a
        /// basin a few yards across, and it is not the file - the file's tile flags are right.
        static bool CoversCentre(float px, float py, Vector3 a, Vector3 b, Vector3 c)
        {
            float d1 = (px - b.X) * (a.Y - b.Y) - (a.X - b.X) * (py - b.Y);
            float d2 = (px - c.X) * (b.Y - c.Y) - (b.X - c.X) * (py - c.Y);
            float d3 = (px - a.X) * (c.Y - a.Y) - (c.X - a.X) * (py - a.Y);
            return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
        }

        /// Is a point below the liquid over it, and is that liquid lethal?
        public (bool Under, bool Hazard) Query(float worldX, float worldY, float worldZ,
                                               float slack)
        {
            var key = ((int)MathF.Floor(worldX / Cell), (int)MathF.Floor(worldY / Cell));
            if (!_top.TryGetValue(key, out var top)) return (false, false);
            return worldZ < top.Z - slack ? (true, top.Hazard) : (false, false);
        }

        /// The liquid surface over a spot, or null where there is none. Needed to measure
        /// how deep a floor lies, which is what decides swimming.
        public float? SurfaceZ(float worldX, float worldY)
        {
            var key = ((int)MathF.Floor(worldX / Cell), (int)MathF.Floor(worldY / Cell));
            return _top.TryGetValue(key, out var top) ? top.Z : null;
        }
    }

    /// Bounds, convex volumes and off-mesh links. Rasterization is done by hand above
    /// so that each triangle keeps its surface class.
    sealed class SoupGeom(float[] verts, int[] tris, RcVec3f bmin, RcVec3f bmax) : IRcInputGeomProvider
    {
        // Built lazily: RcTriMesh constructs a chunky spatial index, which on a dense
        // ADT is millions of triangles of work we no longer need now that rasterization
        // is a single pass.
        RcTriMesh? _mesh;
        readonly List<RcConvexVolume> _volumes = [];
        readonly List<RcOffMeshConnection> _offMesh = [];

        public RcTriMesh GetMesh() => _mesh ??= new RcTriMesh(verts, tris);
        public RcVec3f GetMeshBoundsMin() => bmin;
        public RcVec3f GetMeshBoundsMax() => bmax;
        public IEnumerable<RcTriMesh> Meshes() => [GetMesh()];
        public IList<RcConvexVolume> ConvexVolumes() => _volumes;
        public void AddConvexVolume(RcConvexVolume convexVolume) => _volumes.Add(convexVolume);
        public List<RcOffMeshConnection> GetOffMeshConnections() => _offMesh;
        public void AddOffMeshConnection(RcVec3f start, RcVec3f end, float radius, bool bidir, int area, int flags) =>
            _offMesh.Add(new RcOffMeshConnection(start, end, radius, bidir, area, flags));
        public void RemoveOffMeshConnections(Predicate<RcOffMeshConnection> filter) => _offMesh.RemoveAll(filter);
    }
}
