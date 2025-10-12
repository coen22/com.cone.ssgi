# Denoiser Implementation Checklist

This checklist tracks how the package lines up with research sources that inform the denoising roadmap. Checkmarks indicate items that are already implemented in the current codebase, while empty boxes identify future work.

- [x] **Edge-aware LUT denoiser with adaptive kernel (Fu et al. 2025).** Implemented via the `SSGIAdaptiveLutDenoiser` helper and its `SSGI_EdgeAdaptiveLUT` compute kernels, including optional temporal reprojection driven by the LUT weights.【F:Runtime/Denoisers/SSGIAdaptiveLutDenoiser.cs†L9-L172】【F:Shaders/Resources/SSGI_EdgeAdaptiveLUT.compute†L1-L208】
- [ ] **Adopt NVIDIA NRD pipeline variants (ReBLUR, SIGMA, ReLAX) and Metro Exodus-style recurrent blur.** Only exploratory comments exist; a full NRD integration and recurrent filtering pass still needs to be evaluated against the package architecture.【F:Shaders/ScreenSpaceGlobalIllumination.shader†L944-L1013】
- [x] **Edge-aware Gaussian à-trous denoiser for glossy reflections (Boissé et al. 2023).** Covered by the configurable A-trous compute path that supports multi-iteration filtering with normal, depth, and albedo guidance.【F:Runtime/Denoisers/SSGIEdgeAwareAtrousDenoiser.cs†L8-L200】【F:Shaders/Resources/SSGI_EdgeAwareAtrous.compute†L1-L207】
- [x] **Weighted à-trous linear regression (WALR) refinement.** Available through the dedicated WALR denoiser binding to the `SSGI_WALR_DiffuseGI` compute shader for regression-guided filtering of diffuse GI.【F:Runtime/Denoisers/SSGIWalrDenoiser.cs†L8-L200】【F:Shaders/Resources/SSGI_WALR_DiffuseGI.compute†L1-L208】
- [ ] **Fast Local Neural Regression (FLNR) for 1-spp GI (Salmi et al. 2024).** Neural regression filtering is not yet part of the package and remains a prospective addition.
- [ ] **Temporally stable neural denoising and upsampling (Thomas et al. 2022/2025).** No neural supersampling or denoising networks are currently integrated; further investigation is required.
- [ ] **Ship-ready NRD adoption for production tools (NVIDIA Blog 2025, Chaos Enscape 4.0).** Evaluate feasibility of bundling official NRD backends or providing hooks for external NRD usage to match commercial pipelines.

Use this list to prioritize future enhancements and keep parity with the referenced research.
