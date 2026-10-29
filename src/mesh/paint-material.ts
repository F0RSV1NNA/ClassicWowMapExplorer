import * as THREE from 'three';

/** Paint each screen pixel once; overlap is a stencil union, independent of triangle depth rounding. */
export function paintMaterial():THREE.ShaderMaterial {
    return new THREE.ShaderMaterial({vertexColors:true,side:THREE.DoubleSide,forceSinglePass:true,transparent:true,depthTest:true,depthWrite:false,depthFunc:THREE.LessEqualDepth,stencilWrite:true,stencilRef:1,stencilFunc:THREE.NotEqualStencilFunc,stencilFail:THREE.KeepStencilOp,stencilZFail:THREE.KeepStencilOp,stencilZPass:THREE.ReplaceStencilOp,polygonOffset:true,polygonOffsetFactor:-3,polygonOffsetUnits:-3,
                vertexShader:`
                    attribute vec3 paintBrush; attribute vec2 paintBand;
                    varying vec3 surface; varying vec3 brush; varying vec2 band; varying vec3 tint;
                    #include <common>
                    #include <logdepthbuf_pars_vertex>
                    void main(){surface=position;brush=paintBrush;band=paintBand;tint=color;gl_Position=projectionMatrix*modelViewMatrix*vec4(position,1.0);
                    #include <logdepthbuf_vertex>
                    }`,
                fragmentShader:`
                    varying vec3 surface; varying vec3 brush; varying vec2 band; varying vec3 tint;
                    #include <common>
                    #include <logdepthbuf_pars_fragment>
                    void main(){if(distance(surface.xz,brush.xy)>brush.z||abs(surface.y-band.x)>band.y)discard;
                    #include <logdepthbuf_fragment>
                    #ifdef USE_LOGARITHMIC_DEPTH_BUFFER
                    gl_FragDepth=max(0.0,gl_FragDepth-0.00001);
                    #endif
                    gl_FragColor=vec4(tint,.3);
                    #include <tonemapping_fragment>
                    #include <colorspace_fragment>
                    }`});
}

