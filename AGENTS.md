# RAM Macros collaboration roles

- Luna xhigh feasibility investigator: owns the real-client foreground/background message probe and event-class evidence.
- Luna xhigh macro scheduler investigator: owns concurrent playback, cancellation, result aggregation, and request correlation.
- Luna xhigh RAM-host investigator: owns live target validation, capability separation, quotas, and lifecycle cancellation in the launcher integration.
- Luna xhigh Windows acceptance investigator: owns external-foreground, hidden-client, integrity, HWND-recreation, and cursor/foreground acceptance evidence.
- Sol medium adversarial reviewer: independently challenges foreground theft, hidden-target guarantees, UIPI, raw-input assumptions, stale HWND/PID reuse, held-input cleanup, plugin abuse, queue exhaustion, and shutdown races.

The primary agent reconciles all findings and owns final implementation decisions. Never create repository branches with `agent/` or `codex/` prefixes; use purpose-based prefixes such as `feature/`, `fix/`, `test/`, or `chore/`.
