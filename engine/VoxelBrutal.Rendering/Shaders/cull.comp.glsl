#version 450
layout(local_size_x=64) in;
struct Chunk { vec4 originScale; uvec4 mesh; };
struct Draw { uint vertexCount; uint instanceCount; uint firstVertex; uint firstInstance; };
layout(set=0,binding=1,std430) readonly buffer Chunks { Chunk chunks[]; };
layout(set=0,binding=2,std430) writeonly buffer Commands { Draw commands[]; };
layout(set=0,binding=3) uniform sampler2D depthPyramid;
layout(set=0,binding=4,std430) buffer Results {
    uint earlyCount, lateCount, occludedCount, frustumCount;
    uint drawnQuads, reserved0, reserved1, reserved2;
    uint visibility[];
} results;
layout(push_constant) uniform Frame {
    mat4 viewProjection;
    uint chunkCount, passIndex, occlusionEnabled, reserved;
    vec4 viewportPyramid;
} frame;
bool outside(vec4 plane, vec3 center, vec3 extent) {
    return dot(plane.xyz,center) + plane.w + dot(abs(plane.xyz),extent) < 0;
}
bool occluded(vec3 center, vec3 extent) {
    vec2 rectMin=vec2(1e30), rectMax=vec2(-1e30);
    float nearestDepth=0;
    for(uint corner=0u;corner<8u;++corner) {
        vec3 direction=vec3((corner&1u)!=0u ? 1:-1, (corner&2u)!=0u ? 1:-1, (corner&4u)!=0u ? 1:-1);
        vec4 clip=frame.viewProjection*vec4(center+extent*direction,1);
        // A box touching/crossing the near plane or eye stays visible. No invalid perspective divide.
        if(any(isnan(clip)) || any(isinf(clip)) || clip.w<=0.00001 || clip.z<=0 || clip.z>=clip.w*0.99999) return false;
        vec3 ndc=clip.xyz/clip.w;
        ndc.y=-ndc.y;
        rectMin=min(rectMin,ndc.xy); rectMax=max(rectMax,ndc.xy);
        nearestDepth=max(nearestDepth,ndc.z);
    }
    vec2 screenMin=(rectMin*.5+.5)*frame.viewportPyramid.xy;
    vec2 screenMax=(rectMax*.5+.5)*frame.viewportPyramid.xy;
    // Full pixel border makes raster footprint rounding and silhouette arithmetic conservative.
    ivec2 low=ivec2(clamp(floor(screenMin-1),vec2(0),frame.viewportPyramid.xy-1));
    ivec2 high=ivec2(clamp(ceil(screenMax+1),vec2(0),frame.viewportPyramid.xy-1));
    if(any(greaterThan(low,high))) return false;
    uint footprint=uint(max(high.x-low.x+1,high.y-low.y+1));
    int mip=min(int(ceil(log2(float(max(footprint,1u))))),textureQueryLevels(depthPyramid)-1);
    ivec2 a=low>>mip, b=high>>mip;
    float farthestOccluder=1;
    // The power-of-two cell size covers the complete rectangle with at most four cells.
    for(int y=a.y;y<=b.y;++y) for(int x=a.x;x<=b.x;++x)
        farthestOccluder=min(farthestOccluder,texelFetch(depthPyramid,ivec2(x,y),mip).r);
    // Relative reverse-Z tolerance preserves useful rejection at million-unit distances.
    float bias=max(0.000000000001,nearestDepth*0.0001);
    return farthestOccluder>nearestDepth+bias;
}
void main() {
    uint i=gl_GlobalInvocationID.x;
    if(i>=frame.chunkCount) return;
    Chunk c=chunks[i];
    vec3 extent=vec3(16*c.originScale.w);
    vec3 center=c.originScale.xyz+extent;
    extent+=max(vec3(.01),abs(center)*.000001);
    mat4 t=transpose(frame.viewProjection);
    bool inFrustum=c.mesh.y!=0u && !outside(t[3]+t[0],center,extent) && !outside(t[3]-t[0],center,extent)
        && !outside(t[3]+t[1],center,extent) && !outside(t[3]-t[1],center,extent)
        && !outside(t[2],center,extent) && !outside(t[3]-t[2],center,extent);
    bool seed=c.mesh.z!=0u;
    bool draw=false;
    if(frame.passIndex==0u) {
        draw=inFrustum && (frame.occlusionEnabled==0u || seed);
        if(draw) atomicAdd(results.earlyCount,1u);
        if(frame.occlusionEnabled==0u) {
            results.visibility[i]=inFrustum ? 1u:0u;
            if(inFrustum) atomicAdd(results.frustumCount,1u);
        }
    } else {
        bool hidden=inFrustum && occluded(center,extent);
        bool visible=inFrustum && !hidden;
        results.visibility[i]=visible ? 1u:0u;
        if(inFrustum) atomicAdd(results.frustumCount,1u);
        if(hidden) atomicAdd(results.occludedCount,1u);
        // Test seeds too so an occluded early draw disappears from a future seed set.
        draw=visible && !seed;
        if(draw) atomicAdd(results.lateCount,1u);
    }
    if(draw) atomicAdd(results.drawnQuads,c.mesh.y);
    commands[i]=Draw(c.mesh.y*6u,draw ? 1u:0u,0u,i);
}
