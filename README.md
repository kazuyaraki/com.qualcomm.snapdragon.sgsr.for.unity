# Snapdragon™ Game Super Resolution for Unity

Snapdragon™ Game Super Resolution (SGSR) for Unity Universal Render Pipeline, developed by Snapdragon™ Game Studios.

This component is part of the [Snapdragon™ Game Toolkit](https://www.qualcomm.com/developer/snapdragon-game-toolkit).

---

## Introduction

Snapdragon™ Game Super Resolution (Snapdragon™ GSR or SGSR) integrates upscaling and sharpening in a single GPU pass. The technique lets games render the 3D scene at a reduced resolution for performance and power savings, then reconstruct a full-resolution image — while keeping 2D UI rendered at native device resolution for visual clarity.

This package provides three upscaling variants:

| Variant | Type | Description |
|---|---|---|
| **SGSR v1** | Spatial | Single-pass Lanczos-like filter with adaptive edge-directed sharpening. No temporal state. |
| **SGSR v2 (2-pass FS)** | Temporal | Two-pass fragment shader: Convert + Upscale with history accumulation. |
| **SGSR v2 (3-pass CS)** | Temporal | Three-pass compute shader: Convert + Activate + Upscale, with optional RCAS sharpening. |

---

## Requirements

- Unity 6 (6000.3.x) or later
- Universal Render Pipeline (URP) 17.3.0 or later

### Unity version compatibility

Use the URP/Core packages supplied with your Editor. `ENABLE_UPSCALER_FRAMEWORK` is required on every version.

| Editor | Integration |
|---|---|
| Unity 6.3–6.4 | Original options-based constructors and method interface. |
| Unity 6.5 | Property-based upscaler interface. |
| Unity 6.6 | Parameterless descriptors, per-camera contexts, and explicit jitter ratio. |
| Unity 6.7 | Separate registered ID and display name; experimental beta support. |

Validation on Apple M4 / Metal covers Editor rendering with two cameras, output resolution changes, camera cuts, and jitter toggles. Spatial and 3-pass CS output passed the image smoke checks. **2-pass FS produced nearly black RenderTexture output in this test, including on the unmodified Unity 6.3 package; its rendering correctness remains unverified.** Player builds, Android, and XR have not been validated by these compatibility checks.

When upgrading a pipeline asset from Unity 6.3–6.4 to 6.5 or newer, check the selected upscaler and its saved options before using the inspector: Unity changed its serialized selection field, so the SGSR selection may fall back to a built-in filter. Keep the original asset in version control, reselect SGSR, and verify the option values. This package does not rewrite Unity's pipeline-asset serialization.

The SGSR algorithms, shaders, render passes, and history managers are unchanged. Unity 6.6+ owns a separate SGSR renderer per camera context and releases it when the context expires or its output resolution changes. The previous upscaler names remain the serialized IDs in Unity 6.7.

Starting with Unity 6.5, the Debug upscaler's three variant settings are saved as separate `*.SGSRDebug.*.asset` files next to the pipeline asset. Existing embedded values are copied when validated. Keep these files and their `.meta` files in version control with the pipeline asset; Unity's newer pipeline inspector removes unregistered `UpscalerOptions` sub-assets.

---

## Installation

Add the package via Unity Package Manager using the git URL:

**Window --> Package Manager --> + --> Add package from git URL**

```
https://github.com/SnapdragonGameStudios/com.qualcomm.snapdragon.sgsr.for.unity.git
```

Or add it directly to `<Your Unity Project>/Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.qualcomm.snapdragon.sgsr.for.unity": "https://github.com/SnapdragonGameStudios/com.qualcomm.snapdragon.sgsr.for.unity.git"
  }
}
```

To pin to a specific version:
```
https://github.com/SnapdragonGameStudios/com.qualcomm.snapdragon.sgsr.for.unity.git#v1.0.0
```

---

## Optional: SGSR v1 Render Pass Optimization

This optional patch targets Unity 6.3–6.4. Do not apply it to Unity 6.5 or newer; the compatibility integration does not require changes to Unity packages.

By default, Unity's `IUpscaler` framework requires an upscaler to declare itself "temporal" in order to be executed in the post-process pipeline. For SGSR v1 this is a technicality — it is a purely spatial upscaler, but it must return `IsTemporalUpscaler() == true` to activate the correct execution path. The side-effect is that Unity also schedules **CopyDepth** and **MotionVector** render passes, which are unnecessary for SGSR v1 and waste GPU time.

`Tools/patches/render-pipelines.patch` adds a `RequiresMotionVectors()` method to the `IUpscaler` interface so that spatial upscalers can opt out of those passes. The patch touches two Unity package files:

| File | Change |
|---|---|
| `com.unity.render-pipelines.core` — `IUpscaler.cs` | Adds `RequiresMotionVectors()` to the interface and a default implementation to `AbstractUpscaler`. |
| `com.unity.render-pipelines.universal` — `UniversalRendererRenderGraph.cs` | Checks `RequiresMotionVectors()` before scheduling CopyDepth and MotionVector passes. |

Because these are Unity package files they must be **embedded** (moved from `Library/PackageCache` into `Packages/`) before the patch can be applied.

### Applying the patch (automated)

Run the provided PowerShell script from any working directory. The path depends on how the package was installed:

**Installed via git URL** (package lives in `Library/PackageCache`):
```powershell
.\Library\PackageCache\com.qualcomm.snapdragon.sgsr.for.unity\Tools\embed-render-pipelines-core.ps1
```

**Installed as a local embedded package** (package lives in `Packages`):
```powershell
.\Packages\com.qualcomm.snapdragon.sgsr.for.unity\Tools\embed-render-pipelines-core.ps1
```

The script will:
1. Move `com.unity.render-pipelines.core` from `Library/PackageCache` into `Packages/` (skipped if already there).
2. Move `com.unity.render-pipelines.universal` from `Library/PackageCache` into `Packages/` (skipped if already there).
3. Apply `render-pipelines.patch` using `patch.exe` (bundled with Git for Windows). Skipped if the patch markers are already present.

> **Prerequisite:** Git for Windows must be installed so that `patch.exe` is available. The script locates it automatically via the `git` executable.

Re-running the script is safe — each step detects its own completion and skips.

### Activating the optimization

After the patch is applied, add the following symbol to **Edit → Project Settings → Player → Other Settings → Scripting Define Symbols** for each build target:

```
SGSR_APPLIED_PATCH
```

Without this symbol the SGSR package compiles and runs correctly but the CopyDepth and MotionVector passes are not suppressed. Adding it activates the `RequiresMotionVectors() == false` override in `SGSRIUpscaler`.

### Applying the patch manually

If the script cannot be used, apply the patch by hand from the `Packages/` directory.

**Installed via git URL** (patch file lives in `Library/PackageCache`):
```sh
# From <Unity project root>
patch -p1 --forward --input "Library/PackageCache/com.qualcomm.snapdragon.sgsr.for.unity/Tools/patches/render-pipelines.patch"
```

**Installed as a local embedded package** (patch file lives in `Packages`):
```sh
# From <Unity project root>
patch -p1 --forward --input "Packages/com.qualcomm.snapdragon.sgsr.for.unity/Tools/patches/render-pipelines.patch"
```

Or view `render-pipelines.patch` directly and apply the marked changes. Every modification is bracketed by `// BEGIN SGSR MODIFICATION` / `// END SGSR MODIFICATION` comments.

---

## Setup

### Step 1 — Add the scripting define

SGSR uses Unity's `IUpscaler` framework, which must be explicitly enabled via a scripting define symbol.

**Edit --> Project Settings --> Player --> Other Settings --> Scripting Define Symbols**

Add the following symbol for each build target (Android, Standalone, etc.):

```
ENABLE_UPSCALER_FRAMEWORK
```

Click **Apply**. Unity will recompile scripts. After recompilation, the SGSR upscaling options will appear in the URP pipeline asset Inspector.

### Step 2 — Configure the URP pipeline asset

Open each URP pipeline asset that should use SGSR (e.g. via **Edit --> Project Settings --> Quality**, then click the pipeline asset):

1. Set **Render Scale** to the desired value (e.g. `0.6667` for a ~67% resolution reduction).
2. Set **Upscaling Filter** to the desired SGSR variant:
   - `SGSR 1.0 Spatial Upscaler`
   - `SGSR 2.0 Temporal Upscaler (2-Pass FS)`
   - `SGSR 2.0 Temporal Upscaler (3-Pass CS)`
   - `SGSR Debug Upscaler`

On Unity 6.7, set **Scaling Mode** to **Upscaling** and put the desired SGSR variant first in **Upscaler Priority**, then choose the Render Scale. The first supported entry in that list is selected.

### Step 3 — Configure SGSR options

After completing Step 2, open the pipeline asset Inspector. An options sub-asset will be auto-populated for the selected variant. See the [Options Reference](#options-reference) section below for all available settings.

### Step 4 — Enable Post Processing on the Camera

On each Camera component that should display SGSR output, enable post processing:

**Camera component --> Rendering --> Post Processing: enabled**

### Step 5 — Enter Play mode

SGSR is now active for all Game cameras. No Renderer Feature or camera component is required.

---

## Variants

### SGSR v1 — Spatial Upscaler

A pure spatial upscaler. Each frame is processed independently with no temporal history. Best suited for cases where temporal artifacts (ghosting, shimmer) are unacceptable, or where simplicity and determinism are required.

**Algorithm:** 12-tap Lanczos-like scaling filter with adaptive edge-directed sharpening.

**Quality Modes:**

| Mode | Description |
|---|---|
| **High Quality** | Full edge-direction detection with dual-pass weighted filtering. Best output quality. |
| **Mobile** | Simplified Lanczos2 filter without edge direction. Faster, suited for performance-constrained devices. |
| **VR** | Direction-aware filtering with tighter output clamping (±23/255) to reduce over-sharpening in VR. |

---

### SGSR v2 — Temporal Upscaler (2-Pass Fragment Shader)

A temporal upscaler using two fragment shader passes. Accumulates history across frames for higher quality than spatial upscaling, especially on fine detail and thin features.

**Passes:**
1. **Convert** — Converts input color to YCoCg, dilates motion vectors, and computes depth.
2. **Upscale** — Blends current frame with history using a Lanczos kernel and temporal weighting.

**Per-camera state:** History buffers are managed per-camera using ping-pong RTHandles. History is automatically reset on scene load or camera cut detection.

---

### SGSR v2 — Temporal Upscaler (3-Pass Compute Shader)

A temporal upscaler using three compute shader passes, with an optional fourth sharpening pass. Provides the highest quality output and the most configuration options.

**Passes:**
1. **Convert** — Converts input color to YCoCg, dilates motion vectors, and computes depth.
2. **Activate** — Detects disocclusion and generates a reactive mask. Optionally computes pixel-lock lifetimes for thin feature preservation.
3. **Upscale** — Main temporal accumulation with history blending. Outputs scene color and history simultaneously via MRT.
4. **Sharpen** *(optional)* — RCAS (Reactive Contrast Adaptive Sharpening) pass applied after upscaling.

**Per-camera state:** History buffers (including per-pixel lock lifetimes) are managed per-camera. History is automatically reset on scene load or camera cut detection.

---

## Options Reference

### SGSR v1 Options (`SGSROptions`)

| Setting | Type | Default | Description |
|---|---|---|---|
| **Quality** | `SGSRQuality` | `HighQuality` | Upscaling quality mode. See [Quality Modes](#sgsr-v1--spatial-upscaler) above. |
| **Half Precision** | `bool` | `true` | Use 16-bit float arithmetic in the shader. Recommended on mobile for performance. |

---

### SGSR v2 Options — 2-Pass FS (`SGSR2Options`)

| Setting | Type | Default | Description |
|---|---|---|---|
| **Min Lerp Contribution** | `float` | `0.3` | Floor on how much the current frame contributes to the temporal blend. Higher values reduce ghosting but may increase temporal noise. |
| **Detect Camera Cuts** | `bool` | `true` | Automatically detect hard camera cuts and reset temporal history. Disable if you use Cinemachine or manage resets manually via `UniversalAdditionalCameraData.resetHistory`. |
| **Enable Jitter** | `bool` | `true` | Apply per-frame sub-pixel camera jitter to accumulate sub-pixel detail across frames. Disable if you see flickering on static cameras. |
| **Use 5-Tap** | `bool` | `true` | Use a 5-tap Lanczos kernel instead of 9-tap. Faster with minimal quality difference; recommended for most cases. |
| **Compute Thin Feature** | `bool` | `false` | Apply extra sharpening to thin features such as fine lines and hair. Small ALU cost increase. |

---

### SGSR v2 Options — 3-Pass CS (`SGSR2CSOptions`)

| Setting | Type | Default | Description |
|---|---|---|---|
| **Enable Pixel Lock** | `bool` | `false` | Detect and preserve thin features to suppress temporal flicker. |
| **Enable Sharpening** | `bool` | `false` | Run the optional RCAS sharpening pass after Upscale. |
| **Sharpness** | `float` | `1.12` | RCAS sharpening strength. Range: `[1.0, 1.5]`. Only used when **Enable Sharpening** is `true`. |
| **Use 5-Tap** | `bool` | `false` | Use a 5-tap Lanczos kernel. When `false`, uses a 9-tap kernel (higher quality, higher cost). |
| **Enable Jitter** | `bool` | `true` | Apply per-frame sub-pixel camera jitter to accumulate sub-pixel detail across frames. Disable if you see flickering on static cameras. |
| **Detect Camera Cuts** | `bool` | `true` | Automatically detect hard camera cuts by comparing the view-projection matrix between frames and force a history reset. Disable if you manage camera cuts manually via `UniversalAdditionalCameraData.resetHistory`, or if you use Cinemachine (which sets `resetHistory` automatically). |

---

## Debug Panel

SGSR registers a panel in Unity's **Rendering Debugger** (available in the Editor and in Development builds).

**Window --> Analysis --> Rendering Debugger --> SGSR**

The panel is organized into two foldouts.

### Snapdragon Game Super Resolution

| Control | Type | Description |
|---|---|---|
| **Render Scale** | Read-only | Current render scale (0.0-1.0). |
| **Resolution** | Read-only | Input resolution → output resolution. |
| **Enabled** | Toggle | Globally enable or disable SGSR upscaling at runtime. When disabled, the scene still renders at the configured render scale but is upscaled to output resolution via a bilinear blit instead of SGSR. |
| **Active Upscaler** | Enum | Switch the SGSR algorithm at runtime. Only shown (and editable) when **SGSR Debug Upscaler** is selected in the URP pipeline asset. When any other upscaler is active, this row shows `N/A — select 'SGSR Debug Upscaler' in the URP pipeline asset` instead. |

**Active Upscaler options (SGSR Debug Upscaler only):**

| Option | Description |
|---|---|
| `Spatial` | SGSR 1.0 — single-pass spatial upscaler. |
| `TwoPassFS` | SGSR 2.0 — two-pass temporal upscaler (fragment shader). |
| `ThreePassCS` | SGSR 2.0 — three-pass temporal upscaler (compute shader). |
| `Bilinear` | No SGSR upscaling — bilinear blit to output resolution. |

### SGSR v2 (Temporal)

| Control | Type | Description |
|---|---|---|
| **Reset History** | Button | Clear the temporal history for all cameras on the next frame. Use this to remove ghosting artifacts after a scene change (v2 variants only). |
| **3-pass CS Debug Output** | Enum | Blit a 3-pass CS intermediate texture to the Game view. `Off` = normal output (v2 CS only). |

**3-pass CS Debug Output options (v2 CS only):**

| Option | Description |
|---|---|
| `Off` | Normal upscaled output. |
| `YCoCgLuma` | Luma channel of the YCoCg-converted input color (Convert pass output). |
| `DilatedMotionDepthLuma` | Dilated motion vector and depth buffer (Convert pass output). |
| `ReactiveMask` | Reactive mask generated by the Activate pass. |
| `NewLocks` | Per-pixel lock lifetimes (Activate pass output, pixel-lock only). |
| `HistoryOutput` | Raw history accumulation buffer before the final composite. |

---

## Getting in Contact

* [Report an Issue on GitHub](../../issues)
* [Open a Discussion on GitHub](../../discussions)
* [Join our Discord](https://discord.gg/npRUAyY66c)

---

## License

Snapdragon™ Game Super Resolution is licensed under the BSD 3-Clause License. See [LICENSE.txt](LICENSE.txt) for the full license text.
