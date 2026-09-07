# Validation — 2026-09-07

## 0.3.1 metadata patch

Version **0.3.1** adds the package author name `Phuc Nguyen` and GitHub profile URL, and updates release documentation. The manifest parses as valid JSON and the release diff passes whitespace checks. Runtime code, Editor code, shaders, tests, samples, and all `.meta` files are unchanged from **0.3.0**.

Unity test suites, clean-project installation, and Package Manager visual verification were not rerun for this metadata-only patch. The results below belong to **0.3.0**.

## 0.3.0 environment and results

SDF Image **0.3.0**, Unity **6000.0.83f1**, Windows, Direct3D 11 / NVIDIA RTX 3060. Validation ran in isolated projects under the source project's ignored `Build` directory. The consuming project's source metadata and one generated descriptor reference in `DialogWin.prefab` were migrated for the rename; no scene was changed by this audit.

| Check | Result |
| --- | --- |
| Built-in pipeline, uGUI 2.0.0, library in Assets | **58 / 58 EditMode tests passed**, zero skipped |
| Built-in pipeline, local UPM installation `com.sdfimage.ugui@0.3.0` | **58 / 58 EditMode tests passed**, zero skipped, 8.382 s |
| URP 17.0.4, uGUI 2.0.0, Gamma, HDR off, MSAA 1 | **58 / 58 EditMode tests passed**, zero skipped, 10.442 s |
| Destination project, URP 17.0.4, Linear color, existing HDR configuration | **58 / 58 EditMode tests passed**, zero skipped, 12.357 s |
| URP demo through automatic source baking | Rendered and visually inspected; no SDF shader compiler errors |
| StandaloneWindows64 player script compilation | **Passed**, 17 assemblies including `SDFUI.dll`; no `SDFUI.Editor.dll` in player output |

Unmodified final raw reports: [Built-in with local UPM](Documentation~/Tests-Builtin-UPM.xml), [URP Gamma](Documentation~/Tests-URP.xml), [destination URP Linear](Documentation~/Tests-URP-Linear.xml). Preview: [Unity URP render](Documentation~/preview.png). Final logs contain no C# errors, shader compiler errors, or inconsistent importer-result warnings. Earlier reports remain archived outside the distribution.

## What the tests establish

- Five distance-transform cases: independent brute-force comparison, threshold behavior, full/empty masks, invalid input.
- Eight asynchronous source-import cases: default off; enable; disable; active cancellation without automatic restart; source edits; rapid settings changes; maximum size; multiple sprites in one sheet.
- Generated descriptors and both textures share the source image's asset path; each descriptor is actually attached to its original Sprite. Original texture pixels, imported dimensions, and main-asset identity are preserved. Stable object IDs survive rebake/reimport. Tests detect separate `.asset` output and import loops.
- Sixteen component/layout cases: independent material lifecycle, stencil style refresh, expanded drawing with unchanged raycast bounds, nine-slice borders, collapsed slice centers, a thin `1024×3 → 64×1` bake at two Canvas PPU values, standard Image source/override swaps, normal Image fallback, effect toggles preserving style values, legacy Sliced/Preserve Aspect migration, layout parity with Unity Image before/after baking and effect toggles, and allocation-free warmed-up style animation with direct animated Sprite swaps.
- Fourteen GPU render cases: inner/outer outline, shadow direction, composite opacity, translated RectMask2D, single/nested stencil masks, SDF silhouette used as a Mask, several transformed images batched with a normal background Graphic, antialiased-edge seam repair, inactive-outline transparency guards, and authored transparency on unevenly stretched Simple/Sliced images.
- Two Inspector integration cases: the real custom ImageEditor with serialized Source assignment and Undo/Redo, Generate binding back to the same component without a helper, and legacy helper migration respecting a deliberately cleared source. These exercise Editor actions and serialization; they do not automate visual mouse interaction with the Inspector.
- Eleven Editor pipeline cases: preserve other tools' importer metadata before/after the SDF block, handle escaped/malformed/oversized metadata, restore cache publication without unnecessary pointer writes, retain disabled warm-cache data across reimport, and include Resources/preloaded dependencies while excluding Editor-only Resources from build checks. Custom dependency tracking connects published cache content to imported artifacts.
- Two package integration cases: public assembly names, shader/resource lookup, package identity, 64×64 component icon binding, and all six shipped sample components resolve with the renamed library. These also passed with the library installed through UPM.

The final demo uses 256px mathematical source artwork, the same asynchronous importer, and URP rendering. Its six SDF graphics use a single Image-derived component each, with no Auto Bake helper. It demonstrates outer/inner/center outlines, drop shadow, glow, nine-slice, and RectMask2D.

The release migration also ran all 58 cases in the destination project's original Linear/HDR configuration. A material color readback comparison was changed from exact float equality to a per-channel tolerance of 0.000001; component style values remain checked exactly. Runtime code and project settings were unchanged. All 60 pre-existing destination Assets, Packages and ProjectSettings files retained their hashes.

## Rename and serialized data

All existing library `.meta` GUIDs were preserved. In a separate Unity import check, four source sprites retained their GUIDs and source object IDs, all generated descriptors/color/distance textures remained embedded at the original source path, and six sample components resolved correctly. See the [recorded object identities](Documentation~/Rename-verification.json). Generated descriptor IDs changed with the import identifier rename; the shipped demo and consuming prefab were updated to the new IDs. Original source PNG bytes were not changed.

## Dark-edge regression

The reported keychain sprite was reproduced in an isolated URP fixture using a copy of the source PNG and importer settings. Its edge RGB was bright; the defect came from subtracting binary SDF coverage from a different, antialiased source alpha. Background/shadow showed through the resulting gap.

The earlier shader failed both white-on-white edge tests at brightness **0.451**; the corrected shader passed the **>0.98** brightness and alpha checks. It covers the join over the source filtering footprint, retains authored transparency farther inside, and leaves zero-width/transparent-outline behavior unchanged. Version 0.3.0 also keeps that footprint in source-pixel units when the image is stretched unevenly. Before/after URP renders of the actual sprite and the final demo were inspected. The change requires no new texture samples and no texture rebake.

## Bake responsiveness measurement

The integration test starts an active 512×512 bake, cancels it, checks that it does not restart/publish, then explicitly requests it again. Defaults produce 576×576 textures after padding. Timings include queue completion and observing the published SDF, measured once per final suite on this machine:

| Pipeline | Total bake | Largest observed Editor update gap | Cancel API call |
| --- | ---: | ---: | ---: |
| Built-in, local UPM | 104.1 ms | 4.1 ms | 0.6 ms |
| URP | 128.2 ms | 11.2 ms | 0.5 ms |

The Editor continued updating throughout. These are observations, not worst-case guarantees: texture import/publication and GPU resource creation still require the main thread. Larger sheets, slow storage, import workers and other Editor activity can change the numbers. No production mobile FPS claim follows from these timings.

## Old-library comparison

The previous `Assets/com.nickeltin.sdf` implementation was inspected from project Git history as requested. Its GPU generator ran jump-flood passes on full-resolution data, performed synchronous readback, and reduced resolution afterward; yielding between whole imports did not make each texture bake asynchronous.

This library reduces the source region first, processes alpha only for distance generation, uses asynchronous readback and one cancellable CPU worker, and publishes cached results during import. It keeps the useful source-owned asset workflow while avoiding separate baked Assets files. It does not reuse the old implementation's source code.

## Not established by these checks

- No complete player build or Android/iOS device run; player script compilation is a separate check from a packaged player.
- No performance profile in the existing game's actual UI or a low-end phone.
- Sprite Atlas packing in a player, third-party material modifiers, and all supported mobile graphics APIs have not been exercised here. Linear rendering passed the same 14 GPU regression cases in the destination project; this does not cover every HDR or post-processing configuration.
- This release implements Simple/Sliced uGUI graphics. It does not claim every feature of the reference commercial asset.
