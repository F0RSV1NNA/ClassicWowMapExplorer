"""Check captured mesh snapshots against named collision points and a flat control.

Requires numpy. This is a bounded capture regression, not world-wide validation.
"""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np

parser = argparse.ArgumentParser(description=__doc__)
for name in ['baseline', 'candidate', 'fixture', 'out']:
    parser.add_argument('--' + name, required=True)
args = parser.parse_args()
fixture = json.loads(Path(args.fixture).read_text())


def mesh(folder):
    data = json.loads((Path(folder) / 'surface-weld.json').read_text())
    vertices = np.array([[p[k] for k in ['X', 'Y', 'Z']] for p in data['verts']])
    triangles = vertices[np.array(data['indices']).reshape(-1, 3)]
    return triangles[np.isin(data['areas'], [10, 11, 12])]


def heights(triangles, x, y):
    a, b, c = triangles[:, 0], triangles[:, 1], triangles[:, 2]
    determinant = (b[:, 1]-c[:, 1])*(a[:, 0]-c[:, 0])+(c[:, 0]-b[:, 0])*(a[:, 1]-c[:, 1])
    valid = abs(determinant) > 1e-9
    determinant = np.where(valid, determinant, 1)
    u = ((b[:, 1]-c[:, 1])*(x-c[:, 0])+(c[:, 0]-b[:, 0])*(y-c[:, 1])) / determinant
    v = ((c[:, 1]-a[:, 1])*(x-c[:, 0])+(a[:, 0]-c[:, 0])*(y-c[:, 1])) / determinant
    h = u*a[:, 2]+v*b[:, 2]+(1-u-v)*c[:, 2]
    return h[valid & (u >= -1e-6) & (v >= -1e-6) & (u+v <= 1.000001)].tolist()


baseline, candidate = mesh(args.baseline), mesh(args.candidate)
points = []
for p in fixture['points']:
    old, new = heights(baseline, p['x'], p['y']), heights(candidate, p['x'], p['y'])
    passed = any(p['floor'] <= h <= p['floor']+p['maxClearance'] for h in new)
    points.append(dict(p, baselineHeights=old, candidateHeights=new, passed=passed))

negative = []
for p in fixture.get('negativePoints', []):
    before, after = heights(baseline, p['x'], p['y']), heights(candidate, p['x'], p['y'])
    lo, hi = p['heightBand']
    negative.append(dict(p, baselineHeights=before, candidateHeights=after, passed=not any(lo <= h <= hi for h in after)))

control = fixture['flatControl']
x0, y0, x1, y1 = control['bounds']
low, high = control['heightBand']


def crop(triangles):
    return triangles[(triangles[:, :, 0].min(1) <= x1) & (triangles[:, :, 0].max(1) >= x0)
                     & (triangles[:, :, 1].min(1) <= y1) & (triangles[:, :, 1].max(1) >= y0)
                     & (triangles[:, :, 2].min(1) <= high+.5) & (triangles[:, :, 2].max(1) >= low-.5)]


a, b = crop(baseline), crop(candidate)
samples = 0
changes = []
largest = 0
for x in np.arange(x0, x1, control['spacing']):
    for y in np.arange(y0, y1, control['spacing']):
        before = [h for h in heights(a, x, y) if low <= h <= high]
        if not before:
            continue
        samples += 1
        after = heights(b, x, y)
        delta = min((abs(h-before[0]) for h in after), default=None)
        if delta is not None:
            largest = max(largest, delta)
        if delta is None or delta > control['tolerance']:
            changes.append(dict(x=float(x), y=float(y), before=before, after=after))

passed = all(p['passed'] for p in points+negative) and samples > 0 and not changes
result = dict(exit=0 if passed else 1, scope='captured Abbey source only',
              fixtureSha256=hashlib.sha256(Path(args.fixture).read_bytes()).hexdigest(),
              candidateSnapshotSha256=hashlib.sha256((Path(args.candidate)/'surface-weld.json').read_bytes()).hexdigest(),
              points=points, negativePoints=negative, flatWalkway=dict(samples=samples, changes=changes, largestDelta=largest))
Path(args.out).write_text(json.dumps(result, indent=2)+'\n')
print(json.dumps(dict(exit=result['exit'], pointsPassed=sum(p['passed'] for p in points),
                      negativePointsPassed=sum(p['passed'] for p in negative), flatSamples=samples, changedFlatSamples=len(changes), largestFlatDelta=largest)))
raise SystemExit(result['exit'])
