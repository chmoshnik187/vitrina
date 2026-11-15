# Clean-Room Desktop Mechanics and Feature-Ideas Brief

**Status:** Design brief (clean-room / generalized)  
**Date:** 2026-11-15  
**Product context:** Vitrines — Windows desktop app (WPF, .NET 8) with a UI-agnostic `Vitrines.Core` and a desktop/UI layer in `Vitrines.App`.

## Clean-room statement and scope

- This document synthesizes **generalized, well-known** desktop shell and container/organizer mechanics (common Windows shell behavior, standard window-management patterns, and broadly used desktop widget/organizer paradigms).
- The user referenced external inspiration links during the request. Those sources were **not** consulted for proprietary specifics in this brief, and this document does **not** reproduce, describe, or infer proprietary implementation details, source code, assets, layouts, distinctive interaction designs, or trademarked behaviors of any specific product.
- All ideas below are expressed as **independent design options** for Vitrines’ own architecture. Any similarity to existing products is incidental to common platform capabilities and widely known UX patterns.
- Where Windows behavior is complex or version-dependent, items are framed as concepts to validate through prototyping on supported Windows versions (e.g. Windows 10/11) rather than as guarantees.

## How this maps to Vitrines architecture

- **`Vitrines.Core` (domain/logic, no UI):** models, rules, sorting/orchestration, watcher, storage, snapshots/profiles, history/undo, inbox, file operations, safety policies.
- **`Vitrines.App` / `Desktop` (WPF + Win32 interop):** vitrines as windows, visual styling, input/drag-drop UI, layering/z-order/no-activate behavior, shell/display event handling, animations, accessibility presentation.
- **Cross-cutting:** configuration and persistence live in Core/storage; Desktop applies and visualizes state. Prefer keeping policy/decisions testable in Core and platform-specific window behavior isolated behind `App/Desktop` abstractions.

---

## 1. Window and layer behavior on Windows desktop

### 1.1 Container window model (general concepts)

| Mechanic | Generalized behavior | Design notes for Vitrines | Feasibility / risk (Windows, WPF) |
|---|---|---|---|
| Top-level container windows | Each container is an independent window positioned on the desktop; can be moved/resized like a lightweight utility window. | Keep one window per vitrine; persist bounds/state in layout. Separate “collection” vs “portal” content model (already in product concept) from window chrome/behavior. | **High.** Standard WPF windows. Risk: behavior varies with shell features; isolate Win32 tweaks in `Desktop`. |
| Show/hide all containers | A global toggle hides or shows all containers without destroying state (often via hotkey/tray action). | Implement as visibility state in manager; remember prior visibility per profile/session if useful. | **High.** Straightforward in WPF. Risk: focus/activation side effects when showing. |
| Minimize / restore | Containers can minimize to taskbar or be restored to previous bounds. | Decide product stance: minimize-to-taskbar vs hide-to-tray/global-toggle to reduce desktop clutter. Persist last normal bounds. | **High** for normal minimize/restore. **Medium** if avoiding taskbar presence (tool-window styles can affect activation/task switching). |
| Roll-up (collapse to title bar / “shade”) | Container collapses to a compact header showing title/actions; expands on click/toggle. | Useful for dense desktops. Store expanded/collapsed state per vitrine. | **Medium.** Custom chrome/layout in WPF. Risk: resizing/hit-testing edge cases; accessibility must expose expanded state. |
| Always-on-bottom (desktop-layer feel) | Containers sit behind normal app windows but above the wallpaper/icon layer in perceived use, so they behave like desktop-attached organizers. | Goal: “available on desktop, not in the way.” Use a managed bottom z-order strategy and reassert when needed (on show, shell events). Avoid fragile assumptions about undocumented shell internals. | **Medium.** Achievable with Win32 z-order management; **risk** across Windows updates, Explorer restarts, and multi-monitor changes. Prefer resilient reapply logic over one-time hacks. |
| Normal/topmost modes (optional) | Some users may want a container temporarily above other windows. | Offer as explicit per-vitrine or transient mode, defaulting to bottom/normal as appropriate. | **High** for topmost; **Medium** to combine cleanly with no-activate/bottom goals. |
| No-activate / focus rules | Clicking/dragging inside a container should not steal focus from the user’s foreground app unless intentional (e.g. text input/rename). | Apply non-activating styles for passive interactions; allow explicit activation for keyboard-heavy editing fields. Document which actions activate. | **Medium.** Win32 extended styles/messages can help, but input/IME/caret and accessibility focus require careful testing. |
| Z-order among containers | Deterministic stacking between containers (e.g. bring selected container forward within the bottom group). | Maintain an internal order; reapply within allowed band. Persist order if users rely on overlap. | **Medium.** Needs coordination to avoid fighting the shell or user expectations. |

### 1.2 Show-desktop and shell interaction concepts

| Mechanic | Generalized behavior | Design notes for Vitrines | Feasibility / risk |
|---|---|---|---|
| “Show desktop” interaction | System action that reveals the desktop; utility windows may hide, minimize, or remain depending on window type/configuration. | Define desired outcome (e.g. containers remain visible as desktop companions). Test with taskbar “Show desktop”, Win+D, Aero Peek behaviors conceptually; provide a fallback “show all” command. | **Medium–High risk.** Behavior can differ by Windows version and window flags. Implement defensively and make recoverable via hotkey/tray. |
| Shell restart / Explorer restart | When the shell restarts, custom-layered windows can lose placement/z-order or disappear from expected layer. | Detect/recover: listen for relevant shell messages where appropriate, and/or periodically/occasionally validate visibility and re-pin/reposition. Persist state independently of shell lifetime. | **Medium.** Requires robust recovery paths; avoid tight coupling to undocumented window hierarchies. |
| Taskbar/task switching presence | Containers may be excluded from Alt+Tab/taskbar to feel like desktop furniture, or included for discoverability. | Choose per mode (passive desktop container vs manageable window). Provide an obvious way to summon/focus via global UI (tray/panel/hotkey). | **Medium.** Style choices affect activation and accessibility tooling visibility. |

### 1.3 Multi-monitor, DPI, and display changes

| Mechanic | Generalized behavior | Design notes for Vitrines | Feasibility / risk |
|---|---|---|---|
| Multi-monitor placement | Containers can live on any monitor; positions are meaningful in virtual-screen coordinates. | Save monitor-relative hints if helpful, but always be able to clamp into the current virtual desktop/work area on restore. | **High.** **Risk:** monitor IDs/configs change (docking/remote sessions). |
| DPI awareness and scaling | Correct sizing/positioning under per-monitor DPI (PMv2) and dynamic DPI changes. | Ensure app is PerMonitorV2-aware (already a product direction). Scale visuals and hit targets appropriately; retest after DPI change. | **High** in WPF with correct manifest/config; **Medium** for mixed-DPI edge cases in custom rendering/interop. |
| Resolution / monitor add-remove | Previously valid bounds may become off-screen. | On display-change events and on load, clamp/migrate windows into visible work areas with a sensible minimum size. | **High.** Must be deterministic and non-destructive. |

### 1.4 Global hotkeys (general)

| Mechanic | Generalized behavior | Design notes for Vitrines | Feasibility / risk |
|---|---|---|---|
| System-wide shortcuts | Hotkeys toggle visibility, create/focus a container, run “sort now”, etc., even when app not focused. | Register/unregister cleanly; allow user customization and conflict detection; fail gracefully if a combo is taken. | **Medium.** Win32 registration is standard but global namespace collisions and permissions/session changes are common. |
| Discoverability & safety | Users must be able to see/change/disable hotkeys. | Expose in settings with clear labels; provide non-hotkey alternatives (tray, panel). | **High** (UX/process). |

---

## 2. Container (vitrine) interactions

### 2.1 Drag-and-drop (in / out / between)

| Mechanic | Generalized behavior | Design notes for Vitrines | Feasibility / risk | Primary layer |
|---|---|---|---|---|
| Drop into container | Accept files/folders/shortcuts from Explorer or other sources; show clear drop target/hover state. | Route to Core as an intent (add/move/link) based on container mode and settings; perform file ops via `IFileOperator`-style abstractions. | **High** in WPF for basic data formats; **Medium** for shell namespace objects, virtual files, or elevated-source drops. | App + Core |
| Drag out of container | Drag items to Explorer/desktop/other apps as files/links as appropriate. | Preserve semantics (move vs copy vs link) consistent with modifiers/policy; never surprise-move pinned items without intent. | **Medium.** Correct DataObject formats and effects require care. | App + Core |
| Move between containers | Reassign item membership; for collections this may mean moving/relocating managed storage; for portals it may mean filesystem move within/between real folders (with confirmation/policy). | Make the resulting physical effect explicit in UI and history. Use undoable operations where possible. | **Medium–High.** Depends on portal semantics and cross-volume moves. | Core + App |
| Drag container (reposition) | Drag by header/empty area moves the window. | Respect snap/alignment helpers; avoid accidental drags when interacting with items. | **High.** | App/Desktop |

### 2.2 Portals vs collections (conceptual distinction)

| Mode | Generalized behavior | Design notes | Feasibility / risk | Primary layer |
|---|---|---|---|---|
| Collection | Container represents an app-managed set; items may be stored/moved into managed storage or tracked by reference per product policy. | Clear ownership: where bytes live, what “remove” means, and how backups/snapshots include state. | **High** (already aligned). Risk: user confusion if semantics unclear. | Core + App |
| Portal | Container reflects a real folder’s contents; actions map to real filesystem operations in that folder. | Show source path prominently; handle external changes via refresh/watcher; define behavior for delete/move carefully. | **Medium.** Needs robust refresh, error handling, and permission awareness. | Core + App/Desktop |

### 2.3 Selection, context menus, renaming

| Mechanic | Generalized behavior | Design notes for Vitrines | Feasibility / risk |
|---|---|---|---|
| Single/multi-selection | Click, Ctrl/Shift range/toggle selection; keyboard navigation. | Standard list/grid selection model; keep focus/selection visuals accessible. | **High.** |
| Context menus | Contextual actions: open, reveal, rename, remove, pin/lock, move to…, properties-like info (general). | Commands should map to Core operations where stateful; disable unavailable actions with reasons. | **High.** |
| Rename (item/container) | Inline or dialog rename with validation (illegal chars, collisions). | Distinguish display name vs filesystem name where applicable; log/audit meaningful renames. | **High.** |
| Container management | Create/delete/duplicate/configure container from UI. | Confirm destructive deletes; preserve ability to undo or recover via snapshots where feasible. | **High.** |

### 2.4 Resizing, moving, snapping, alignment

| Mechanic | Generalized behavior | Design notes for Vitrines | Feasibility / risk |
|---|---|---|---|
| Resize/move | Standard window resize grips and drag; min/max constraints. | Enforce sensible minimums for header/content; persist per container. | **High.** |
| Snapping/alignment | Snap to screen edges, other containers, or a grid; optional alignment guides. | Implement as lightweight layout assistance (post-drag adjust) rather than invasive modal behavior. | **Medium.** Guide rendering and multi-monitor edges need polish. |
| Layout presets/profiles | Save/restore arrangements (conceptually similar to snapshots/profiles). | Leverage existing snapshot/profile direction in Core/storage; include bounds, mode, visual prefs. | **High.** |

### 2.5 Overflow, scrolling, empty states

| Mechanic | Generalized behavior | Design notes for Vitrines | Feasibility / risk |
|---|---|---|---|
| Overflow handling | When items exceed visible area: scroll, paginate, or compact modes. | Default to virtualized/scrollable view for performance; offer density options. | **High.** Use virtualization for large sets. |
| Empty states | Clear messaging and primary actions (drop here, add rule, open folder/portal). | Different copy for collection vs portal vs “no matches/inbox.” | **High.** |
| Loading/refresh states | Indicate when contents are updating (portal refresh, watcher batch). | Non-blocking indicators; avoid flicker. | **High.** |

---

## 3. Item behaviors

| Mechanic | Generalized behavior | Design notes for Vitrines | Feasibility / risk | Primary layer |
|---|---|---|---|---|
| Open | Default action opens file/folder/link with associated handler. | Respect Windows shell execution; handle missing targets gracefully. | **High.** Risk: security prompts/blocked file types are OS/user-policy dependent. | App |
| Reveal in folder | Show item in its containing folder in Explorer. | Works for real paths and resolved targets; define behavior for virtual/managed items. | **High.** | App |
| Pin / lock | Prevent auto-sort/move/remove by automation; mark as user-managed. | Map to rule override/exception or item flag in Core; show lock indicator. | **High.** | Core + App |
| Rename | Change display/filesystem name per item type. | Validate, handle conflicts, support undo where operation is reversible. | **High.** | Core + App |
| Remove | Remove from container vs delete from disk vs unlink reference (distinct meanings). | Require explicit choice/confirmation for destructive actions; log in history. | **High.** | Core + App |
| Preview / tooltip | Hover/details show name, path, type, size/date, rule/source (general metadata). | Keep previews lightweight by default (text/tooltips, cached icons); avoid heavy thumbnailing unless opted-in. | **Medium** for rich thumbnails/previews (shell handlers, performance). | App (+ Core metadata) |
| Icons | Show recognizable icons for files/folders/shortcuts/apps. | Use system icon resolution with caching; handle missing/broken shortcuts. | **High.** | App/Desktop |
| Type distinctions | Files, folders, shortcuts/links, and app references behave differently (targets, move semantics). | Model types explicitly in Core; surface type badges/labels in UI. | **High.** | Core + App |

---

## 4. Sorting and rules automation UX

### 4.1 Triggers and execution model

| Mechanic | Generalized behavior | Design notes for Vitrines | Feasibility / risk | Primary layer |
|---|---|---|---|---|
| Watcher-based triggers | React to filesystem events in configured source folders. | Debounce/batch, wait for file stability, ignore temp/incomplete patterns, prevent feedback loops on app-produced moves. | **High** conceptually; **Medium** operationally (event storms, missed events, network paths). | Core/Watcher |
| Manual run | “Sort now” for a folder or all sources. | Reuse same pipeline as automation for consistency; show progress/summary. | **High.** | Core + App |
| Drag-drop as trigger | User-initiated classification/move via drop. | Treat as explicit intent; may bypass some auto rules or apply a chosen rule/profile. | **High.** | App + Core |
| Scheduling | Periodic runs at intervals or times (optional). | Only when automation enabled per settings; avoid overlapping runs; log outcomes. | **Medium.** Background timing + app lifecycle considerations. | Core + App |

### 4.2 Rule authoring, preview, and explainability

| Mechanic | Generalized behavior | Design notes for Vitrines | Feasibility / risk | Primary layer |
|---|---|---|---|---|
| Ordered/prioritized rules | First-match or priority-based evaluation with clear precedence. | Show effective order, allow reorder, support exception rules and global excludes. | **High.** | Core + App |
| Conditions (general) | Match on name/pattern, extension/type, size, dates, source location, etc. | Keep predicates composable and testable; validate regex/pattern inputs. | **High.** | Core |
| Actions (general) | Route to container/portal, move/copy/link per policy, rename via template, send to inbox, ignore. | Prefer non-destructive defaults; make destination and physical effect explicit. | **High.** | Core |
| Preview / dry-run | Simulate outcomes without changing files; show which rule would match and why. | Essential for trust. Provide per-item and batch summaries; reuse engine explain output. | **High.** | Core + App |
| “Why did this happen?” | Per-action explanation (matched rule/condition) and per-skip reason. | Store reason codes/messages in history/inbox; surface in UI filters. | **High.** | Core + App |

### 4.3 Undo/history, inbox/unmatched, conflicts, safety

| Mechanic | Generalized behavior | Design notes for Vitrines | Feasibility / risk | Primary layer |
|---|---|---|---|---|
| History / audit log | Record automated and relevant manual actions with timestamps, source, rule, from/to. | Append-only log with filters; bound/rotate storage thoughtfully. | **High.** | Core/Storage |
| Undo | Reverse supported operations (e.g. moves/renames) individually or in batches where safe. | Undo is best-effort: original paths may be occupied/removed; report partial failures clearly. | **Medium.** Feasibility depends on operation type and external changes. | Core + App |
| Inbox / unmatched handling | Items that don’t match, fail, or need review go to a holding area with reasons. | Provide quick actions: create rule from item, move manually, ignore/exclude, retry. | **High.** | Core + App |
| Conflict cues | Detect name collisions, missing sources, permission/lock errors, cross-volume constraints. | Policies like rename/skip/replace must be explicit and configurable; show pre-action warnings in dry-run where detectable. | **Medium.** Not all conflicts are knowable before execution. | Core + App |
| Safety defaults | Avoid destructive actions by default; protect system/hidden/temp paths via configurable excludes. | Global “do not touch” rules, pin/lock, conservative conflict policy, and loop prevention are baseline safety features. | **High.** | Core |

---

## 5. Visual design and feedback

### 5.1 Themes and materials

| Mechanic | Generalized behavior | Design notes for Vitrines | Feasibility / risk |
|---|---|---|---|
| Themes (light/dark/glass-like variants) | Consistent color tokens for background, text, borders, accents, states. | Maintain theme resources/styles; ensure contrast ratios and legibility on varied wallpapers. | **High.** |
| Translucency / glass effects | Semi-transparent surfaces that hint at desktop behind the container (where supported). | In WPF, use supported backdrop/blur/translucency techniques appropriate to target Windows versions; provide opaque fallback. Avoid relying on deprecated/undocumented effects. | **Medium.** Visual results and performance vary by GPU/OS/settings; must degrade gracefully. |
| Density and scale | Adjustable icon size, padding, font scale for readability and capacity. | Expose a few presets plus fine controls if needed; persist per profile. | **High.** |

### 5.2 Motion and state indicators

| Mechanic | Generalized behavior | Design notes for Vitrines | Feasibility / risk |
|---|---|---|---|
| Subtle animations | Appear/disappear, item transitions, or target highlight to confirm actions. | Keep short and purposeful; never block interaction; ensure they communicate causality (e.g. item routed to container). | **Medium.** WPF animations are feasible; performance tuning needed for many items. |
| State indicators | Badges/icons for pinned/locked, portal vs collection, syncing/refreshing, errors, rule-driven placement. | Prefer clear iconography + text in tooltips; avoid color-only signaling. | **High.** |
| Progress/feedback | Indicate background sorting, long file operations, or partial failures. | Non-modal notifications/summaries linked to history/inbox. | **High.** |

### 5.3 Accessibility and reduced motion

| Mechanic | Generalized behavior | Design notes for Vitrines | Feasibility / risk |
|---|---|---|---|
| Keyboard access | Navigate containers/items, invoke context actions, rename, and toggle visibility without mouse. | Define focus order and shortcuts; ensure non-activating window behavior does not break keyboard workflows. | **Medium.** Requires deliberate focus/activation design. |
| Screen reader / UI automation | Exposed names, roles, states (expanded/collapsed, selected, locked). | Use standard WPF automation patterns/properties; label icon-only buttons. | **Medium–High.** Needs verification with assistive tech. |
| Reduced motion | Honor system/app preference to minimize or disable animations. | Global toggle and/or follow OS setting; replace motion with instant state changes/highlights. | **High.** |
| Contrast and hit targets | Readable text over translucent backgrounds; adequately sized controls. | Provide solid/high-contrast theme options and scalable UI. | **High.** |

---

## 6. Suggested incremental roadmap (mapped to Core vs App/Desktop)

The following items are **generalized next steps**; prioritize based on user value, current gaps, and risk tolerance. “Depends on” notes assume continued separation of concerns.

| # | Roadmap item | Description (generalized) | Primary area | Feasibility | Risk | Depends on / notes |
|---|---|---|---|---|---|---|
| 1 | Harden desktop-layer recovery | Improve resilience of bottom-layer/no-activate behavior across shell restart, display/DPI changes, and show-desktop scenarios; add self-healing checks. | App/Desktop (+ settings in Core) | Medium | **High** (Windows variability) | Build on existing interop; add diagnostics/logging hooks. |
| 2 | Global hotkey system | User-configurable hotkeys for show/hide all, create/focus vitrine, sort now; conflict detection and safe fallback UI. | App/Desktop (+ Core commands) | Medium | **Medium** (conflicts, focus) | Clear command routing from UI to Core operations. |
| 3 | Roll-up / compact container state | Optional collapse-to-header and compact density modes per vitrine, persisted in layout/profile. | App/Desktop (+ layout model in Core) | Medium | **Low–Medium** (UX/accessibility) | Extend layout schema; ensure automation states visible when collapsed. |
| 4 | Snapping and alignment aids | Edge/container/grid snapping during move/resize with minimal visual guides. | App/Desktop | Medium | **Low–Medium** | Pure UI/layout assistance; persist only final bounds. |
| 5 | Richer drag-drop semantics | Clear move/copy/link affordances, better drop-target feedback, and consistent behavior across collection/portal modes. | App + Core | Medium | **Medium** (shell formats, cross-volume) | Align with file-op policies and undo/history. |
| 6 | Dry-run and explainability UX polish | Side-by-side preview, per-rule match reasons, and “simulate all sources” summaries with exportable results (optional). | Core (explain) + App (UX) | High | **Low** | Leverages existing rule engine/history concepts. |
| 7 | Inbox triage workflow | Batch actions (create rule, assign, ignore), smarter grouping by reason, and quick filters. | Core + App | High | **Low–Medium** | Requires stable reason taxonomy in Core. |
| 8 | Conflict center and preflight checks | Central view for pending/failed operations (locks, name collisions, permissions) with retry/skip/rename choices. | Core + App | Medium | **Medium** (not all issues pre-detectable) | Integrate with file operator and history. |
| 9 | Accessibility & reduced-motion pass | Systematic keyboard navigation, automation properties, high-contrast validation, and reduced-motion compliance. | App/Desktop (+ theme tokens) | Medium | **Low–Medium** | Cross-cutting; add UI tests/checklists where possible. |
| 10 | Performance/scale improvements | UI virtualization, icon/metadata caching, throttled refresh for portals and large batches, watcher backpressure metrics. | App + Core/Watcher | Medium | **Medium** (edge cases) | Measure with large folders before/after. |
| 11 | Expanded Core test coverage | Tests for schema migrations, watcher/orchestrator stress scenarios, undo edge cases, and explain/dry-run determinism. | Core/Tests | High | **Low** | Improves confidence for roadmap items above. |
| 12 | Optional diagnostics bundle (user-controlled) | Local-only export of logs/config summaries for troubleshooting (no proprietary data collection implied). | Core + App | Medium | **Low–Medium** (privacy/scope) | Must be opt-in, documented, and respect user data boundaries. |

### Roadmap prioritization guidance

- **Stabilize platform behavior first:** items 1, 9, 10 reduce user-visible fragility on Windows.
- **Build trust in automation next:** items 6, 7, 8 make behavior predictable and recoverable.
- **Then enhance ergonomics:** items 2–5 improve daily desktop interaction without changing core data semantics.

## Validation checklist (for future implementation/prototyping)

- Verify window layering, focus/no-activate, and show-desktop behavior on supported Windows builds and common multi-monitor/DPI setups.
- Confirm drag-drop and file operations for files, folders, and shortcuts across same-volume and cross-volume scenarios, including permission errors and locked files.
- Validate dry-run parity with real execution, undo limitations, and history/inbox accuracy.
- Test with reduced motion enabled, keyboard-only navigation, and screen reader tooling; verify contrast on translucent themes.
- Profile watcher and UI refresh under bursty filesystem events and large portals/collections.

*End of clean-room brief.*
