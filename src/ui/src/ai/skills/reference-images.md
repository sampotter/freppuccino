---
name: reference-images
description: How to model from photos / sketches the user attached. Load whenever the prompt has reference images.
---
## Reading the reference
- The user's images arrive in a message starting "[reference images attached by the user: ...]". They stay in the
  conversation for the whole chat, so you can look at them again after each capture.
- First describe to yourself what the object is, then break it into simple parts (body, neck, handle, legs, ...).
  Each part should map to one primitive or a small CSG group. Ignore background, shadows, reflections and texture.
- Several images of the same object are different views of it: combine them (front + side give width, depth, height).
- A sketch or drawing is a shape description, not a photo: keep its proportions, smooth out wobbly lines.

## Size and proportions
- Images have no scale. If the user gave a size, use it. Otherwise identify the object and use its typical real size
  in the document's units, then derive every other dimension from ratios measured in the image
  (e.g. "the handle is ~0.6 of the body height", "the neck is ~1/3 of the body width").
- Write the ratios into your plan before building. Perspective shrinks far parts: trust straight-on views more.

## Matching colors
- Colors are part of matching the reference: every part gets the color it has in the image. Don't leave parts
  default grey.
- Pick each part's dominant color under even light as "#RRGGBB": ignore highlights, shadows and reflections
  (a red mug with a white highlight is red). Note them in your plan next to the parts.
- Set colors on create_primitive, or afterwards for many parts in one go with set_color colors: [{ref, color}, ...].
- The render shades parts with light from above, so a captured color looks a little darker than the hex you set,
  especially on sides and undersides. Compare the top-lit areas with the reference before you change a color.
- Small colored details (eyes, buttons, stripes, labels) are separate small primitives with their own color. Keep them
  in their own union with a small k, so their color doesn't fade into the body.
- After the shape is right, compare colors in the capture with the reference and fix them with set_color.

## Checking against the reference
- Capture the view that matches the reference camera angle (front for a straight-on photo, perspective for a 3/4 shot)
  and compare silhouettes side by side: overall aspect ratio first, then part sizes, then details.
- Fix the biggest mismatch first. Say what you could not match (fine detail, textures, non-smooth features).
