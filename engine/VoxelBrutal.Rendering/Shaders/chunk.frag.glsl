#version 450
layout(location=0) flat in uint material;
layout(location=1) flat in vec3 normal;
layout(location=2) in vec3 relativePosition;
layout(location=0) out vec4 outColor;
vec3 palette(uint m) {
    // The demo's TerrainGenerator material IDs. Replace with a material table in the next stage.
    if (m==1u) return vec3(.43,.46,.49); // stone
    if (m==2u) return vec3(.32,.22,.14); // dirt
    if (m==3u) return vec3(.22,.39,.12); // grass
    if (m==4u) return vec3(.75,.73,.59);
    if (m==5u) return vec3(.86,.9,.94);
    return .25 + .5*fract(vec3(.618,.417,.713)*float(m));
}
void main() {
    vec3 sun = normalize(vec3(.45,.85,.25));
    float light = .35 + .65*max(dot(normal,sun),0);
    vec3 color = palette(material)*light;
    float fog = 1.-exp(-length(relativePosition)*.000035);
    outColor = vec4(mix(color,vec3(.48,.64,.79),fog),1);
}
