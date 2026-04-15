# Fix SubParts Rendering in Vehicle Editor

## Problem

When a SubPart is added to an existing Part in the vehicle editor via the "Add SubParts" collapsing header in the Part menu, it is **not rendered visually**. The SubPart data is correctly attached to the Part (persists through save/load and appears on launch), but it is invisible during editing.

## Root Cause Analysis

### How Rendering Works

`PartTree.UpdateRenderData()` iterates **only** over `PartTree.Modules` to find `PartModelModule` instances and call their `UpdateRenderData()`. This is the sole path for submitting part meshes to the GPU renderer.

```
PartTree.UpdateRenderData()
  → Modules.Get<PartModelModule>()      // from PartTree.Modules ONLY
    → foreach: partModelModule.UpdateRenderData()
      → PartModel.AddInstance()          // submits to GPU
```

### How SubParts Are Added at Runtime

The stock editor's "Add SubParts" UI calls `part.AddSubPart(subPart)`:

```csharp
// VehicleEditor.cs line ~2750
Part subPart = new Part($"{item3.Id} {part.SubParts.Length}", item3);
part.AddSubPart(subPart);
```

### What `Part.AddSubPart()` Does (and Doesn't Do)

```csharp
public void AddSubPart(Part subPart)
{
    _subParts.Add(subPart);
    subPart.PartParent = this;
    subPart.Tree = Tree;                     // ✅ Links to PartTree
    SubtreeModules.AddFrom(subPart.Modules); // ✅ Part-level aggregation
    ResetModuleProperties();                 // ✅ Updates part flags
    // ❌ DOES NOT call PartTree.Modules.AddFrom(subPart.Modules)
    // ❌ DOES NOT propagate SubPart's modules to PartTree
}
```

**The new SubPart's `PartModelModule` is never added to `PartTree.Modules`, so `UpdateRenderData()` never sees it.**

### How It Works at Initialization (Correctly)

When a PartTree is first created, `ReinitializeDerivedValues()` is called, which:

1. `ClearAllDerivedValues()` — clears `PartTree.Modules` and `States`
2. `AddModulesAndStaticParts()` — iterates ALL Parts AND their SubParts, aggregating all modules into `PartTree.Modules`
3. `AddModuleStates(oldStates)` — restores stateful module data (fuel levels, battery charge, etc.)
4. `RecomputeAllDerivedData()` — rebuilds physics (mass, controls, resource managers)
5. `StageList.ResetCaches()` / `SequenceList.ResetCaches()`

This is why SubParts that exist at load/spawn time render correctly — their modules get swept into `PartTree.Modules` during initialization.

## The Fix

### Strategy: Call `ReinitializeDerivedValues()` on User Request

Per the KSA developers' advice, calling `PartTree.ReinitializeDerivedValues()` will re-aggregate all modules (including newly added SubParts) into the PartTree. This is the safest and most complete fix.

The fix is **user-initiated** via the existing "Refresh Vehicle" button in `DrawRefreshVehicleWindow()`.

### Why `ReinitializeDerivedValues()` and Not a Lighter Approach

| Approach | Pros | Cons |
|----------|------|------|
| `ReinitializeDerivedValues(oldStates)` | Complete, safe, handles all module types, preserves states | Heavier — rebuilds entire tree's module collections |
| Manual `PartTree.Modules.AddFrom(subPart.Modules)` | Lightweight, O(1) per subpart | Doesn't handle states, doesn't update hot-path caches (Tanks, Batteries, etc.), fragile if KSA internals change |
| `RecomputeAllDerivedData()` only | Lightweight | Does NOT rebuild module collections — won't fix the rendering issue at all |

`ReinitializeDerivedValues()` is the correct choice because:
- It's the same function the engine uses internally for this purpose
- It preserves module states (fuel, battery, etc.) via the `oldStates` parameter
- It handles ALL module types including `PartModelModule`, `PartModelDynamicModule`, `PartModelGlassModule`, `LightModule`
- It rebuilds the hot-path caches that the engine relies on for iteration
- The "heavier" cost is negligible for a user-initiated button press

---

## Implementation Tasks

### Task 1: Implement `DrawRefreshVehicleWindow` Button Logic

**File:** `BuilderPlus.cs` — `DrawRefreshVehicleWindow()` method

**Current code (placeholder):**
```csharp
if (ImGui.Button("Refresh Vehicle", new float2(334f, 36f)))
    Console.WriteLine("refrehsed!");
```

**Replace with:**
```csharp
if (ImGui.Button("Refresh Vehicle", new float2(334f, 36f)))
{
    if (editor.EditingSpace.Parts != null)
    {
        var oldStates = editor.EditingSpace.Parts.States;
        editor.EditingSpace.Parts.ReinitializeDerivedValues(oldStates);
    }
}
```

**What this does:**
1. Captures the current `ModuleStateList` from the PartTree (preserves fuel levels, battery charge, engine states, etc.)
2. Calls `ReinitializeDerivedValues(oldStates)` which:
   - Clears `PartTree.Modules` and `States`
   - Re-aggregates modules from ALL Parts and ALL their SubParts (including newly added ones)
   - Restores module states from the captured `oldStates`
   - Recomputes physics (mass, controls, resource managers)
   - Resets stage/sequence caches

After this call, the new SubPart's `PartModelModule` will be in `PartTree.Modules`, and the next `UpdateRenderData()` frame will render it.

### Task 2: (Optional) Add Unattached Tree Refresh

If unattached part trees (parts being held/placed) also need SubPart refresh, extend the button to iterate those too:

```csharp
if (ImGui.Button("Refresh Vehicle", new float2(334f, 36f)))
{
    if (editor.EditingSpace.Parts != null)
    {
        var oldStates = editor.EditingSpace.Parts.States;
        editor.EditingSpace.Parts.ReinitializeDerivedValues(oldStates);
    }
    foreach (var tree in editor.UnattachedPartTrees)
    {
        var oldStates = tree.States;
        tree.ReinitializeDerivedValues(oldStates);
    }
}
```

### Task 3: (Optional) Improve UX — Status Feedback

Add a brief visual indicator that the refresh occurred (tooltip, flash, etc.):

```csharp
static string _refreshStatus = "";
static float _refreshTimer = 0f;

// In button handler:
_refreshStatus = "Vehicle refreshed!";
_refreshTimer = 2.0f;

// After button:
if (_refreshTimer > 0f)
{
    _refreshTimer -= ImGui.GetIO().DeltaTime;
    ImGui.TextColored(new float4(0.3f, 0.9f, 0.3f, 1f), _refreshStatus);
}
```

---

## Verification Steps

1. Open the vehicle editor with a vehicle that has at least one Part
2. Open the Part menu (click on a part)
3. Expand "Add SubParts" and add a SubPart (e.g., a solar panel, antenna)
4. Observe: SubPart is **invisible** (bug confirmed)
5. Click "Refresh Vehicle" button
6. Observe: SubPart now **renders correctly**
7. Verify: SubPart's transform UI (position/rotation/scale) still works
8. Verify: Launching the vehicle works correctly
9. Verify: Saving and loading the vehicle preserves the SubPart
10. Verify: Module states (fuel, battery) are not reset by the refresh

## Risk Assessment

- **Low risk**: `ReinitializeDerivedValues()` is the engine's own function used during normal tree initialization
- **State preservation**: Passing `oldStates` ensures no data loss (fuel, battery charge, engine state)
- **Performance**: Acceptable for a user-initiated action; would be problematic if called every frame
- **Scope**: Isolated to button click — no changes to part spawning, attachment, or other editor flows
