using StarMap.API;
using HarmonyLib;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using System.Reflection;
using Brutal.GlfwApi;

namespace BuilderPlus;

[StarMapMod]
public class BuilderPlusMod
{
    // Harmony instance used to apply patches; the ID must be unique for this mod
    private static Harmony _harmony = new Harmony("com.builderplus.mod");
    // Stores the mod directory path for loading external resources (fonts, JSON, etc.)
    public static string ModPath = "";

    private static bool _hasRan = false;

    [StarMapImmediateLoad]
    public void OnLoad(KSA.Mod mod)
    {
        // Save the mod directory path
        ModPath = mod.DirectoryPath;
        // Apply all Harmony patches in the assembly
        _harmony.PatchAll();
    }

    [StarMapUnload]
    public void OnUnload()
    {
        // Remove all patches applied by this mod using its Harmony ID
        _harmony.UnpatchAll("com.builderplus.mod");
    }
}

// Patch applied to VehicleEditor.Initialize
[HarmonyPatch(typeof(VehicleEditor), "Initialize")]
public class HideOriginalPartWindow
{
    // Runs after the original Initialize method
    static void Postfix(VehicleEditor __instance)
    {
        try
        {
            // Get the private static field "_partWindow" from VehicleEditor
            FieldInfo? partWindowField = typeof(VehicleEditor)
                .GetField("_partWindow", BindingFlags.NonPublic | BindingFlags.Static);
            // Exit if the field was not found
            if (partWindowField == null) return;
            // Get the actual part window instance (static field → null instance)
            object? partWindow = partWindowField.GetValue(null);
            // Exit if the window instance is null
            if (partWindow == null) return;
            // Get the private "_show" field from ImGuiWindow (controls visibility)
            FieldInfo? showField = typeof(ImGuiWindow)
                .GetField("_show", BindingFlags.NonPublic | BindingFlags.Instance);
            // Set the window visibility to false (hide it)
            showField?.SetValue(partWindow, false);
        }
        catch (Exception e)
        {
            // Log any error to avoid crashing the mod
            Console.WriteLine($"[BuilderPlus] Error: {e.Message}");
        }
    }
}

// Patch applied to StageList.DrawEditorStageWindow
[HarmonyPatch(typeof(StageList), nameof(StageList.DrawEditorStageWindow))]
public class HideOriginalStageWindow
{
    static bool Prefix() => false;
}

// Patch applied to Alert.DrawAll
[HarmonyPatch(typeof(Alert), nameof(Alert.DrawAll))]
class HideAlertsInEditor
{
    // Prefix runs before drawing alerts; returning false prevents all alerts from rendering
    static bool Prefix()
    {
        if (Program.EditorFlag)  // Only blocks popups if the Program detects that we are in the editor
            return false;
        return true;  // Allow alerts to draw normally outside of the editor
    }
}

// Patch applied to VehicleEditor.Dispose (called when leaving the editor / launching)
// Unrotates the craft, that was rotated in the editor for better building, 
[HarmonyPatch(typeof(VehicleEditor), nameof(VehicleEditor.Dispose))]
public class UnrotateOnLaunch
{
    // Credit @ tomservo291
    // Force an update to the vehicle part tree physics after rotating the root part on launch.  
    private static void UpdateVehiclePhysics(Vehicle vehicle)
    {
        if (vehicle == null) return;

        try
        {
            // Invoke private method to recompute static mass using reflection (Traverse)
            Traverse.Create(vehicle.Parts).Method("RecomputeStaticMass").GetValue();

            // Recalculate bounding boxes, mass, aero, and flight computer config
            vehicle.UpdateAfterPartTreeModification();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BuilderPlus] Error: Vehicle physics update error: {ex.Message}");
        }
    }

    // Runs before the editor is disposed (right before launch)
    static void Prefix(ref VehicleEditor __instance)
    {
        // Only run if we are launching a new vehicle and parts exist
        if (__instance.LaunchNewVehicle && __instance.EditingSpace.Parts != null)
        {
            // Get the root part of the vehicle
            var root = __instance.EditingSpace.Parts.Root;
            if (root == null) return;
            
            // Create inverse rotation to undo the editor rotation (-90° on Y axis)
            var inverseRotation = doubleQuat.CreateFromAxisAngle(new double3(0, 1, 0), -Math.PI / 2.0);
            
            // Apply rotation to root part
            root.Asmb2ParentAsmb = doubleQuat.Multiply(inverseRotation, root.Asmb2ParentAsmb);

            // Recompute bounding box to prevent physics glitches (e.g., bouncing)
            root.BoundingBoxVehicleAsmb = root.ComputeBoundingBoxVehicleAsmb();

            // Iterate through all child parts to also apply rotation
            var children = new PartTreeChildrenIterator(root);
            while (true)
            {
                Part? next = children.GetNextNode();
                if (next == null) break;
                next.Asmb2ParentAsmb = doubleQuat.Multiply(inverseRotation, next.Asmb2ParentAsmb);
                double3 relative = next.PositionParentAsmb - root.PositionParentAsmb;
                next.PositionParentAsmb = root.PositionParentAsmb + relative.Transform(inverseRotation);
                next.PositionParentAsmbSafe = next.PositionParentAsmb;
                next.BoundingBoxVehicleAsmb = next.ComputeBoundingBoxVehicleAsmb();
            }

            // Update safe rotation for root
            root.Asmb2ParentAsmbSafe = root.Asmb2ParentAsmb;
            
            // If launching an existing vehicle, update physics to match new transforms
            if (__instance.ExistingVehicle != null)
                UpdateVehiclePhysics(__instance.ExistingVehicle);
            
            // Recompute all derived data in the part tree (safety update)
            __instance.EditingSpace.Parts.RecomputeAllDerivedData();
        }
    }
}

// Patch applied to SequenceList.DrawEditorSequenceWindow
[HarmonyPatch(typeof(SequenceList), nameof(SequenceList.DrawEditorSequenceWindow))]
public class HideOriginalSequenceWindow
{
    // Prefix runs before the original method; returning false prevents the default sequence UI from rendering
    static bool Prefix() => false;
}

// Patch applied to VehicleEditor.UpdateSelected for uniform scale when using scale gizmo and shift
[HarmonyPatch(typeof(VehicleEditor), nameof(VehicleEditor.UpdateSelected))]
public class UniformScalePatch
{
    // Runs after the original selection update logic
    static void Postfix(VehicleEditor __instance)
    {
        // Only apply when Shift is pressed
        if (!Program.ShiftDown) return;
        // Ensure a part is selected
        if (__instance.Selected == null) return;
        // Only apply when using the scale gizmo
        if (__instance.HighlightedGizmo != __instance.ScaleGizmo) return;
        // Ensure the gizmo is actively being dragged
        if (!__instance.GizmoGrabbed) return;

        // Get current scale of the selected part
        double3 scale = __instance.Selected.Scale;
        // Get which axis is being manipulated (X, Y, or Z)
        int idx = __instance.HighlightedGizmoSegmentIndex;
        
        // Extract the value of the active axis
        double value = idx switch
        {
            0 => scale.X,
            1 => scale.Y,
            2 => scale.Z,
            _ => 1.0
        };
        
        // Apply uniform scaling by setting all axes to the same value
        __instance.Selected.Scale = new double3(value, value, value);
    }
}

// Patch that overrides mouse input to implement "sticky grab" behavior in the editor
[HarmonyPatch(typeof(VehicleEditor), nameof(VehicleEditor.OnMouseButton))]
public class StickyGrabPatch
{
     // Tracks whether sticky grab mode is active and handles spawn-related input skipping
    static bool _stickyGrab = false;
    static int _spawnSkips = 0;

    // Reference to the currently grabbed part (root of the grabbed subtree)
    public static Part? GrabbedPart = null;

    // Public state flags used by other systems/UI
    public static bool IsStickyGrabbing => _stickyGrab;
    public static bool IsSpawning = false;

    // Marks that a part was just spawned to ignore the first mouse release event
    public static void MarkSpawned(bool isFirst = false)
    {
        _spawnSkips = 1;
        IsSpawning = true;
        _isFirstPart = isFirst;
    }
    
    // Cancels sticky grab and resets all related state
    public static void CancelStickyGrab()
    {
        _stickyGrab = false;
        _spawnSkips = 0;
        GrabbedPart = null;
        IsSpawning = false;
    }

    // Tracks whether the current interaction involves the first placed part (for camera centering)
    static bool _isFirstPart = false;

    // Runs after mouse input; handles grab/release logic on left mouse button release
    static void Postfix(VehicleEditor __instance, GlfwMouseButton button, GlfwButtonAction action)
    {
        // Only process left-clicks and ignore input when ImGui is capturing the mouse
        if (button != GlfwMouseButton.Number1) return;
        if (ImGui.GetIO().WantCaptureMouse) return;
        
        if (action == GlfwButtonAction.Release)
        {
            // Skip initial release events after spawning a part to avoid unintended grabs
            if (_spawnSkips > 0)
            {
                _spawnSkips--;

                // Once skipping is done, clear spawning state and optionally center camera on first part
                if (_spawnSkips == 0)
                {
                    IsSpawning = false;
                                        
                    if (_isFirstPart && Program.Editor?.EditingSpace.Parts?.Root != null)
                    {
                        var root = Program.Editor.EditingSpace.Parts.Root;
                        Program.Editor.CameraOffset = root.PositionParentAsmb.Transform(Program.Editor.EditingSpace.Asmb2Ecl);
                        _isFirstPart = false;
                    }
                }
                return;
            }
            
            // If a part is under the cursor
            if (__instance.Highlighted != null)
            {
                if (!_stickyGrab)
                {
                    // Activate sticky grab: mark the selected part and all its children as grabbed
                    _stickyGrab = true;
                    GrabbedPart = __instance.Highlighted;
                    __instance.Highlighted.Grabbed = true;
                    var children = new PartTreeChildrenIterator(__instance.Highlighted);
                    while (true)
                    {
                        var next = children.GetNextNode();
                        if (next == null) break;
                        next.Grabbed = true;
                    }
                }
                else
                {
                    // Release sticky grab and trigger snapshot for undo/redo
                    _stickyGrab = false;
                    GrabbedPart = null;
                    VehicleEditorUIPatch.MarkSnapshotNeeded();
                    
                    // Optionally recenter camera if root or first part is involved
                    if (Program.Editor?.EditingSpace.Parts?.Root != null)
                    {
                        var root = Program.Editor.EditingSpace.Parts.Root;
                        bool isRoot = GrabbedPart == root || __instance.Selected == root;
                        
                        if (_isFirstPart || isRoot)
                        {
                            Program.Editor.CameraOffset = root.PositionParentAsmb.Transform(Program.Editor.EditingSpace.Asmb2Ecl);
                            _isFirstPart = false;
                        }
                    }
                }
            }
        }
    }
}

// Patch applied to Program.OnScroll to override default scroll behavior in the editor
[HarmonyPatch(typeof(Program), nameof(Program.OnScroll))]
public class ProgramScrollPatch
{
    static bool Prefix(Program __instance, GlfwWindow window, double2 offset)
    {
        // Allow default behavior if no editor is active or ImGui is using the mouse
        if (Program.Editor == null) return true;
        if (ImGui.GetIO().WantCaptureMouse) return true;
        
        bool shift = Program.ShiftDown;
        var editor = Program.Editor;
        
        if (shift)
        {
            // Zoom behavior: adjust orbit distance when Shift is held
            editor.EditingSpace.OrbitView.DistancePower -= offset.Y * 2.0;

            // Clamp minimum zoom distance to avoid invalid values
            editor.EditingSpace.OrbitView.DistancePower = Math.Max(1.0, editor.EditingSpace.OrbitView.DistancePower);

            // Skip original scroll behavior
            return false;
        }
        else
        {
            // Pan behavior: move camera along its local up direction
            var camera = Program.MainViewport.GetCamera();
            // Convert camera up vector to world (ECL) space
            double3 upEcl = double3.UnitY.Transform(camera.WorldRotation);
            // Move camera offset proportionally to zoom level and scroll input
            editor.CameraOffset += upEcl * offset.Y * editor.EditingSpace.OrbitView.DistancePower * 0.05;
            // Skip original scroll behavior
            return false;
        }
    }
}

// Patch applied to VehicleEditor.OnDrawUi to fully override and render custom UI
[HarmonyPatch(typeof(VehicleEditor), nameof(VehicleEditor.OnDrawUi))]
public class VehicleEditorUIPatch
{
    // Current selected category/filter in the parts catalog
    static EditorTag _selectedTag = EditorTag.All;

    // Search input text for filtering parts
    static string _searchText = "";

    // UI state toggles
    static bool _showStages = false;
    static bool _showSavePopup = false;
    static bool _showLoadPanel = false;

    // Font references (icon font and custom pixel font)
    static ImFontPtr _iconFont = default;
    static ImFontPtr _pixelFont = default;

    // Flags for font loading and UI initialization
    static bool _pixelFontLoaded = false;
    static bool _initialized = false;
   
    // Save system state
    static string _saveName = "";

    // Theme color definitions used across the custom UI
    static readonly float4 BG_DARK      = new float4(0.10f, 0.10f, 0.12f, 1.00f);
    static readonly float4 BG_MID       = new float4(0.15f, 0.15f, 0.18f, 1.00f);
    static readonly float4 ACCENT_BLUE  = new float4(0.20f, 0.50f, 0.90f, 1.00f);
    static readonly float4 ACCENT_HOVER = new float4(0.25f, 0.60f, 1.00f, 1.00f);
    static readonly float4 TEXT_DIM     = new float4(0.60f, 0.60f, 0.65f, 1.00f);
    static readonly float4 HEADER_COLOR = new float4(0.30f, 0.70f, 1.00f, 1.00f);
    static readonly float4 ACTIVE_COLOR = new float4(0.20f, 0.50f, 0.90f, 1.00f);

    // Undo/Redo system storing snapshots of part states
    static List<PartInstance> _undoStack = new List<PartInstance>();
    static List<PartInstance> _redoStack = new List<PartInstance>();

    // Indicates whether a snapshot should be taken (for undo system)
    static bool _snapshotPending = true;

    // Subpart editing state (selected part and its transform controls)
    static Part? _selectedSubPart = null;
    static MenuTRSHolder _subPartTRS;
    static bool _subPartWindowOpen = false;

    // Icon mapping for each category using FontAwesome glyphs
    static readonly Dictionary<string, string> CategoryIcons = new()
    {
        { "All",         "\uf005" },
        { "Capsules",    "\uf508" },
        { "Cargo",       "\uf466" },
        { "Coupling",    "\uf0c1" },
        { "Electrical",  "\uf0e7" },
        { "Engines",     "\uf135" },
        { "Fuel Tanks",  "\uf52f" },
        { "Interstage",  "\uf248" },
        { "Lights",      "\uf0eb" },
        { "Passage",     "\uf52b" },
        { "RCS",         "\uf192" },
        { "Radial",      "\uf110" },
        { "Structural",  "\uf6e3" },
        { "Tanks",       "\uf575" },
    };

    // Resets initialization state (forces UI reinitialization on next frame)
    public static void ResetFlags()
    {
        _initialized = false;
    }

    // Stores mapping between internal part IDs and user-friendly names (loaded from JSON)
    static Dictionary<string, string> _partNames = new();
    // Indicates whether part names have already been loaded to avoid reloading every frame
    static bool _partNamesLoaded = false;

    // Loads part friendly names from a JSON file (only once)
    static void LoadPartNames()
    {
        // Prevent reloading if already done
        if (_partNamesLoaded) return;
        _partNamesLoaded = true;
        
        try
        {
            // Build full path to partnames.json inside the mod directory
            var path = Path.Combine(Directory.GetCurrentDirectory(), BuilderPlusMod.ModPath, "partnames.json");
            
            if (File.Exists(path))
            {
                // Read and deserialize JSON into dictionary
                var json = File.ReadAllText(path);
                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);

                // Store loaded names if valid
                if (dict != null) _partNames = dict;
            }
            else
            {
                Console.WriteLine($"[BuilderPlus] partnames.json not found");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[BuilderPlus] Error loading part names: {e.Message}");
        }
    }

    // Returns a user-friendly name for a part, falling back to display name or ID
    static string GetFriendlyName(PartTemplate part)
    {
        if (_partNames.TryGetValue(part.Id, out var name))
            return name;
        if (part.DisplayName != part.Id)
            return part.DisplayName;
        return part.Id;
    }

    // Applies global HUD-style theme to ImGui (colors, rounding, spacing)
    static void ApplyHudTheme()
    {
        var style = ImGui.GetStyle();

        // Configure layout and rounding for windows and UI elements    
        style.WindowRounding = 10f;
        style.ChildRounding  = 8f;
        style.FrameRounding  = 6f;
        style.WindowBorderSize = 1.5f;

        style.WindowPadding = new float2(10f, 10f);
        style.ItemSpacing   = new float2(6f, 6f);

        var colors = style.Colors;

        // Background and frame styling (dark transparent HUD look)
        colors[(int)ImGuiCol.WindowBg]      = new float4(0f, 0f, 0f, 0.25f);
        colors[(int)ImGuiCol.ChildBg]       = new float4(0f, 0f, 0f, 0f);
        colors[(int)ImGuiCol.FrameBg]       = new float4(0f, 0f, 0f, 0.2f);

        // Button states (idle, hover, active)
        colors[(int)ImGuiCol.Button]        = new float4(0f, 0f, 0f, 0f);
        colors[(int)ImGuiCol.ButtonHovered] = new float4(0.2f, 0.6f, 1f, 0.2f);
        colors[(int)ImGuiCol.ButtonActive]  = new float4(0.2f, 0.6f, 1f, 0.4f);

        // Header elements (e.g., selectable, collapsing headers)
        colors[(int)ImGuiCol.Header]        = new float4(0.2f, 0.6f, 1f, 0.15f);
        colors[(int)ImGuiCol.HeaderHovered] = new float4(0.2f, 0.6f, 1f, 0.3f);
        colors[(int)ImGuiCol.HeaderActive]  = new float4(0.2f, 0.6f, 1f, 0.5f);

        // Text and borders
        colors[(int)ImGuiCol.Text]          = new float4(0.7f, 0.9f, 1f, 1f);
        colors[(int)ImGuiCol.Border]        = new float4(0.2f, 0.6f, 1f, 0.6f);

        // Scrollbar styling
        colors[(int)ImGuiCol.ScrollbarBg]   = new float4(0f, 0f, 0f, 0f);
        colors[(int)ImGuiCol.ScrollbarGrab] = new float4(0.2f, 0.6f, 1f, 0.6f);
    }

    // Main UI override for VehicleEditor: handles initialization, input, and full custom rendering
    static bool Prefix(VehicleEditor __instance, Viewport inViewport)
    {
        // One-time initialization: load resources and prepare editor state
        if (!_initialized)
        {
            __instance.Connecting = true;
            _initialized = true;
            LoadPartNames();
            LoadPixelFont();
        }

        // Handle all keyboard shortcuts and editor interactions (undo/redo, rotation, delete, toggles)
        var io = ImGui.GetIO();
        if (!io.WantCaptureKeyboard)
        {
            bool ctrl = ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl);
            bool shift = ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
            
            // Undo / Redo system
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Z) && !shift)
            {
                Undo(__instance);
                return false;
            }
            if (ctrl && (ImGui.IsKeyPressed(ImGuiKey.Y) || (ImGui.IsKeyPressed(ImGuiKey.Z) && shift)))
            {
                Redo(__instance);
                return false;
            }

           // Determine which part should be rotated (grabbed or temporarily detached) 
           var partToRotate = StickyGrabPatch.GrabbedPart;

            if (partToRotate == null && __instance.Highlighted != null)
            {
                var h = __instance.Highlighted;
                bool isDetached = h.FakeTranslucent 
                    || __instance.UnattachedPartTrees.Contains(h.Tree)
                    || (h.Grabbed && __instance.EditingSpace.Parts != h.Tree)
                    || StickyGrabPatch.IsSpawning;
                
                if (isDetached)
                    partToRotate = h;
            }

            // Rotation controls using WASD + QE (with Shift for larger increments)
            if (partToRotate != null)
            {
                double angle = Math.PI / 12.0;
                if (shift) angle = Math.PI / 4.0;
                
                double3 rotAxis = new double3(0, 0, 0);

                if (ImGui.IsKeyPressed(ImGuiKey.S))
                    rotAxis = new double3(1, 0, 0);
                else if (ImGui.IsKeyPressed(ImGuiKey.W))
                    rotAxis = new double3(-1, 0, 0);
                else if (ImGui.IsKeyPressed(ImGuiKey.A))
                    rotAxis = new double3(0, 0, 1);
                else if (ImGui.IsKeyPressed(ImGuiKey.D))
                    rotAxis = new double3(0, 0, -1);
                else if (ImGui.IsKeyPressed(ImGuiKey.Q))
                    rotAxis = new double3(0, 1, 0);
                else if (ImGui.IsKeyPressed(ImGuiKey.E))
                    rotAxis = new double3(0, -1, 0);

                // Apply rotation to root and all children in the subtree
                if (rotAxis.LengthSquared() > 0)
                {
                    var root = partToRotate;
                    var localRot = doubleQuat.CreateFromAxisAngle(rotAxis, angle);
                    
                    root.Asmb2ParentAsmbSafe = doubleQuat.Multiply(localRot, root.Asmb2ParentAsmbSafe);
                    root.Asmb2ParentAsmb = root.Asmb2ParentAsmbSafe;
                    
                    var children = new PartTreeChildrenIterator(root);
                    while (true)
                    {
                        Part? next = children.GetNextNode();
                        if (next == null) break;
                        next.Asmb2ParentAsmbSafe = doubleQuat.Multiply(localRot, next.Asmb2ParentAsmbSafe);
                        next.Asmb2ParentAsmb = next.Asmb2ParentAsmbSafe;
                        double3 relative = next.PositionParentAsmb - root.PositionParentAsmb;
                        next.PositionParentAsmb = root.PositionParentAsmb + relative.Transform(localRot);
                        next.PositionParentAsmbSafe = next.PositionParentAsmb;
                    }
                }
            }

            // Delete logic: handles both root trees and detached subtrees safely
            if (ImGui.IsKeyPressed(ImGuiKey.Delete) && !io.WantCaptureKeyboard)
            {
                if (__instance.Selected != null)
                {
                    TakeSnapshot(__instance);
                    var delPart = __instance.Selected;
                    var delTree = delPart.Tree;
                    
                    // Delete entire tree if detached or root
                    if (__instance.UnattachedPartTrees.Contains(delTree) || delPart == delTree.Root)
                    {
                        var delParts = delTree.Parts;
                        for (int d = delParts.Length - 1; d >= 0; d--)
                        {
                            delParts[d].Grabbed = false;
                            delParts[d].Selected = false;
                            delParts[d].Highlighted = false;
                            delParts[d].FakeTranslucent = false;
                            try { delParts[d].Disconnect(); } catch { }
                        }
                        if (__instance.EditingSpace.Parts == delTree)
                            __instance.EditingSpace.Parts = null;
                        __instance.UnattachedPartTrees.Remove(delTree);
                    }
                    else
                    {
                        // Detach connected part and treat it as orphan
                        delPart.Disconnect();
                        __instance.UnattachedPartTrees.Add(delPart.Tree);
                        var orphanParts = delPart.Tree.Parts;
                        for (int d = 0; d < orphanParts.Length; d++)
                        {
                            orphanParts[d].Grabbed = false;
                            orphanParts[d].Selected = false;
                            orphanParts[d].Highlighted = false;
                            orphanParts[d].FakeTranslucent = false;
                            try { orphanParts[d].Disconnect(); } catch { }
                        }
                        __instance.UnattachedPartTrees.Remove(delPart.Tree);
                    }
                    
                    // Reset selection and UI state
                    __instance.Selected = null;
                    __instance.Highlighted = null;
                    __instance.PartMenus.Clear();
                    StickyGrabPatch.CancelStickyGrab();
                    MarkSnapshotNeeded();
                    return false;
                }
            }

            // Editor toggles (symmetry, snapping, connecting)
            if (ImGui.IsKeyPressed(ImGuiKey.R))
                __instance.RadialSymmetry = !__instance.RadialSymmetry;

            if (ImGui.IsKeyPressed(ImGuiKey.X) && !shift)
                __instance.SymmetryIndex = (__instance.SymmetryIndex + 1) % __instance.Symmetries.Length;

            if (ImGui.IsKeyPressed(ImGuiKey.X) && shift)
                __instance.SymmetryIndex = __instance.SymmetryIndex == 0
                    ? __instance.Symmetries.Length - 1
                    : __instance.SymmetryIndex - 1;

            if (ImGui.IsKeyPressed(ImGuiKey.C))
                __instance.Snapping = !__instance.Snapping;

            if (ImGui.IsKeyPressed(ImGuiKey.V))
                __instance.Connecting = !__instance.Connecting;

        }

        // Snapshot system: capture state when nothing is being grabbed
        if (_snapshotPending && __instance.EditingSpace.Parts != null)
        {
            bool anyGrabbed = false;
            var allParts = __instance.EditingSpace.AllParts;
            for (int i = 0; i < allParts.Length; i++)
            {
                if (allParts[i].Grabbed) { anyGrabbed = true; break; }
            }
            if (!anyGrabbed)
                TakeSnapshot(__instance);
        }
            
        bool usedFont = false;

        try
        {
            double4x4 matrixVehicleAsmb2Ego = __instance.EditingSpace.GetMatrixAsmb2Ego(inViewport.GetCamera());

            // Apply UI theme and font before rendering custom UI
            ApplyHudTheme(); 
            PushPixelFont(out usedFont);

            // Main custom UI panels
            DrawPartsPanel(__instance, inViewport);
            DrawToolbar(__instance, inViewport);

            // Ensure only one part menu is active
            while (__instance.PartMenus.Count > 1)
                __instance.PartMenus.RemoveAt(0);

            if (__instance.PartMenus.Count > 0)
                DrawCustomPartMenu(__instance, 0, inViewport);

            // Draw subpart menus managed by the game
            for (int i = __instance.SubPartMenus.Count - 1; i >= 0; i--)
                __instance.DrawSubPartUi(i, inViewport);

            // Optional panels and overlays
            if (_showStages)
                DrawStagePanel(__instance, inViewport);
            __instance.EditingSpace.DrawResourceWindow(inViewport);

            DrawSequencePanel(__instance, inViewport);

            if (__instance.ExistingVehicle == null)
                DrawLaunchPanel(__instance, inViewport);

            if (_showSavePopup)
                DrawSavePopup(__instance);
            if (_showLoadPanel)
                DrawLoadPanel(__instance, inViewport);

            // Floating subpart transform window anchored to the part in screen space
            if (_subPartWindowOpen && _selectedSubPart != null)
            {
                double4x4 matrixAsmb2Ego = __instance.EditingSpace.GetMatrixAsmb2Ego(inViewport.GetCamera());
                double3 posEgo = _selectedSubPart.PositionEgo(in matrixAsmb2Ego);

                var screenPos = inViewport.Position + inViewport.GetCamera().EgoToScreen(posEgo);

                ImGui.SetNextWindowPos(screenPos, ImGuiCond.Appearing);
                ImGui.SetNextWindowSize(new float2(280f, 0f), ImGuiCond.Appearing);

                bool open = _subPartWindowOpen;

                ImGui.Begin($"SubPart##subpart_{_selectedSubPart.InstanceId}", ref open);

                DrawSubPartTRS();

                ImGui.End();

                // Handle closing of subpart window and cleanup
                if (!open)
                {
                    if (_selectedSubPart != null)
                        _selectedSubPart.Highlighted = false;

                    _selectedSubPart = null;
                    _subPartWindowOpen = false;
                }
            }

            // Ensure selected subpart stays highlighted
            if (_selectedSubPart != null)
            {
                _selectedSubPart.Highlighted = true;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[BuilderPlus] Error en Prefix: {e.Message}");
        }
        finally
        {
            PopFontSafe(usedFont);
        }

        // Skip original UI rendering completely
        return false;
    }

    // Applies a temporary dark theme using ImGui style stacks (Push-based)
    static void PushDarkTheme()
    {
        // Push color overrides for backgrounds, buttons, headers, borders, etc.
        ImGui.PushStyleColor(ImGuiCol.WindowBg,      BG_DARK);
        ImGui.PushStyleColor(ImGuiCol.ChildBg,       BG_MID);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,       BG_MID);
        ImGui.PushStyleColor(ImGuiCol.Button,        BG_MID);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  ACCENT_BLUE);
        ImGui.PushStyleColor(ImGuiCol.Header,        ACCENT_BLUE);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ACCENT_HOVER);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,  ACCENT_BLUE);
        ImGui.PushStyleColor(ImGuiCol.TitleBg,       BG_DARK);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, BG_DARK);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,   BG_DARK);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, BG_MID);
        ImGui.PushStyleColor(ImGuiCol.Border,        new float4(0.25f, 0.25f, 0.30f, 1f));

        // Push rounding settings for windows and UI elements
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
    }

    // Restores previous ImGui style state (must match Push counts exactly)
    static void PopDarkTheme()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(14);
    }
    
    // Loads custom PixelCode font from mod directory and registers it in ImGui
    static void LoadPixelFont()
    {
        // Prevent loading multiple times
        if (_pixelFontLoaded) return;
        _pixelFontLoaded = true;

        try
        {
            // Build full path to the font file inside the mod folder
            var path = Path.Combine(
                Directory.GetCurrentDirectory(),
                BuilderPlusMod.ModPath,
                "PixelCode.font"
            );

            // Check if font file exists
            if (!File.Exists(path))
            {
                Console.WriteLine("[BuilderPlus] PixelCode not found");
                return;
            }
            
            // Access ImGui font atlas
            var io = ImGui.GetIO();
            var atlas = io.Fonts;

            // Convert path to ImString for ImGui
            ImString fontPath = new ImString(path);

            // Load font using current game UI scale
            _pixelFont = atlas.AddFontFromFileTTF(
                fontPath,
                GameSettings.GetFontSize()
            );

            // Register font if successfully loaded
            if (!_pixelFont.IsNull())
            {
                FontManager.Fonts["PixelCode"] = _pixelFont;
                Console.WriteLine("[BuilderPlus] PixelCode loaded OK");
            }
            else
            {
                Console.WriteLine("[BuilderPlus] PixelCode FAILED");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[BuilderPlus] PixelCode error: {e.Message}");
        }
    }

    // Pushes the custom PixelCode font if available and tracks whether it was applied
    static void PushPixelFont(out bool used)
    {
        // Check if the font was loaded and registered
        if (FontManager.Fonts.TryGetValue("PixelCode", out ImFontPtr font))
        {
            // Apply the font with the current UI scale
            ImGui.PushFont(font, GameSettings.GetFontSize());
            used = true;
            return;
        }

        // Fallback: no font applied
        used = false;
    }

    // Safely pops the font only if it was previously pushed
    static void PopFontSafe(bool used)
    {
        if (used)
            ImGui.PopFont();
    }

    // Draws the main vertical toolbar with transform tools, toggles, and utility actions
    static void DrawToolbar(VehicleEditor editor, Viewport viewport)
    {
        // Configure window flags for a fixed, floating toolbar
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoScrollbar;

        // Set fixed position and size relative to the viewport
        ImGui.SetNextWindowPos(viewport.Position + new float2(364f, 40f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new float2(48f, 620f), ImGuiCond.Always);

        // Apply dark theme and compact spacing for toolbar layout
        PushDarkTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(4f, 4f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new float2(2f, 4f));

        ImGui.Begin("##Toolbar", flags);
            
        // Check if icon font is available for rendering icons
        bool hasIconFont = !_iconFont.IsNull();

        // Transform tools (translate, rotate, scale)
        DrawToolButton("\uf0b2", "Translate", editor.TranslateGizmoEnabled,
            () => editor.TranslateGizmoEnabled = !editor.TranslateGizmoEnabled);

        DrawToolButton("\uf2f1", "Rotate", editor.RotationGizmoEnabled,
            () => editor.RotationGizmoEnabled = !editor.RotationGizmoEnabled);

        DrawToolButton("\uf424", "Scale", editor.ScaleGizmoEnabled,
            () => editor.ScaleGizmoEnabled = !editor.ScaleGizmoEnabled);

        // Separator between tool groups
        ImGui.Dummy(new float2(0f, 4f));
        ImGui.Separator();
        ImGui.Dummy(new float2(0f, 4f));

        // Editor toggles (snap, connect, symmetry, stages panel)
        DrawToolButton("\uf1b3", "Snap", editor.Snapping,
            () => editor.Snapping = !editor.Snapping);

        DrawToolButton("\uf542", "Connect", editor.Connecting,
            () => editor.Connecting = !editor.Connecting);

        DrawToolButton("\uf110", "Radial Sym", editor.RadialSymmetry,
            () => editor.RadialSymmetry = !editor.RadialSymmetry);

        DrawToolButton("\uf0db", "Stages", _showStages,
            () => _showStages = !_showStages);

        // Separator before symmetry controls
        ImGui.Dummy(new float2(0f, 4f));
        ImGui.Separator();
        ImGui.Dummy(new float2(0f, 4f));

        // Symmetry amount button (left click = next, right click = previous)
        ImGui.PushStyleColor(ImGuiCol.Button, BG_MID);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);
        if (ImGui.Button($"{editor.SymmetryAmount}x##sym", new float2(40f, 36f)))
            editor.SymmetryIndex = (editor.SymmetryIndex + 1) % editor.Symmetries.Length;
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Symmetry Amount");

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            editor.SymmetryIndex = editor.SymmetryIndex == 0
                ? editor.Symmetries.Length - 1
                : editor.SymmetryIndex - 1;

        // Separator before utility actions
        ImGui.Dummy(new float2(0f, 4f));
        ImGui.Separator();
        ImGui.Dummy(new float2(0f, 4f));

        // Undo / Redo actions with icon support
        ImGui.PushStyleColor(ImGuiCol.Button, BG_MID);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);
        if (hasIconFont) ImGui.PushFont(_iconFont, GameSettings.GetFontSize());
        if (ImGui.Button("\uf0e2##undo", new float2(40f, 36f)))
            Undo(editor);
        if (hasIconFont) ImGui.PopFont();
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Undo (Ctrl+Z)");

        ImGui.PushStyleColor(ImGuiCol.Button, BG_MID);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);
        if (hasIconFont) ImGui.PushFont(_iconFont, GameSettings.GetFontSize());
        if (ImGui.Button("\uf01e##redo", new float2(40f, 36f)))
            Redo(editor);
        if (hasIconFont) ImGui.PopFont();
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Redo (Ctrl+Y)");

        // Save / Load actions
        ImGui.PushStyleColor(ImGuiCol.Button, BG_MID);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);
        if (hasIconFont) ImGui.PushFont(_iconFont, GameSettings.GetFontSize());
        if (ImGui.Button("\uf0c7##save", new float2(40f, 36f)))
            _showSavePopup = true;
        if (hasIconFont) ImGui.PopFont();
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Save Vehicle");

        ImGui.PushStyleColor(ImGuiCol.Button, BG_MID);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);
        if (hasIconFont) ImGui.PushFont(_iconFont, GameSettings.GetFontSize());
        if (ImGui.Button("\uf07c##load", new float2(40f, 36f)))
            _showLoadPanel = !_showLoadPanel;
        if (hasIconFont) ImGui.PopFont();
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Load Vehicle");

        // Cleanup ImGui state (end window and restore styles)
        ImGui.End();          
        ImGui.PopStyleVar(2); 
        PopDarkTheme();     
    }

    // Draws a toolbar button with icon, active state styling, and tooltip
    static void DrawToolButton(string icon, string tooltip, bool active, Action onClick)
    {
        // Choose button color based on whether the tool is active
        float4 color = active ? ACTIVE_COLOR : BG_MID;

        // Apply temporary button styling (normal + hover state)
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);

        // Use icon font if available for rendering the icon glyph
        bool hasIconFont = !_iconFont.IsNull();
        if (hasIconFont) ImGui.PushFont(_iconFont, GameSettings.GetFontSize());

        // Render button with unique ID (tooltip used to avoid ID collisions)
        if (ImGui.Button(icon + "##tool_" + tooltip, new float2(40f, 36f)))
            onClick();

        // Restore font if it was changed
        if (hasIconFont) ImGui.PopFont();

        // Restore previous style colors
        ImGui.PopStyleColor(2);

        // Show tooltip when hovered
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
    }

    // Draws the main Parts Catalogue panel with search, categories, and parts list
    static void DrawPartsPanel(VehicleEditor editor, Viewport viewport)
    {
        // Configure fixed window layout (left panel)
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoTitleBar;

        float screenHeight = ImGui.GetMainViewport().Size.Y;

        // Position and size relative to viewport
        ImGui.SetNextWindowPos(viewport.Position + new float2(20f, 40f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new float2(340f, screenHeight - 60f), ImGuiCond.Always);

        // Apply theme and spacing
        PushDarkTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(10f, 10f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new float2(6f, 6f));

        ImGui.Begin("##BuilderPlusParts", flags);
            
        // Handle click on panel to cancel/delete currently grabbed or previewed parts
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) 
            && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
            && (editor.Selected != null && editor.Selected.FakeTranslucent 
                || StickyGrabPatch.GrabbedPart != null
                || StickyGrabPatch.IsSpawning))
        {
            var part = StickyGrabPatch.GrabbedPart ?? editor.Selected;
            if (part == null) return;

            var tree = part.Tree;

            if (editor.EditingSpace.Parts == tree)
                editor.EditingSpace.Parts = null;

            editor.UnattachedPartTrees.Remove(tree);
            editor.Selected = null;
            editor.Highlighted = null;
            editor.PartMenus.Clear();
            StickyGrabPatch.CancelStickyGrab();
        }

        // Header section (title)
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BG_DARK);
        ImGui.BeginChild("##header", new float2(0f, 40f));
            
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 6f);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4f);

        ImGui.PushStyleColor(ImGuiCol.Text, HEADER_COLOR);
        ImGui.Text("PART CATALOGUE"u8);
        ImGui.PopStyleColor();

        ImGui.EndChild();
        ImGui.PopStyleColor();

        // Search bar with placeholder text
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BG_DARK);
        ImGui.BeginChild("##searchbar"u8, new float2(0f, 36f));

        ImGui.PushStyleColor(ImGuiCol.FrameBg, new float4(0.18f, 0.18f, 0.22f, 1f));
        ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);

        ImInputString searchBuffer = new ImInputString(128);
        searchBuffer.SetValue(_searchText.AsSpan());

        ImGui.InputText("##search"u8, searchBuffer, ImGuiInputTextFlags.None, null, 0);
        _searchText = searchBuffer.ToString();

        // Mark typing state to prevent input conflicts
        if (ImGui.IsItemActive())
            editor.IsUserTyping = true;

        // Draw placeholder text manually when empty
        if (string.IsNullOrEmpty(_searchText) && !ImGui.IsItemActive())
        {
            float2 pos = ImGui.GetItemRectMin();
            ImGui.GetWindowDrawList().AddText(
                pos + new float2(4f, 3f),
                ImGui.ColorConvertFloat4ToU32(TEXT_DIM),
                "Search parts...");
        }

        ImGui.PopItemWidth();
        ImGui.PopStyleColor();

        ImGui.EndChild();
        ImGui.PopStyleColor();

        // Retrieve editor tags (categories) via reflection
        var tagsField = typeof(VehicleEditor)
            .GetField("_editorTags", BindingFlags.NonPublic | BindingFlags.Static);

        List<EditorTag> tags = tagsField?.GetValue(null) as List<EditorTag> ?? new List<EditorTag>();

        // Ensure icon font is loaded (FontAwesome)
        if (_iconFont.IsNull())
        {
            FontManager.Fonts.TryGetValue("fa-solid-900", out _iconFont);
                
            if (_iconFont.IsNull())
            {
                try
                {
                    var fontPath = Path.Combine(Directory.GetCurrentDirectory(), BuilderPlusMod.ModPath, "fa-solid-900.font");
                    if (!File.Exists(fontPath))
                        fontPath = "Content/BuilderPlus/fa-solid-900.font";
                        
                    if (File.Exists(fontPath))
                    {
                        unsafe
                        {
                            ImFontAtlasPtr fonts = ImGui.GetIO().Fonts;

                            ushort[] ranges = new ushort[] { 0xe000, 0xf8ff, 0 };
                            fixed (ushort* rangesPtr = ranges)
                            {
                                ImFontConfig config = new ImFontConfig();

                                config.MergeMode = false;
                                config.SizePixels = GameSettings.GetFontSize();
                                config.GlyphMaxAdvanceX = float.MaxValue;
                                config.RasterizerMultiply = 1f;
                                config.RasterizerDensity = (float)GameSettings.GetFontDensity() / 100f;
                                    
                                _iconFont = fonts.AddFontFromFileTTF(
                                    fontPath,
                                    GameSettings.GetFontSize(),
                                    &config,
                                    new ReadOnlySpan<ushort>(rangesPtr, 3));

                                FontManager.Fonts["fa-solid-900"] = _iconFont;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.Write(e);
                }
            }
        }

        bool hasIconFont = !_iconFont.IsNull();

        // Sidebar with category buttons (icons)
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BG_DARK);
        ImGui.BeginChild("##sidebar"u8, new float2(36f, 0f));

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 2f);

        // "All" category button
        bool allSelected = _selectedTag == EditorTag.All;
        ImGui.PushStyleColor(ImGuiCol.Button,
            allSelected ? ACCENT_BLUE : new float4(0f, 0f, 0f, 0f));

        if (hasIconFont) ImGui.PushFont(_iconFont, GameSettings.GetFontSize());
        if (ImGui.Button("\uf005##All", new float2(32f, 32f)))
            _selectedTag = EditorTag.All;
        if (hasIconFont) ImGui.PopFont();

        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("All");
                
        // Loop through all categories
        foreach (EditorTag tag in tags)
        {
            if (tag == EditorTag.Hidden) continue;

            bool selected = tag == _selectedTag;
            string icon = CategoryIcons.TryGetValue(tag.Tag, out var ico) ? ico : "?";

            ImGui.PushStyleColor(ImGuiCol.Button,
                selected ? ACCENT_BLUE : new float4(0f, 0f, 0f, 0f));

            if (hasIconFont) ImGui.PushFont(_iconFont, GameSettings.GetFontSize());
            if (ImGui.Button(icon + "##" + tag.Tag, new float2(32f, 32f)))
                _selectedTag = tag;
            if (hasIconFont) ImGui.PopFont();

            ImGui.PopStyleColor();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(tag.Tag);
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.SameLine();

        // Main area with filtered parts list
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BG_MID);
        ImGui.BeginChild("##partsArea"u8);

        DrawPartsList(editor, viewport, _selectedTag, _searchText);

        ImGui.EndChild();
        ImGui.PopStyleColor();

        // Cleanup
        ImGui.End();
        ImGui.PopStyleVar(2);
        PopDarkTheme();
    }

    // Draws the list of parts grouped by category with filtering, spawning, and interaction logic
    static void DrawPartsList(VehicleEditor editor, Viewport viewport, EditorTag tag, string search)
    {
        // Compute transformation matrix for spawning parts in correct space
        double4x4 matrixAsmb2Ego = editor.EditingSpace.GetMatrixAsmb2Ego(viewport.GetCamera());

        // Access all available parts via reflection from ModLibrary
        var allPartsField = typeof(ModLibrary)
            .GetField("AllParts", BindingFlags.NonPublic | BindingFlags.Static);
        if (allPartsField == null) return;

        var allParts    = allPartsField.GetValue(null);
        var getList     = allParts?.GetType().GetMethod("GetList");
        var partsList   = getList?.Invoke(allParts, null) as System.Collections.IEnumerable;
        if (partsList == null) return;

        // Filter and group parts by category based on tag and search input
        var grouped = new Dictionary<string, List<PartTemplate>>();
        foreach (var obj in partsList)
        {
            if (obj is not PartTemplate part) continue;

            // Skip hidden/subparts and filter by tag and search
            if (part.IsSubPart || part.IsHidden) continue;
            if (tag != EditorTag.All && !part.HasEditorTag(tag)) continue;
            if (!string.IsNullOrEmpty(search) &&
                !GetFriendlyName(part).Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !part.Id.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;

            string category = tag == EditorTag.All ? GetFirstTag(part) : tag.Tag;

            if (!grouped.ContainsKey(category))
                grouped[category] = new List<PartTemplate>();

            grouped[category].Add(part);
        }

        // Layout spacing for grid
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(6f, 6f));

        // Iterate through grouped categories
        foreach (var group in grouped)
        {
            // Category header
            ImGui.Dummy(new float2(0f, 4f));
            ImGui.PushStyleColor(ImGuiCol.Text, HEADER_COLOR);
            ImGui.Text("▾ " + group.Key.ToUpper());
            ImGui.PopStyleColor();
            ImGui.Separator();

            // Render parts in a 3-column grid
            if (ImGui.BeginTable("##table_" + group.Key, 3, ImGuiTableFlags.None))
            {
                foreach (PartTemplate part in group.Value)
                {
                    ImGui.TableNextColumn();

                    // Generate thumbnail and unique button ID
                    part.Thumbnail?.CreateImGuiThumbnail(Program.LinearClampedSampler);

                    ImString strId = new ImString(8, 1);
                    strId.AppendLiteral("##btn_".AsSpan());
                    strId.AppendFormatted(part.Id.AsSpan());

                    // Style thumbnail button
                    ImGui.PushStyleColor(ImGuiCol.Button,        BG_DARK);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);

                    if (part?.Thumbnail?.ImGuiImageRef != null)
                    {
                        ImGui.ImageButton(strId, part.Thumbnail.ImGuiImageRef,
                            new float2(82f), null, null, null, null);
                    }

                    ImGui.PopStyleColor(2);

                    if (part == null)
                        return;

                    // Handle part spawn on click
                    if (ImGui.IsItemActivated())
                    {
                        TakeSnapshot(editor);

                        // Clear current grabbed/previewed parts before spawning new one
                        if (editor.Selected != null && (editor.Selected.FakeTranslucent || StickyGrabPatch.IsStickyGrabbing || editor.Selected.Grabbed))
                        {
                            var delPart = editor.Selected;
                            var delTree = delPart.Tree;
                            var delParts = delTree.Parts;

                            for (int d = 0; d < delParts.Length; d++)
                            {
                                delParts[d].Grabbed = false;
                                delParts[d].Selected = false;
                                delParts[d].Highlighted = false;
                                delParts[d].FakeTranslucent = false;
                                delParts[d].Disconnect();
                            }

                            if (editor.EditingSpace.Parts == delTree)
                                editor.EditingSpace.Parts = null;

                            editor.UnattachedPartTrees.Remove(delTree);
                            editor.Selected = null;
                            editor.PartMenus.Clear();
                            StickyGrabPatch.CancelStickyGrab();
                        }

                        // Spawn new part and mark spawning state
                        bool wasEmpty = editor.EditingSpace.Parts == null;
                        StickyGrabPatch.MarkSpawned(wasEmpty);
                        editor.SpawnPart(part, in matrixAsmb2Ego, viewport);

                        // Apply initial rotation to newly spawned part tree
                        if (editor.Highlighted != null)
                        {
                            var rotation = doubleQuat.CreateFromAxisAngle(new double3(0, 1, 0), Math.PI / 2.0);
                            var root = editor.Highlighted;
                            var treeParts = root.Tree.Parts;
                                
                            for (int r = 0; r < treeParts.Length; r++)
                            {
                                var p = treeParts[r];

                                p.Asmb2ParentAsmb = doubleQuat.Multiply(rotation, p.Asmb2ParentAsmb);
                                p.Asmb2ParentAsmbSafe = p.Asmb2ParentAsmb;

                                if (p != root)
                                {
                                    double3 relative = p.PositionParentAsmb - root.PositionParentAsmb;
                                    p.PositionParentAsmb = root.PositionParentAsmb + relative.Transform(rotation);
                                    p.PositionParentAsmbSafe = p.PositionParentAsmb;
                                }
                            }
                        }
                    }

                    // Tooltip with part name (and optional mass info)
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        float totalMass = 0f;

                        foreach (var massTemplate in part.InertMasses)
                        {
                            totalMass += massTemplate.GetMassPropertiesAsmb().Props.Mass;
                        }

                        // List of fuel types
                        List<string> fuelTypes = new List<string>();

                        foreach(RocketCoreTemplate core in part.RocketCores)
                        {
                            // Find the Reactants for the fuel
                            Part tempPart = new Part("##temp", part);
                            ReadOnlySpan<Reactant> reactants = core.Create(tempPart).Combustion.Reactants;

                            // Create the string for the fuel type(s) and add to the list
                            string tempFuel = "- ";
                            for(int i = 0; i < reactants.Length; i++)
                            {
                                if (i != 0)
                                    tempFuel += " and ";
                                tempFuel += reactants[i].SubstancePhase.Name;  // Actual reactant name
                            }

                            if (!fuelTypes.Contains(tempFuel))
                                fuelTypes.Add(tempFuel);
                            tempPart.Dispose();
                        }

                        string massStr = totalMass >= 1f 
                            ? $"{totalMass:F2} t" 
                            : $"{totalMass * 1000f:F0} kg";
                            
                        ImGui.BeginTooltip();
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 4f);
                        ImGui.Text(GetFriendlyName(part));
                        if(fuelTypes.Count > 0)
                        {
                            ImGui.Text("Accepted Fuels: ");
                            foreach (string fuel in fuelTypes)
                            {
                                ImGui.Text(fuel);
                            }
                        }
                        ImGui.EndTooltip();
                    }
                }

                ImGui.EndTable();
            }

            ImGui.Dummy(new float2(0f, 6f));
        }

        ImGui.PopStyleVar();
    }

    // Draws the launch configuration panel (vehicle name, body, location, and launch button)
    static void DrawLaunchPanel(VehicleEditor editor, Viewport viewport)
    {
        // Configure floating panel (bottom-right corner)
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoTitleBar;

        float screenHeight = ImGui.GetMainViewport().Size.Y;
        float screenWidth  = ImGui.GetMainViewport().Size.X;

        // Calculate panel size dynamically based on content
        float panelWidth   = 300f;
        float padding = ImGui.GetStyle().WindowPadding.Y * 2f;
        float panelHeight = padding + 25f + 6f + (ImGui.GetFrameHeightWithSpacing() * 4f) + 10f + 40f + 10f;

        // Position panel in bottom-right corner
        ImGui.SetNextWindowPos(
            viewport.Position + new float2(screenWidth - panelWidth - 20f, screenHeight - panelHeight - 20f),
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(new float2(panelWidth, panelHeight), ImGuiCond.Always);

        // Apply theme and padding
        PushDarkTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(10f, 10f));

        ImGui.Begin("##LaunchPanel", flags);

        // Header
        ImGui.PushStyleColor(ImGuiCol.Text, HEADER_COLOR);
        ImGui.Text("LAUNCH"u8);
        ImGui.PopStyleColor();
        ImGui.Separator();
        ImGui.Dummy(new float2(0f, 4f));

        // Access private editor fields via reflection (celestial + location data)
        var celestialField  = typeof(VehicleEditor).GetField("_selectedCelestial", BindingFlags.NonPublic | BindingFlags.Instance);
        var locationField   = typeof(VehicleEditor).GetField("_selectedLocation",  BindingFlags.NonPublic | BindingFlags.Instance);
        var celestialsField = typeof(VehicleEditor).GetField("_celestialObjects",  BindingFlags.NonPublic | BindingFlags.Instance);
        var locationsField  = typeof(VehicleEditor).GetField("_locationObjects",   BindingFlags.NonPublic | BindingFlags.Instance);

        var selectedCelestial = celestialField?.GetValue(editor) is CelestialObject c ? c : default;
        var selectedLocation  = locationField?.GetValue(editor)  is LocationObject  l ? l : default;
        var celestials        = celestialsField?.GetValue(editor) as List<CelestialObject>;
        var locations         = locationsField?.GetValue(editor)  as List<LocationObject>;

        // Table layout for inputs (labels + fields)
        if (ImGui.BeginTable("##launchTable", 2, ImGuiTableFlags.None))
        {
            ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("##field", ImGuiTableColumnFlags.WidthStretch);

            // Vehicle name input
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text("Name"u8);

            ImGui.TableNextColumn();
            ImGui.PushItemWidth(-1f);

            ImInputString nameBuffer = new ImInputString(128);
            nameBuffer.SetValue(editor.EditingSpace.Id.AsSpan());

            ImGui.InputText("##vehicleName"u8, nameBuffer, ImGuiInputTextFlags.None, null, 0);

            if (ImGui.IsItemActive())
                editor.IsUserTyping = true;

            editor.EditingSpace.Id = nameBuffer.ToString();

            ImGui.PopItemWidth();

            // Celestial body selection (dropdown)
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text("Body"u8);

            ImGui.TableNextColumn();
            ImGui.PushItemWidth(-1f);

            if (celestials != null)
            {
                if (ImGui.BeginCombo("##bodyCombo"u8, selectedCelestial.GetName()))
                {
                    foreach (var cel in celestials)
                    {
                        bool isSelected = cel.Equals(selectedCelestial);

                        if (ImGui.Selectable(cel.GetName(), isSelected, ImGuiSelectableFlags.None, null))
                        {
                            // Update selected celestial and refresh available locations
                            selectedCelestial = cel;
                            celestialField?.SetValue(editor, selectedCelestial);

                            typeof(VehicleEditor)
                                .GetMethod("SetLocations", BindingFlags.NonPublic | BindingFlags.Instance)
                                ?.Invoke(editor, null);

                            var updatedLocations = locationsField?.GetValue(editor) as List<LocationObject>;

                            if (updatedLocations?.Count > 0)
                                locationField?.SetValue(editor, updatedLocations[0]);
                        }
                    }
                    ImGui.EndCombo();
                }
            }

            ImGui.PopItemWidth();

            // Launch location selection (dropdown)
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text("Location"u8);

            ImGui.TableNextColumn();
            ImGui.PushItemWidth(-1f);

            if (locations != null)
            {
                if (ImGui.BeginCombo("##locCombo"u8, selectedLocation.GetName()))
                {
                    foreach (var loc in locations)
                    {
                        bool isSelected = loc.Equals(selectedLocation);

                        if (ImGui.Selectable(loc.GetName(), isSelected, ImGuiSelectableFlags.None, null))
                        {
                            selectedLocation = loc;
                            locationField?.SetValue(editor, selectedLocation);
                        }
                    }
                    ImGui.EndCombo();
                }
            }

            ImGui.PopItemWidth();

            ImGui.EndTable();
        }

        ImGui.Dummy(new float2(0f, 6f));

        // Launch button (green styled)
        ImGui.PushStyleColor(ImGuiCol.Button,        new float4(0.1f, 0.55f, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new float4(0.1f, 0.75f, 0.3f, 1f));

        if (ImGui.Button("▶  LAUNCH VEHICLE##launch", new float2(ImGui.GetContentRegionAvail().X, 40f)))
        {
            // Trigger launch and exit editor
            editor.LaunchNewVehicle = true;
            Program.EditorFlag = false;
        }

        ImGui.PopStyleColor(2);

        // Cleanup
        ImGui.End();
        ImGui.PopStyleVar();
        PopDarkTheme();
    }

    // Draws the stage management panel with stages, parts per stage, and editing controls
    static void DrawStagePanel(VehicleEditor editor, Viewport viewport)
    {
        // Configure floating panel layout (to the right of parts + toolbar)
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoTitleBar;

        float screenHeight = ImGui.GetMainViewport().Size.Y;
        float panelWidth   = 220f;

        const float PARTS_WIDTH   = 360f;
        const float TOOLBAR_WIDTH = 52f;

        ImGui.SetNextWindowPos(
            viewport.Position + new float2(PARTS_WIDTH + TOOLBAR_WIDTH, 40f),
            ImGuiCond.Always);

        // Dynamically calculate panel height based on number of stages and parts
        int stageCount = editor.EditingSpace.Parts?.StageList.Count ?? 0;
        int totalParts = editor.EditingSpace.Parts != null 
            ? editor.EditingSpace.AllParts.Length 
            : 0;

        float contentHeight = 30f + (stageCount * 36f) + (totalParts * 32f) + 50f;
        float maxHeight = screenHeight - 100f;
        float panelHeight = Math.Min(contentHeight, maxHeight);

        ImGui.SetNextWindowSize(new float2(panelWidth, panelHeight), ImGuiCond.Always);

        // Apply semi-transparent background styling
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new float4(0f, 0f, 0f, 0.7f));
        ImGui.PushStyleColor(ImGuiCol.Border,   new float4(0f, 0f, 0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(4f, 4f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new float2(2f, 4f));

        if (!ImGui.Begin("##StagePanel", flags))
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
            return;
        }

        ImGui.PushID("BB_Stages");

        // Header
        ImGui.PushStyleColor(ImGuiCol.Text, HEADER_COLOR);
        ImGui.Text("STAGES"u8);
        ImGui.PopStyleColor();

        ImGui.PushStyleColor(ImGuiCol.Separator, ACCENT_BLUE);
        ImGui.Separator();
        ImGui.PopStyleColor();

        if (editor.EditingSpace.Parts != null)
        {
            var stages = editor.EditingSpace.Parts.StageList;

            // Access internal stage list via reflection
            var stageListField = typeof(StageList)
                .GetField("_stages", BindingFlags.NonPublic | BindingFlags.Instance);

            var stagesList = stageListField?.GetValue(stages) as System.Collections.IList;

            if (stagesList != null)
            {
                // Iterate stages in reverse order (top-down UI)
                for (int i = stagesList.Count - 1; i >= 0; i--)
                {
                    var stage = stagesList[i];
                    if (stage == null) continue;

                    // Retrieve stage number
                    var numberProp = stage.GetType().GetProperty("Number");
                    int number = numberProp?.GetValue(stage) is int n ? n : i;

                    // Collect all parts belonging to this stage
                    var allEditorParts = editor.EditingSpace.AllParts;
                    var stageParts = new List<Part>();

                    for (int k = 0; k < allEditorParts.Length; k++)
                    {
                        if (allEditorParts[k].Stage == number)
                            stageParts.Add(allEditorParts[k]);
                    }

                    // Stage header button (toggle highlight for all parts in stage)
                    bool anyHighlighted = stageParts.Any(p => p.HighlightedForStage);
                    float4 headerColor = anyHighlighted ? ACCENT_BLUE : BG_MID;

                    ImGui.PushStyleColor(ImGuiCol.Button,        headerColor);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);
                    ImGui.PushStyleColor(ImGuiCol.Text,          HEADER_COLOR);

                    if (ImGui.Button($"Stage {number:D2} ({stageParts.Count})##header_{number}",
                        new float2(210f, 28f)))
                    {
                        bool newState = !anyHighlighted;
                        foreach (Part p in stageParts)
                            p.HighlightedForStage = newState;
                    }

                    ImGui.PopStyleColor(3);
                    ImGui.Dummy(new float2(0f, 2f));

                    // Render each part inside the stage
                    foreach (Part part in stageParts)
                    {
                        string friendlyName = GetFriendlyName(part.FullPart.Template);

                        // Shortened name for compact UI
                        string shortName = friendlyName.Length > 12
                            ? friendlyName.Substring(0, 12)
                            : friendlyName;

                        float4 btnColor = part.HighlightedForStage ? ACCENT_BLUE : BG_MID;

                        ImGui.PushStyleColor(ImGuiCol.Button,        btnColor);
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);

                        // Button toggles highlight state for individual part
                        if (ImGui.Button(shortName + "##stagepart_" + number + "_" + part.InstanceId, new float2(210f, 28f)))
                            part.HighlightedForStage = !part.HighlightedForStage;

                        ImGui.PopStyleColor(2);

                        // Hover: show tooltip and highlight part in scene
                        bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
                        if (hovered)
                        {
                            ImGui.SetTooltip(GetFriendlyName(part.FullPart.Template));
                            part.Highlighted = true;
                        }
                        else
                        {
                            part.Highlighted = false;
                        }

                        // Right-click context menu to move part to another stage
                        if (ImGui.BeginPopupContextItem("##moveStage_" + part.InstanceId))
                        {
                            ImGui.Text("Move to stage:");

                            for (int s = 0; s < stagesList.Count; s++)
                            {
                                var stg = stagesList[s];
                                var numProp = stg?.GetType().GetProperty("Number");
                                int stgNum = numProp?.GetValue(stg) is int sn ? sn : s;

                                if (stgNum == number) continue;

                                if (ImGui.Selectable($"Stage {stgNum:D2}", false, ImGuiSelectableFlags.None, null))
                                {
                                    var stageProp = typeof(Part).GetProperty("Stage");
                                    stageProp?.SetValue(part, stgNum);

                                    editor.EditingSpace.Parts.RecomputeAllDerivedData();
                                }
                            }

                            ImGui.EndPopup();
                        }
                    }

                    // Separator between stages
                    ImGui.PushStyleColor(ImGuiCol.Separator, ACCENT_BLUE);
                    ImGui.Separator();
                    ImGui.PopStyleColor();
                }
            }

            // Add new stage button
            ImGui.Dummy(new float2(0f, 4f));

            ImGui.PushStyleColor(ImGuiCol.Button,        ACCENT_BLUE);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);

            if (ImGui.Button("+ Add Stage##addstage", new float2(210f, 28f)))
            {
                Stage newStage = new Stage(editor.EditingSpace.Parts, stages.Count);
                stages.Add(newStage);

                editor.EditingSpace.Parts.RecomputeAllDerivedData();
            }

            ImGui.PopStyleColor(2);
        }

        // Cleanup
        ImGui.PopID(); 
        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }
    
    // Draws the sequence panel for managing activation order of parts (right-side vertical UI)
    static void DrawSequencePanel(VehicleEditor editor, Viewport viewport)
    {
        // Configure floating panel (right side, above launch panel)
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoTitleBar;

        // Check if icon font is available for rendering icons
        bool hasIconFont = !_iconFont.IsNull();

        float screenHeight = ImGui.GetMainViewport().Size.Y;
        float screenWidth  = ImGui.GetMainViewport().Size.X;

        float panelWidth   = 70f;
        float launchHeight = 280f;

        // Position panel aligned to right side
        ImGui.SetNextWindowPos(
            viewport.Position + new float2(screenWidth - panelWidth - 20f, 40f),
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            new float2(panelWidth, screenHeight - launchHeight - 60f),
            ImGuiCond.Always);

        // Apply semi-transparent styling
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new float4(0f, 0f, 0f, 0.7f));
        ImGui.PushStyleColor(ImGuiCol.Border,   new float4(0f, 0f, 0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(4f, 4f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new float2(2f, 4f));

        if (!ImGui.Begin("##SequencePanel", flags))
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
            return;
        }

        ImGui.PushID("BB_Sequences");

        if (editor.EditingSpace.Parts != null)
        {
            var sequences = editor.EditingSpace.Parts.SequenceList;

            // Access internal sequence list via reflection
            var seqListField = typeof(SequenceList)
                .GetField("_sequences", BindingFlags.NonPublic | BindingFlags.Instance);

            var seqList = seqListField?.GetValue(sequences) as List<Sequence>;

            if (seqList != null)
            {
                // Iterate sequences in reverse (top-down)
                for (int i = seqList.Count - 1; i >= 0; i--)
                {
                    Sequence seq = seqList[i];
                    int number   = seq.Number;

                    // Collect all parts assigned to this sequence
                    var allParts = editor.EditingSpace.AllParts;
                    var seqParts = new List<Part>();

                    for (int k = 0; k < allParts.Length; k++)
                    {
                        if (allParts[k].Sequence == number && allParts[k].Sequenceable)
                            seqParts.Add(allParts[k]);
                    }

                    // Header button (toggle highlight for entire sequence)
                    bool anyHighlighted = seqParts.Any(p => p.HighlightedForSequence);
                    float4 headerColor  = anyHighlighted ? ACCENT_BLUE : BG_MID;

                    ImGui.PushStyleColor(ImGuiCol.Button,        headerColor);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);
                    ImGui.PushStyleColor(ImGuiCol.Text,          HEADER_COLOR);

                    if (ImGui.Button($"{number:D2}##seqheader_{number}", new float2(58f, 24f)))
                    {
                        bool newState = !anyHighlighted;
                        foreach (Part p in seqParts)
                            p.HighlightedForSequence = newState;
                    }

                    ImGui.PopStyleColor(3);

                    // Right-click context menu to swap sequences
                    if (ImGui.BeginPopupContextItem("##seqctx_" + number))
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, HEADER_COLOR);
                        ImGui.Text($"Move Seq {number:D2} to:");
                        ImGui.PopStyleColor();

                        ImGui.Separator();
                            
                        for (int s = 0; s < seqList.Count; s++)
                        {
                            int otherNum = seqList[s].Number;
                            if (otherNum == number) continue;

                            if (ImGui.Selectable($"Sequence {otherNum:D2}", false, ImGuiSelectableFlags.None, null))
                            {
                                // Swap sequence assignments between two sequence numbers
                                var allParts2 = editor.EditingSpace.AllParts;

                                for (int k = 0; k < allParts2.Length; k++)
                                {
                                    if (allParts2[k].Sequence == number)
                                        allParts2[k].SetSequence(otherNum);
                                    else if (allParts2[k].Sequence == otherNum)
                                        allParts2[k].SetSequence(number);
                                }

                                editor.EditingSpace.Parts.RecomputeAllDerivedData();
                            }
                        }

                        ImGui.EndPopup();
                    }

                    ImGui.Dummy(new float2(0f, 2f));
                        
                    // Render parts inside sequence (icon buttons)
                    foreach (Part part in seqParts)
                    {
                        string partTag = GetFirstTag(part.FullPart.Template);
                        string icon = CategoryIcons.TryGetValue(partTag, out var ico) ? ico : "?";

                        float4 btnColor = part.HighlightedForSequence ? ACCENT_BLUE : BG_MID;

                        ImGui.PushStyleColor(ImGuiCol.Button,        btnColor);
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);

                        if (hasIconFont) ImGui.PushFont(_iconFont, GameSettings.GetFontSize());

                        if (ImGui.Button(icon + "##seqpart_" + number + "_" + part.InstanceId,
                            new float2(58f, 36f)))
                            part.HighlightedForSequence = !part.HighlightedForSequence;

                        if (hasIconFont) ImGui.PopFont();

                        ImGui.PopStyleColor(2);

                        // Hover: tooltip + highlight in scene
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        {
                            ImGui.SetTooltip(GetFriendlyName(part.FullPart.Template));
                            part.Highlighted = true;
                        }
                        else
                        {
                            part.Highlighted = false;
                        }
                    }

                    // Separator between sequences
                    ImGui.PushStyleColor(ImGuiCol.Separator, ACCENT_BLUE);
                    ImGui.Separator();
                    ImGui.PopStyleColor();
                }
            }

            // Add new sequence button (icon)
            ImGui.Dummy(new float2(0f, 4f));

            ImGui.PushStyleColor(ImGuiCol.Button,        ACCENT_BLUE);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);
                
            if (hasIconFont) ImGui.PushFont(_iconFont, GameSettings.GetFontSize());

            if (ImGui.Button("\uf055##addseq", new float2(58f, 36f)))
            {
                Sequence newSeq = new Sequence(editor.EditingSpace.Parts, sequences.Count + 1);
                sequences.Add(newSeq);

                editor.EditingSpace.Parts.RecomputeAllDerivedData();
            }

            if (hasIconFont) ImGui.PopFont();

            ImGui.PopStyleColor(2);
        }

        // Cleanup
        ImGui.PopID();
        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

   // Draws the save popup window for saving the current vehicle configuration
    static void DrawSavePopup(VehicleEditor editor)
    {
        // Set popup size when it appears
        ImGui.SetNextWindowSize(new float2(300f, 120f), ImGuiCond.Appearing);

        // Apply theme and padding
        PushDarkTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(10f, 10f));
            
        // Begin popup window (can be closed by user)
        bool open = true;
        ImGui.Begin("Save Vehicle##SavePopup", ref open);

        // Sync popup visibility state
        if (!open) _showSavePopup = false;

        // Input field for vehicle name
        ImGui.Text("Vehicle Name:");
        ImGui.PushItemWidth(-1f);

        ImInputString saveBuffer = new ImInputString(128);
        saveBuffer.SetValue(_saveName.AsSpan());

        ImGui.InputText("##saveName"u8, saveBuffer, ImGuiInputTextFlags.None, null, 0);

        // Store updated name and mark typing state
        _saveName = saveBuffer.ToString();
        if (ImGui.IsItemActive())
            editor.IsUserTyping = true;

        ImGui.PopItemWidth();

        ImGui.Dummy(new float2(0f, 4f));

        // Save button (green styled)
        ImGui.PushStyleColor(ImGuiCol.Button, new float4(0.1f, 0.55f, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new float4(0.1f, 0.75f, 0.3f, 1f));

        if (ImGui.Button("Save##doSave", new float2(ImGui.GetContentRegionAvail().X, 30f)))
        {
            // Validate name and ensure vehicle exists before saving
            if (!string.IsNullOrEmpty(_saveName) && editor.EditingSpace.Parts != null)
            {
                UncompressedVehicleSave.FromRootPart(editor.EditingSpace.Parts, _saveName);

                // Reset state after saving
                _showSavePopup = false;
                _saveName = "";
            }
        }

        ImGui.PopStyleColor(2);

        // Cleanup
        ImGui.End();
        ImGui.PopStyleVar();
        PopDarkTheme();
    }    

    // Draws the load panel window for browsing, loading, and deleting saved vehicles
    static void DrawLoadPanel(VehicleEditor editor, Viewport viewport)
    {
        // Configure centered floating panel
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoTitleBar;

        float screenHeight = ImGui.GetMainViewport().Size.Y;
        float screenWidth  = ImGui.GetMainViewport().Size.X;

        float panelWidth  = 350f;
        float panelHeight = screenHeight - 100f;

        // Center panel horizontally
        ImGui.SetNextWindowPos(
            viewport.Position + new float2((screenWidth - panelWidth) / 2f, 50f),
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(new float2(panelWidth, panelHeight), ImGuiCond.Always);

        // Apply theme and padding
        PushDarkTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(10f, 10f));

        ImGui.Begin("##LoadPanel", flags);

        // Header
        ImGui.PushStyleColor(ImGuiCol.Text, HEADER_COLOR);
        ImGui.Text("LOAD VEHICLE"u8);
        ImGui.PopStyleColor();

        ImGui.Separator();
        ImGui.Dummy(new float2(0f, 4f));

        // Top actions (refresh + close)
        ImGui.PushStyleColor(ImGuiCol.Button, ACCENT_BLUE);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);

        if (ImGui.Button("Refresh##refreshSaves", new float2(80f, 28f)))
            VehicleSaves.Refresh();

        ImGui.PopStyleColor(2);

        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Button, new float4(0.6f, 0.15f, 0.15f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new float4(0.8f, 0.2f, 0.2f, 1f));

        if (ImGui.Button("Close##closeLoad", new float2(80f, 28f)))
            _showLoadPanel = false;

        ImGui.PopStyleColor(2);

        ImGui.Dummy(new float2(0f, 6f));
        ImGui.Separator();
        ImGui.Dummy(new float2(0f, 4f));

        // List all saved vehicles
        var saves = VehicleSaves.AsSpan();

        for (int i = 0; i < saves.Length; i++)
        {
            var save = saves[i];

            float availWidth  = ImGui.GetContentRegionAvail().X;
            float deleteWidth = 36f;

            // Load button (main row)
            ImGui.PushStyleColor(ImGuiCol.Button, BG_MID);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);

            if (ImGui.Button(save.Id + "##loadsave_" + i, new float2(availWidth - deleteWidth - 4f, 32f)))
            {
                var partTree = save.Load(viewport);

                if (partTree != null)
                {
                    editor.LoadVehicle(partTree);
                    _showLoadPanel = false;
                }
            }

            ImGui.PopStyleColor(2);

            // Tooltip with metadata
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip($"Last modified: {save.LastUpdate}");

            ImGui.SameLine();

            // Delete button (icon)
            bool hasIconFont2 = !_iconFont.IsNull();

            ImGui.PushStyleColor(ImGuiCol.Button, new float4(0.6f, 0.15f, 0.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new float4(0.8f, 0.2f, 0.2f, 1f));

            if (hasIconFont2) ImGui.PushFont(_iconFont, GameSettings.GetFontSize());

            if (ImGui.Button("\uf1f8##deletesave_" + i, new float2(deleteWidth, 32f)))
            {
                save.Delete();
                VehicleSaves.Refresh();
            }

            if (hasIconFont2) ImGui.PopFont();

            ImGui.PopStyleColor(2);
        }

        // Cleanup
        ImGui.End();
        ImGui.PopStyleVar();
        PopDarkTheme();
    }

   // Draws the custom floating part menu attached to a selected part in the editor
    static void DrawCustomPartMenu(VehicleEditor editor, int index, Viewport viewport)
    {
        // Access the part menu entry and validate part
        ref PartMenu menu = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(editor.PartMenus)[index];            
        var part = menu.Part;
        if (part == null) return;

        string friendlyName = GetFriendlyName(part.FullPart.Template);

        // Position UI near the part in 3D space (screen projection)
        double4x4 matrixAsmb2Ego = editor.EditingSpace.GetMatrixAsmb2Ego(viewport.GetCamera());
        double3 posEgo = part.PositionEgo(in matrixAsmb2Ego);

        ImGui.SetNextWindowPos(viewport.Position + viewport.GetCamera().EgoToScreen(posEgo), ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new float2(280f, 0f), ImGuiCond.Appearing);

        // Apply theme and layout spacing
        PushDarkTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(10f, 10f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(4f, 6f));

        bool open = true;

        ImGui.Begin($"{friendlyName}##partmenu_{part.InstanceId}", ref open, ImGuiWindowFlags.NoCollapse);

        // Handle window close: cleanup highlights and references
        if (!open)
        {
            editor.PartMenus.Remove(menu);

            if (part.TreeParent != null)
                part.TreeParent.Highlighted = false;

            var link2 = part.SymmetryLink;
            if (link2.HasValue)
            {
                foreach (var sym in link2.Value.Symmetries)
                    foreach (var sp in sym.Parts)
                        sp.Highlighted = false;
            }

            ImGui.End();
            ImGui.PopStyleVar(2);
            PopDarkTheme();
            return;
        }

        // === HEADER (name + mass info) ===
        ImGui.PushStyleColor(ImGuiCol.Text, HEADER_COLOR);
        ImGui.Text(friendlyName);
        ImGui.PopStyleColor();

        float totalMass = 0f;
        Span<InertMass> masses = part.SubtreeModules.Get<InertMass>();

        for (int i = 0; i < masses.Length; i++)
            totalMass += masses[i].MassPropertiesAsmb.Props.Mass;

        string massStr = totalMass >= 1f ? $"{totalMass:F2} t" : $"{totalMass * 1000f:F0} kg";

        // List of fuel types
        List<string> fuelTypes = new List<string>();

        foreach(RocketCore core in part.SubtreeModules.Get<RocketCore>())
        {
            // Find the Reactants for the fuel
            ReadOnlySpan<Reactant> reactants = core.Combustion.Reactants;

            // Create the string for the fuel type(s) and add to the list
            string tempFuel = "- ";
            for (int i = 0; i < reactants.Length; i++)
            {
                if (i != 0)
                    tempFuel += " and ";
                tempFuel += reactants[i].SubstancePhase.Name;  // Actual reactant name
            }

            if(!fuelTypes.Contains(tempFuel))
                fuelTypes.Add(tempFuel);
        }

        ImGui.PushStyleColor(ImGuiCol.Text, TEXT_DIM);
        ImGui.Text($"Mass: {massStr}");
        if(fuelTypes.Count > 0)
        {
            ImGui.Text("Accepted Fuels: ");
            foreach(string fuel in fuelTypes)
            {
                ImGui.TextWrapped(fuel);
            }
        }
        ImGui.PopStyleColor();

        ImGui.PushStyleColor(ImGuiCol.Separator, ACCENT_BLUE);
        ImGui.Separator();
        ImGui.PopStyleColor();

        ImGui.Dummy(new float2(0f, 2f));

        // === ACTION BUTTONS (delete, reroot, copy) ===
        float btnWidth = (ImGui.GetContentRegionAvail().X - 8f) / 3f;

        // Delete part (with full cleanup)
        ImGui.PushStyleColor(ImGuiCol.Button, new float4(0.6f, 0.15f, 0.15f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new float4(0.8f, 0.2f, 0.2f, 1f));

        if (ImGui.Button("Delete##partact", new float2(btnWidth, 28f)))
        {
            TakeSnapshot(editor);

            if (part.Tree.Root == part && editor.EditingSpace.Parts != null && part.Tree == editor.EditingSpace.Parts)
                editor.EditingSpace.Parts = null;

            editor.UnattachedPartTrees.Remove(part.Tree);
            part.Disconnect();

            editor.Selected = null;
            editor.Highlighted = null;
            editor.PartMenus.Remove(menu);

            StickyGrabPatch.CancelStickyGrab();
            MarkSnapshotNeeded();

            ImGui.End();
            ImGui.PopStyleVar(2);
            PopDarkTheme();
            return;
        }

        ImGui.PopStyleColor(2);

        // Reroot part tree
        ImGui.SameLine();

        if (part.TreeParent == null) ImGui.BeginDisabled();

        ImGui.PushStyleColor(ImGuiCol.Button, BG_MID);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);

        if (ImGui.Button("Reroot##partact", new float2(btnWidth, 28f)))
            part.Tree.Reroot(part);

        ImGui.PopStyleColor(2);

        if (part.TreeParent == null) ImGui.EndDisabled();

        // Copy part subtree
        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Button, BG_MID);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);

        if (ImGui.Button("Copy##partact", new float2(btnWidth, 28f)))
        {
            uint localId = 1u;
            var copy = PartTree.Deserialize(part.GetReferenceWithChildren(ref localId));

            editor.UnattachedPartTrees.Add(copy);

            var copyParts = copy.Parts;
            for (int i = 0; i < copyParts.Length; i++)
                copyParts[i].FakeTranslucent = true;
        }

        ImGui.PopStyleColor(2);

        // === SECOND ROW ACTIONS (parent/symmetry highlighting) ===
        ImGui.Dummy(new float2(0f, 2f));
        float halfWidth = (ImGui.GetContentRegionAvail().X - 4f) / 2f;

        // Highlight parent
        if (part.TreeParent == null) ImGui.BeginDisabled();

        ImGui.PushStyleColor(ImGuiCol.Button, BG_MID);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);

        var parent = part.TreeParent;

        ImGui.BeginDisabled(parent == null);

        ImGui.PushStyleColor(ImGuiCol.Button, BG_MID);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);

        if (ImGui.Button("Parent##hp", new float2(halfWidth, 28f)) && parent != null)
            parent.Highlighted = !parent.Highlighted;

        ImGui.PopStyleColor(2);

        ImGui.EndDisabled();

        ImGui.PopStyleColor(2);

        if (part.TreeParent == null) ImGui.EndDisabled();

        // Highlight symmetry parts
        ImGui.SameLine();

        if (!part.SymmetryLink.HasValue) ImGui.BeginDisabled();

        ImGui.PushStyleColor(ImGuiCol.Button, BG_MID);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);

        if (part.SymmetryLink is { } link)
        {
            if (ImGui.Button("Sym##hs", new float2(halfWidth, 28f)))
            {
                foreach (var sym in link.Symmetries)
                    foreach (var sp in sym.Parts)
                        sp.Highlighted = !sp.Highlighted;
            }
        }
        else
        {
            ImGui.BeginDisabled();
            ImGui.Button("Sym##hs", new float2(halfWidth, 28f));
            ImGui.EndDisabled();
        }

        ImGui.PopStyleColor(2);

        if (!part.SymmetryLink.HasValue) ImGui.EndDisabled();

         ImGui.Dummy(new float2(0f, 4f));
        ImGui.PushStyleColor(ImGuiCol.Separator, ACCENT_BLUE);
        ImGui.Separator();
        ImGui.PopStyleColor();

        // === STAGE & SEQUENCE ===
        if (ImGui.BeginTable("##stageseq", 2, ImGuiTableFlags.None))
        {
            ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.PushStyleColor(ImGuiCol.Text, TEXT_DIM);
            ImGui.Text("Stage");
            ImGui.PopStyleColor();
            ImGui.TableNextColumn();
            int stage = part.Stage;
            ImGui.PushItemWidth(-1f);
            if (ImGui.InputInt("##stage", ref stage, 1, 1, ImGuiInputTextFlags.None))
            {
                part.SetStage(Math.Max(0, stage));
                part.FullPart.Tree.RecomputeAllDerivedData();
            }
            ImGui.PopItemWidth();

            if (part.Sequenceable)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Text, TEXT_DIM);
                ImGui.Text("Sequence");
                ImGui.PopStyleColor();
                ImGui.TableNextColumn();
                int seq = part.Sequence;
                ImGui.PushItemWidth(-1f);
                if (ImGui.InputInt("##seq", ref seq, 1, 1, ImGuiInputTextFlags.None))
                    part.SetSequence(Math.Max(0, seq));
                ImGui.PopItemWidth();
            }

            ImGui.EndTable();
        }

        // === RESOURCES / FUEL ===
        Span<Tank> tanks = part.SubtreeModules.Get<Tank>();
        if (tanks.Length > 0)
        {
            ImGui.Dummy(new float2(0f, 2f));
            ImGui.PushStyleColor(ImGuiCol.Separator, ACCENT_BLUE);
            ImGui.Separator();
            ImGui.PopStyleColor();

            ImGui.PushStyleColor(ImGuiCol.Text, HEADER_COLOR);
            ImGui.Text("Resources");
            ImGui.PopStyleColor();

            // Combustion selector
           var combustionsField = typeof(VehicleEditor)
                .GetField("_combustionObjects", BindingFlags.NonPublic | BindingFlags.Instance);
            var combustions = combustionsField?.GetValue(editor);
            Console.WriteLine($"[BP] combustions type: {combustions?.GetType()}");
            Console.WriteLine($"[BP] combustions null: {combustions == null}");
            
            if (combustions != null)
            {
                if (!menu.SelectedCombustionSet)
                {
                    var combustionList = combustions as List<CombustionObject>;
                    if (combustionList != null && combustionList.Count > 0)
                    {
                        // Match tank's current substances against combustion reactants
                        var tankMoles = new List<Substance>();
                        foreach (var mole in tanks[0].Moles)
                            tankMoles.Add(mole.SubstancePhase.Substance);
                        
                        bool found = false;
                        foreach (var combObj in combustionList)
                        {
                            bool match = true;
                            foreach (var sub in tankMoles)
                            {
                                bool subFound = false;
                                var reactants = combObj.Combustion.Reactants;
                                for (int r = 0; r < reactants.Length; r++)
                                {
                                    if (reactants[r].SubstancePhase.Substance == sub)
                                    { subFound = true; break; }
                                }
                                if (!subFound) { match = false; break; }
                            }
                            if (match)
                            {
                                menu.SelectedCombustion = combObj;
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                            menu.SelectedCombustion = combustionList[0];
                    }
                    menu.SelectedCombustionSet = true;
                }

                ImGuiHelper.BeginColumns(2);
                if (combustions is List<CombustionObject> list)
                {
                    ImGuiHelper.DrawCombo("Resource"u8, ref menu.SelectedCombustion, list);
                }
                ImGuiHelper.EndColumns();
            }

            ImGui.PushStyleColor(ImGuiCol.Button, ACCENT_BLUE);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);
            if (ImGui.Button("Refill##refuel", new float2(ImGui.GetContentRegionAvail().X, 28f)))
            {
                for (int i = 0; i < tanks.Length; i++)
                    tanks[i].ConfigureFor(menu.SelectedCombustion.Combustion);
            }
            ImGui.PopStyleColor(2);
        }

        // === BATTERY ===
        Span<Battery> batteries = part.SubtreeModules.Get<Battery>();
        if (batteries.Length > 0)
        {
            ImGui.Dummy(new float2(0f, 2f));
            ImGui.PushStyleColor(ImGuiCol.Separator, ACCENT_BLUE);
            ImGui.Separator();
            ImGui.PopStyleColor();

            ImGui.PushStyleColor(ImGuiCol.Text, HEADER_COLOR);
            ImGui.Text("Battery");
            ImGui.PopStyleColor();

            for (int i = 0; i < batteries.Length; i++)
            {
                var battery = batteries[i];
                var stateRef = part.FullPart.Tree.Batteries
                    .GetModuleAndAllMutableStatesForInitializationByIdx(battery.StatesIdx);
                int charge = (int)stateRef.State.Charge.Value();
                int maxCharge = (int)battery.MaximumCapacity.Value();

                ImGui.PushStyleColor(ImGuiCol.Text, TEXT_DIM);
                ImGui.Text("Charge");
                ImGui.PopStyleColor();
                ImGui.PushItemWidth(-1f);
                if (ImGui.SliderInt("##charge_" + i, ref charge, 0, maxCharge, "%d J", ImGuiSliderFlags.None))
                    stateRef.State.Charge = new Joules(charge);
                ImGui.PopItemWidth();
            }
        }

        // === TRS ===
        if (ImGui.CollapsingHeader("TRS"))
        {
            var trs = menu.MenuTRS;

            // sync con el part (solo si no está editando)
            if (!trs.IsEditing)
            {
                trs.Translation = part.PositionParentAsmb;
                trs.RotationQuat = part.Asmb2ParentAsmb;
                trs.Scale = part.Scale;
            }

            // POSITION
           var posD = trs.Translation;
            var pos = new float3((float)posD.X, (float)posD.Y, (float)posD.Z);

            if (ImGui.DragFloat3("Position", ref pos, 0.1f))
            {
                trs.Translation = new double3(pos.X, pos.Y, pos.Z);
                part.PositionParentAsmb = trs.Translation;
                trs.IsEditing = true;
            }

            // ROTATION (Euler)
            var rotD = trs.RotationEuler;
            var rot = new float3((float)rotD.X, (float)rotD.Y, (float)rotD.Z);

            if (ImGui.DragFloat3("Rotation", ref rot, 1f))
            {
                trs.RotationEuler = new double3(rot.X, rot.Y, rot.Z);
                trs.RotationQuat = doubleQuat.CreateFromYawPitchRoll(rot.Y, rot.X, rot.Z);
                part.Asmb2ParentAsmb = trs.RotationQuat;
                trs.IsEditing = true;
            }

            // SCALE
            var scaleD = trs.Scale;
            var scale = new float3((float)scaleD.X, (float)scaleD.Y, (float)scaleD.Z);

            if (ImGui.DragFloat3("Scale", ref scale, 0.01f))
            {
                trs.Scale = new double3(scale.X, scale.Y, scale.Z);
                part.Scale = trs.Scale;
                trs.IsEditing = true;
            }

            // guardar cambios
            menu.MenuTRS = trs;
        }

        // === SUBPARTS ===
        if (ImGui.CollapsingHeader("SubParts##partsub"))
        {
            var subParts = part.SubParts;

            for (int i = 0; i < subParts.Length; i++)
            {
                var sp = subParts[i];

                ImGui.PushID((int)sp.InstanceId);

                bool selected = (_selectedSubPart == sp);

                if (ImGui.Selectable(sp.DisplayName, selected))
                {
                    _selectedSubPart = sp;
                    _subPartWindowOpen = true;

                    // init TRS
                    _subPartTRS.Translation = sp.PositionParentAsmb;
                    _subPartTRS.RotationQuat = sp.Asmb2ParentAsmb;
                    _subPartTRS.RotationEuler = double3.Zero;
                    _subPartTRS.Scale = sp.Scale;
                    _subPartTRS.IsEditing = false;
                }

                ImGui.PopID();
            }
        }

        // === ADD SUBPARTS ===
        var modLibType = typeof(ModLibrary);
        var allPartsField = modLibType.GetField("AllParts", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        var allParts = allPartsField?.GetValue(null);

        var getListMethod = allParts?.GetType().GetMethod("GetList");

        var partList = getListMethod?.Invoke(allParts, null) as IEnumerable<PartTemplate>;

        if (ImGui.CollapsingHeader("Add SubParts##partaddsub"))
        {
            if (partList != null)
            {
                foreach (var template in partList)
                {
                    if (!template.IsSubPart) continue;

                    if (ImGui.Button($"Add {template.Id}##add_{template.Id}_{part.InstanceId}"))
                    {
                        var subPart = new Part($"{template.Id} {part.SubParts.Length}", template);

                        subPart.Tree = part.Tree;

                        subPart.PositionParentAsmb = new double3(0, 0, 0);
                        subPart.Asmb2ParentAsmb = doubleQuat.Identity;
                        subPart.Scale = new double3(1, 1, 1);

                        part.AddSubPart(subPart);

                        var pos = part.PositionParentAsmb;
                        part.PositionParentAsmb = pos + new double3(1e-9, 0, 0);
                        part.PositionParentAsmb = pos;
                    }
                }
            }
        }
        // === SCALE (collapsible) ===
        ImGui.Dummy(new float2(0f, 2f));
        if (ImGui.CollapsingHeader("Scale##partscale", ImGuiTreeNodeFlags.None))
        {
            double3 scale = part.Scale;
            float sx = (float)scale.X, sy = (float)scale.Y, sz = (float)scale.Z;
            bool changed = false;

            ImGui.PushStyleColor(ImGuiCol.Text, TEXT_DIM);
            ImGui.Text("X"); ImGui.SameLine();
            ImGui.PopStyleColor();
            ImGui.PushItemWidth(-1f);
            if (ImGui.DragFloat("##sx", ref sx, 0.01f, 0.1f, 10f, "%.2f", ImGuiSliderFlags.None))
                changed = true;
            ImGui.PopItemWidth();

            ImGui.PushStyleColor(ImGuiCol.Text, TEXT_DIM);
            ImGui.Text("Y"); ImGui.SameLine();
            ImGui.PopStyleColor();
            ImGui.PushItemWidth(-1f);
            if (ImGui.DragFloat("##sy", ref sy, 0.01f, 0.1f, 10f, "%.2f", ImGuiSliderFlags.None))
                changed = true;
            ImGui.PopItemWidth();

            ImGui.PushStyleColor(ImGuiCol.Text, TEXT_DIM);
            ImGui.Text("Z"); ImGui.SameLine();
            ImGui.PopStyleColor();
            ImGui.PushItemWidth(-1f);
            if (ImGui.DragFloat("##sz", ref sz, 0.01f, 0.1f, 10f, "%.2f", ImGuiSliderFlags.None))
                changed = true;
            ImGui.PopItemWidth();

            if (changed)
                part.Scale = new double3(sx, sy, sz);
        }

        // === CONNECTIONS (collapsible) ===
        if (ImGui.CollapsingHeader("Connections##partconn", ImGuiTreeNodeFlags.None))
        {
            foreach (var conn in part.Connections)
            {
                var other = conn.OtherPart(part);
                string connName = GetFriendlyName(other.FullPart.Template);
                ImGui.PushStyleColor(ImGuiCol.Button, BG_MID);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);
                if (ImGui.Button($"{connName}##conn_{other.InstanceId}", new float2(-1f, 24f)))
                    other.Highlighted = !other.Highlighted;
                ImGui.PopStyleColor(2);
            }
        }

        // === INFO (collapsible) ===
        if (ImGui.CollapsingHeader("Info##partinfo", ImGuiTreeNodeFlags.None))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, TEXT_DIM);
            ImGui.Text($"ID: {part.Id}");
            ImGui.Text($"Root: {(part.IsRoot ? "Yes" : "No")}");
            ImGui.Text($"Instance: {part.InstanceId}");
            ImGui.PopStyleColor();
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
        PopDarkTheme();
    }

    // Draws the TRS (Transform) controls for the currently selected subpart
    static void DrawSubPartTRS()
    {
        // Ensure a subpart is selected before rendering controls
        if (_selectedSubPart == null) return;

        // Collapsible section for transform controls (default open)
        if (ImGui.CollapsingHeader("TRS", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // === POSITION ===
            // Convert current double3 position to float3 for ImGui editing
            var pos = new float3(
                (float)_subPartTRS.Translation.X,
                (float)_subPartTRS.Translation.Y,
                (float)_subPartTRS.Translation.Z
            );

            // Update translation when user drags values
            if (ImGui.DragFloat3("Position", ref pos, 0.1f))
            {
                _subPartTRS.Translation = new double3(pos.X, pos.Y, pos.Z);
                _selectedSubPart.PositionParentAsmb = _subPartTRS.Translation;
            }

            // === ROTATION ===
            // Edit rotation in Euler angles (converted from stored double3)
            var rot = new float3(
                (float)_subPartTRS.RotationEuler.X,
                (float)_subPartTRS.RotationEuler.Y,
                (float)_subPartTRS.RotationEuler.Z
            );

            // Update rotation (convert Euler → Quaternion for engine)
            if (ImGui.DragFloat3("Rotation", ref rot, 1f))
            {
                _subPartTRS.RotationEuler = new double3(rot.X, rot.Y, rot.Z);
                _subPartTRS.RotationQuat =
                    doubleQuat.CreateFromYawPitchRoll(rot.Y, rot.X, rot.Z);

                _selectedSubPart.Asmb2ParentAsmb = _subPartTRS.RotationQuat;
            }

            // === SCALE ===
            // Convert current scale to float3 for editing
            var scale = new float3(
                (float)_subPartTRS.Scale.X,
                (float)_subPartTRS.Scale.Y,
                (float)_subPartTRS.Scale.Z
            );

            // Update scale when modified
            if (ImGui.DragFloat3("Scale", ref scale, 0.01f))
            {
                _subPartTRS.Scale = new double3(scale.X, scale.Y, scale.Z);
                _selectedSubPart.Scale = _subPartTRS.Scale;
            }
        }
    }

    // Returns the first valid editor tag (category) assigned to a part
    static string GetFirstTag(PartTemplate part)
    {
        // Access internal editor tags via reflection
        var tagsField = typeof(VehicleEditor)
            .GetField("_editorTags", BindingFlags.NonPublic | BindingFlags.Static);

        List<EditorTag> allTags = tagsField?.GetValue(null) as List<EditorTag> ?? new List<EditorTag>();

        // Find the first matching tag (excluding generic/hidden ones)
        foreach (EditorTag tag in allTags)
        {
            if (tag == EditorTag.All || tag == EditorTag.Hidden) continue;

            if (part.HasEditorTag(tag))
                return tag.Tag;
        }

        // Fallback category if no tag matches
        return "Other";
    }

    // Saves the current vehicle state into the undo stack
    static void TakeSnapshot(VehicleEditor editor)
    {
        // Ensure there is a valid part tree to snapshot
        if (editor.EditingSpace.Parts == null) return;

        // Serialize current part tree
        uint id = 1;
        var snapshot = editor.EditingSpace.Parts.Serialize(ref id);

        // Push snapshot to undo stack
        _undoStack.Add(snapshot);

        // Limit undo history size to avoid memory issues
        if (_undoStack.Count > 50)
            _undoStack.RemoveAt(0);

        // Clear redo stack since new action invalidates redo history
        _redoStack.Clear();

        // Mark snapshot as handled
        _snapshotPending = false;
    }

    // Restores the previous state of the vehicle from the undo stack
    static void Undo(VehicleEditor editor)
    {
        // Do nothing if no undo history exists
        if (_undoStack.Count == 0) return;
            
        // Save current state into redo stack before undoing
        if (editor.EditingSpace.Parts != null)
        {
            uint id = 1;
            _redoStack.Add(editor.EditingSpace.Parts.Serialize(ref id));
        }
            
        // Retrieve last snapshot from undo stack
        var snapshot = _undoStack[_undoStack.Count - 1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
            
        // Deserialize snapshot into a new part tree
        var newParts = PartTree.Deserialize(snapshot);
        newParts.RecomputeAllDerivedData();

        // Replace current editor state with restored snapshot
        editor.EditingSpace.Parts = newParts;

        // Clear transient UI/editor state
        editor.UnattachedPartTrees.Clear();
        editor.PartMenus.Clear();
        editor.SubPartMenus.Clear();
        editor.Selected = null;
        editor.Highlighted = null;
    }

    // Reapplies a previously undone state from the redo stack
    static void Redo(VehicleEditor editor)
    {
        // Do nothing if no redo history exists
        if (_redoStack.Count == 0) return;
            
        // Save current state into undo stack before redoing
        if (editor.EditingSpace.Parts != null)
        {
            uint id = 1;
            _undoStack.Add(editor.EditingSpace.Parts.Serialize(ref id));
        }
            
        // Retrieve last snapshot from redo stack
        var snapshot = _redoStack[_redoStack.Count - 1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
            
        // Deserialize snapshot into a new part tree
        var newParts = PartTree.Deserialize(snapshot);
        newParts.RecomputeAllDerivedData();

        // Replace current editor state with restored snapshot
        editor.EditingSpace.Parts = newParts;

        // Clear transient UI/editor state
        editor.UnattachedPartTrees.Clear();
        editor.PartMenus.Clear();
        editor.SubPartMenus.Clear();
        editor.Selected = null;
        editor.Highlighted = null;
    }

    // Marks that a new snapshot should be taken when safe (no active interactions)
    public static void MarkSnapshotNeeded() => _snapshotPending = true;

}