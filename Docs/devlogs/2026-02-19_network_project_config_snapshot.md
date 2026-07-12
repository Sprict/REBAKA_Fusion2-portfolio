# NetworkProjectConfig Snapshot (2026-02-19)

## Context
- Validation setup: same-PC test, `Main Editor = Host`, `Virtual Player = Client`.
- Source of truth:
  - `Assets/Level/Photon/Fusion/Resources/NetworkProjectConfig.fusion`
  - `Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion`
  - Unity Inspector screenshot on 2026-02-19.

## Current Effective Settings
- `HubMode`: `Single` (`2`)
- `PeerMode`: `Single` (`0`)
- `PhysicsForecast`: `false`
- `InputTransferMode`: `Redundancy` (Inspector)
- `SimulationUpdateTimeMode`: `Unscaled Delta Time` (Inspector)
- `PlayerCount`: `4` (Inspector) / file currently `10` (note: keep aligned before release)
- `TickRateSelection`: `Default` (`Client=0, ClientSendInterval=0, ServerTickInterval=0, ServerSendInterval=0`)
- `Resolved Default Tick/Send (Host/Server mode)`: `Tick=64`, `Send=32`
- `Resolved Default Tick/Send (Shared mode)`: `Tick=32`, `Send=16`
- `InvokeRenderInBatchMode`: `true`
- `LagCompensation.Enabled`: `false`
- `NetworkConditions.Enabled`: `false`
- `ReliableDataTransferModes`: `Everything`
- `AssembliesToWeave`: `Assembly-CSharp`, `Assembly-CSharp-firstpass`, `MyProject.Scripts`

## Why This Snapshot Exists
- Client visual stutter analysis depends heavily on:
  - tick/send rate,
  - render timeframe/source (`Local/Remote`, `Interpolated/Latest`),
  - physics forecast and resimulation behavior.
- This snapshot is the baseline for future A/B changes.

## Follow-up Checklist
- If changing tick/send rate, always log before/after with `category=session_config`.
- Keep `Assets/Level/.../NetworkProjectConfig.fusion` and `Assets/Photon/.../NetworkProjectConfig.fusion` identical.
- Before release, align `PlayerCount` between Inspector and `.fusion` file to avoid hidden config drift.
