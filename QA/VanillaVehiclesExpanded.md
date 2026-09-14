# Vanilla Vehicles Expanded — PR #621 QA

Tested September 13, 2026 (Pacific) against the installed releases of Vehicle Framework and base Vanilla Vehicles Expanded. This branch includes upstream `1a050f0`, PR #621's `213de88`, and the five compatibility fixes in `9698f45`.

**24 of 31 behavior groups passed, six are partial/blocked, and one is not applicable. This is not an all-green release sign-off.** Existing qualified caravan tests were retained; other packs and features absent from base VVE were excluded.

Runtime: RimWorld 1.6.4871 rev591; Multiplayer 0.11.5+4a3be27-dirty; Vehicle Framework 1.6.2144. The final tested referenced compatibility DLL SHA256 was `9C46F5A1225BD39AF472C4115137B86943C3A02EAB24B1478ED97E30AB9D2DC2`. Release build: zero warnings/errors. Both peers used matching mod lists and binaries.

## Compatibility fixes verified

- Cargo tab serialization: client drop and host drop-all succeed without `No writer for type`.
- Buffered fuel target: no idle GUI command flood; host/client slider and auto-refuel changes agree; fresh joins pass.
- Held-pawn references: second-card crew assignment/removal and both directions of passenger-role dragging work.
- Turret target clearing: opening/canceling clears the target on both peers; subsequent refill no longer causes the reproduced desync.
- World-unload targeter cleanup: joining with the preliminary landing selector open clears it without an exception; a fresh selector can land normally.

## Completed end-to-end paths

- Shared caravan/cargo sessions, native vehicle and crew selection, route return, quantity/accept/reset/cancel operations, item orders, boarding and disembarking.
- Fuel controls and inventory consumption; turret targeting/firing, fire-mode and auto-target changes; partial-magazine reload and quota changes.
- Map launch, world reroute, airborne rejoin, targeted landing and cancellation.
- Dinghy boarded, exited through a synchronized water corridor, formed a river caravan, moved along 43271↔43272 and returned to the map. Vehicle, driver, meals and fuel agreed.
- Caravan Rest and Repair toggles agreed in both directions. Dinghy side-panel health repaired from 42.2291946 to 80 on both peers. Bunsen returned to the colony with its self-targeted idle job intact.
- Tango reached its destination after a native wall was built across the direct route; the wall remained intact on both peers.
- Final save/rejoin preserved vehicle IDs, crews, cargo, health, positions, the wall and self-targeted idle jobs. Both peers remained `ClientPlaying`, `desynced=false`, with zero open sessions and no open attention item after another 310 authoritative ticks.

## Partial or blocked groups

| Groups | Remaining qualification |
|---|---|
| L4, A3 — world cargo/passengers | Map cargo passes. Native in-flight discard submitted the item ID but MP resolved it as null. VF's released `AerialVehicleInFlight.IThingHolder.GetChildHolders` is empty, preventing recursive discovery of nested cargo. The full world passenger-manifest case is also not qualified. Fix the framework holder enumeration and retest; no replacement VF binary or compatibility workaround was deployed. |
| T3, T4 — reload controls | Native reload 29→30 consumed one steel; quota host 10→60/client 60→30 and Bunsen ammo→fuel +5 passed. Released VF disables the whole turret gizmo when empty, also disabling Reload and Reload from fuel. Current VF source changes that behavior, but the installed release does not. The alternate autoload dialog is behind a disabled feature flag and was not enabled for QA. |
| C4 — force departure | The synchronized command switches to the boarding duty but VF leaves the current gathering job running. A normal boarding order was used to finish the boat departure. Immediate forced departure is not claimed as passed. Idle-job restoration passes. |
| C6 — stash/retrieve | Both peers preserved the same vehicle, pawns and cargo through stash/recovery and return. VF fallback seating logged `Unable to add Sullivan to vehicle Bunsen` on both peers and left him dismounted. This framework behavior needs separate follow-up; no compatibility workaround was added. |

T5 deploy/undeploy is not applicable: released stock VVE definitions do not expose `deployTime`.

Incidental crew exhaustion, a manhunting hare and QA-driver failures were separated from patch failures. Affected cases were retried with synchronized native fixture setup. All semantic passes include delayed checks on both peers; a successful tool call alone was not counted.

Both games were stopped through GABS, the original installed compatibility DLLs were restored and verified, and the temporary QA helper was removed. Upstream PR #621 and its original head branch were not modified by this run.
