# Gameplay & movement

[← README](../README.md) · [Networking](networking.md) · [Architecture](architecture.md) · **Gameplay**

The character is driven by a single pure, engine-free state-machine step, `JumpNowBro.Util.Movement.Step`, which both the host simulation and the client-side predictor call as the **same compiled symbol**. That
shared determinism is what makes client prediction and reconciliation possible at all (see
[Networking](networking.md#host-sim--client-prediction--reconciliation)), and it's why the core is
CI-testable and locked by a 300-tick golden master (see [Architecture](architecture.md#testing--ci)).

## Shared control

The shared-control mechanic is implemented as pure data: a host-owned `ControlMap` routes two raw input
frames into one `EffectiveInput`, and `SwapTrigger` volumes flip ownership mid-level. Keeping the map
host-authoritative (the client's copy is HUD-only) means the simulation never reads a value that could be
stale across the wire. The trigger fires a **static** cross-assembly event, so `Gameplay` never has to
reference the `Networking` assembly, preserving the clean Phase 1 / Phase 2 boundary.

```mermaid
flowchart LR
    P1["Player 1 frame"] --> ROUTE{"ControlMap.Route<br/>(host-authoritative)"}
    P2["Player 2 frame"] --> ROUTE
    ROUTE -->|"move owner"| EFF["EffectiveInput"]
    ROUTE -->|"jump owner"| EFF
    ROUTE -->|"dash owner"| EFF
    EFF --> STEP["Movement.Step (deterministic)"]
    STEP --> BODY["One shared character"]
    SWAP["SwapTrigger volume"] -->|"flip action owner"| ROUTE
    STEP -.->|"same symbol"| CLIENT["Client predictor replay"]
```

<sub>Each action is owned by exactly one player; `SwapTrigger` volumes reassign ownership mid-level, and the routed input feeds the one deterministic step shared by host and client.</sub>

## Movement & feel

The feel is **Celeste-scoped, not Celeste-complete**: instant-accel horizontal run, a fixed-impulse jump
with **variable height** (release-to-halve while rising), **coyote time** and **jump buffering** (both
0.1 s) to forgive the input side, and a freeze-then-burst **dash** with one charge refunded on landing.
Wall jump/climb and 8-way dash are explicitly out of scope to keep the state machine small and the netcode
surface honest, the dash is horizontal-only for the MVP.

A dash that touches ground and finishes airborne grants the same 0.1-second coyote window as a walk-off.
A dash spent entirely in the air grants no extra jump. Ground contact is stored in the movement snapshot
so client reconciliation preserves this distinction; the dash charge stays spent until landing.

```mermaid
stateDiagram-v2
    [*] --> Falling
    Grounded --> Jumping: jump (coyote / buffer)
    Grounded --> Dashing: dash + charge
    Grounded --> Falling: walk off ledge (arm coyote)
    Jumping --> Falling: velY <= 0
    Jumping --> Jumping: release while rising (velY *= 0.5)
    Jumping --> Dashing: dash + charge
    Falling --> Grounded: land (refund dash)
    Falling --> Jumping: jump (coyote / buffer)
    Falling --> Dashing: dash + charge
    Dashing --> Dashing: freeze ticks, then burst velX
    Dashing --> Falling: dashTimer <= 0
    Falling --> [*]: posY < fallLimit (die)
```

<sub>`Movement.Step` state machine: 0.1 s coyote/buffer forgiveness on jump, variable-height cut on release, one-charge dash refunded on landing, fall-limit death.</sub>

A subtle but load-bearing choice: the dash **freeze-frame is counted in ticks, not seconds**. Because client
reconciliation replays buffered ticks under a variable `dt`, a seconds-based freeze would drift between the
original sim and the replay; an integer tick count is rollback-stable. The same instinct drove separate float
fields in the wire `MovementState` (no `Vector2`) for an engine-free, replay-friendly layout. Per-tick
transitions are reported out-of-band as **edge flags** (Jumped / JumpCut / Dashed / Landed / Died) so the
presentation layer fires juice and death off explicit edges rather than diffing state.

## Hazards, checkpoints & levels

Hazards (spikes), checkpoints, and the level goal are thin **host-authoritative** trigger volumes, all gated
on `Authority.IsHost`, so the client's local collisions never desync state. Deaths arrive on the client via a
`STATE` death-count delta / `Death` `EVENT`; level loads via a `LevelLoad` `EVENT`. Death routes through one
0.4 s respawn coroutine (a fall below the fall-limit or a hazard touch both lead there), teleporting to the
last checkpoint via `transform.position` + `Physics2D.SyncTransforms` so the next-tick collision cast sees
the new pose immediately.

Three short levels (`Level_01/02/03`) load **additively over a persistent `Bootstrap` scene**. Keeping
`Bootstrap` resident (and at build index 0) is what lets the byte-encoded scene index in the protocol stay
stable, and it's why you always start from `Bootstrap`. Starting tuning constants (run 9, jump 16, gravity 50, coyote/buffer 0.1 s, dash 4 units over 0.15 s) live on a `PlayerTuning` ScriptableObject so they
can be iterated without touching code.
