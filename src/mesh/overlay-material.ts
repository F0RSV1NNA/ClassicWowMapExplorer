import * as THREE from 'three';

/** Fill and every triangle edge in one pass, with no decimation or distance LOD. */
export function overlayMaterial(wireframe: boolean): THREE.ShaderMaterial {
	return new THREE.ShaderMaterial({
		vertexColors: true, side: THREE.DoubleSide, forceSinglePass: true,
		transparent: true, depthTest: true, depthWrite: true,
		polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2,
		uniforms: { wireOnly: { value: wireframe ? 1 : 0 } },
		vertexShader: `
			attribute vec2 meshCorner;
			varying vec3 bary;
			varying vec3 tint;
			#include <common>
			#include <logdepthbuf_pars_vertex>
			void main() {
				bary = vec3(meshCorner, 1.0 - meshCorner.x - meshCorner.y);
				tint = color;
				gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
				#include <logdepthbuf_vertex>
			}`,
		fragmentShader: `
			uniform float wireOnly;
			varying vec3 bary;
			varying vec3 tint;
			#include <common>
			#include <logdepthbuf_pars_fragment>
			void main() {
				#include <logdepthbuf_fragment>
				vec3 width = max(fwidth(bary), vec3(0.000001));
				vec3 inside = smoothstep(vec3(0.0), width, bary);
				float edge = 1.0 - min(min(inside.x, inside.y), inside.z);
				float alpha = mix(0.35, 0.95, edge);
				if (wireOnly > 0.5) { alpha = 0.95 * edge; if (alpha < 0.01) discard; }
				gl_FragColor = vec4(tint, alpha);
				#include <tonemapping_fragment>
				#include <colorspace_fragment>
			}`,
	});
}

export function overlayCorners(triangles: number): Uint8Array {
	const corners = new Uint8Array(triangles * 6);
	for (let t = 0; t < triangles; t++) { corners[t * 6] = 255; corners[t * 6 + 3] = 255; }
	return corners;
}
