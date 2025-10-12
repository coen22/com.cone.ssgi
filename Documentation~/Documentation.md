# Documentation

Welcome to the Screen Space Global Illumination (SSGI) for URP documentation hub. Use this page as an entry point to the
package's guides, feature breakdowns, and reference material.

## Getting Started

- **Package Overview:** Review the [README](../README.md) for installation, renderer feature setup, and high-level
  requirements before importing the package into a project.
- **Sample Scene:** Import the *Relaxing Corner* sample through the Unity Package Manager to validate your setup and explore
  recommended lighting settings.

## Feature Guides

- **Renderer Feature Configuration:** Consult `Runtime/ScreenSpaceGlobalIlluminationURP.cs` and the associated volume component
  for details on available parameters, temporal accumulation controls, and render-graph integration points.
- **Denoiser Variants:** Use the [Denoiser Implementation Checklist](./DenoiserImplementationChecklist.md) to understand which
  research-inspired denoisers are implemented today and what is planned next.

## Contributing

- **Extending Denoisers:** When authoring new denoiser helpers, mirror the structure of existing classes under
  `Runtime/Denoisers/` and include cross-links to the guiding research in the checklist.
- **Testing Expectations:** Validate changes in both forward and deferred URP paths using the sample scene, and document your
  findings in pull requests for quick reviewer context.

Additional topic-specific guides will be added over time. If you cannot find the answer you need, open an issue so the team can
expand this hub.