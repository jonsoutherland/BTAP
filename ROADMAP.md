# BTAP Roadmap

Strategic backlog for turning BTAP from "looks like an editor" into "is an editor", ordered by impact.

## Tier 1 — "It actually edits video"

The current preview pipeline reads property data but never uses it. These unlock most of the existing UI.

- **Transform applied to preview/export** — Scale, Pos X/Y, Rotation, Opacity. Need to wrap `MediaPlayerElement` in a `CompositeTransform` or render via `Win2D` per frame.
- **Color grading applied** — Exposure / Contrast / Saturation / Temperature / Tint / Lift / Gamma / Gain. Same pipeline as transforms; needs a real-time color shader.
- **Effects actually rendering** — Gaussian Blur, Vignette, Chroma Key, etc. Each effect type needs an implementation; even a few common ones (Blur, Opacity, Color) unlocks dozens of UI buttons.
- **Speed applied to playback** — `MediaPlayer.PlaybackRate` per clip; also a per-clip parameter in `MediaClip` for export.
- **Title clips render real text** — currently a colored box. Need `MediaOverlay` with a `Direct2D` / `Win2D` text bitmap, or generate to a transparent image.
- **Cross Dissolve / transitions between adjacent clips** — `MediaOverlay` with opacity ramp during overlap region.

## Tier 2 — Professional editing workflow

- **Multi-select clips** — Ctrl+click, marquee drag. Today every operation is single-clip only.
- **Copy / Paste / Cut clips** — Edit menu has the items, no underlying clipboard.
- **Linked A/V clips** — Moving a video clip should move its associated audio together.
- **Audio fades + EQ + Pan actually applied** — `AudioGraph` pipeline replaces the simple `MediaPlayer`; mixer node per track.
- **Audio mixer panel** — per-track volume/pan sliders with VU meters.
- **Audio waveforms on clips** — visual scrubbing aid (`AudioGraph.SubmixNode` analyser or pre-decoded peak file).
- **Video thumbnails on clips** — first-frame at minimum, ideally one every few seconds. `Windows.Media.Editing.MediaClip.GetThumbnailAsync` can do this.
- **In/Out points** — set marks on the timeline and export only that range.
- **Three-point editing** — source In/Out + timeline In, drop clip from media bin honoring those marks.

## Tier 3 — Project & media management

- **Autosave** — periodic background `ProjectSerializer.Save` to a `.btap.autosave`; restore offer on next launch.
- **Project settings dialog** — change resolution / fps / color space after creation. Currently locked at construction.
- **Bin organization** — sub-bins / folders / labels in the media library.
- **OS file drag into bin and timeline** — currently only the preview area accepts file drops; should accept anywhere.
- **Missing-media handling** — on project load, prompt to relink moved/deleted source files.
- **Proxy media** — generate lower-res proxies for smooth editing, swap to original for export.
- **Track rename / reorder / remove / resize** — Track headers should be right-clickable.
- **Marker editor** — rename, color, navigate Next/Prev (`,` and `.` keys), markers with notes.

## Tier 4 — Export & output

- **Export presets** — YouTube 1080p, Instagram 9:16, Cinema 4K, etc. — one-click profiles.
- **Custom encoding settings** — bitrate, codec choice (H.264 / H.265 / ProRes), GOP length.
- **Export region** — render only the In/Out range, not the whole timeline.
- **Audio-only export** — WAV/MP3 of the audio tracks.
- **Frame export** — save current playhead frame as PNG/JPG.
- **Image-sequence export** — PNG per frame for VFX hand-off.
- **Render queue** — queue multiple exports; background processing.

## Tier 5 — Color & finishing

- **Vector scope / waveform monitor / histogram** — accessed via COLOR mode.
- **Curves** — RGB and luma curve editors.
- **Color wheels** — replace the Lift / Gamma / Gain sliders with proper trackball wheels.
- **LUT support** — load `.cube` files, apply per-clip.
- **HDR-aware preview**.

## Tier 6 — Animation

- **Keyframes for any parameter** — animate Position, Scale, Opacity, Effect intensity over time. Today everything is a static value.
- **Keyframe interpolation modes** — linear / hold / ease in/out / bezier.
- **Motion blur on transforms**.

## Tier 7 — Quality of life

- **Recent media on landing page** — beside Recent Projects.
- **Quick-launch search (Ctrl+P)** — fuzzy command palette.
- **Status notifications panel** — non-blocking toasts for "Exported", "Autosaved", "File relinked".
- **Workspace presets** — different panel layouts (Edit / Color / Audio / Trim).
- **Window dragging** — currently the title bar can't move the window because no drag region is wired.
- **Customizable keyboard shortcuts**.
- **Real keyboard accelerators on menu items** — currently the menu shortcuts are just hint text; the real handling is in `OnEditorKeyDown`. Merging them would let menus highlight when their key is held, etc.
- **About dialog with build/version from assembly metadata** instead of hardcoded "0.1 (dev)".
- **First-run tour / onboarding overlay**.

## Tier 8 — Advanced & ambitious

- **Multiple sequences per project** — like Premiere; one project, many timelines.
- **Nested sequences** — drop a sequence on another timeline.
- **GPU-accelerated effect pipeline** — Win2D / DirectX compositor; required for real-time playback once you stack effects.
- **Background rendering / cache** — render previews of effect-heavy regions to disk.
- **Plugin SDK** — let users add custom effects/transitions.
- **Multi-user / cloud collaboration** — comments, shared projects.
- **Captions / subtitles** — caption track type, SRT/VTT import + export.
- **Speech-to-text** — auto-transcribe a clip, edit on text (Descript-style).
- **Auto-color / auto-audio leveling**.

---

## Suggested next milestone

If picking one tier to tackle next, **Tier 1 — Transform & Color rendering** is the highest leverage.

Reasoning:

- It uses UI that already exists (every Inspector slider currently does nothing visible).
- The same pipeline (Win2D / `MediaPlayerElement` overlay) unlocks Effects, Title rendering, and Transitions later.
- Without it, the app *looks* like an editor but only does cuts — and that's the gap users notice first.

Technical approach:

1. Replace `MediaPlayerElement` with a `SwapChainPanel` or `Microsoft.Graphics.Canvas.UI.Xaml.CanvasAnimatedControl`.
2. Decode frames via `MediaPlayer` → upload to a `CanvasBitmap`.
3. Apply a `Win2D` effect chain (the existing `ClipEffect.Name` strings map to Win2D's effect types).
4. Render to the surface; same chain runs at export time per-frame.

### Standalone wins (good lower-scope alternatives)

- **Multi-select + Copy/Paste** (Tier 2) — touches no rendering, just timeline interactions.
- **Video thumbnails on clips** (Tier 2) — high visual impact, modest scope.
- **Autosave + crash recovery** (Tier 3) — protects users' work.
- **Export presets** (Tier 4) — a polish win on top of the new export.
