# Repository Guidelines

## Project Structure & Module Organization
- `Runtime/` holds the core C# feature scripts (`ScreenSpaceGlobalIlluminationURP.cs`, volume settings, and the `Denoisers/` helpers).
- `Shaders/` contains the HLSL kernels and shared utility includes used by the SSGI passes.
- `Editor/` exposes custom inspectors and configuration UI for the volume component.
- `Samples~/` provides the “Relaxing Corner” scene for end-to-end validation; import it through the Unity Package Manager when needed.

## Build, Test, and Development Commands
- **Unity Editor**: open the project or embed the package in a test project. Enter Play Mode to validate runtime behaviour.
- **Sample import**: `Window → Package Manager → SSGI for Universal RP → Import` to load the reference scene.
- **Shader reimport**: right-click the `Shaders/` folder and choose *Reimport* after modifying HLSL files to refresh compiled variants.

## Coding Style & Naming Conventions
- Follow Unity C# conventions: PascalCase for types, camelCase for fields, constants in PascalCase with `_` only for private serialized fields when Unity requires it.
- Keep indentation at 4 spaces in C# and HLSL; avoid tabs.
- Place new runtime logic under `Runtime/` and editor-only utilities under `Editor/`. Mirror existing file names (`ScreenSpaceGlobalIllumination*.cs`) when extending features.

## Testing Guidelines
- Prefer Play Mode validation inside the provided sample scene; tweak volume overrides and confirm visual stability across motion.
- When adding denoiser variants, include sanity checks for both forward and deferred URP paths (standard and Render Graph).
- Document manual test steps in pull requests until automated coverage exists.

## Commit & Pull Request Guidelines
- Use concise, imperative commit messages (e.g., `Add single-frame denoiser strategy`), mirroring the existing history.
- For PRs, provide: summary, key screenshots or GIFs for visual changes, test notes (scene, camera path, platform), and linked issues if applicable.
- Call out shader changes explicitly so reviewers can focus on variant reimport impacts.

## Agent Tips
- When scripting new denoisers, create a helper class under `Runtime/Denoisers/` to keep `ScreenSpaceGlobalIlluminationURP.cs` lean.
- Reuse existing Shader IDs and material properties; new parameters should be added to `SSGIInput.hlsl` and wired through the volume.
- Keep Render Graph fallbacks in mind—maintain copy paths when compute support is unavailable.
