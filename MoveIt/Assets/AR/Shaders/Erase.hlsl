#ifndef MOVEIT_ERASE_INCLUDED
#define MOVEIT_ERASE_INCLUDED

// Diminished reality for one erased object.
//
// Best: an inpainted photo (EraseInpainter.cs). When the user confirms the erase, one photo is taken and the object
// painted out of it with texture from the rest of the photo. The floor or wall point behind the object is projected
// into that photo, so the fill is pinned to the room and stays still as the user moves.
// Until that's ready, and for spots the photo doesn't cover, real pixels where possible. BackgroundMemory keeps snapshots of the room from earlier viewpoints; the point
// behind the object is looked up in each, and used if that snapshot's depth shows it was actually visible.
// Fallback, for spots never seen (e.g. right under the object):
// The object is covered by a box. The room around it is modelled as the floor plus up to two walls
// (the wall behind it and, in a corner, the side wall). For every pixel of the box we find which of
// those surfaces the view ray would hit if the object weren't there, then copy the camera image from a
// point on that same surface that the box does not hide. The point is found by walking along the
// surface until the camera can see it past the box, then mirroring across that edge, so the floor or
// wall texture continues seamlessly and in the right perspective.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

float4x4 _EraseWorldToBox;  // World space to box space: metres, origin at the box centre, axes along the box.
float3 _EraseHalfExtents;   // Half size of the box, including padding.
float _EraseFloorY;         // World height of the floor under the object.
float4 _EraseWallPoint;     // First wall (or stand-in backdrop): a point on it. Always used.
float3 _EraseWallNormal;    // First wall's horizontal normal, pointing into the room.
float4 _EraseWall2Point;    // Second wall, for corners: a point on it; w = 1 when there is one.
float3 _EraseWall2Normal;   // Second wall's horizontal normal, pointing into the room.
float _EraseMargin;         // Extra distance past the edge before sampling.
float _EraseSkirt;          // How far around the box the floor is also replaced, to remove the object's shadow.

// ARCore's environment depth image, to avoid copying from spots where another object stands in front of the
// floor or wall. _EraseDepthScale is 0 when there's no depth.
TEXTURE2D(_EraseDepthTexture);
SAMPLER(sampler_EraseDepthTexture);
float4x4 _EraseDisplayMatrix;
float _EraseDepthScale;

// Snapshots of the room from earlier viewpoints (see BackgroundMemory.cs). Must match maxSnapshots there.
#define MEMORY_SNAPSHOTS 8
TEXTURE2D_ARRAY(_MemoryColors);
SAMPLER(sampler_MemoryColors);
TEXTURE2D_ARRAY(_MemoryDepths);
SAMPLER(sampler_MemoryDepths);
float4x4 _MemoryViewProj[MEMORY_SNAPSHOTS];
float4x4 _MemoryDisplay[MEMORY_SNAPSHOTS];
float4 _MemoryPositions[MEMORY_SNAPSHOTS];   // xyz: camera position; w: depth scale to metres
float _MemoryCount;

// Inpainted photo of the spot (see above). Alpha says what it shows: 1 painted in (the erased object was there),
// about 0.6 floor, 0 anything else. _EraseHasInpaint is 1 once there is one.
TEXTURE2D(_EraseInpaintTex);
SAMPLER(sampler_EraseInpaintTex);
float4x4 _EraseInpaintViewProj;
float _EraseHasInpaint;

// Top-down map of the floor around the object, built from the inpainted photo, for floor the photo doesn't reach
// (seen from the side). _EraseFloorMapMatrix maps a world point to its UV; _EraseHasFloorMap is 1 once there is one.
TEXTURE2D(_EraseFloorMap);
SAMPLER(sampler_EraseFloorMap);
float4x4 _EraseFloorMapMatrix;
float _EraseHasFloorMap;

#define ERASE_SKIRT_HEIGHT 0.03

#define ERASE_STEPS 32
#define ERASE_STEP 0.08
#define ERASE_NONE 1e9

#define SURFACE_FLOOR 0
#define SURFACE_WALL 1
#define SURFACE_WALL2 2

struct Attributes
{
    float4 positionOS : POSITION;
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
};

Varyings EraseVert(Attributes input)
{
    Varyings output;
    output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
    output.positionCS = TransformWorldToHClip(output.positionWS);
    return output;
}

// True when the segment from a to a + d (box space) passes through the axis-aligned box at center with halfSize.
bool SegmentHitsAabb(float3 a, float3 d, float3 center, float3 halfSize)
{
    d += (abs(d) < 1e-6) ? 1e-6 : 0.0;
    float3 inv = 1.0 / d;
    float3 t0 = (center - halfSize - a) * inv;
    float3 t1 = (center + halfSize - a) * inv;
    float3 tMin = min(t0, t1);
    float3 tMax = max(t0, t1);
    float tNear = max(max(tMin.x, tMin.y), tMin.z);
    float tFar = min(min(tMax.x, tMax.y), tMax.z);
    return tNear <= tFar && tFar >= 0.0 && tNear <= 1.0;
}

// True when the line of sight from the camera to p passes through the box, or through the thin slab of
// floor around its base (the skirt) that hides the object's contact shadow.
bool SightBlockedByBox(float3 cameraBS, float3 pWS)
{
    float3 pBS = mul(_EraseWorldToBox, float4(pWS, 1.0)).xyz;
    float3 d = pBS - cameraBS;
    if (SegmentHitsAabb(cameraBS, d, 0.0, _EraseHalfExtents))
        return true;

    float3 skirtCenter = float3(0.0, -_EraseHalfExtents.y, 0.0);
    float3 skirtHalf = float3(_EraseHalfExtents.x + _EraseSkirt, ERASE_SKIRT_HEIGHT, _EraseHalfExtents.z + _EraseSkirt);
    return SegmentHitsAabb(cameraBS, d, skirtCenter, skirtHalf);
}

bool HasWall2()
{
    return _EraseWall2Point.w > 0.5;
}

// True when p is on the room side of every wall except the one it lies on.
bool InRoom(float3 p, int surface)
{
    const float tolerance = 1e-3;
    if (surface != SURFACE_WALL && dot(p - _EraseWallPoint.xyz, _EraseWallNormal) < -tolerance)
        return false;
    if (HasWall2() && surface != SURFACE_WALL2 && dot(p - _EraseWall2Point.xyz, _EraseWall2Normal) < -tolerance)
        return false;
    if (surface != SURFACE_FLOOR && p.y < _EraseFloorY - tolerance)
        return false;
    return true;
}

float IntersectWall(float3 cameraWS, float3 dir, float3 wallPoint, float3 wallNormal)
{
    float dn = dot(dir, wallNormal);
    if (dn > -1e-4)
        return ERASE_NONE;

    float t = dot(wallPoint - cameraWS, wallNormal) / dn;
    return t > 0.0 ? t : ERASE_NONE;
}

// Where the view ray first hits the room behind the object, and which surface it hits.
bool HitBackground(float3 cameraWS, float3 dir, out float3 hit, out int surface)
{
    float best = ERASE_NONE;
    surface = SURFACE_FLOOR;

    if (dir.y < -1e-4)
    {
        float t = (_EraseFloorY - cameraWS.y) / dir.y;
        if (t > 0.0 && InRoom(cameraWS + dir * t, SURFACE_FLOOR))
            best = t;
    }

    float tWall = IntersectWall(cameraWS, dir, _EraseWallPoint.xyz, _EraseWallNormal);
    if (tWall < best && InRoom(cameraWS + dir * tWall, SURFACE_WALL))
    {
        best = tWall;
        surface = SURFACE_WALL;
    }

    if (HasWall2())
    {
        float tWall2 = IntersectWall(cameraWS, dir, _EraseWall2Point.xyz, _EraseWall2Normal);
        if (tWall2 < best && InRoom(cameraWS + dir * tWall2, SURFACE_WALL2))
        {
            best = tWall2;
            surface = SURFACE_WALL2;
        }
    }

    hit = cameraWS + dir * best;
    return best < ERASE_NONE;
}

// How far p must move along dir (on its surface) before the camera can see it past the box.
float ExitDistance(float3 cameraBS, float3 p, float3 dir, int surface)
{
    float previous = 0.0;

    [loop]
    for (int i = 1; i <= ERASE_STEPS; i++)
    {
        float s = i * ERASE_STEP;
        float3 q = p + dir * s;

        // Walking off the surface (past a wall or below the floor) leads nowhere visible.
        if (!InRoom(q, surface))
            return ERASE_NONE;

        if (!SightBlockedByBox(cameraBS, q))
        {
            float lo = previous;
            float hi = s;

            [unroll]
            for (int j = 0; j < 4; j++)
            {
                float mid = 0.5 * (lo + hi);
                if (SightBlockedByBox(cameraBS, p + dir * mid))
                    lo = mid;
                else
                    hi = mid;
            }

            return hi;
        }

        previous = s;
    }

    return ERASE_NONE;
}

float2 WorldToScreenUV(float3 p)
{
    float4 clip = TransformWorldToHClip(p);
    float2 ndc = clip.xy / clip.w;
    return float2(ndc.x * 0.5 + 0.5, ndc.y * 0.5 * _ProjectionParams.x + 0.5);
}

float WorldToDeviceDepth(float3 p)
{
    float4 clip = TransformWorldToHClip(p);
    float depth = clip.z / clip.w;
#if !UNITY_REVERSED_Z
    depth = depth * 0.5 + 0.5;
#endif
    return depth;
}

bool OnScreen(float2 uv)
{
    return all(uv >= 0.0) && all(uv <= 1.0);
}

// Measured depth at a screen UV in metres, or 0 when there's no depth.
float MeasuredDepth(float2 uv)
{
    if (_EraseDepthScale <= 0.0)
        return 0.0;

    float2 depthUV = mul(float4(uv, 1.0, 0.0), _EraseDisplayMatrix).xy;
    return SAMPLE_TEXTURE2D_LOD(_EraseDepthTexture, sampler_EraseDepthTexture, depthUV, 0).r * _EraseDepthScale;
}

// True when the camera actually sees the surface at p: the measured depth there isn't much nearer than p,
// so nothing else stands in front of it.
bool SurfaceVisible(float3 p)
{
    if (_EraseDepthScale <= 0.0)
        return true;

    float2 uv = WorldToScreenUV(p);
    if (!OnScreen(uv))
        return false;

    float measured = MeasuredDepth(uv);
    if (measured <= 0.01)
        return true;

    float expected = -TransformWorldToView(p).z;
    return measured > expected - (0.1 + 0.04 * expected);
}

// True when what the camera sees at this pixel is the erased object itself (or its shadow on the floor right
// around it): the measured depth lands inside the box or the skirt. Everything else seen through the box's
// outline is real background (furniture behind it, a person in front of it) and keeps its image and depth,
// so the box doesn't have to fit tightly. Without depth, every pixel inside the outline counts.
bool ShowsErasedObject(float3 cameraWS, float3 dir, float2 uv)
{
    float measured = MeasuredDepth(uv);
    if (measured <= 0.01)
        return true;

    // Depth is measured along the camera's view axis, not along the ray.
    float3 forward = -UNITY_MATRIX_V[2].xyz;
    float3 measuredWS = cameraWS + dir * (measured / max(dot(dir, forward), 1e-3));
    float3 p = mul(_EraseWorldToBox, float4(measuredWS, 1.0)).xyz;

    // Slack for depth noise at the object's edges.
    const float slack = 0.05;
    if (all(abs(p) <= _EraseHalfExtents + slack))
        return true;

    float3 skirtHalf = float3(_EraseHalfExtents.x + _EraseSkirt, ERASE_SKIRT_HEIGHT + slack, _EraseHalfExtents.z + _EraseSkirt);
    return all(abs(p - float3(0.0, -_EraseHalfExtents.y, 0.0)) <= skirtHalf);
}

// From the edge of what the box hides (distance s along dir), finds a point on the same surface to copy:
// the mirror image across the edge if the camera sees it (and mirror is set), otherwise the nearest clear point
// just past the edge, which stretches the edge outwards.
bool FindSamplePoint(float3 hit, float3 dir, float s, int surface, bool mirror, out float3 samplePoint)
{
    samplePoint = hit + dir * (2.0 * s + _EraseMargin);
    if (mirror && InRoom(samplePoint, surface) && SurfaceVisible(samplePoint))
        return true;

    [loop]
    for (int i = 0; i < 10; i++)
    {
        float3 q = hit + dir * (s + _EraseMargin + i * ERASE_STEP);
        if (!InRoom(q, surface))
            break;
        if (SurfaceVisible(q))
        {
            samplePoint = q;
            return true;
        }
    }

    // Nothing clear found: copy the mirror image anyway (in a cluttered room it shows clutter, which looks far
    // more natural than stretching a single row of pixels into streaks), unless it's off screen or off the surface.
    float3 mirrored = hit + dir * (2.0 * s + _EraseMargin);
    samplePoint = mirror && InRoom(mirrored, surface) && OnScreen(WorldToScreenUV(mirrored))
        ? mirrored
        : hit + dir * (s + _EraseMargin);
    return false;
}

float MemoryDepth(int k, float2 uv)
{
    float2 depthUV = mul(float4(uv, 1.0, 0.0), _MemoryDisplay[k]).xy;
    return SAMPLE_TEXTURE2D_ARRAY_LOD(_MemoryDepths, sampler_MemoryDepths, depthUV, k, 0).r * _MemoryPositions[k].w;
}

// True when snapshot k saw the point at uv (distance expected) itself, not something in front of it. The depth
// image is coarse and blurry at object edges, so nothing may be nearer anywhere in a small ring around the point
// either; otherwise colours from the edge of the erased object leak in as a faint outline.
bool SeenInSnapshot(int k, float2 uv, float expected)
{
    float tolerance = 0.05 + 0.03 * expected;
    float measured = MemoryDepth(k, uv);
    if (measured <= 0.01 || abs(measured - expected) > tolerance)
        return false;

    const float ring = 0.012;
    float2 offsets[8] =
    {
        float2(ring, 0), float2(-ring, 0), float2(0, ring), float2(0, -ring),
        float2(ring, ring) * 0.7, float2(-ring, ring) * 0.7, float2(ring, -ring) * 0.7, float2(-ring, -ring) * 0.7,
    };

    [unroll]
    for (int i = 0; i < 8; i++)
    {
        float neighbour = MemoryDepth(k, uv + offsets[i]);
        if (neighbour > 0.01 && neighbour < expected - tolerance - 0.1)
            return false;
    }

    return true;
}

// Looks up the room point p in the snapshots. Uses a snapshot only if its depth at p matches p's distance, i.e.
// nothing (like the erased object) stood in front of p from there. Of those, prefers the one whose viewing
// direction is closest to the current one, so perspective and lighting match best.
bool SampleMemory(float3 p, float3 cameraWS, out half3 color)
{
    color = 0;
    float bestScore = -2.0;
    int bestIndex = -1;
    float2 bestUV = 0;
    float3 viewDir = normalize(p - cameraWS);

    [loop]
    for (int k = 0; k < MEMORY_SNAPSHOTS; k++)
    {
        if (k >= (int)_MemoryCount)
            break;

        float4 clip = mul(_MemoryViewProj[k], float4(p, 1.0));
        if (clip.w <= 0.05)
            continue;

        float2 uv = clip.xy / clip.w * 0.5 + 0.5;
        if (any(uv < 0.01) || any(uv > 0.99))
            continue;

        // If the line from that snapshot's camera to p passes through the box, the object was in the way. This
        // catches what depth can't: where the object meets the floor, its depth is nearly the floor's.
        float3 snapshotBS = mul(_EraseWorldToBox, float4(_MemoryPositions[k].xyz, 1.0)).xyz;
        float3 pointBS = mul(_EraseWorldToBox, float4(p, 1.0)).xyz;
        if (SegmentHitsAabb(snapshotBS, pointBS - snapshotBS, 0.0, _EraseHalfExtents))
            continue;

        if (!SeenInSnapshot(k, uv, clip.w))
            continue;

        float score = dot(normalize(p - _MemoryPositions[k].xyz), viewDir);
        if (score > bestScore)
        {
            bestScore = score;
            bestIndex = k;
            bestUV = uv;
        }
    }

    if (bestIndex < 0)
        return false;

    color = SAMPLE_TEXTURE2D_ARRAY_LOD(_MemoryColors, sampler_MemoryColors, bestUV, bestIndex, 0).rgb;
    return true;
}

bool HasInpaint()
{
    return _EraseHasInpaint > 0.5;
}

// The room point p as seen in the inpainted photo, if the photo covers it, with the photo's alpha there.
bool SampleInpainted(float3 p, out half3 color, out float alpha)
{
    color = 0;
    alpha = 0.0;
    if (!HasInpaint())
        return false;

    float4 clip = mul(_EraseInpaintViewProj, float4(p, 1.0));
    if (clip.w <= 0.05)
        return false;

    float2 uv = clip.xy / clip.w * 0.5 + 0.5;
    if (any(uv < 0.002) || any(uv > 0.998))
        return false;

    half4 sample = SAMPLE_TEXTURE2D_LOD(_EraseInpaintTex, sampler_EraseInpaintTex, uv, 0);
    color = sample.rgb;
    alpha = sample.a;
    return true;
}

// True when the live depth shows something clearly in front of the erased box at this pixel (the user's hand, or
// furniture between the camera and the spot when looking from another side): that stays visible.
bool SomethingInFrontOfBox(float3 cameraWS, float3 dir, float2 uv)
{
    float measured = MeasuredDepth(uv);
    if (measured <= 0.01)
        return false;

    float3 originBS = mul(_EraseWorldToBox, float4(cameraWS, 1.0)).xyz;
    float3 dirBS = mul((float3x3)_EraseWorldToBox, dir);
    dirBS += (abs(dirBS) < 1e-6) ? 1e-6 : 0.0;
    float3 t0 = (-_EraseHalfExtents - originBS) / dirBS;
    float3 t1 = (_EraseHalfExtents - originBS) / dirBS;
    float entry = max(max(min(t0.x, t1.x), min(t0.y, t1.y)), min(t0.z, t1.z));

    float3 forward = -UNITY_MATRIX_V[2].xyz;
    float alongRay = measured / max(dot(dir, forward), 1e-3);
    return alongRay < max(entry, 0.0) - (0.1 + 0.04 * alongRay);
}

// The floor point p in the floor map, if the map covers it.
bool SampleFloorMap(float3 p, out half3 color)
{
    color = 0;
    if (_EraseHasFloorMap < 0.5)
        return false;

    float2 uv = mul(_EraseFloorMapMatrix, float4(p, 1.0)).xy;
    if (any(uv < 0.002) || any(uv > 0.998))
        return false;

    color = SAMPLE_TEXTURE2D_LOD(_EraseFloorMap, sampler_EraseFloorMap, uv, 0).rgb;
    return true;
}

// Whether this pixel shows the erased object and gets replaced. Wherever the inpainted photo covers the background
// point, the whole outline is replaced from it (unless something is in front of the box): the photo shows either
// the painted-in spot or what really was there, and it doesn't flicker or leave edges of the object showing the
// way the noisy live depth does. Outside the photo, the live depth decides.
bool ReplacePixel(float3 cameraWS, float3 dir, float3 positionWS, float3 hit, int surface, out bool inPhoto,
    out half3 photoColor, out bool inMap, out half3 mapColor)
{
    // Kept as numbers rather than bools: Unity's Metal translation miscompiles a select between bool results.
    float alpha;
    bool covered = SampleInpainted(hit, photoColor, alpha);

    // Floor is taken from the photo only where the photo shows floor there; seen from the side, a floor point behind
    // the object can be where the photo showed something else (the side of a bed), which would be pasted onto the
    // floor. The floor map has floor everywhere.
    float usePhoto = covered ? 1.0 : 0.0;
    if (surface == SURFACE_FLOOR && alpha < 0.4)
        usePhoto = 0.0;
    inPhoto = usePhoto > 0.5;
    inMap = false;
    mapColor = 0;
    if (!inPhoto && surface == SURFACE_FLOOR)
        inMap = SampleFloorMap(hit, mapColor);

    float2 uv = WorldToScreenUV(positionWS);
    float replace;
    if (covered && alpha > 0.8)
    {
        // The photo saw the erased object here: replace, unless something is in front of it now.
        replace = SomethingInFrontOfBox(cameraWS, dir, uv) ? 0.0 : 1.0;
    }
    else
    {
        // Elsewhere (seen from another side than the photo), the live depth decides, so furniture behind the box
        // stays visible.
        replace = ShowsErasedObject(cameraWS, dir, uv) ? 1.0 : 0.0;
    }

    return replace > 0.5;
}

// From inside the box there's nothing sensible to show (every view ray starts inside the erased volume), so
// the region isn't drawn while the camera is in it, e.g. when the user walks to where a bed was erased.
bool CameraInsideBox(float3 cameraBS)
{
    return all(abs(cameraBS) <= _EraseHalfExtents + 0.1);
}

// Depth-only pass, drawn before opaque objects: replaces the real object's depth (from ARCore
// occlusion) with the depth of the floor or wall behind it, so furniture can stand where it was.
half4 EraseDepthFrag(Varyings input, out float depth : SV_Depth) : SV_Target
{
    float3 cameraWS = GetCameraPositionWS();
    float3 dir = normalize(input.positionWS - cameraWS);

    float3 hit;
    int surface;
    if (!HitBackground(cameraWS, dir, hit, surface))
        discard;

    // Only pixels that show the erased object (or its shadow) get the background's depth; the rest keep theirs.
    float3 cameraBS = mul(_EraseWorldToBox, float4(cameraWS, 1.0)).xyz;
    bool inPhoto;
    half3 photoColor;
    bool inMap;
    half3 mapColor;
    if (CameraInsideBox(cameraBS) || !SightBlockedByBox(cameraBS, hit) ||
        !ReplacePixel(cameraWS, dir, input.positionWS, hit, surface, inPhoto, photoColor, inMap, mapColor))
        discard;

    depth = WorldToDeviceDepth(hit);
    return 0;
}

// Colour pass, drawn after opaque objects: paints the floor or walls over the real object.
// It writes the background depth so virtual furniture in front of it stays visible.
half4 EraseFillFrag(Varyings input, out float depth : SV_Depth) : SV_Target
{
    float3 cameraWS = GetCameraPositionWS();
    float3 dir = normalize(input.positionWS - cameraWS);

    float3 hit;
    int surface;
    if (!HitBackground(cameraWS, dir, hit, surface))
        discard;

    // The drawn box is larger than the erased volume (it also covers the shadow skirt); pixels whose
    // background isn't hidden are already correct, so leave them.
    float3 cameraBS = mul(_EraseWorldToBox, float4(cameraWS, 1.0)).xyz;
    bool inPhoto;
    half3 photoColor;
    bool inMap;
    half3 mapColor;
    if (CameraInsideBox(cameraBS) || !SightBlockedByBox(cameraBS, hit) ||
        !ReplacePixel(cameraWS, dir, input.positionWS, hit, surface, inPhoto, photoColor, inMap, mapColor))
        discard;

    depth = WorldToDeviceDepth(hit);
#if UNITY_REVERSED_Z
    depth += 1e-6;
#else
    depth -= 1e-6;
#endif

    // The inpainted photo: painted-in where the object hid the spot, the real room where it didn't.
    if (inPhoto)
        return half4(photoColor, 1.0);

    // Floor the photo doesn't reach, from the floor map.
    if (inMap)
        return half4(mapColor, 1.0);

    // Real pixels from an earlier viewpoint, when the point behind the object was seen.
    half3 remembered;
    if (SampleMemory(hit, cameraWS, remembered))
        return half4(remembered, 1.0);

    // Last resort, for views far from where the photo was taken (from the side): copied from around the object,
    // which looks smeared, but better than the object showing through.

    // Directions to walk along the surface. Copying from beside the object is preferred: copying from
    // above (on a wall) or from nearer the camera (on the floor) is often lit differently.
    float3 up = float3(0.0, 1.0, 0.0);
    float3 dirs[3];
    float3 cost = float3(1.0, 1.0, 1.0);
    if (surface == SURFACE_FLOOR)
    {
        float3 toCamera = cameraWS - hit;
        toCamera.y = 0.0;
        toCamera = normalize(toCamera + float3(1e-5, 0.0, 0.0));
        float3 side = cross(up, toCamera);
        dirs[0] = toCamera;
        dirs[1] = side;
        dirs[2] = -side;
        cost.x = 1.3;
    }
    else
    {
        float3 normal = surface == SURFACE_WALL ? _EraseWallNormal : _EraseWall2Normal;
        float3 along = normalize(cross(up, normal));
        dirs[0] = along;
        dirs[1] = -along;
        dirs[2] = up;
        cost.z = 2.5;
    }

    float bestScore = ERASE_NONE;
    float3 bestSample = hit;

    [unroll]
    for (int k = 0; k < 3; k++)
    {
        float s = ExitDistance(cameraBS, hit, dirs[k], surface);
        if (s >= ERASE_NONE)
            continue;

        float3 samplePoint;
        // Upwards on a wall, stretch rather than mirror: mirroring would copy whatever hangs above the object
        // (a picture frame) into the gap.
        bool mirror = !(surface != SURFACE_FLOOR && k == 2);
        bool clear = FindSamplePoint(hit, dirs[k], s, surface, mirror, samplePoint);

        // Prefer directions that reach a clear spot quickly; ones that only find something in the way come last.
        float score = (s + (clear ? 0.0 : 10.0)) * cost[k];
        if (score < bestScore)
        {
            bestScore = score;
            bestSample = samplePoint;
        }
    }

    if (bestScore >= ERASE_NONE)
        discard;

    float2 uv = WorldToScreenUV(bestSample);
    return half4(SampleSceneColor(saturate(uv)), 1.0);
}

#endif
