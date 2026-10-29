/*
Copyright (c) 2009-2010 Mikko Mononen memon@inside.org
recast4j copyright (c) 2015-2019 Piotr Piastucki piotr@jtilia.org
DotRecast Copyright (c) 2023-2024 Choi Ikpil ikpil@naver.com

This software is provided 'as-is', without any express or implied
warranty.  In no event will the authors be held liable for any damages
arising from the use of this software.
Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:
1. The origin of this software must not be misrepresented; you must not
 claim that you wrote the original software. If you use this software
 in a product, an acknowledgment in the product documentation would be
 appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
 misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.
*/

using System;
using System.Collections.Generic;
using DotRecast.Core;
using DotRecast.Recast;


// Vendored from DotRecast 2026.3.1 (RcContours.cs, zlib licence above) with one fix.
//
// Upstream decides outline-vs-hole with
//     winding = CalcAreaOfPolygon2D(...) < 0 ? -1 : 1
// where CalcAreaOfPolygon2D returns (doubledArea + 1) / 2. Two things go wrong for us.
// Integer division rounds a thin hole whose doubled area is -1 up to 0, and zero then
// tests as a positive winding, so a degenerate contour is filed as a second OUTLINE for
// its region and BuildContours throws "Multiple outlines for region N". The region is not
// actually broken - it is one connected, single-area, single-layer piece, which is why
// every invariant check passes and the error looks impossible.
//
// Severing indoor steps makes this common rather than rare: cutting the links across a
// railing or a stair riser leaves many one-cell-wide regions and pinched rings, which is
// precisely the shape that simplifies to a zero-area contour. Tile [27,29] lost half its
// polygons to the fallback chain over it.
//
// The fix: wind on the exact doubled area, and drop contours that enclose nothing, since
// a zero-area contour cannot become a polygon anyway. Everything else is upstream's.
// Explorer extension: preserve a nonzero raw contour's winding through simplification.
// Retry with tighter error before treating a collapsed outline as noise; otherwise its
// surviving holes become orphaned and force the whole tile onto the plain-build fallback.
// Explorer also assigns holes to their enclosing outline and surface area when a
// split region contains several outlines. Region ids and portal flags stay intact.

namespace MapExtract;
    
    using static RcRecast;

    public static class NavContours
    {
        internal static int GetCornerHeight(int x, int y, int i, int dir, RcCompactHeightfield chf, out bool isBorderVertex)
        {
            isBorderVertex = false;

            ref RcCompactSpan s = ref chf.spans[i];
            int ch = s.y;
            int dirp = (dir + 1) & 0x3;
            bool Emitted(int span)=>chf.areas[span]!=RC_NULL_AREA&&chf.spans[span].reg!=0;

            int[] regs =
            {
                0, 0, 0, 0
            };

            // Combine region and area codes in order to prevent
            // border vertices which are in between two areas to be removed.
            regs[0] = chf.spans[i].reg | (chf.areas[i] << 16);

            if (GetCon(s, dir) != RC_NOT_CONNECTED)
            {
                int ax = x + GetDirOffsetX(dir);
                int ay = y + GetDirOffsetY(dir);
                int ai = chf.cells[ax + ay * chf.width].index + GetCon(s, dir);
                ref RcCompactSpan @as = ref chf.spans[ai];
                if (Emitted(ai)) ch = Math.Max(ch, @as.y);
                regs[1] = Emitted(ai)?chf.spans[ai].reg | (chf.areas[ai] << 16):0;
                if (Emitted(ai) && GetCon(@as, dirp) != RC_NOT_CONNECTED)
                {
                    int ax2 = ax + GetDirOffsetX(dirp);
                    int ay2 = ay + GetDirOffsetY(dirp);
                    int ai2 = chf.cells[ax2 + ay2 * chf.width].index + GetCon(@as, dirp);
                    ref RcCompactSpan as2 = ref chf.spans[ai2];
                    if (Emitted(ai2)) ch = Math.Max(ch, as2.y);
                    regs[2] = Emitted(ai2)?chf.spans[ai2].reg | (chf.areas[ai2] << 16):0;
                }
            }

            if (GetCon(s, dirp) != RC_NOT_CONNECTED)
            {
                int ax = x + GetDirOffsetX(dirp);
                int ay = y + GetDirOffsetY(dirp);
                int ai = chf.cells[ax + ay * chf.width].index + GetCon(s, dirp);
                ref RcCompactSpan @as = ref chf.spans[ai];
                if (Emitted(ai)) ch = Math.Max(ch, @as.y);
                regs[3] = Emitted(ai)?chf.spans[ai].reg | (chf.areas[ai] << 16):0;
                if (Emitted(ai) && GetCon(@as, dir) != RC_NOT_CONNECTED)
                {
                    int ax2 = ax + GetDirOffsetX(dir);
                    int ay2 = ay + GetDirOffsetY(dir);
                    int ai2 = chf.cells[ax2 + ay2 * chf.width].index + GetCon(@as, dir);
                    ref RcCompactSpan as2 = ref chf.spans[ai2];
                    if (Emitted(ai2)) ch = Math.Max(ch, as2.y);
                    regs[2] = Emitted(ai2)?chf.spans[ai2].reg | (chf.areas[ai2] << 16):0;
                }
            }

            // Check if the vertex is special edge vertex, these vertices will be removed later.
            for (int j = 0; j < 4; ++j)
            {
                int a = j;
                int b = (j + 1) & 0x3;
                int c = (j + 2) & 0x3;
                int d = (j + 3) & 0x3;

                // The vertex is a border vertex there are two same exterior cells in a row,
                // followed by two interior cells and none of the regions are out of bounds.
                bool twoSameExts = (regs[a] & regs[b] & RC_BORDER_REG) != 0 && regs[a] == regs[b];
                bool twoInts = ((regs[c] | regs[d]) & RC_BORDER_REG) == 0;
                bool intsSameArea = (regs[c] >> 16) == (regs[d] >> 16);
                bool noZeros = regs[a] != 0 && regs[b] != 0 && regs[c] != 0 && regs[d] != 0;
                if (twoSameExts && twoInts && intsSameArea && noZeros)
                {
                    isBorderVertex = true;
                    break;
                }
            }

            return ch;
        }

        private static void WalkContour(int x, int y, int i, RcCompactHeightfield chf, int[] flags, List<int> points)
        {
            // Choose the first non-connected edge
            int dir = 0;
            while ((flags[i] & (1 << dir)) == 0)
                dir++;

            int startDir = dir;
            int starti = i;

            int area = chf.areas[i];

            int iter = 0;
            while (++iter < 40000)
            {
                if ((flags[i] & (1 << dir)) != 0)
                {
                    // Choose the edge corner
                    bool isBorderVertex = false;
                    bool isAreaBorder = false;
                    int px = x;
                    int py = GetCornerHeight(x, y, i, dir, chf, out isBorderVertex);
                    int pz = y;
                    switch (dir)
                    {
                        case 0:
                            pz++;
                            break;
                        case 1:
                            px++;
                            pz++;
                            break;
                        case 2:
                            px++;
                            break;
                    }

                    int r = 0;
                    ref RcCompactSpan s = ref chf.spans[i];
                    if (GetCon(s, dir) != RC_NOT_CONNECTED)
                    {
                        int ax = x + GetDirOffsetX(dir);
                        int ay = y + GetDirOffsetY(dir);
                        int ai = chf.cells[ax + ay * chf.width].index + GetCon(s, dir);
                        r = chf.spans[ai].reg;
                        if (area != chf.areas[ai])
                            isAreaBorder = true;
                    }

                    if (isBorderVertex)
                        r |= RC_BORDER_VERTEX;
                    if (isAreaBorder)
                        r |= RC_AREA_BORDER;
                    points.Add(px);
                    points.Add(py);
                    points.Add(pz);
                    points.Add(r);

                    flags[i] &= ~(1 << dir); // Remove visited edges
                    dir = (dir + 1) & 0x3; // Rotate CW
                }
                else
                {
                    int ni = -1;
                    int nx = x + GetDirOffsetX(dir);
                    int ny = y + GetDirOffsetY(dir);
                    ref RcCompactSpan s = ref chf.spans[i];
                    if (GetCon(s, dir) != RC_NOT_CONNECTED)
                    {
                        ref RcCompactCell nc = ref chf.cells[nx + ny * chf.width];
                        ni = nc.index + GetCon(s, dir);
                    }

                    if (ni == -1)
                    {
                        throw new InvalidOperationException("rcBuildContours: Boundary walk lost its neighbour.");
                    }

                    x = nx;
                    y = ny;
                    i = ni;
                    dir = (dir + 3) & 0x3; // Rotate CCW
                }

                if (starti == i && startDir == dir)
                {
                    return;
                }
            }
            throw new InvalidOperationException("rcBuildContours: Boundary walk exceeded its limit before closing.");
        }

        private static float DistancePtSeg(int x, int z, int px, int pz, int qx, int qz)
        {
            float pqx = qx - px;
            float pqz = qz - pz;
            float dx = x - px;
            float dz = z - pz;
            float d = pqx * pqx + pqz * pqz;
            float t = pqx * dx + pqz * dz;
            if (d > 0)
                t /= d;
            if (t < 0)
                t = 0;
            else if (t > 1)
                t = 1;

            dx = px + t * pqx - x;
            dz = pz + t * pqz - z;

            return dx * dx + dz * dz;
        }

        private static void SimplifyContour(List<int> points, List<int> simplified, float maxError, int maxEdgeLen, int buildFlags, bool closedPortal = false)
        {
            // Add initial points.
            bool hasConnections = false;
            for (int i = 0; i < points.Count; i += 4)
            {
                if ((points[i + 3] & RC_CONTOUR_REG_MASK) != 0)
                {
                    hasConnections = true;
                    break;
                }
            }

            if (hasConnections)
            {
                // The contour has some portals to other regions.
                // Add a new point to every location where the region changes.
                for (int i = 0, ni = points.Count / 4; i < ni; ++i)
                {
                    int ii = (i + 1) % ni;
                    bool differentRegs = (points[i * 4 + 3] & RC_CONTOUR_REG_MASK) != (points[ii * 4 + 3] & RC_CONTOUR_REG_MASK);
                    bool areaBorders = (points[i * 4 + 3] & RC_AREA_BORDER) != (points[ii * 4 + 3] & RC_AREA_BORDER);
                    if (differentRegs || areaBorders)
                    {
                        simplified.Add(points[i * 4 + 0]);
                        simplified.Add(points[i * 4 + 1]);
                        simplified.Add(points[i * 4 + 2]);
                        simplified.Add(i);
                    }
                }
            }

            if (simplified.Count == 0)
            {
                // If there is no connections at all,
                // create some initial points for the simplification process.
                // Find lower-left and upper-right vertices of the contour.
                int llx = points[0];
                int lly = points[1];
                int llz = points[2];
                int lli = 0;
                int urx = points[0];
                int ury = points[1];
                int urz = points[2];
                int uri = 0;
                for (int i = 0; i < points.Count; i += 4)
                {
                    int x = points[i + 0];
                    int y = points[i + 1];
                    int z = points[i + 2];
                    if (x < llx || (x == llx && z < llz))
                    {
                        llx = x;
                        lly = y;
                        llz = z;
                        lli = i / 4;
                    }

                    if (x > urx || (x == urx && z > urz))
                    {
                        urx = x;
                        ury = y;
                        urz = z;
                        uri = i / 4;
                    }
                }

                simplified.Add(llx);
                simplified.Add(lly);
                simplified.Add(llz);
                simplified.Add(lli);

                simplified.Add(urx);
                simplified.Add(ury);
                simplified.Add(urz);
                simplified.Add(uri);
            }

            // Add points until all raw points are within
            // error tolerance to the simplified shape.
            int pn = points.Count / 4;
            for (int i = 0; i < simplified.Count / 4;)
            {
                int ii = (i + 1) % (simplified.Count / 4);

                int ax = simplified[i * 4 + 0];
                int az = simplified[i * 4 + 2];
                int ai = simplified[i * 4 + 3];

                int bx = simplified[ii * 4 + 0];
                int bz = simplified[ii * 4 + 2];
                int bi = simplified[ii * 4 + 3];

                // Find maximum deviation from the segment.
                float maxd = 0;
                int maxi = -1;
                int ci, cinc, endi;

                // Traverse the segment in lexilogical order so that the
                // max deviation is calculated similarly when traversing
                // opposite segments.
                if (bx > ax || (bx == ax && bz > az))
                {
                    cinc = 1;
                    ci = (ai + cinc) % pn;
                    endi = bi;
                }
                else
                {
                    cinc = pn - 1;
                    ci = (bi + cinc) % pn;
                    endi = ai;
                    int temp = ax;
                    ax = bx;
                    bx = temp;
                    temp = az;
                    az = bz;
                    bz = temp;
                }

                // Tessellate only outer edges or edges between areas.
                if (closedPortal || (points[ci * 4 + 3] & RC_CONTOUR_REG_MASK) == 0 || (points[ci * 4 + 3] & RC_AREA_BORDER) != 0)
                {
                    while (ci != endi)
                    {
                        float d = DistancePtSeg(points[ci * 4 + 0], points[ci * 4 + 2], ax, az, bx, bz);
                        if (d > maxd)
                        {
                            maxd = d;
                            maxi = ci;
                        }

                        ci = (ci + cinc) % pn;
                    }
                }

                // If the max deviation is larger than accepted error,
                // add new point, else continue to next segment.
                if (maxi != -1 && maxd > (maxError * maxError))
                {
                    // Add the point.
                    simplified.Insert((i + 1) * 4 + 0, points[maxi * 4 + 0]);
                    simplified.Insert((i + 1) * 4 + 1, points[maxi * 4 + 1]);
                    simplified.Insert((i + 1) * 4 + 2, points[maxi * 4 + 2]);
                    simplified.Insert((i + 1) * 4 + 3, maxi);
                }
                else
                {
                    ++i;
                }
            }

            // Split too long edges.
            if (maxEdgeLen > 0 && (buildFlags & (RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES | RcBuildContoursFlags.RC_CONTOUR_TESS_AREA_EDGES)) != 0)
            {
                for (int i = 0; i < simplified.Count / 4;)
                {
                    int ii = (i + 1) % (simplified.Count / 4);

                    int ax = simplified[i * 4 + 0];
                    int az = simplified[i * 4 + 2];
                    int ai = simplified[i * 4 + 3];

                    int bx = simplified[ii * 4 + 0];
                    int bz = simplified[ii * 4 + 2];
                    int bi = simplified[ii * 4 + 3];

                    // Find maximum deviation from the segment.
                    int maxi = -1;
                    int ci = (ai + 1) % pn;

                    // Tessellate only outer edges or edges between areas.
                    bool tess = false;
                    // Wall edges.
                    if ((buildFlags & RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES) != 0 && (points[ci * 4 + 3] & RC_CONTOUR_REG_MASK) == 0)
                    {
                        tess = true;
                    }

                    // Edges between areas.
                    if ((buildFlags & RcBuildContoursFlags.RC_CONTOUR_TESS_AREA_EDGES) != 0 && (points[ci * 4 + 3] & RC_AREA_BORDER) != 0)
                    {
                        tess = true;
                    }

                    if (tess)
                    {
                        int dx = bx - ax;
                        int dz = bz - az;
                        if (dx * dx + dz * dz > maxEdgeLen * maxEdgeLen)
                        {
                            // Round based on the segments in lexilogical order so that the
                            // max tesselation is consistent regardless in which direction
                            // segments are traversed.
                            int n = bi < ai ? (bi + pn - ai) : (bi - ai);
                            if (n > 1)
                            {
                                if (bx > ax || (bx == ax && bz > az))
                                    maxi = (ai + n / 2) % pn;
                                else
                                    maxi = (ai + (n + 1) / 2) % pn;
                            }
                        }
                    }

                    // If the max deviation is larger than accepted error,
                    // add new point, else continue to next segment.
                    if (maxi != -1)
                    {
                        // Add the point.
                        simplified.Insert((i + 1) * 4 + 0, points[maxi * 4 + 0]);
                        simplified.Insert((i + 1) * 4 + 1, points[maxi * 4 + 1]);
                        simplified.Insert((i + 1) * 4 + 2, points[maxi * 4 + 2]);
                        simplified.Insert((i + 1) * 4 + 3, maxi);
                    }
                    else
                    {
                        ++i;
                    }
                }
            }

            for (int i = 0; i < simplified.Count / 4; ++i)
            {
                // The edge vertex flag is take from the current raw point,
                // and the neighbour region is take from the next raw point.
                int ai = (simplified[i * 4 + 3] + 1) % pn;
                int bi = simplified[i * 4 + 3];
                simplified[i * 4 + 3] = (points[ai * 4 + 3] & (RC_CONTOUR_REG_MASK | RC_AREA_BORDER))
                                        | points[bi * 4 + 3] & RC_BORDER_VERTEX;
            }
        }

        /// A contour with no enclosed area is noise - it yields no polygon, and upstream's
        /// winding test would file it as a second outline for its region.
        private static bool EnclosesNothing(List<int> simplified)
        {
            int n = simplified.Count / 4;
            int area = 0;
            for (int i = 0, j = n - 1; i < n; j = i++)
                area += simplified[i * 4] * simplified[j * 4 + 2] - simplified[j * 4] * simplified[i * 4 + 2];
            return area == 0;
        }

        internal static long SignedContourArea(IReadOnlyList<int> vertices)
        {
            long area = 0;
            for (int i = 0, j = vertices.Count / 4 - 1; i < vertices.Count / 4; j = i++)
                area += (long)vertices[i * 4] * vertices[j * 4 + 2] - (long)vertices[j * 4] * vertices[i * 4 + 2];
            return area;
        }

        internal static void PreserveContourWinding(List<int> raw, List<int> simplified, float error, int edgeLength, int flags)
        {
            int winding = Math.Sign(SignedContourArea(raw));
            if (winding == 0 || (simplified.Count >= 12 && Math.Sign(SignedContourArea(simplified)) == winding)) return;
            // Retain the original neighbour/border flag construction in SimplifyContour.
            // Copying raw vertices directly would assign incorrect portal-edge flags.
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                simplified.Clear();
                SimplifyContour(raw, simplified, attempt == 5 ? 0 : error / (1 << attempt), edgeLength, flags);
                RemoveDegenerateSegments(simplified);
                if (simplified.Count >= 12 && Math.Sign(SignedContourArea(simplified)) == winding) return;
            }
            // A closed portal loop has no region transitions to seed. Both sides
            // otherwise collapse to the same two endpoints, even at zero error.
            // Apply the usual deterministic geometric simplification to this loop,
            // preserving its original edge flags. Copying every raster vertex here
            // exhausts Detour's 16-bit vertex budget on dense city tiles.
            for (int attempt = 0; attempt <= 5; attempt++)
            {
                simplified.Clear();
                SimplifyContour(raw, simplified, attempt == 5 ? 0 : error / (1 << attempt), edgeLength, flags, true);
                RemoveDegenerateSegments(simplified);
                if (simplified.Count >= 12 && Math.Sign(SignedContourArea(simplified)) == winding) return;
            }
            throw new InvalidOperationException("Contour simplification could not preserve a nonzero raw boundary.");
        }

        // Winding recovery can refine a portal on one contour only. Its
        // neighbour must retain the same raw boundary breakpoints, otherwise
        // the two valid regions leave a triangular crack between them.
        internal static int SynchronizePortalContours(RcContourSet cset)
        {
            var regions=cset.conts.Select(c=>c.reg).ToHashSet();
            var required=new Dictionary<(int,int),HashSet<(int,int)>>();
            static (int,int) Pair(int a,int b)=>a<b?(a,b):(b,a);
            foreach(var c in cset.conts)for(int i=0;i<c.nverts;i++){
                int next=(i+1)%c.nverts,other=c.verts[i*4+3]&RC_CONTOUR_REG_MASK;
                if(other==0||other==c.reg||(other&RC_BORDER_REG)!=0||!regions.Contains(other))continue;
                var pair=Pair(c.reg,other);if(!required.TryGetValue(pair,out var points))required[pair]=points=new();
                points.Add((c.verts[i*4],c.verts[i*4+2]));points.Add((c.verts[next*4],c.verts[next*4+2]));
            }
            int added=0;
            foreach(var c in cset.conts){
                var output=new List<int>();
                bool Equal(int raw,int simplified)=>c.rverts[raw*4]==c.verts[simplified*4]&&c.rverts[raw*4+2]==c.verts[simplified*4+2];
                for(int i=0;i<c.nverts;i++){
                    for(int k=0;k<4;k++)output.Add(c.verts[i*4+k]);
                    int next=(i+1)%c.nverts,other=c.verts[i*4+3]&RC_CONTOUR_REG_MASK;
                    if(other==0||other==c.reg||!required.TryGetValue(Pair(c.reg,other),out var points))continue;
                    // Match the ordered raw portal run, including repeated XY
                    // vertices at spurs. A coordinate match alone is ambiguous.
                    for(int start=0;start<c.nrverts;start++){
                        if(!Equal(start,i))continue;
                        var interior=new List<int>();bool found=false;
                        for(int step=1;step<c.nrverts;step++){
                            int raw=(start+step)%c.nrverts;
                            if((c.rverts[raw*4+3]&RC_CONTOUR_REG_MASK)!=other)break;
                            if(Equal(raw,next)){found=true;break;}
                            if(points.Contains((c.rverts[raw*4],c.rverts[raw*4+2])))interior.Add(raw);
                        }
                        if(!found)continue;
                        foreach(int raw in interior){
                            int following=(raw+1)%c.nrverts;
                            output.Add(c.rverts[raw*4]);output.Add(c.rverts[raw*4+1]);output.Add(c.rverts[raw*4+2]);
                            output.Add((c.rverts[following*4+3]&(RC_CONTOUR_REG_MASK|RC_AREA_BORDER))|(c.rverts[raw*4+3]&RC_BORDER_VERTEX));added++;
                        }
                        break;
                    }
                }
                c.verts=output.ToArray();c.nverts=output.Count/4;
            }
            return added;
        }

        /// Twice the signed area, exactly - no rounding, so a hole never rounds up to zero
        /// and a degenerate contour stays distinguishable from a real one.
        private static int CalcDoubleAreaOfPolygon2D(int[] verts, int nverts)
        {
            int area = 0;
            for (int i = 0, j = nverts - 1; i < nverts; j = i++)
            {
                int vi = i * 4;
                int vj = j * 4;
                area += verts[vi + 0] * verts[vj + 2] - verts[vj + 0] * verts[vi + 2];
            }

            return area;
        }

        private static int CalcAreaOfPolygon2D(int[] verts, int nverts)
        {
            int area = 0;
            for (int i = 0, j = nverts - 1; i < nverts; j = i++)
            {
                int vi = i * 4;
                int vj = j * 4;
                area += verts[vi + 0] * verts[vj + 2] - verts[vj + 0] * verts[vi + 2];
            }

            return (area + 1) / 2;
        }

        private static bool IntersectSegContour(int d0, int d1, int i, int n, int[] verts, int[] d0verts, int[] d1verts)
        {
            // For each edge (k,k+1) of P
            int[] pverts = new int[4 * 4];
            for (int g = 0; g < 4; g++)
            {
                pverts[g] = d0verts[d0 + g];
                pverts[4 + g] = d1verts[d1 + g];
            }

            d0 = 0;
            d1 = 4;
            for (int k = 0; k < n; k++)
            {
                int k1 = RcMeshs.Next(k, n);
                // Skip edges incident to i.
                if (i == k || i == k1)
                    continue;
                int p0 = k * 4;
                int p1 = k1 * 4;
                for (int g = 0; g < 4; g++)
                {
                    pverts[8 + g] = verts[p0 + g];
                    pverts[12 + g] = verts[p1 + g];
                }

                p0 = 8;
                p1 = 12;
                if (RcMeshs.VEqual(pverts, d0, p0) || RcMeshs.VEqual(pverts, d1, p0) ||
                    RcMeshs.VEqual(pverts, d0, p1) || RcMeshs.VEqual(pverts, d1, p1))
                    continue;

                if (RcMeshs.Intersect(pverts, d0, d1, p0, p1))
                    return true;
            }

            return false;
        }

        private static bool InCone(int i, int n, int[] verts, int pj, int[] vertpj)
        {
            int pi = i * 4;
            int pi1 = RcMeshs.Next(i, n) * 4;
            int pin1 = RcMeshs.Prev(i, n) * 4;
            int[] pverts = new int[4 * 4];
            for (int g = 0; g < 4; g++)
            {
                pverts[g] = verts[pi + g];
                pverts[4 + g] = verts[pi1 + g];
                pverts[8 + g] = verts[pin1 + g];
                pverts[12 + g] = vertpj[pj + g];
            }

            pi = 0;
            pi1 = 4;
            pin1 = 8;
            pj = 12;
            // If P[i] is a convex vertex [ i+1 left or on (i-1,i) ].
            if (RcMeshs.LeftOn(pverts, pin1, pi, pi1))
                return RcMeshs.Left(pverts, pi, pj, pin1) && RcMeshs.Left(pverts, pj, pi, pi1);
            // Assume (i-1,i,i+1) not collinear.
            // else P[i] is reflex.
            return !(RcMeshs.LeftOn(pverts, pi, pj, pi1) && RcMeshs.LeftOn(pverts, pj, pi, pin1));
        }

        private static void RemoveDegenerateSegments(List<int> simplified)
        {
            // Remove adjacent vertices which are equal on xz-plane,
            // or else the triangulator will get confused.
            int npts = simplified.Count / 4;
            for (int i = 0; i < npts; ++i)
            {
                int ni = RcMeshs.Next(i, npts);

                // if (Vequal(&simplified[i*4], &simplified[ni*4]))
                if (simplified[i * 4] == simplified[ni * 4]
                    && simplified[i * 4 + 2] == simplified[ni * 4 + 2])
                {
                    // Degenerate segment, remove.
                    simplified.RemoveAt(i * 4);
                    simplified.RemoveAt(i * 4);
                    simplified.RemoveAt(i * 4);
                    simplified.RemoveAt(i * 4);
                    npts--;
                }
            }
        }

        private static void MergeContours(RcContour ca, RcContour cb, int ia, int ib)
        {
            int maxVerts = ca.nverts + cb.nverts + 2;
            int[] verts = new int[maxVerts * 4];

            int nv = 0;

            // Copy contour A.
            for (int i = 0; i <= ca.nverts; ++i)
            {
                int dst = nv * 4;
                int src = ((ia + i) % ca.nverts) * 4;
                verts[dst + 0] = ca.verts[src + 0];
                verts[dst + 1] = ca.verts[src + 1];
                verts[dst + 2] = ca.verts[src + 2];
                verts[dst + 3] = ca.verts[src + 3];
                nv++;
            }

            // Copy contour B
            for (int i = 0; i <= cb.nverts; ++i)
            {
                int dst = nv * 4;
                int src = ((ib + i) % cb.nverts) * 4;
                verts[dst + 0] = cb.verts[src + 0];
                verts[dst + 1] = cb.verts[src + 1];
                verts[dst + 2] = cb.verts[src + 2];
                verts[dst + 3] = cb.verts[src + 3];
                nv++;
            }

            ca.verts = verts;
            ca.nverts = nv;

            cb.verts = null;
            cb.nverts = 0;
        }

        // Finds the lowest leftmost vertex of a contour.
        private static int[] FindLeftMostVertex(RcContour contour)
        {
            int minx = contour.verts[0];
            int minz = contour.verts[2];
            int leftmost = 0;
            for (int i = 1; i < contour.nverts; i++)
            {
                int x = contour.verts[i * 4 + 0];
                int z = contour.verts[i * 4 + 2];
                if (x < minx || (x == minx && z < minz))
                {
                    minx = x;
                    minz = z;
                    leftmost = i;
                }
            }

            return new int[] { minx, minz, leftmost };
        }

        private static void MergeRegionHoles(RcContext ctx, RcContourRegion region)
        {
            // Sort holes from left to right.
            for (int i = 0; i < region.nholes; i++)
            {
                int[] minleft = FindLeftMostVertex(region.holes[i].contour);
                region.holes[i].minx = minleft[0];
                region.holes[i].minz = minleft[1];
                region.holes[i].leftmost = minleft[2];
            }

            Array.Sort(region.holes, RcContourHoleComparer.Shared);

            int maxVerts = region.outline.nverts;
            for (int i = 0; i < region.nholes; i++)
                maxVerts += region.holes[i].contour.nverts;

            RcPotentialDiagonal[] diags = new RcPotentialDiagonal[maxVerts];

            RcContour outline = region.outline;

            // Merge holes into the outline one by one.
            for (int i = 0; i < region.nholes; i++)
            {
                RcContour hole = region.holes[i].contour;

                int index = -1;
                int bestVertex = region.holes[i].leftmost;
                for (int iter = 0; iter < hole.nverts; iter++)
                {
                    // Find potential diagonals.
                    // The 'best' vertex must be in the cone described by 3 consecutive vertices of the outline.
                    // ..o j-1
                    // |
                    // | * best
                    // |
                    // j o-----o j+1
                    // :
                    int ndiags = 0;
                    int corner = bestVertex * 4;
                    for (int j = 0; j < outline.nverts; j++)
                    {
                        if (InCone(j, outline.nverts, outline.verts, corner, hole.verts))
                        {
                            int dx = outline.verts[j * 4 + 0] - hole.verts[corner + 0];
                            int dz = outline.verts[j * 4 + 2] - hole.verts[corner + 2];
                            diags[ndiags] = new RcPotentialDiagonal(j, dx * dx + dz * dz);
                            ndiags++;
                        }
                    }

                    // Sort potential diagonals by distance, we want to make the connection as short as possible.
                    Array.Sort(diags, 0, ndiags, RcPotentialDiagonalComparer.Shared);

                    // Find a diagonal that is not intersecting the outline not the remaining holes.
                    index = -1;
                    for (int j = 0; j < ndiags; j++)
                    {
                        int pt = diags[j].vert * 4;
                        bool intersect = IntersectSegContour(pt, corner, diags[j].vert, outline.nverts, outline.verts,
                            outline.verts, hole.verts);
                        for (int k = i; k < region.nholes && !intersect; k++)
                            intersect |= IntersectSegContour(pt, corner, -1, region.holes[k].contour.nverts,
                                region.holes[k].contour.verts, outline.verts, hole.verts);
                        if (!intersect)
                        {
                            index = diags[j].vert;
                            break;
                        }
                    }

                    // If found non-intersecting diagonal, stop looking.
                    if (index != -1)
                        break;
                    // All the potential diagonals for the current vertex were intersecting, try next vertex.
                    bestVertex = (bestVertex + 1) % hole.nverts;
                }

                if (index == -1)
                {
                    throw new InvalidOperationException($"rcBuildContours: Failed to merge hole {i} in region {hole.reg}, area {hole.area}.");
                }

                MergeContours(region.outline, hole, index, bestVertex);
            }
        }

        /// @par
        ///
        /// The raw contours will match the region outlines exactly. The @p maxError and @p maxEdgeLen
        /// parameters control how closely the simplified contours will match the raw contours.
        ///
        /// Simplified contours are generated such that the vertices for portals between areas match up.
        /// (They are considered mandatory vertices.)
        ///
        /// Setting @p maxEdgeLength to zero will disabled the edge length feature.
        ///
        /// See the #rcConfig documentation for more information on the configuration parameters.
        ///
        /// @see rcAllocContourSet, rcCompactHeightfield, rcContourSet, rcConfig
        internal static void MergeContourHoles(RcContext ctx, RcContourSet cset)
        {
            // Grid splitting and area boundaries can leave multiple outlines under
            // one region id. Assign each hole to its enclosing outline, rather than
            // assuming a region id names exactly one planar outline.
            var groups = new Dictionary<(int Region, int Area), List<RcContourRegion>>();
            foreach (var contour in cset.conts)
            {
                if (CalcDoubleAreaOfPolygon2D(contour.verts, contour.nverts) <= 0) continue;
                var key = (contour.reg, contour.area);
                if (!groups.TryGetValue(key, out var outlines)) groups[key] = outlines = new();
                outlines.Add(new RcContourRegion { outline = contour });
            }
            var holes = new Dictionary<RcContourRegion, List<RcContourHole>>();
            foreach (var contour in cset.conts)
            {
                if (CalcDoubleAreaOfPolygon2D(contour.verts, contour.nverts) >= 0) continue;
                RcContourRegion? owner = null;
                long smallest = long.MaxValue;
                if (groups.TryGetValue((contour.reg, contour.area), out var outlines))
                    foreach (var candidate in outlines)
                    {
                        long area = Math.Abs(SignedContourArea(candidate.outline.verts));
                        if (area >= smallest || !Contains(candidate.outline, contour.verts[0], contour.verts[2])) continue;
                        owner = candidate; smallest = area;
                    }
                if (owner == null) throw new InvalidOperationException($"rcBuildContours: No enclosing outline for hole in region {contour.reg}, area {contour.area}.");
                if (!holes.TryGetValue(owner, out var owned)) holes[owner] = owned = new();
                owned.Add(new RcContourHole { contour = contour });
            }
            foreach (var pair in holes)
            {
                pair.Key.holes = pair.Value.ToArray(); pair.Key.nholes = pair.Value.Count;
                MergeRegionHoles(ctx, pair.Key);
            }

            static bool Contains(RcContour outline, int x, int z)
            {
                bool inside = false;
                for (int i = 0, j = outline.nverts - 1; i < outline.nverts; j = i++)
                {
                    int ax = outline.verts[j * 4], az = outline.verts[j * 4 + 2];
                    int bx = outline.verts[i * 4], bz = outline.verts[i * 4 + 2];
                    long cross = (long)(x - ax) * (bz - az) - (long)(z - az) * (bx - ax);
                    if (cross == 0 && x >= Math.Min(ax, bx) && x <= Math.Max(ax, bx) && z >= Math.Min(az, bz) && z <= Math.Max(az, bz)) return true;
                    if ((az > z) != (bz > z) && x < ax + (double)(bx - ax) * (z - az) / (bz - az)) inside = !inside;
                }
                return inside;
            }
        }

        public static RcContourSet BuildContours(RcContext ctx, RcCompactHeightfield chf, float maxError, int maxEdgeLen,
            int buildFlags)
        {
            int w = chf.width;
            int h = chf.height;
            int borderSize = chf.borderSize;
            RcContourSet cset = new RcContourSet();

            using var timer = ctx.ScopedTimer(RcTimerLabel.RC_TIMER_BUILD_CONTOURS);

            cset.bmin = chf.bmin;
            cset.bmax = chf.bmax;
            if (borderSize > 0)
            {
                // If the heightfield was build with bordersize, remove the offset.
                float pad = borderSize * chf.cs;
                cset.bmin.X += pad;
                cset.bmin.Z += pad;
                cset.bmax.X -= pad;
                cset.bmax.Z -= pad;
            }

            cset.cs = chf.cs;
            cset.ch = chf.ch;
            cset.width = chf.width - chf.borderSize * 2;
            cset.height = chf.height - chf.borderSize * 2;
            cset.borderSize = chf.borderSize;
            cset.maxError = maxError;

            int[] flags = new int[chf.spanCount];

            ctx.StartTimer(RcTimerLabel.RC_TIMER_BUILD_CONTOURS_TRACE);

            // Mark boundaries.
            for (int y = 0; y < h; ++y)
            {
                for (int x = 0; x < w; ++x)
                {
                    ref RcCompactCell c = ref chf.cells[x + y * w];
                    for (int i = c.index, ni = c.index + c.count; i < ni; ++i)
                    {
                        int res = 0;
                        ref RcCompactSpan s = ref chf.spans[i];
                        if (chf.spans[i].reg == 0 || (chf.spans[i].reg & RC_BORDER_REG) != 0)
                        {
                            flags[i] = 0;
                            continue;
                        }

                        for (int dir = 0; dir < 4; ++dir)
                        {
                            int r = 0;
                            if (GetCon(s, dir) != RC_NOT_CONNECTED)
                            {
                                int ax = x + GetDirOffsetX(dir);
                                int ay = y + GetDirOffsetY(dir);
                                int ai = chf.cells[ax + ay * w].index + GetCon(s, dir);
                                r = chf.spans[ai].reg;
                            }

                            if (r == chf.spans[i].reg)
                                res |= (1 << dir);
                        }

                        flags[i] = res ^ 0xf; // Inverse, mark non connected edges.
                    }
                }
            }

            ctx.StopTimer(RcTimerLabel.RC_TIMER_BUILD_CONTOURS_TRACE);

            List<int> verts = new List<int>(256);
            List<int> simplified = new List<int>(64);

            for (int y = 0; y < h; ++y)
            {
                for (int x = 0; x < w; ++x)
                {
                    ref RcCompactCell c = ref chf.cells[x + y * w];
                    for (int i = c.index, ni = c.index + c.count; i < ni; ++i)
                    {
                        // A retained one-cell region has all four boundary bits.
                        // It may be a valid floor fragment between area labels,
                        // not an isolated obstacle. Its four-edge contour is valid.
                        if (flags[i] == 0)
                        {
                            flags[i] = 0;
                            continue;
                        }

                        int reg = chf.spans[i].reg;
                        if (reg == 0 || (reg & RC_BORDER_REG) != 0)
                            continue;
                        int area = chf.areas[i];

                        verts.Clear();
                        simplified.Clear();

                        ctx.StartTimer(RcTimerLabel.RC_TIMER_BUILD_CONTOURS_WALK);
                        WalkContour(x, y, i, chf, flags, verts);
                        ctx.StopTimer(RcTimerLabel.RC_TIMER_BUILD_CONTOURS_WALK);

                        ctx.StartTimer(RcTimerLabel.RC_TIMER_BUILD_CONTOURS_SIMPLIFY);
                        SimplifyContour(verts, simplified, maxError, maxEdgeLen, buildFlags);
                        RemoveDegenerateSegments(simplified);
                        PreserveContourWinding(verts, simplified, maxError, maxEdgeLen, buildFlags);
                        ctx.StopTimer(RcTimerLabel.RC_TIMER_BUILD_CONTOURS_SIMPLIFY);

                        // Store region->contour remap info.
                        // Create contour.
                        if (simplified.Count / 4 >= 3 && !EnclosesNothing(simplified))
                        {
                            RcContour cont = new RcContour();
                            cset.conts.Add(cont);

                            cont.nverts = simplified.Count / 4;
                            cont.verts = new int[simplified.Count];
                            for (int l = 0; l < cont.verts.Length; l++)
                            {
                                cont.verts[l] = simplified[l];
                            }

                            if (borderSize > 0)
                            {
                                // If the heightfield was build with bordersize, remove the offset.
                                for (int j = 0; j < cont.nverts; ++j)
                                {
                                    cont.verts[j * 4] -= borderSize;
                                    cont.verts[j * 4 + 2] -= borderSize;
                                }
                            }

                            cont.nrverts = verts.Count / 4;
                            cont.rverts = new int[verts.Count];
                            for (int l = 0; l < cont.rverts.Length; l++)
                            {
                                cont.rverts[l] = verts[l];
                            }

                            if (borderSize > 0)
                            {
                                // If the heightfield was build with bordersize, remove the offset.
                                for (int j = 0; j < cont.nrverts; ++j)
                                {
                                    cont.rverts[j * 4] -= borderSize;
                                    cont.rverts[j * 4 + 2] -= borderSize;
                                }
                            }

                            cont.reg = reg;
                            cont.area = area;
                        }
                    }
                }
            }

            SynchronizePortalContours(cset);
            MergeContourHoles(ctx, cset);

            return cset;
        }
    }
