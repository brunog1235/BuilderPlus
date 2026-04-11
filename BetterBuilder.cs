using StarMap.API;
using HarmonyLib;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using System.Reflection;
using Brutal.GlfwApi;

namespace BetterBuilder;

[StarMapMod]
public class BetterBuilderMod
{
    private static Harmony _harmony = new Harmony("com.betterbuilder.mod");

    [StarMapImmediateLoad]
    public void OnLoad(KSA.Mod mod)
    {
        Console.WriteLine("[BetterBuilder] Cargando mod...");
        _harmony.PatchAll();
        Console.WriteLine("[BetterBuilder] Patches aplicados!");
    }

    [StarMapUnload]
    public void OnUnload()
    {
        _harmony.UnpatchAll("com.betterbuilder.mod");
        Console.WriteLine("[BetterBuilder] Mod descargado.");
    }
}

[HarmonyPatch(typeof(VehicleEditor), "Initialize")]
public class HideOriginalPartWindow
{
    static void Postfix(VehicleEditor __instance)
    {
        try
        {
            FieldInfo? partWindowField = typeof(VehicleEditor)
                .GetField("_partWindow", BindingFlags.NonPublic | BindingFlags.Static);
            if (partWindowField == null) return;
            object? partWindow = partWindowField.GetValue(null);
            if (partWindow == null) return;
            FieldInfo? showField = typeof(ImGuiWindow)
                .GetField("_show", BindingFlags.NonPublic | BindingFlags.Instance);
            showField?.SetValue(partWindow, false);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[BetterBuilder] Error: {e.Message}");
        }
    }
}

[HarmonyPatch(typeof(StageList), nameof(StageList.DrawEditorStageWindow))]
public class HideOriginalStageWindow
{
    static bool Prefix() => false;
}

[HarmonyPatch(typeof(SequenceList), nameof(SequenceList.DrawEditorSequenceWindow))]
public class HideOriginalSequenceWindow
{
    static bool Prefix() => false;
}

[HarmonyPatch(typeof(FontManager), nameof(FontManager.RegenerateFonts))]
public class LoadIconFont
{
    static unsafe void Postfix()
    {
        try
        {
            ImFontAtlasPtr fonts = ImGui.GetIO().Fonts;
            
            // Glyph ranges de Font Awesome 6 Solid
            ushort[] ranges = new ushort[] { 0xe000, 0xf8ff, 0 };
            
            fixed (ushort* rangesPtr = ranges)
            {
                ImFontConfig config = new ImFontConfig();
                config.MergeMode = true; // Merge con la fuente principal
                config.SizePixels = GameSettings.GetFontSize();
                config.GlyphMaxAdvanceX = float.MaxValue;
                config.RasterizerMultiply = 1f;
                config.RasterizerDensity = (float)GameSettings.GetFontDensity() / 100f;
                
                fonts.AddFontFromFileTTF(
                    "Content/BetterBuilder/fa-solid-900.ttf",
                    GameSettings.GetFontSize(),
                    &config,
                    new ReadOnlySpan<ushort>(rangesPtr, 3));
            }
            
            Console.WriteLine("[BetterBuilder] Icon font loaded!");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[BetterBuilder] Error loading icon font: {e.Message}");
        }
    }
}

[HarmonyPatch(typeof(VehicleEditor), nameof(VehicleEditor.OnMouseButton))]
public class StickyGrabPatch
{
    static bool _stickyGrab = false;
    static int _spawnSkips = 0;
    public static Part? GrabbedPart = null;
    public static bool IsStickyGrabbing => _stickyGrab;
    public static bool IsSpawning = false;
    public static void MarkSpawned()
    {
        _spawnSkips = 1;
        IsSpawning = true;
    }
    
    public static void CancelStickyGrab()
    {
        _stickyGrab = false;
        _spawnSkips = 0;
        GrabbedPart = null;
        IsSpawning = false;
    }
    
    static void Postfix(VehicleEditor __instance, GlfwMouseButton button, GlfwButtonAction action)
    {
        if (button != GlfwMouseButton.Number1) return;
        if (ImGui.GetIO().WantCaptureMouse) return; // Ignorar clicks en UI
        
        if (action == GlfwButtonAction.Release)
        {
            if (_spawnSkips > 0)
            {
                _spawnSkips--;
                if (_spawnSkips == 0)
                    IsSpawning = false;
                return;
            }
            
            if (__instance.Highlighted != null)
            {
                if (!_stickyGrab)
            {
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
                _stickyGrab = false;
                GrabbedPart = null;
            }
            }
        }
    }
}

// Cancela la ventana original del editor y la reemplaza con la nuestra
[HarmonyPatch(typeof(VehicleEditor), nameof(VehicleEditor.OnDrawUi))]
public class VehicleEditorUIPatch
{
    static EditorTag _selectedTag = EditorTag.All;
    static string _searchText = "";

    static bool _showStages = false;

    static ImFontPtr _iconFont = default;
    
    static readonly float4 BG_DARK      = new float4(0.10f, 0.10f, 0.12f, 1.00f);
    static readonly float4 BG_MID       = new float4(0.15f, 0.15f, 0.18f, 1.00f);
    static readonly float4 ACCENT_BLUE  = new float4(0.20f, 0.50f, 0.90f, 1.00f);
    static readonly float4 ACCENT_HOVER = new float4(0.25f, 0.60f, 1.00f, 1.00f);
    static readonly float4 TEXT_DIM     = new float4(0.60f, 0.60f, 0.65f, 1.00f);
    static readonly float4 HEADER_COLOR = new float4(0.30f, 0.70f, 1.00f, 1.00f);
    static readonly float4 ACTIVE_COLOR = new float4(0.20f, 0.50f, 0.90f, 1.00f);
    static bool _initialized = false;

   static readonly Dictionary<string, string> CategoryIcons = new()
    {
        { "All",         "\uf005" }, // star
        { "Capsules",    "\uf508" }, // user-astronaut
        { "Cargo",       "\uf466" }, // box-open
        { "Coupling",    "\uf0c1" }, // link
        { "Electrical",  "\uf0e7" }, // bolt
        { "Engines",     "\uf135" }, // rocket
        { "Fuel Tanks",  "\uf52f" }, // gas-pump
        { "Interstage",  "\uf248" }, // layer-group
        { "Lights",      "\uf0eb" }, // lightbulb
        { "Passage",     "\uf52b" }, // door-open
        { "RCS",         "\uf192" }, // circle-dot
        { "Radial",      "\uf110" }, // circle-notch
        { "Structural",  "\uf6e3" }, // your pick
        { "Tanks",       "\uf575" }, // your pick
    };

    // Prefix cancela el OnDrawUi original
    static bool Prefix(VehicleEditor __instance, Viewport inViewport)
    {
        if (!_initialized)
        {
            __instance.Connecting = true;
            _initialized = true;
        }
            
        try
        {
            double4x4 matrixVehicleAsmb2Ego = __instance.EditingSpace.GetMatrixAsmb2Ego(inViewport.GetCamera());

            // Dibujamos nuestra UI
            DrawPartsPanel(__instance, inViewport);
            DrawToolbar(__instance, inViewport);

            // Conservamos las ventanas importantes del juego
            for (int i = __instance.PartMenus.Count - 1; i >= 0; i--)
                __instance.DrawPartUi(i, inViewport);

            for (int i = __instance.SubPartMenus.Count - 1; i >= 0; i--)
                __instance.DrawSubPartUi(i, inViewport);

            if (_showStages)
                DrawStagePanel(__instance, inViewport);
            __instance.EditingSpace.DrawResourceWindow(inViewport);

            DrawSequencePanel(__instance, inViewport);

            if (__instance.ExistingVehicle == null)
                DrawLaunchPanel(__instance, inViewport);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[BetterBuilder] Error en Prefix: {e.Message}");
        }

        // Sticky grab: si clickeamos una parte colocada, hacerla "pegajosa"
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) 
            && !ImGui.GetIO().WantCaptureMouse
            && __instance.Highlighted != null 
            && !__instance.Highlighted.FakeTranslucent
            && !__instance.GizmoGrabbed)
        {
            var part = __instance.Highlighted;
            
            // Desconectar y hacer translúcida
            if (part.Disconnect())
            {
                __instance.UnattachedPartTrees.Add(part.Tree);
                var treeParts = part.Tree.Parts;
                for (int i = 0; i < treeParts.Length; i++)
                    treeParts[i].FakeTranslucent = true;
            }
            
            part.Grabbed = true;
            part.Selected = true;
            __instance.Selected = part;
            
            // Simular soltar el mouse para que quede "pegada"
            ImGui.Internal.FocusWindow(null);
            ImGui.GetIO().AddMouseButtonEvent(0, false);
        }

        return false; // Cancela el método original
    }

    static void PushDarkTheme()
    {
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
    }

    static void PopDarkTheme() => ImGui.PopStyleColor(14);

    // Barra de herramientas vertical
    static void DrawToolbar(VehicleEditor editor, Viewport viewport)
    {
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoScrollbar;

        float screenHeight = ImGui.GetMainViewport().Size.Y;
        float screenWidth  = ImGui.GetMainViewport().Size.X;

        ImGui.SetNextWindowPos(viewport.Position + new float2(364f, 40f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new float2(48f, 400f), ImGuiCond.Always);

        PushDarkTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(4f, 4f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new float2(2f, 4f));

        ImGui.Begin("##Toolbar", flags);

        DrawToolButton("\uf0b2", "Translate", editor.TranslateGizmoEnabled,
        () => editor.TranslateGizmoEnabled = !editor.TranslateGizmoEnabled);

        DrawToolButton("\uf2f1", "Rotate", editor.RotationGizmoEnabled,
            () => editor.RotationGizmoEnabled = !editor.RotationGizmoEnabled);

        DrawToolButton("\uf424", "Scale", editor.ScaleGizmoEnabled,
            () => editor.ScaleGizmoEnabled = !editor.ScaleGizmoEnabled);

        ImGui.Dummy(new float2(0f, 4f));
        ImGui.Separator();
        ImGui.Dummy(new float2(0f, 4f));

        DrawToolButton("\uf1b3", "Snap", editor.Snapping,
            () => editor.Snapping = !editor.Snapping);

        DrawToolButton("\uf542", "Connect", editor.Connecting,
            () => editor.Connecting = !editor.Connecting);

        DrawToolButton("\uf110", "Radial Sym", editor.RadialSymmetry,
            () => editor.RadialSymmetry = !editor.RadialSymmetry);

        DrawToolButton("\uf0db", "Stages", _showStages,
            () => _showStages = !_showStages);

        ImGui.Dummy(new float2(0f, 4f));
        ImGui.Separator();
        ImGui.Dummy(new float2(0f, 4f));

        // Symmetry amount
        ImGui.Dummy(new float2(0f, 4f));
        ImGui.Separator();
        ImGui.Dummy(new float2(0f, 4f));

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

        ImGui.End();          
        ImGui.PopStyleVar(2); 
        PopDarkTheme();     
    }

    static void DrawToolButton(string icon, string tooltip, bool active, Action onClick)
    {
        float4 color = active ? ACTIVE_COLOR : BG_MID;
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);

        bool hasIconFont = !_iconFont.IsNull();
        if (hasIconFont) ImGui.PushFont(_iconFont, GameSettings.GetFontSize());
        if (ImGui.Button(icon + "##tool_" + tooltip, new float2(40f, 36f)))
            onClick();
        if (hasIconFont) ImGui.PopFont();

        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
    }

    static void DrawPartsPanel(VehicleEditor editor, Viewport viewport)
    {
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoTitleBar;

        float screenHeight = ImGui.GetMainViewport().Size.Y;

        ImGui.SetNextWindowPos(viewport.Position + new float2(20f, 40f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new float2(340f, screenHeight - 60f), ImGuiCond.Always);

        PushDarkTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new float2(2f, 2f));

        ImGui.Begin("##BetterBuilderParts", flags);
        
        // Delete parts
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) 
            && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
            && (editor.Selected != null && editor.Selected.FakeTranslucent 
                || StickyGrabPatch.GrabbedPart != null
                || StickyGrabPatch.IsSpawning))
        {
            var part = StickyGrabPatch.GrabbedPart ?? editor.Selected;
            var tree = part.Tree;
            
            if (editor.EditingSpace.Parts == tree)
                editor.EditingSpace.Parts = null;
            
            editor.UnattachedPartTrees.Remove(tree);
            editor.Selected = null;
            editor.Highlighted = null;
            editor.PartMenus.Clear();
            StickyGrabPatch.CancelStickyGrab();
        }

        // Header
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BG_DARK);
        ImGui.BeginChild("##header"u8, new float2(0f, 35f));
        ImGui.PushStyleColor(ImGuiCol.Text, HEADER_COLOR);
        ImGui.Text("PART CATALOGUE"u8);
        ImGui.PopStyleColor();
        ImGui.EndChild();
        ImGui.PopStyleColor();

        // Barra de búsqueda
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BG_DARK);
        ImGui.BeginChild("##searchbar"u8, new float2(0f, 36f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new float4(0.18f, 0.18f, 0.22f, 1f));
        ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
        ImInputString searchBuffer = new ImInputString(128);
        searchBuffer.SetValue(_searchText.AsSpan());
        ImGui.InputText("##search"u8, searchBuffer, ImGuiInputTextFlags.None, null, 0);
        _searchText = searchBuffer.ToString();
        if (ImGui.IsItemActive())
            editor.IsUserTyping = true;
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

        // Tags
        var tagsField = typeof(VehicleEditor)
            .GetField("_editorTags", BindingFlags.NonPublic | BindingFlags.Static);
        List<EditorTag> tags = tagsField?.GetValue(null) as List<EditorTag> ?? new List<EditorTag>();

        // Cargamos el icon font si no está cargado
        if (_iconFont.IsNull())
            FontManager.Fonts.TryGetValue("fa-solid-900", out _iconFont);

        // Usamos el icon font si está disponible
        bool hasIconFont = !_iconFont.IsNull();
       

        // Sidebar
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BG_DARK);
        ImGui.BeginChild("##sidebar"u8, new float2(36f, 0f));

        // Botón All manual
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

        // Partes
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BG_MID);
        ImGui.BeginChild("##partsArea"u8);
        DrawPartsList(editor, viewport, _selectedTag, _searchText);
        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.End();
        ImGui.PopStyleVar(2);
        PopDarkTheme();
    }

    static void DrawPartsList(VehicleEditor editor, Viewport viewport, EditorTag tag, string search)
    {
        double4x4 matrixAsmb2Ego = editor.EditingSpace.GetMatrixAsmb2Ego(viewport.GetCamera());

        var allPartsField = typeof(ModLibrary)
            .GetField("AllParts", BindingFlags.NonPublic | BindingFlags.Static);
        if (allPartsField == null) return;

        var allParts    = allPartsField.GetValue(null);
        var getList     = allParts?.GetType().GetMethod("GetList");
        var partsList   = getList?.Invoke(allParts, null) as System.Collections.IEnumerable;
        if (partsList == null) return;

        var grouped = new Dictionary<string, List<PartTemplate>>();
        foreach (var obj in partsList)
        {
            if (obj is not PartTemplate part) continue;
            if (part.IsSubPart || part.IsHidden) continue;
            if (tag != EditorTag.All && !part.HasEditorTag(tag)) continue;
            if (!string.IsNullOrEmpty(search) &&
                !part.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;

            string category = tag == EditorTag.All ? GetFirstTag(part) : tag.Tag;
            if (!grouped.ContainsKey(category))
                grouped[category] = new List<PartTemplate>();
            grouped[category].Add(part);
        }

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(3f, 3f));
        foreach (var group in grouped)
        {
            ImGui.Dummy(new float2(0f, 4f));
            ImGui.PushStyleColor(ImGuiCol.Text, HEADER_COLOR);
            ImGui.Text("▾ " + group.Key.ToUpper());
            ImGui.PopStyleColor();
            ImGui.Separator();

            if (ImGui.BeginTable("##table_" + group.Key, 3, ImGuiTableFlags.None))
            {
                foreach (PartTemplate part in group.Value)
                {
                    ImGui.TableNextColumn();
                    part.Thumbnail?.CreateImGuiThumbnail(Program.LinearClampedSampler);
                    ImString strId = new ImString(8, 1);
                    strId.AppendLiteral("##btn_".AsSpan());
                    strId.AppendFormatted(part.Id.AsSpan());
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
                    if (ImGui.IsItemActivated())
                    {
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
                        
                        StickyGrabPatch.MarkSpawned();
                        editor.SpawnPart(part, in matrixAsmb2Ego, viewport);
                    }
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(part.DisplayName);
                }
                ImGui.EndTable();
            }
            ImGui.Dummy(new float2(0f, 6f));
        }
        ImGui.PopStyleVar();
    }
    static void DrawLaunchPanel(VehicleEditor editor, Viewport viewport)
    {
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoTitleBar;

        float screenHeight = ImGui.GetMainViewport().Size.Y;
        float screenWidth  = ImGui.GetMainViewport().Size.X;
        float panelWidth   = 300f;
        // Altura dinámica basada en contenido
        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        float padding = ImGui.GetStyle().WindowPadding.Y * 2f;
        float panelHeight = padding + 25f + 6f + (ImGui.GetFrameHeightWithSpacing() * 4f) + 10f + 40f + 10f;

        ImGui.SetNextWindowPos(
            viewport.Position + new float2(screenWidth - panelWidth - 20f, screenHeight - panelHeight - 20f),
            ImGuiCond.Always);
        ImGui.SetNextWindowSize(new float2(panelWidth, panelHeight), ImGuiCond.Always);

        ImGui.SetNextWindowPos(
            viewport.Position + new float2(screenWidth - panelWidth - 20f, screenHeight - panelHeight - 20f),
            ImGuiCond.Always);
        ImGui.SetNextWindowSize(new float2(panelWidth, panelHeight), ImGuiCond.Always);

        PushDarkTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(10f, 10f));

        ImGui.Begin("##LaunchPanel", flags);

        ImGui.PushStyleColor(ImGuiCol.Text, HEADER_COLOR);
        ImGui.Text("LAUNCH"u8);
        ImGui.PopStyleColor();
        ImGui.Separator();
        ImGui.Dummy(new float2(0f, 4f));

        var celestialField  = typeof(VehicleEditor).GetField("_selectedCelestial", BindingFlags.NonPublic | BindingFlags.Instance);
        var locationField   = typeof(VehicleEditor).GetField("_selectedLocation",  BindingFlags.NonPublic | BindingFlags.Instance);
        var celestialsField = typeof(VehicleEditor).GetField("_celestialObjects",  BindingFlags.NonPublic | BindingFlags.Instance);
        var locationsField  = typeof(VehicleEditor).GetField("_locationObjects",   BindingFlags.NonPublic | BindingFlags.Instance);

        var selectedCelestial = celestialField?.GetValue(editor) is CelestialObject c ? c : default;
        var selectedLocation  = locationField?.GetValue(editor)  is LocationObject  l ? l : default;
        var celestials        = celestialsField?.GetValue(editor) as List<CelestialObject>;
        var locations         = locationsField?.GetValue(editor)  as List<LocationObject>;

        if (ImGui.BeginTable("##launchTable", 2, ImGuiTableFlags.None))
        {
            ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("##field", ImGuiTableColumnFlags.WidthStretch);

            // Vehicle Name
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text("Name"u8);
            ImGui.TableNextColumn();
            ImGui.PushItemWidth(-1f);
            ImInputString nameBuffer = new ImInputString(128);
            nameBuffer.SetValue(editor.EditingSpace.Id.AsSpan());
            ImGui.InputText("##vehicleName"u8, nameBuffer, ImGuiInputTextFlags.None, null, 0);
            if (ImGui.IsItemActive()) editor.IsUserTyping = true;
            editor.EditingSpace.Id = nameBuffer.ToString();
            ImGui.PopItemWidth();

            // Launch Body
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
                            selectedCelestial = cel;
                            celestialField?.SetValue(editor, selectedCelestial);
                            // Actualizamos las locations para el nuevo celestial
                            typeof(VehicleEditor)
                                .GetMethod("SetLocations", BindingFlags.NonPublic | BindingFlags.Instance)
                                ?.Invoke(editor, null);
                            // Actualizamos el selected location al primero disponible
                            var updatedLocations = locationsField?.GetValue(editor) as List<LocationObject>;
                            if (updatedLocations?.Count > 0)
                                locationField?.SetValue(editor, updatedLocations[0]);
                        }
                    }
                    ImGui.EndCombo();
                }
            }
            ImGui.PopItemWidth();
            // Location
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

        ImGui.PushStyleColor(ImGuiCol.Button,        new float4(0.1f, 0.55f, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new float4(0.1f, 0.75f, 0.3f, 1f));
        if (ImGui.Button("▶  LAUNCH VEHICLE##launch", new float2(ImGui.GetContentRegionAvail().X, 40f)))
        {
            editor.LaunchNewVehicle = true;
            Program.EditorFlag = false;
        }
        ImGui.PopStyleColor(2);

        ImGui.End();
        ImGui.PopStyleVar();
        PopDarkTheme();
    }

    static void DrawStagePanel(VehicleEditor editor, Viewport viewport)
    {
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoTitleBar;

        float screenHeight = ImGui.GetMainViewport().Size.Y;
        float screenWidth  = ImGui.GetMainViewport().Size.X;
        float panelWidth   = 220f;

        const float PARTS_WIDTH   = 360f;
        const float TOOLBAR_WIDTH = 52f;

        ImGui.SetNextWindowPos(
            viewport.Position + new float2(PARTS_WIDTH + TOOLBAR_WIDTH, 40f),
            ImGuiCond.Always);

        int stageCount = editor.EditingSpace.Parts?.StageList.Count ?? 0;
        int totalParts = editor.EditingSpace.Parts != null 
            ? editor.EditingSpace.AllParts.Length 
            : 0;
        float contentHeight = 30f + (stageCount * 36f) + (totalParts * 32f) + 50f;
        float maxHeight = screenHeight - 100f;
        float panelHeight = Math.Min(contentHeight, maxHeight);

        ImGui.SetNextWindowSize(new float2(panelWidth, panelHeight), ImGuiCond.Always);

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
        ImGui.PushID("BB_Sequences");

        ImGui.PushStyleColor(ImGuiCol.Text, HEADER_COLOR);
        ImGui.Text("STAGES"u8);
        ImGui.PopStyleColor();
        ImGui.PushStyleColor(ImGuiCol.Separator, ACCENT_BLUE);
        ImGui.Separator();
        ImGui.PopStyleColor();

        if (editor.EditingSpace.Parts != null)
        {
            var stages = editor.EditingSpace.Parts.StageList;
            var stageListField = typeof(StageList)
                .GetField("_stages", BindingFlags.NonPublic | BindingFlags.Instance);
            var stagesList = stageListField?.GetValue(stages) as System.Collections.IList;

            if (stagesList != null)
            {
                for (int i = stagesList.Count - 1; i >= 0; i--)
                {
                    var stage = stagesList[i];
                    if (stage == null) continue;

                    var numberProp = stage.GetType().GetProperty("Number");
                    int number = numberProp?.GetValue(stage) is int n ? n : i;

                    var allEditorParts = editor.EditingSpace.AllParts;
                    var stageParts = new List<Part>();
                    for (int k = 0; k < allEditorParts.Length; k++)
                    {
                        if (allEditorParts[k].Stage == number)
                            stageParts.Add(allEditorParts[k]);
                    }

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

                    foreach (Part part in stageParts)
                    {
                        string shortName = part.DisplayName.Length > 12
                            ? part.DisplayName.Substring(0, 12)
                            : part.DisplayName;

                        float4 btnColor = part.HighlightedForStage ? ACCENT_BLUE : BG_MID;
                        ImGui.PushStyleColor(ImGuiCol.Button,        btnColor);
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);

                        if (ImGui.Button(shortName + "##stagepart_" + number + "_" + part.InstanceId, new float2(210f, 28f)))
                            part.HighlightedForStage = !part.HighlightedForStage;

                        ImGui.PopStyleColor(2);

                        bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
                        if (hovered)
                        {
                            ImGui.SetTooltip(part.DisplayName);
                            part.Highlighted = true;
                        }
                        else
                        {
                            part.Highlighted = false;
                        }

                        // Right-click popup para mover a otra stage
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

                    ImGui.PushStyleColor(ImGuiCol.Separator, ACCENT_BLUE);
                    ImGui.Separator();
                    ImGui.PopStyleColor();
                }
            }

            // Botón Add Stage
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

        ImGui.PopID(); 
        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    static void DrawSequencePanel(VehicleEditor editor, Viewport viewport)
    {
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoTitleBar;

        bool hasIconFont = !_iconFont.IsNull();

        float screenHeight = ImGui.GetMainViewport().Size.Y;
        float screenWidth  = ImGui.GetMainViewport().Size.X;
        float panelWidth   = 70f;
        float launchHeight = 280f;

        ImGui.SetNextWindowPos(
            viewport.Position + new float2(screenWidth - panelWidth - 20f, 40f),
            ImGuiCond.Always);
        ImGui.SetNextWindowSize(
            new float2(panelWidth, screenHeight - launchHeight - 60f),
            ImGuiCond.Always);

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
        ImGui.PushID("BB_Stages");

        if (editor.EditingSpace.Parts != null)
        {
            var sequences = editor.EditingSpace.Parts.SequenceList;
            var seqListField = typeof(SequenceList)
                .GetField("_sequences", BindingFlags.NonPublic | BindingFlags.Instance);
            var seqList = seqListField?.GetValue(sequences) as List<Sequence>;

            if (seqList != null)
            {
                for (int i = seqList.Count - 1; i >= 0; i--)
                {
                    Sequence seq = seqList[i];
                    int number   = seq.Number;

                    var allParts = editor.EditingSpace.AllParts;
                    var seqParts = new List<Part>();
                    for (int k = 0; k < allParts.Length; k++)
                    {
                        if (allParts[k].Sequence == number && allParts[k].Sequenceable)
                            seqParts.Add(allParts[k]);
                    }

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
                    ImGui.Dummy(new float2(0f, 2f));
                    
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
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        {
                            ImGui.SetTooltip(part.DisplayName);
                            part.Highlighted = true;
                        }
                        else
                        {
                            part.Highlighted = false;
                        }
                    }

                    ImGui.PushStyleColor(ImGuiCol.Separator, ACCENT_BLUE);
                    ImGui.Separator();
                    ImGui.PopStyleColor();
                }
            }

            ImGui.Dummy(new float2(0f, 4f));
            ImGui.PushStyleColor(ImGuiCol.Button,        ACCENT_BLUE);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ACCENT_HOVER);
            
            if (hasIconFont) ImGui.PushFont(_iconFont, GameSettings.GetFontSize());
            if (ImGui.Button("\uf055##addseq", new float2(58f, 36f))) // circle-plus
            {
                Sequence newSeq = new Sequence(editor.EditingSpace.Parts, sequences.Count + 1);
                sequences.Add(newSeq);
                editor.EditingSpace.Parts.RecomputeAllDerivedData();
            }
            if (hasIconFont) ImGui.PopFont();
            ImGui.PopStyleColor(2);
        }

        ImGui.PopID();
        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }
    static string GetFirstTag(PartTemplate part)
    {
        var tagsField = typeof(VehicleEditor)
            .GetField("_editorTags", BindingFlags.NonPublic | BindingFlags.Static);
        List<EditorTag> allTags = tagsField?.GetValue(null) as List<EditorTag> ?? new List<EditorTag>();
        foreach (EditorTag tag in allTags)
        {
            if (tag == EditorTag.All || tag == EditorTag.Hidden) continue;
            if (part.HasEditorTag(tag)) return tag.Tag;
        }
        return "Other";
    }
    
  
}