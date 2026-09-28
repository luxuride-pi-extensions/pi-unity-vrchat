---
name: unity-with-reference
description: Use attached reference images or style cues while planning Unity work.
---

# Unity With Reference

Use this skill when the user supplies an image, a moodboard, or a style
description and expects the Unity result to match it.

## Reading a reference for Unity specifically

A reference image describes appearance, not structure. Extract, in order:

1. **Silhouette** — the outline of the object as seen. This is geometry, and it
   is the thing most often wrong. Compare the render's silhouette to the
   reference before touching materials.
2. **Proportion** — relative sizes of parts. A cube with correct material and
   wrong proportions reads as wrong no matter how good the shading is.
3. **Material cues** — roughness from specular highlight size, metalness from
   colored (not white) reflections, emissive from parts that glow independently
   of the light.
4. **Color and value** — the base albedo, checked in shadow *and* highlight.
5. **Lighting** — direction, hardness (shadow edge), and intensity. A wrong
   light setup makes a correct model look wrong.

## Plan before building

State which of the five you will address and how, in one or two sentences, then
build. Do not start by guessing at materials when the silhouette is the problem.

## Screenshot matching

When asked to match a screenshot, also match the *camera*: focal length (FOV),
height, and angle. Match lighting direction even if that means adding a light
that the reference clearly shows. A model that only looks right from a
particular angle and light is not finished — say so if that is the best you can
do.

## Adapting rather than copying

When the user says "like X" they mean the *category* of thing, not X's exact
design. Build the archetype: for "like a brutalist library" that is heavy
concrete, deep shadow, monumental shelving — not that specific building.
