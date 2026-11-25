# Desktop standing sprites

Built-in ImageGen extended the existing neutral portraits into transparent full-body sprites. The GPT standing sprite is the shared body reference. ImageGen redrew the other five sprites using GPT's proportions and each character's original palette, clothes, and accessories. The selected PNGs were resized to 420 pixels high for `Assets/Standing/`. The original generated PNGs remain in the Codex image output folder.

## Prompt set

The GPT reference extends its neutral portrait to a standing sprite with a short body, large head, forward stance, and both shoes aligned to the same baseline. The prompt below uses each first-pass standing sprite as image 1 and GPT as image 2.

```text
Image 1 is the character to redraw. Image 2 is our APPROVED, PRECISE full-body SD MODEL SHEET for anatomy, proportions, pose, eye scale, outline thickness and head size. Reconstruct Image 1 with the EXACT SAME silhouette proportions and neutral forward pose as Image 2. Treat Image 2 as a drawing template: place the head crown (without hair tufts), eye line, chin, neck, shoulders, lowered hands, hip joints, knees, ankles and both shoe soles at the same relative positions and widths as Image 2. The two characters must look like siblings of equal body build when placed side-by-side, with a large equally wide round head, tiny neck, same stocky body, and two equally short legs. Their eye centers should be just as far apart. Keep the same stance, even parallel shoes and visible forearms resting at sides. Match Image 2's contour and flat cel shading. The face, hairstyle, accessories, clothing and colors are Image 1's character, as described below. Longer hair, hair ornaments, coat hems and skirts can change the outer outline only; the underlying body shape and scale must stay on the common template. Export one character fully visible, at the same framing and 3:4 vertical aspect ratio as Image 2. True transparent background, clear margin on all sides including the soles, no words, no shadow, no floor, no second figure.
```

| Sprite | Character details |
| --- | --- |
| `orch.png` | Master: peach-colored short curls including one swept tuft, subdued olive eyes. Preserve the long open ivory gown and sage trim over the light pink shirt and sage trousers, with closed cream shoes. Keep two open coat fronts, visible shirt and trouser legs. The coat fronts end midway down the shins, following that common pose. Keep the gown even if the reference uses a jacket. |
| `claude.png` | Claude: long wavy light-brown hair, round glasses, black bow and star ornament, brown-gold trim and cream blouse, brown pleated skirt and matching short boots. Preserve those colors and recognizable expression. Hair may overlap shoulders at the sides. |
| `gemini.png` | Gemini: blonde pigtails with turquoise bows and pink-purple tips, blue eyes, star pins, gold-and-navy outfit, teal bow, skirt and shoes. A single small upward curl. Keep the pigtails outside the common head silhouette. |
| `grok.png` | Grok: long golden blonde hair, gray eyes and one black clip with a diagonal white X, black jacket, pleated skirt, tights and boots. Preserve the hair lock shapes, colors, half-lidded expression and outfit. |
| `deepseek.png` | DeepSeek: navy blue hair, blue eyes, two rectangular blue hair clips, white side accessories, the navy-and-white collar and skirt, matching socks and shoes. Preserve those colors and distinctive expression. |
