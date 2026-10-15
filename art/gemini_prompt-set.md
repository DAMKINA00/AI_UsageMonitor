# Gemini prompt set

Tool: built-in image_gen.imagegen; transparent_background=true.

## Shared prompt
Edit the reference into ONE new pose sprite of the EXACT SAME blonde twin-tail girl. This is one frame in the same sprite animation. Preserve identical face proportions, head size, eye color BLUE in both eyes, hair shape, X pin on viewer LEFT, two FOUR-POINT multicolor sparkle hairpins on viewer RIGHT, large teal bows, neck bow, white blouse, black suspenders, navy pleated skirt with tiny colored hem dots, dark brown tights, black Mary Jane shoes. Preserve ALL four separate hard-edged hair-tip color bands: CORAL, PINK, MAGENTA, BLUE-VIOLET. Thin warm-brown outlines, flat music-video anime fills, no gradients, no glossy highlights. Maintain 3.5-head body proportions, narrow vertically hanging twin tails. No text, letters, logos, backgrounds, floor, ground shadow, white border or ambient glow. Actual transparent alpha outside the character, one portrait 832x1248 PNG. Full subject centered x416 and total width including props/hair <=540px; keep props close to body. Standing soles at y1210 with 38px below; standing height about1125, crown aroundy85. Preserve scale of the reference. 

## Pose instructions
- idle: one hand hip, V sign beside cheek, smug smile
- work1: three opaque pictogram cards held at chest, focused face
- work2: same work1, right index finger taps blue document card
- work3: same work1, eyes closed blink
- playing: hug a five-point star cushion, cheek pressed, eyes closed smile
- sleeping: standing asleep, lavender sleep mask above forehead, relaxed arms
- tired: slumped, red-yellow-blue toy hammer, gloom lines and sweat
- collapsed: face-down foreshortened prone body on same floor line, small ghost
- panic: grip own twin tails, squeezed eyes, two tear streams
- happy: jump, both arms raised, eyes closed smile, no sparkles
- waiting: point at viewer, other hand hip, puffed cheeks and impatient eyes

## Export operations
System.Drawing: clean alpha<8 numerical background specks, uniformly resample to832x1248 canvas, center, align alpha>=128 lower bound to y1210 (happy y1090), preserve local edits forwork2/work3 by usingwork1 pixels outside hand/eye regions, embed PNG sRGB rendering intent0. Exact reference duplicates.

## Known visual limitations
Not pixel-identical head geometry between independent poses. Residual soft shading differs from strict all-flat MV target. Hair-tip color bands are discrete, but exact specified HEX matches are not guaranteed. Four-color hairpin segments are approximate. No original Claude/Codex file was modified.
