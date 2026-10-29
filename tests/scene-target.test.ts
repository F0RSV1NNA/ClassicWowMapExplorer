import {test,expect} from 'bun:test';
import * as THREE from 'three';
import {createSceneTarget} from '../src/viewer/sceneTarget';
test('scene stencil depth texture has matching render target attachment and retains float precision',()=>{
 const target=createSceneTarget({capabilities:{maxSamples:4}} as THREE.WebGLRenderer);
 expect(target.depthTexture?.format).toBe(THREE.DepthStencilFormat);
 expect(target.stencilBuffer).toBe(true);
 expect(target.depthBuffer).toBe(true);
 expect(target.depthTexture?.type).toBe(THREE.FloatType);
 target.dispose();
});
