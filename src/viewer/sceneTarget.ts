import * as THREE from 'three';

/** Keep scene color/depth/stencil attachments consistent, including multisample resolve. */
export function createSceneTarget(renderer:THREE.WebGLRenderer):THREE.WebGLRenderTarget {
 return new THREE.WebGLRenderTarget(1,1,{
  type:THREE.HalfFloatType,
  samples:Math.min(4,renderer.capabilities.maxSamples),
  depthBuffer:true,
  stencilBuffer:true,
  depthTexture:new THREE.DepthTexture(1,1,THREE.FloatType,undefined,undefined,undefined,undefined,undefined,undefined,THREE.DepthStencilFormat),
 });
}
