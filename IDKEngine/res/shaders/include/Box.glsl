#ifndef Box_H
#define Box_H

AppInclude(include/Constants.glsl)

struct Box
{
    vec3 Min;
    vec3 Max;
};

vec3 BoxSize(Box box)
{
    return box.Max - box.Min;
}

vec3 BoxCenter(Box box)
{
    return (box.Max + box.Min) * 0.5;
}

float BoxArea(Box box)
{
    vec3 size = BoxSize(box);
    return 2.0 * (size.x * size.y + size.x * size.z + size.z * size.y);  
}

vec3 BoxGetVertexPos(Box box, int index)
{
    bool isMaxX = (index & 1) != 0;
    bool isMaxY = (index & 2) != 0;
    bool isMaxZ = (index & 4) != 0;
    return vec3(isMaxX ? box.Max.x : box.Min.x, isMaxY ? box.Max.y : box.Min.y, isMaxZ ? box.Max.z : box.Min.z);
}

void BoxGrowToFit(inout Box box, vec3 point)
{
    box.Min = min(box.Min, point);
    box.Max = max(box.Max, point);
}

Box BoxTransform(Box box, mat4 matrix)
{
    Box newBox = box;
    newBox.Min = vec3(FLOAT_MAX);
    newBox.Max = vec3(FLOAT_MIN);
    
    for (int i = 0; i < 8; i++)
    {
        vec3 vertexPos = BoxGetVertexPos(box, i);
        BoxGrowToFit(newBox, (matrix * vec4(vertexPos, 1.0)).xyz);
    }

    return newBox;
}

Box BoxTransformPerspective(Box box, mat4 matrix, out bool vertexBehindFrustum)
{
    Box newBox = box;
    newBox.Min = vec3(FLOAT_MAX);
    newBox.Max = vec3(FLOAT_MIN);
    vertexBehindFrustum = false;

    for (int i = 0; i < 8; i++)
    {
        vec3 vertexPos = BoxGetVertexPos(box, i);
        vec4 clipPos = (matrix * vec4(vertexPos, 1.0));
        if (clipPos.w <= 0.0)
        {
            vertexBehindFrustum = true;
        }

        vec3 ndc = clipPos.xyz / clipPos.w;

        BoxGrowToFit(newBox, ndc);
    }

    return newBox;
}

#endif