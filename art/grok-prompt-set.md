# Grok mascot — 2026-10-19 proportion and outline revision

Built-in ImageGen edit mode, `transparent_background=true`, no CLI fallback or raster post-processing. The five selected PNGs are copied unchanged to `Assets/Characters/grok_*.png`. Standing images are 1038×1515–1516; collapsed is 1774×887.

The revised idle references the previous Grok idle for identity and Gemini/Claude idle for proportions and sticker style. Sleeping, work1 and panic reference the revised idle plus their old pose. Collapsed references the revised idle and Claude collapsed for the pose. Original generated files remain in the Codex generated_images folder.

Identity: long blonde hair, gray-blue eyes, black square X clip, black jacket/skirt, charcoal tights and ankle boots. The revision reduces the head relative to the body and adds a white sticker border. Exhausted now uses a collapsed pose. Other moods retain the existing pose reuse and procedural animation.

## Exact prompts

### idle

```text
Use case: style-transfer. Edit target image 1 is the blonde Grok mascot; images 2 (Gemini) and 3 (Claude) are proportion, flat illustration, and white sticker border references. Create ONE replacement full-body Grok idle PNG sprite for this existing desktop mascot set. Keep Grok's identity: loose long blonde hair, cool gray-blue half-lidded eyes, black square hairclip with a white diagonal X, black jacket over black highneck top, pleated black skirt, opaque charcoal tights and ankle boots. Correct the oversized head: match the slimmer approximately 3.3-head-high full body proportions of reference 2, with head width comparable to its head, longer legs and a smaller face. Simplify the original's shaded thick hair to the same clean thin dark-brown linework and mostly flat cel colors as references 2 and 3. Add a continuous, crisp white sticker outline around the complete silhouette, about 1 percent of full standing character height, including shoes and hair tips. White outline must be opaque; exterior must be genuinely transparent alpha zero, no haze, no shadow, no colored speckles. Preserve standing idle gesture one hand at hip, other loosely at side. Center the COMPLETE figure with 8 percent transparent clearance on every side. No cropped hair, hands, or boots. No words or extra symbols. The X hairclip stays a diagonal X, never a plus sign. Do not include the reference characters. One full-body standing sprite only.
```

### sleeping

```text
Edit image 1, the revised blonde Grok mascot, into her sleeping standing pose from image 2. Preserve EXACTLY image 1's smaller head, 3.3 heads full body proportions, hair silhouette, thin dark outline, colors, black X hair clip, black jacket/skirt/tights/boots and white sticker perimeter. Close both eyes peacefully and lower both hands loosely to her sides. Do NOT adopt image 2's oversized head, stocky proportions or missing outline. ONE complete full-body character, same scale and centered framing as image 1, transparent background, ample clear margins, no text, no effects. Crisp smooth opaque white sticker outline around silhouette including hair and shoes; clean alpha-zero exterior and gaps between legs, no white haze or speckles. Minimal flat cel shading matching existing anime sticker sprites.
```

### work1

```text
Use image 1 as the EXACT revised Grok character/model sheet. Edit ONLY arms and face into the tablet-working action shown in image 2: holding a dark tablet with one hand and touching screen with the other, focused calm half-lidded blue-gray eyes. Retain image 1's small head, slender 3.3 heads high proportions, long blonde hair, single black square X hairclip, black jacket and skirt, tights and full ankle boots. Do not copy image 2's large head. Match the thin outlines and flat cel anime sticker style of image 1. Full body, same head size and shoe baseline as image 1. Opaque white sticker outline around complete silhouette including hair, hands and boots; genuine transparent exterior. Clean gaps and no halo, flecks or texture outside the outline. Centered complete figure with clear margins, no cropping, no words, one character.
```

### panic

```text
Edit image 1 into the startled/panic pose inspired by image 2. Image 1 is the exact Grok character and proportion reference: small head, slender 3.3 heads tall body, long loose blonde hair, blue-gray eyes, diagonal X on black hairclip, black jacket, skirt, tights, ankle boots, white sticker outline. Keep its exact head size, leg length, framing and baseline. Change expression to wide worried eyes, small open mouth, and both hands curled beside cheeks. Do NOT copy image 2's oversized head or short legs. Preserve the thin dark linework and simple flat cel color style. One complete full-body sprite including shoes; clear transparent margins, no background, no text, no floating symbols. Smooth opaque white sticker outline around whole silhouette and genuinely transparent gaps; no haze or speckles.
```

### collapsed

```text
Create one exhausted collapsed static pose for the SAME blonde Grok anime sticker mascot in image 1. Image 2 is ONLY a pose and style reference: the character lies face down horizontally with head and both hands toward the viewer-left, legs and complete boot soles stretching toward the right, small pale soul wisp floating above her back. Preserve Grok identity and proportions: long blonde hair splayed around head, small head relative to long body (3.3-head standing proportions), closed gray-blue eyes, black square diagonal white-X clip, black jacket, black pleated skirt, opaque charcoal tights and black ankle boots. Same thin dark linework, minimal flat cel shading and consistent opaque white sticker border as the standing Grok reference. Keep head roughly the same physical scale as image 1 when the full body is laid out: don't enlarge head or shorten torso/legs. Canvas landscape, entire head, both hands, hair, both complete boots and soul wisp inside frame with 8 percent clear margins. Transparent alpha-zero background and smooth opaque white outline, no pink, no backdrop, no letters, no ground shadow, no speckles. One Grok character only; no Claude. Cute comedic fatigue, no injury.
```

## Review

All five generated sprites were inspected, then rendered beside the other three characters on the app's dark background. The 38 shipped images have transparent clearance around their visible alpha bounds. WPF uses the idle silhouette height for scale and anchors each pose to its visible right edge and floor; the original Claude/Codex collapsed poses have a head-size calibration of 1.25/1.2. The regression suite exercises all moods, working frames, and two display heights, and emits a comparison PNG for visual inspection. The three earlier character sets are retained.
