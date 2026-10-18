#version 450
layout(local_size_x=64) in;
struct Chunk { vec4 originScale; uvec4 mesh; };
struct Draw { uint vertexCount; uint instanceCount; uint firstVertex; uint firstInstance; };
layout(set=0,binding=1,std430) readonly buffer Chunks { Chunk chunks[]; };
layout(set=0,binding=2,std430) writeonly buffer Commands { Draw commands[]; };
layout(push_constant) uniform Frame { mat4 viewProjection; uint chunkCount; } frame;
bool outside(vec4 plane, vec3 center, vec3 extent) {
    return dot(plane.xyz,center) + plane.w + dot(abs(plane.xyz),extent) < 0;
}
void main() {
    uint i=gl_GlobalInvocationID.x;
    if(i>=frame.chunkCount) return;
    Chunk c=chunks[i];
    vec3 extent=vec3(16*c.originScale.w);
    vec3 center=c.originScale.xyz+extent;
    // Enlarge at long distances to keep float arithmetic conservative at frustum silhouettes.
    extent += max(vec3(.01),abs(center)*.000001);
    mat4 t=transpose(frame.viewProjection);
    bool visible = !outside(t[3]+t[0],center,extent) && !outside(t[3]-t[0],center,extent)
        && !outside(t[3]+t[1],center,extent) && !outside(t[3]-t[1],center,extent)
        && !outside(t[2],center,extent) && !outside(t[3]-t[2],center,extent);
    commands[i]=Draw(c.mesh.y*6u, visible ? 1u:0u, 0u, i);
}
