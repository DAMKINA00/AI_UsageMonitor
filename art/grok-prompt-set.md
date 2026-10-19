# Grok mascot

Generated with the built-in ImageGen tool (`transparent_background=true`); no CLI/API fallback or bitmap editing. Four original RGBA PNGs are embedded under `Assets/Characters/grok_*.png`. Source files are 1038×1516.

Identity reference: AI_ChatterBox `src/renderer/public/characters/grok/neutral.png` (long blonde hair, gray-blue eyes, black X clip, black jacket). Style and body reference: AIUsageMonitor `gemini_idle.png`. Grok wears a black skirt, charcoal tights and ankle boots. Four poses share the existing stage animation; remaining moods reuse idle or sleeping.

Original fictional monitor voice: dry, brief and playful Korean casual speech. Quota facts remain in the UI; dialogue does not invent account status. These lines are new, not quotations from a meme or real person.

## Final prompts

### idle

```text
Make ONE isolated full-body 3.5-head-tall chibi mascot sprite. Image 1 provides Grok identity ONLY: long loose warm blonde hair, matte gray-blue eyes, black square white-X hair clip on viewer right, black jacket and highneck shirt. Image 2 provides fullbody proportions and clean flat cutout STYLE only. Grok wears black pleated skirt, opaque charcoal tights, black ankle boots. Preserve full head, complete loose hair and boot soles inside canvas, centered portrait. CRITICAL: completely flat vector-like solid color fills, crisp hard outline, ABSOLUTELY NO GRADIENTS, NO HALO, NO HAZE, NO GLOW, NO BROWN CLOUDS, NO FLOOR SHADOW. Outside the precisely bounded character silhouette must be empty transparent alpha zero. Transparent RGBA. No background, text, captions, objects unrelated to pose or extra characters. Modest clothing. Standing idle, calm cheeky half-lidded smile, one hand on hip and one casually at waist.
```

### sleeping

```text
Make ONE isolated full-body 3.5-head-tall chibi mascot sprite. Image 1 provides Grok identity ONLY: long loose warm blonde hair, matte gray-blue eyes, black square white-X hair clip on viewer right, black jacket and highneck shirt. Image 2 provides fullbody proportions and clean flat cutout STYLE only. Grok wears black pleated skirt, opaque charcoal tights, black ankle boots. Preserve full head, complete loose hair and boot soles inside canvas, centered portrait. CRITICAL: completely flat vector-like solid color fills, crisp hard outline, ABSOLUTELY NO GRADIENTS, NO HALO, NO HAZE, NO GLOW, NO BROWN CLOUDS, NO FLOOR SHADOW. Outside the precisely bounded character silhouette must be empty transparent alpha zero. Transparent RGBA. No background, text, captions, objects unrelated to pose or extra characters. Modest clothing. Standing sleepy with both eyes closed, tiny relaxed smile, arms resting loosely, same standing height, no sleep symbols.
```

### work1

```text
Make ONE isolated full-body 3.5-head-tall chibi mascot sprite. Image 1 provides Grok identity ONLY: long loose warm blonde hair, matte gray-blue eyes, black square white-X hair clip on viewer right, black jacket and highneck shirt. Image 2 provides fullbody proportions and clean flat cutout STYLE only. Grok wears black pleated skirt, opaque charcoal tights, black ankle boots. Preserve full head, complete loose hair and boot soles inside canvas, centered portrait. CRITICAL: completely flat vector-like solid color fills, crisp hard outline, ABSOLUTELY NO GRADIENTS, NO HALO, NO HAZE, NO GLOW, NO BROWN CLOUDS, NO FLOOR SHADOW. Outside the precisely bounded character silhouette must be empty transparent alpha zero. Transparent RGBA. No background, text, captions, objects unrelated to pose or extra characters. Modest clothing. Focused awake expression, holding a small dark tablet close to chest with one hand, other index finger tapping tablet, no visible text.
```

### panic

```text
Make ONE isolated full-body 3.5-head-tall chibi mascot sprite. Image 1 provides Grok identity ONLY: long loose warm blonde hair, matte gray-blue eyes, black square white-X hair clip on viewer right, black jacket and highneck shirt. Image 2 provides fullbody proportions and clean flat cutout STYLE only. Grok wears black pleated skirt, opaque charcoal tights, black ankle boots. Preserve full head, complete loose hair and boot soles inside canvas, centered portrait. CRITICAL: completely flat vector-like solid color fills, crisp hard outline, ABSOLUTELY NO GRADIENTS, NO HALO, NO HAZE, NO GLOW, NO BROWN CLOUDS, NO FLOOR SHADOW. Outside the precisely bounded character silhouette must be empty transparent alpha zero. Transparent RGBA. No background, text, captions, objects unrelated to pose or extra characters. Modest clothing. Worried startled expression, eyes wide, small open mouth, hands beside cheeks, same standing height, no tear streams or symbols.
```

## Visual review

Full bodies and boots are visible, blonde/black/X identity preserved, transparent RGBA backgrounds. Panic has brighter eye highlights than the identity reference; independent poses have slight proportion differences. Existing characters were not replaced. Earlier halo drafts were rejected and are not shipped.
