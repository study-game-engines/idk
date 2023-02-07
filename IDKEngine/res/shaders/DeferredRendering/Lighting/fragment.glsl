#version 460 core
#define EPSILON 0.001
#define PI 3.14159265
#extension GL_ARB_bindless_texture : require

layout(location = 0) out vec4 FragColor;

struct Light
{
    vec3 Position;
    float Radius;
    vec3 Color;
    float _pad0;
};

struct PointShadow
{
    samplerCube Texture;
    samplerCubeShadow ShadowTexture;
    
    mat4 ProjViewMatrices[6];

    float NearPlane;
    float FarPlane;
    int LightIndex;
    float _pad0;
};

layout(std140, binding = 0) uniform BasicDataUBO
{
    mat4 ProjView;
    mat4 View;
    mat4 InvView;
    vec3 ViewPos;
    float _pad0;
    mat4 Projection;
    mat4 InvProjection;
    mat4 InvProjView;
    mat4 PrevProjView;
    float NearPlane;
    float FarPlane;
    float DeltaUpdate;
    float Time;
} basicDataUBO;

layout(std140, binding = 1) uniform ShadowDataUBO
{
    #define GLSL_MAX_UBO_POINT_SHADOW_COUNT 16 // used in shader and client code - keep in sync!
    PointShadow PointShadows[GLSL_MAX_UBO_POINT_SHADOW_COUNT];
    int PointCount;
} shadowDataUBO;

layout(std140, binding = 2) uniform LightsUBO
{
    #define GLSL_MAX_UBO_LIGHT_COUNT 256 // used in shader and client code - keep in sync!
    Light Lights[GLSL_MAX_UBO_LIGHT_COUNT];
    int Count;
} lightsUBO;

layout(std140, binding = 4) uniform SkyBoxUBO
{
    samplerCube Albedo;
} skyBoxUBO;

layout(std140, binding = 6) uniform GBufferDataUBO
{
    sampler2D AlbedoAlpha;
    sampler2D NormalSpecular;
    sampler2D EmissiveRoughness;
    sampler2D Velocity;
    sampler2D Depth;
} gBufferDataUBO;

vec3 GetBlinnPhongLighting(Light light, vec3 viewDir, vec3 normal, vec3 albedo, float specular, float roughness, vec3 sampleToLight);
float Visibility(PointShadow pointShadow, vec3 normal, vec3 lightToSample);
vec3 NDCToWorld(vec3 ndc);

uniform bool IsVXGI;

in InOutVars
{
    vec2 TexCoord;
} inData;

void main()
{
    ivec2 imgCoord = ivec2(gl_FragCoord.xy);
    vec2 uv = inData.TexCoord;
    
    float depth = texture(gBufferDataUBO.Depth, uv).r;
    if (depth == 1.0)
    {
        FragColor = vec4(0.0);
        return;
    }

    vec4 albedoAlpha = texture(gBufferDataUBO.AlbedoAlpha, uv);
    vec4 normalSpecular = texture(gBufferDataUBO.NormalSpecular, uv);
    vec4 emissiveRoughness = texture(gBufferDataUBO.EmissiveRoughness, uv);

    vec3 ndc = vec3(uv, depth) * 2.0 - 1.0;
    vec3 fragPos = NDCToWorld(ndc);

    vec3 albedo = albedoAlpha.rgb;
    vec3 normal = normalSpecular.rgb;
    float specular = normalSpecular.a;
    vec3 emissive = emissiveRoughness.rgb;
    float roughness = emissiveRoughness.a;

    vec3 viewDir = normalize(fragPos - basicDataUBO.ViewPos);

    vec3 directLighting = vec3(0.0);
    for (int i = 0; i < shadowDataUBO.PointCount; i++)
    {
        PointShadow pointShadow = shadowDataUBO.PointShadows[i];
        Light light = lightsUBO.Lights[i];
        vec3 sampleToLight = light.Position - fragPos;
        directLighting += GetBlinnPhongLighting(light, viewDir, normal, albedo, specular, roughness, sampleToLight) * Visibility(pointShadow, normal, -sampleToLight);
    }

    for (int i = shadowDataUBO.PointCount; i < lightsUBO.Count; i++)
    {
        Light light = lightsUBO.Lights[i];
        vec3 sampleToLight = light.Position - fragPos;
        directLighting += GetBlinnPhongLighting(light, viewDir, normal, albedo, specular, roughness, sampleToLight);
    }

    vec3 indirectLight = vec3(0.0);
    if (!IsVXGI)
    {
        indirectLight = vec3(0.03) * albedo;
    }

    FragColor = vec4(directLighting + indirectLight + emissive, albedoAlpha.a);
}

vec3 GetBlinnPhongLighting(Light light, vec3 viewDir, vec3 normal, vec3 albedo, float specular, float roughness, vec3 sampleToLight)
{
    float fragToLightLength = length(sampleToLight);

    vec3 lightDir = sampleToLight / fragToLightLength;
    float cosTerm = dot(normal, lightDir);
    if (cosTerm > 0.0)
    {
        vec3 diffuseContrib = light.Color * cosTerm * albedo;  
    
        vec3 specularContrib = vec3(0.0);
        vec3 halfwayDir = normalize(lightDir + -viewDir);
        float temp = dot(normal, halfwayDir);
        // TODO: Implement enery presevering lighting system so we can have specular highlights in VXGI too
        if (!IsVXGI && temp > 0.0)
        {
            float spec = pow(temp, 256.0 * (1.0 - roughness));
            specularContrib = light.Color * spec * specular;
        }
        
        vec3 attenuation = light.Color / (4.0 * PI * fragToLightLength * fragToLightLength);

        return (diffuseContrib + specularContrib) * attenuation;
    }
    return vec3(0.0);
}

// Source: https://learnopengl.com/Advanced-Lighting/Shadows/Point-Shadows
const vec3 SHADOW_SAMPLE_OFFSETS[] =
{
   vec3( 1.0,  1.0,  1.0 ), vec3(  1.0, -1.0,  1.0 ), vec3( -1.0, -1.0,  1.0 ), vec3( -1.0,  1.0,  1.0 ), 
   vec3( 1.0,  1.0, -1.0 ), vec3(  1.0, -1.0, -1.0 ), vec3( -1.0, -1.0, -1.0 ), vec3( -1.0,  1.0, -1.0 ),
   vec3( 1.0,  1.0,  0.0 ), vec3(  1.0, -1.0,  0.0 ), vec3( -1.0, -1.0,  0.0 ), vec3( -1.0,  1.0,  0.0 ),
   vec3( 1.0,  0.0,  1.0 ), vec3( -1.0,  0.0,  1.0 ), vec3(  1.0,  0.0, -1.0 ), vec3( -1.0,  0.0, -1.0 ),
   vec3( 0.0,  1.0,  1.0 ), vec3(  0.0, -1.0,  1.0 ), vec3(  0.0, -1.0, -1.0 ), vec3(  0.0,  1.0, -1.0 )
};

float Visibility(PointShadow pointShadow, vec3 normal, vec3 lightToSample)
{
    float lightToFragLength = length(lightToSample);

    float twoDist = lightToFragLength * lightToFragLength;
    float twoNearPlane = pointShadow.NearPlane * pointShadow.NearPlane;
    float twoFarPlane = pointShadow.FarPlane * pointShadow.FarPlane;
    
    const float MIN_BIAS = EPSILON;
    const float MAX_BIAS = 1.5;
    float twoBias = mix(MAX_BIAS * MAX_BIAS, MIN_BIAS * MIN_BIAS, max(dot(normal, lightToSample / lightToFragLength), 0.0));

    // Map from [nearPlane, farPlane] to [0.0, 1.0]
    float mapedDepth = (twoDist - twoBias - twoNearPlane) / (twoFarPlane - twoNearPlane);
    
    const float DISK_RADIUS = 0.08;
    float shadowFactor = texture(pointShadow.ShadowTexture, vec4(lightToSample, mapedDepth));
    for (int i = 0; i < SHADOW_SAMPLE_OFFSETS.length(); i++)
    {
        shadowFactor += texture(pointShadow.ShadowTexture, vec4(lightToSample + SHADOW_SAMPLE_OFFSETS[i] * DISK_RADIUS, mapedDepth));
    }

    return shadowFactor / 21.0;
}

vec3 NDCToWorld(vec3 ndc)
{
    vec4 worldPos = basicDataUBO.InvProjView * vec4(ndc, 1.0);
    return worldPos.xyz / worldPos.w;
}