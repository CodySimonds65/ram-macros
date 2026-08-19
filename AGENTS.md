# Foreground automation work

This repository uses purpose-based branches only (`feature/`, `fix/`, `test/`,
and similar); never use `agent/` or `codex/` prefixes.

Responsibilities for the foreground automation rollout:

- Feasibility investigator: maintain the real-client A/B evidence and classify
  which event classes Roblox consumes.
- Macro scheduler investigator: maintain ordered multi-account sessions,
  cancellation, result aggregation, and request correlation.
- RAM-host investigator: review capability separation, live HWND identity,
  quotas, and lifecycle cancellation with the host repository.
- Windows acceptance investigator: run foreground, stale-window, mixed-DPI,
  cursor, and user-takeover tests on real clients.
- Adversarial reviewer: independently challenge foreground theft, UIPI and raw
  input assumptions, stale PID/HWND reuse, held-input cleanup, plugin abuse,
  queue exhaustion, and shutdown races.

Foreground `SendInput` is intentionally serialized through the host. It may
briefly change focus and move the cursor; a user takeover cancels automation.
Legacy background message requests remain wire-compatible but fail closed with
`foreground-required` and must never be described as gameplay delivery.
