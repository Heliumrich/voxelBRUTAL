#version 450
struct Chunk { vec4 originScale; uvec4 mesh; };
layout(set=0,binding=0,std430) readonly buffer Quads { uvec2 quads[]; };
layout(set=0,binding=1,std430) readonly buffer Chunks { Chunk chunks[]; };
layout(push_constant) uniform Frame { mat4 viewProjection; uint chunkCount; } frame;
layout(location=0) flat out uint material;
layout(location=1) flat out vec3 normal;
layout(location=2) out vec3 relativePosition;
const uvec2 corners[6] = uvec2[6](uvec2(0,0),uvec2(1,0),uvec2(1,1),uvec2(0,0),uvec2(1,1),uvec2(0,1));
void main() {
    Chunk c = chunks[gl_InstanceIndex];
    uvec2 q = quads[c.mesh.x + uint(gl_VertexIndex)/6u];
    uint face = (q.x >> 28u) & 7u;
    uint axis = face >> 1u;
    bool positive = (face & 1u) != 0u;
    uint vertex = uint(gl_VertexIndex) % 6u;
    uvec2 uv = corners[vertex];
    // Reversing U preserves a rectangle while reversing its winding.
    if (!positive) uv.x = 1u-uv.x;
    vec3 p = vec3((q.x & 63u) | (((q.y>>16u)&1u)<<6u),
                  ((q.x>>6u)&63u) | (((q.y>>17u)&1u)<<6u),
                  ((q.x>>12u)&63u) | (((q.y>>18u)&1u)<<6u));
    uint width=(((q.x>>18u)&31u) | (((q.y>>19u)&1u)<<5u))+1u;
    uint height=(((q.x>>23u)&31u) | (((q.y>>20u)&1u)<<5u))+1u;
    p[(axis+1u)%3u] += float(uv.x*width);
    p[(axis+2u)%3u] += float(uv.y*height);
    if((q.y & (1u<<21u))!=0u) p*=.5;
    relativePosition = c.originScale.xyz + p*c.originScale.w;
    normal = vec3(0); normal[axis] = positive ? 1 : -1;
    material = q.y & 65535u;
    gl_Position = frame.viewProjection * vec4(relativePosition,1);
    gl_Position.y = -gl_Position.y;
}
