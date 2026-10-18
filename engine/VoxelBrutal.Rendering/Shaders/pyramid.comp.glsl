#version 450
layout(local_size_x=8,local_size_y=8) in;
layout(set=0,binding=0) uniform sampler2D sourceDepth;
layout(set=0,binding=1,r32f) uniform writeonly image2D targetMip;
layout(push_constant) uniform Reduction { uvec4 info; } reduction;
void main() {
    ivec2 p=ivec2(gl_GlobalInvocationID.xy);
    if(any(greaterThanEqual(p,imageSize(targetMip)))) return;
    float depth=0;
    if(reduction.info.x==0u) {
        // Exact copy: power-of-two padding remains uncovered far depth, even for odd viewports.
        if(all(lessThan(p,ivec2(reduction.info.yz)))) depth=texelFetch(sourceDepth,p,0).r;
    } else {
        ivec2 sourceMax=textureSize(sourceDepth,0)-1;
        ivec2 s=p*2;
        depth=min(min(texelFetch(sourceDepth,min(s,sourceMax),0).r,
                      texelFetch(sourceDepth,min(s+ivec2(1,0),sourceMax),0).r),
                  min(texelFetch(sourceDepth,min(s+ivec2(0,1),sourceMax),0).r,
                      texelFetch(sourceDepth,min(s+ivec2(1,1),sourceMax),0).r));
    }
    imageStore(targetMip,p,vec4(depth));
}
