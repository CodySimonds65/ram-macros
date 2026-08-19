# RAM Macros

RAM Macros is a standalone Apache-2.0 Roblox Account Manager plugin for portable, window-relative macro recording and playback. It exchanges length-prefixed JSON with the launcher host and requests one guarded foreground automation session for each batch. Selected accounts run in order through the single desktop-wide input stream; focus may switch briefly and mouse events may move the cursor, then the prior client is restored when safe. User takeover cancels without fighting for focus.

Legacy background-message requests remain wire-compatible but fail closed with `foreground-required`; a posted message is never reported as gameplay consumption.

The `.ramacro` bundle is RAM’s own versioned ZIP format. Bundles contain metadata and optional preview assets only—never executables—and unknown optional fields survive a round trip. Standard playback follows account-selection order; multi-window playback requires explicit role mappings.

Build with the .NET 8 Windows SDK. The release package contains `plugin.json`, `ram-macros.exe`, `plugin.zip`, `plugin.sha256`, and a pinned Ed25519 signature.

## Official releases

After a PR is merged, the repository workflow publishes the matching semantic version automatically. If both manifests still contain the latest published version, the workflow creates a patch-only release commit and publishes the next patch version; major and minor version changes remain explicit. Configure `RAM_PLUGIN_SIGNING_KEY` (Ed25519 private PEM) and `RAM_PLUGIN_SIGNING_PUBLIC_KEY` (matching public PEM) repository secrets first. The public key must match the launcher trust anchor; missing secrets fail closed and never publish unsigned official assets. Manual dispatch remains available as a recovery path.
