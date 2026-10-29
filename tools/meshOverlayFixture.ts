import * as THREE from 'three';
import { overlayCorners, overlayMaterial } from '../src/mesh/overlay-material';

/** Actual WebGL shader/draw-count check; no game data or full-world fixture. */
export async function checkOverlay() {
	const canvas = document.createElement('canvas'); canvas.width = canvas.height = 64;
	const renderer = new THREE.WebGLRenderer({ canvas, logarithmicDepthBuffer: true });
	renderer.setSize(64, 64); renderer.setClearColor(0);
	let shaderError = '';
	renderer.debug.onShaderError = (gl, _program, vertex, fragment) => { shaderError = gl.getShaderInfoLog(vertex) + '\n' + gl.getShaderInfoLog(fragment); };
	const scene = new THREE.Scene(), camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.1, 10);
	camera.position.z = 2;
	const geometry = new THREE.PlaneGeometry(1.8, 1.8).toNonIndexed();
	geometry.setAttribute('meshCorner', new THREE.BufferAttribute(overlayCorners(2), 2, true));
	const color = new THREE.Color(0xf2338c), colors = new Float32Array(18);
	for (let i = 0; i < 18; i += 3) colors.set([color.r, color.g, color.b], i);
	geometry.setAttribute('color', new THREE.BufferAttribute(colors, 3));
	const old = [false, true].map(wireframe => new THREE.Mesh(geometry, new THREE.MeshBasicMaterial({ vertexColors: true, wireframe, transparent: true, side: THREE.DoubleSide, depthWrite: false, opacity: wireframe ? 0.95 : 0.35 })));
	for (const mesh of old) scene.add(mesh);
	renderer.render(scene, camera); const previousDrawCalls = renderer.info.render.calls;
	for (const mesh of old) { scene.remove(mesh); (mesh.material as THREE.Material).dispose(); }
	const material = overlayMaterial(false), mesh = new THREE.Mesh(geometry, material); scene.add(mesh);
	const draw = () => {
		renderer.render(scene, camera);
		if (shaderError) throw Error(shaderError);
		const pixels = new Uint8Array(64 * 64 * 4), gl = renderer.getContext();
		gl.readPixels(0, 0, 64, 64, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
		if (gl.getError() !== gl.NO_ERROR) throw Error('WebGL readback failed');
		let lit = 0; for (let i = 0; i < pixels.length; i += 4) if (pixels[i] > 10) lit++;
		return { calls: renderer.info.render.calls, triangles: renderer.info.render.triangles, lit };
	};
	const filled = draw(); material.uniforms.wireOnly.value = 1; const wire = draw();
	geometry.setIndex([0, 1, 2]); const filtered = draw();
	if (previousDrawCalls !== 4 || filled.calls !== 1 || filled.triangles !== 2 || wire.triangles !== 2 || filled.lit <= wire.lit || !wire.lit || filtered.triangles !== 1) throw Error(JSON.stringify({ previousDrawCalls, filled, wire, filtered }));
	geometry.dispose(); material.dispose(); renderer.dispose();
	return { previousDrawCalls, filled, wire, filtered, allTrianglesPreserved: true };
}
