# RAM Macros

RAM Macros is a standalone Apache-2.0 RoRoRo/Roblox Account Manager plugin for portable, window-relative macro recording and playback. It exchanges length-prefixed JSON with the launcher host and never uses foreground-input APIs. Background delivery is attempted through the host broker; a rejected or stale target is reported and skipped.

The `.ramacro` bundle is RAM’s own versioned ZIP format. Bundles contain metadata and optional preview assets only—never executables—and unknown optional fields survive a round trip. Standard playback follows account-selection order; multi-window playback requires explicit role mappings.

Build with the .NET 8 Windows SDK. The release package contains `plugin.json`, `ram-macros.exe`, `plugin.zip`, `plugin.sha256`, and a pinned Ed25519 signature.

## Official releases

The repository workflow publishes a release from a matching `vMAJOR.MINOR.PATCH` tag or a manual dispatch. Configure `RAM_PLUGIN_SIGNING_KEY` (Ed25519 private PEM) and `RAM_PLUGIN_SIGNING_PUBLIC_KEY` (matching public PEM) repository secrets first. The public key must match the launcher trust anchor; missing secrets fail closed and never publish unsigned official assets.
