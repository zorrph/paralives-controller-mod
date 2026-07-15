using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.TextCore;
using TMPro;
using HarmonyLib;

namespace ControllerMod
{
    [BepInPlugin(PluginGuid, "Paralives Controller Mod", PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.zorrph.paralives.controllermod";
        public const string PluginVersion = "1.1.1";

        // The pre-release development GUID - existing config files carry this name and are
        // migrated to the new one on first launch (the GUID names the .cfg file).
        // Older GUIDs whose config files migrate forward automatically, newest first.
        private static readonly string[] LegacyGuids =
        {
            "net.kmarlin.paralives.controllermod", // v1.0.0-v1.1.0
            "com.yourname.controllerfix",          // pre-release builds
        };

        internal static ManualLogSource Log;

        // Set once by KeyBindingPatch.LoadPostfix; used by Update() to gate the D-pad-bound
        // build-tool actions below.
        internal static InputActionAsset Actions;

        private static readonly string[] DPadBuildActions =
        {
            // FloorUp/FloorDown are native game actions we never rebound - they're gamepad-bound
            // to dpad up/down by default and fire regardless of any open UI, stealing D-pad input
            // from popups like the interaction menu (confirmed: dpad-up both failed to navigate
            // the interaction popup AND changed floors in the background at the same time).
            "BuildWall", "PipetteMode", "SledgehammerMode", "ToggleGrid", "FloorUp", "FloorDown",
            // The LB/RB+D-pad combo actions (this mod's bindings) share the same physical D-pad
            // the menu navigation reads, so they get the same window-open gating.
            "Undo", "Redo", "QuickSave", "CenterCameraOnCharacter",
            "PauseTime", "TimeSpeed0", "TimeSpeed1", "TimeSpeed2",
            "DuplicateItem", "LotMode"
        };

        private bool _dpadBuildActionsEnabled = true;

        internal static UnityEngine.GameObject OwnGameObject;

        // BG3-style world cursor: toggled with left-stick click (leftStickPress) by default,
        // configurable below. While active, the right stick moves the reticle instead of the
        // camera (View disabled).
        internal static bool VirtualCursorEnabled;
        internal static Vector2 VirtualCursorPosition;

        private GameObject _cursorVisualGO;
        private RectTransform _cursorVisualRect;
        private static Sprite _cachedReticleSprite;

        // User-rebindable gamepad bindings, persisted to BepInEx/config/com.yourname.controllerfix.cfg.
        // Values are Unity Input System binding paths (e.g. "<Gamepad>/buttonNorth"); two paths
        // joined with '+' become a modifier combo (e.g. "<Gamepad>/leftShoulder+<Gamepad>/select").
        internal static ConfigEntry<string> CfgMenu;
        internal static ConfigEntry<string> CfgCatalog;
        internal static ConfigEntry<string> CfgBuildWall;
        internal static ConfigEntry<string> CfgPipetteMode;
        internal static ConfigEntry<string> CfgSledgehammerMode;
        internal static ConfigEntry<string> CfgToggleGrid;
        internal static ConfigEntry<string> CfgDelete;
        internal static ConfigEntry<string> CfgLargeRotateItem;
        internal static ConfigEntry<string> CfgToggleParaBuild;
        internal static ConfigEntry<string> CfgTownMap;
        internal static ConfigEntry<string> CfgPhotoMode;
        internal static ConfigEntry<string> CfgUndo;
        internal static ConfigEntry<string> CfgRedo;
        internal static ConfigEntry<string> CfgQuickSave;
        internal static ConfigEntry<string> CfgCenterCameraOnCharacter;
        internal static ConfigEntry<string> CfgPauseTime;
        internal static ConfigEntry<string> CfgTimeSpeed0;
        internal static ConfigEntry<string> CfgTimeSpeed1;
        internal static ConfigEntry<string> CfgTimeSpeed2;
        internal static ConfigEntry<string> CfgFamilyTree;
        internal static ConfigEntry<string> CfgCalendar;
        internal static ConfigEntry<string> CfgLotMode;
        internal static ConfigEntry<string> CfgDuplicateItem;
        internal static ConfigEntry<string> CfgNoSnapHold;
        internal static ConfigEntry<string> CfgMoveVerticallyHold;
        internal static ConfigEntry<string> CfgCursorToggleButton;
        internal static ConfigEntry<float> CfgCursorSpeed;

        // Verbose diagnostics (heartbeat, lifecycle watches, nav/combo traces). Off for normal
        // play; flip on when reporting a bug so the log tells the whole story.
        internal static bool DebugLogging;

        private void BindConfig()
        {
            const string bindingsSection = "Gamepad Bindings";
            const string pathHelp = " (Input System binding path; combine two with '+' for a modifier combo)";
            CfgMenu = Config.Bind(bindingsSection, "Menu", "<Gamepad>/start", "Toggle the pause menu" + pathHelp);
            CfgCatalog = Config.Bind(bindingsSection, "Catalog", "<Gamepad>/buttonNorth", "Open/close the build catalog" + pathHelp);
            // Build tools live on RB+D-pad combos, NOT bare D-pad: the game natively binds
            // FloorUp/FloorDown to bare dpad up/down, so bare-dpad tool bindings double-fired
            // (dpad-down switched floors AND opened the sledgehammer). Bare dpad up/down stays
            // the native floor switch; bare dpad left/right is time-speed stepping, handled in
            // code by UpdateTimeStepping() rather than a binding.
            CfgBuildWall = Config.Bind(bindingsSection, "BuildWall", "<Gamepad>/rightShoulder+<Gamepad>/dpad/left", "Wall tool (build mode)" + pathHelp);
            CfgPipetteMode = Config.Bind(bindingsSection, "PipetteMode", "<Gamepad>/rightShoulder+<Gamepad>/dpad/right", "Pipette/eyedropper tool (build mode)" + pathHelp);
            CfgSledgehammerMode = Config.Bind(bindingsSection, "SledgehammerMode", "<Gamepad>/rightShoulder+<Gamepad>/dpad/down", "Sledgehammer/bulldozer tool (build mode)" + pathHelp);
            CfgToggleGrid = Config.Bind(bindingsSection, "ToggleGrid", "<Gamepad>/rightShoulder+<Gamepad>/dpad/up", "Toggle the build grid" + pathHelp);
            CfgDelete = Config.Bind(bindingsSection, "Delete", "<Gamepad>/buttonWest", "Delete/sell the selected object" + pathHelp);
            CfgLargeRotateItem = Config.Bind(bindingsSection, "LargeRotateItem", "<Gamepad>/leftShoulder+<Gamepad>/buttonWest", "90-degree rotate of the selected object" + pathHelp);
            CfgToggleParaBuild = Config.Bind(bindingsSection, "ToggleParaBuild", "<Gamepad>/leftShoulder+<Gamepad>/select", "Toggle between live and build mode" + pathHelp);
            CfgTownMap = Config.Bind(bindingsSection, "TownMap", "<Gamepad>/select", "Open the town map" + pathHelp);
            CfgPhotoMode = Config.Bind(bindingsSection, "PhotoMode", "<Gamepad>/rightStickPress", "Toggle photo mode" + pathHelp);

            // Keyboard-only actions given gamepad combos by this mod (found via the binding
            // audit). LB+D-pad = editing helpers, RB+D-pad = time controls.
            CfgUndo = Config.Bind(bindingsSection, "Undo", "<Gamepad>/leftShoulder+<Gamepad>/dpad/left", "Undo (Ctrl+Z equivalent)" + pathHelp);
            CfgRedo = Config.Bind(bindingsSection, "Redo", "<Gamepad>/leftShoulder+<Gamepad>/dpad/right", "Redo (Ctrl+Y equivalent)" + pathHelp);
            CfgQuickSave = Config.Bind(bindingsSection, "QuickSave", "<Gamepad>/leftShoulder+<Gamepad>/dpad/up", "Quick save (F5 equivalent)" + pathHelp);
            CfgCenterCameraOnCharacter = Config.Bind(bindingsSection, "CenterCameraOnCharacter", "<Gamepad>/leftShoulder+<Gamepad>/dpad/down", "Center camera on the selected character (F equivalent)" + pathHelp);
            // PauseTime/TimeSpeed0-2 have no gamepad bindings by default: D-pad left/right step
            // the time speed down/up (left eventually pauses; right resumes), see
            // UpdateTimeStepping(). Leave these empty unless you want dedicated buttons too.
            CfgPauseTime = Config.Bind(bindingsSection, "PauseTime", "", "Pause/unpause game time (Space equivalent); empty = use D-pad left time stepping" + pathHelp);
            CfgTimeSpeed0 = Config.Bind(bindingsSection, "TimeSpeed0", "", "Time speed 1 (normal); empty = use D-pad left/right time stepping" + pathHelp);
            CfgTimeSpeed1 = Config.Bind(bindingsSection, "TimeSpeed1", "", "Time speed 2 (fast); empty = use D-pad left/right time stepping" + pathHelp);
            CfgTimeSpeed2 = Config.Bind(bindingsSection, "TimeSpeed2", "", "Time speed 3 (fastest); empty = use D-pad left/right time stepping" + pathHelp);
            CfgFamilyTree = Config.Bind(bindingsSection, "FamilyTree", "<Gamepad>/rightShoulder+<Gamepad>/select", "Open the family tree (T equivalent)" + pathHelp);
            CfgCalendar = Config.Bind(bindingsSection, "Calendar", "<Gamepad>/rightShoulder+<Gamepad>/start", "Open the calendar (C equivalent)" + pathHelp);
            CfgLotMode = Config.Bind(bindingsSection, "LotMode", "<Gamepad>/leftShoulder+<Gamepad>/buttonNorth", "Lot mode (L equivalent)" + pathHelp);
            CfgDuplicateItem = Config.Bind(bindingsSection, "DuplicateItem", "<Gamepad>/rightShoulder+<Gamepad>/buttonWest", "Duplicate the selected item (Ctrl+V equivalent)" + pathHelp);
            // Contextual hold-modifiers: these are gamepad control NAMES (not binding paths)
            // because they don't bind to actions - ContextualTriggerModifiers ORs them into the
            // game's InputManager.Alt/.Shift checks, but only while an item is being
            // placed/moved/resized or a wall is being drawn. Trigger zoom is suspended during
            // exactly that window, so LT/RT don't conflict with their normal camera-zoom role.
            CfgNoSnapHold = Config.Bind(bindingsSection, "NoSnapHold", "leftTrigger",
                "Gamepad control held for No Snap (Alt equivalent) while placing/moving an item or drawing a wall (a control name on the gamepad, e.g. leftTrigger); empty = disabled");
            CfgMoveVerticallyHold = Config.Bind(bindingsSection, "MoveVerticallyHold", "rightTrigger",
                "Gamepad control held to Move Vertically / grid-divide (Shift equivalent) while placing/moving an item or drawing a wall (a control name on the gamepad, e.g. rightTrigger); empty = disabled");

            const string cursorSection = "Virtual Cursor";
            CfgCursorToggleButton = Config.Bind(cursorSection, "ToggleButton", "leftStickPress",
                "Gamepad control that toggles the virtual cursor (a control name on the gamepad, e.g. leftStickPress, rightStickPress, select)");
            CfgCursorSpeed = Config.Bind(cursorSection, "SpeedPixelsPerSecond", 1000f,
                "How fast the virtual cursor moves at full stick deflection, in screen pixels per second");

            DebugLogging = Config.Bind("Advanced", "DebugLogging", false,
                "Verbose diagnostic logging (lifecycle watches, navigation/combo traces, heartbeat). Enable when reporting a bug.").Value;

            // Config values persist in the .cfg file across mod updates, so changing a default
            // above does nothing for existing installs. If an entry still holds the exact old
            // default (i.e. the user never customized it), move it to the new default.
            MigrateOldDefault(CfgBuildWall, "<Gamepad>/dpad/left");
            MigrateOldDefault(CfgPipetteMode, "<Gamepad>/dpad/right");
            MigrateOldDefault(CfgSledgehammerMode, "<Gamepad>/dpad/down");
            MigrateOldDefault(CfgToggleGrid, "<Gamepad>/dpad/up");
            MigrateOldDefault(CfgPauseTime, "<Gamepad>/rightShoulder+<Gamepad>/dpad/up");
            MigrateOldDefault(CfgTimeSpeed0, "<Gamepad>/rightShoulder+<Gamepad>/dpad/left");
            MigrateOldDefault(CfgTimeSpeed1, "<Gamepad>/rightShoulder+<Gamepad>/dpad/down");
            MigrateOldDefault(CfgTimeSpeed2, "<Gamepad>/rightShoulder+<Gamepad>/dpad/right");
            // Stick clicks swapped 2026-07: cursor on LS-press feels more natural (per user
            // feedback), photo mode moves to RS-press.
            MigrateOldDefault(CfgPhotoMode, "<Gamepad>/leftStickPress");
            MigrateOldDefault(CfgCursorToggleButton, "rightStickPress");
        }

        // BepInEx names the config file after the plugin GUID and loads it in the
        // BaseUnityPlugin constructor - before Awake. So: copy the legacy file over, then
        // Config.Reload() so the already-constructed ConfigFile picks the values up before
        // BindConfig() reads them.
        private void MigrateLegacyConfigFile()
        {
            try
            {
                string newPath = System.IO.Path.Combine(Paths.ConfigPath, PluginGuid + ".cfg");
                if (System.IO.File.Exists(newPath))
                {
                    return;
                }
                foreach (var legacyGuid in LegacyGuids)
                {
                    string oldPath = System.IO.Path.Combine(Paths.ConfigPath, legacyGuid + ".cfg");
                    if (System.IO.File.Exists(oldPath))
                    {
                        System.IO.File.Copy(oldPath, newPath);
                        Config.Reload();
                        Log.LogInfo($"Controller Fix: Migrated config from '{legacyGuid}.cfg' to '{PluginGuid}.cfg'.");
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"Controller Fix: Legacy config migration failed (defaults will be used): {e.Message}");
            }
        }

        private static void MigrateOldDefault(ConfigEntry<string> entry, string oldDefault)
        {
            if (entry.Value == oldDefault)
            {
                entry.Value = (string)entry.DefaultValue;
                Log.LogInfo($"Controller Fix: Migrated config '{entry.Definition.Key}' from old default '{oldDefault}' to '{entry.Value}'.");
            }
        }

        void Awake()
        {
            Log = Logger;
            OwnGameObject = gameObject;
            MigrateLegacyConfigFile();
            BindConfig();

            // DontDestroyOnLoad alone did NOT stop the destroy (confirmed by testing), which rules
            // out a normal scene-unload wipe - DontDestroyOnLoad should be bulletproof against
            // that. Something is explicitly calling Destroy() on this object/a component on it.
            // Patch Object.Destroy itself (below) to log a stack trace when it targets us.
            try
            {
                DontDestroyOnLoad(gameObject);
                // DontDestroyOnLoad alone did NOT survive (confirmed: object's scene name went
                // from 'DontDestroyOnLoad' to '' by OnDisable, with zero managed Destroy/
                // DestroyImmediate call intercepted - a native-only teardown). BepInEx's own
                // manager GameObject additionally sets HideAndDontSave, which is the stronger
                // flag that actually blocks native scene-unload sweeps. Match that.
                gameObject.hideFlags = HideFlags.HideAndDontSave;
                if (DebugLogging)
                Log.LogInfo("Controller Fix: [Lifecycle] DontDestroyOnLoad call completed without throwing. " +
                    $"scene='{gameObject.scene.name}' buildIndex={gameObject.scene.buildIndex} " +
                    $"parent='{(gameObject.transform.parent == null ? "<none>" : gameObject.transform.parent.name)}' " +
                    $"hideFlags={gameObject.hideFlags}");
            }
            catch (Exception e)
            {
                Log.LogError($"Controller Fix: DontDestroyOnLoad threw: {e.Message}\n{e.StackTrace}");
            }

            var harmony = new Harmony(PluginGuid);

            // Lifecycle watch patches (Destroy/SetActive/SetParent on our own GameObject) exist
            // purely to diagnose the historical "plugin GameObject torn down" issue - patching
            // Object.Destroy globally is not free, so only install them in debug mode.
            if (DebugLogging)
            try
            {
                var destroy1 = AccessTools.Method(typeof(UnityEngine.Object), "Destroy", new[] { typeof(UnityEngine.Object) });
                var destroy2 = AccessTools.Method(typeof(UnityEngine.Object), "Destroy", new[] { typeof(UnityEngine.Object), typeof(float) });
                var destroyPrefix = AccessTools.Method(typeof(DestroyWatchPatch), nameof(DestroyWatchPatch.Prefix));
                harmony.Patch(destroy1, prefix: new HarmonyMethod(destroyPrefix));
                harmony.Patch(destroy2, prefix: new HarmonyMethod(destroyPrefix));
                Log.LogInfo("Controller Fix: Watching Object.Destroy for calls targeting our own GameObject.");

                // Destroy() never fired our watch on the previous test, so also cover
                // DestroyImmediate - Unity's editor/immediate-mode destroy path, which some
                // engine-internal and "scene sanitizing" code paths use instead of Destroy().
                var destroyImmediate1 = AccessTools.Method(typeof(UnityEngine.Object), "DestroyImmediate", new[] { typeof(UnityEngine.Object) });
                var destroyImmediate2 = AccessTools.Method(typeof(UnityEngine.Object), "DestroyImmediate", new[] { typeof(UnityEngine.Object), typeof(bool) });
                harmony.Patch(destroyImmediate1, prefix: new HarmonyMethod(destroyPrefix));
                harmony.Patch(destroyImmediate2, prefix: new HarmonyMethod(destroyPrefix));
                Log.LogInfo("Controller Fix: Watching Object.DestroyImmediate for calls targeting our own GameObject.");

                // Also watch SetActive(false) on our own GameObject - this alone explains
                // OnDisable but not OnDestroy, so if we see this WITHOUT a matching Destroy/
                // DestroyImmediate log, the destroy is happening through a native engine path
                // (e.g. scene unload) that never calls a managed Object method at all.
                var setActiveMethod = AccessTools.Method(typeof(UnityEngine.GameObject), nameof(UnityEngine.GameObject.SetActive));
                var setActivePrefix = AccessTools.Method(typeof(SetActiveWatchPatch), nameof(SetActiveWatchPatch.Prefix));
                harmony.Patch(setActiveMethod, prefix: new HarmonyMethod(setActivePrefix));
                Log.LogInfo("Controller Fix: Watching GameObject.SetActive for calls targeting our own GameObject.");

                // If nothing reparents us, DontDestroyOnLoad should be bulletproof against scene
                // unload. If something calls SetParent on our transform (or a parent of it) after
                // Awake, Unity silently pulls us back into a regular scene - no exception, no log -
                // undoing DontDestroyOnLoad's protection. Watch for that specifically.
                var setParentMethod = AccessTools.Method(typeof(UnityEngine.Transform), nameof(UnityEngine.Transform.SetParent), new[] { typeof(UnityEngine.Transform) });
                var setParentPrefix = AccessTools.Method(typeof(SetParentWatchPatch), nameof(SetParentWatchPatch.Prefix));
                harmony.Patch(setParentMethod, prefix: new HarmonyMethod(setParentPrefix));
                Log.LogInfo("Controller Fix: Watching Transform.SetParent for calls targeting our own GameObject.");
            }
            catch (Exception e)
            {
                Log.LogError($"Controller Fix: Failed to patch Object.Destroy/DestroyImmediate/SetActive/SetParent: {e.Message}\n{e.StackTrace}");
            }

            if (DebugLogging)
            {
                try
                {
                    foreach (var device in InputSystem.devices)
                    {
                        Log.LogInfo($"Controller Fix: Input device detected: '{device.displayName}' (layout={device.layout}, type={device.GetType().Name})");
                    }
                }
                catch (Exception e)
                {
                    Log.LogError($"Controller Fix: Device enumeration failed: {e.Message}");
                }
            }

            try
            {
                var type = AccessTools.TypeByName("Setting.KeyBindings");
                if (type != null)
                {
                    var targetMethod = AccessTools.Method(type, "LoadAndApplyKeyRebindings");
                    if (targetMethod != null)
                    {
                        var postfix = AccessTools.Method(typeof(KeyBindingPatch), nameof(KeyBindingPatch.LoadPostfix));
                        harmony.Patch(targetMethod, postfix: new HarmonyMethod(postfix));
                        Log.LogInfo("Controller Fix: Surgical strike hook deployed on LoadAndApplyKeyRebindings.");
                    }
                    else
                    {
                        Log.LogError("Controller Fix: Target method 'LoadAndApplyKeyRebindings' missing!");
                    }
                }
                else
                {
                    Log.LogError("Controller Fix: Target class 'Setting.KeyBindings' not found.");
                }

                // Decompiled Back.cs: pressing Cancel (Escape/B) with nothing else to back out
                // of falls through to UI.Get<UIEscapeMenu>(playerIndex).Show(). Escape and B
                // share the single "Cancel" action, so this is what makes B open the pause menu.
                // Patch the shared UIContainer.Show() (UIEscapeMenu has no override of its own)
                // and block it specifically when the trigger this frame was a gamepad B press.
                var showMethod = AccessTools.Method(typeof(UIContainer), nameof(UIContainer.Show));
                var showPrefix = AccessTools.Method(typeof(EscapeMenuGuardPatch), nameof(EscapeMenuGuardPatch.Prefix));
                harmony.Patch(showMethod, prefix: new HarmonyMethod(showPrefix));
                Log.LogInfo("Controller Fix: Guarded UIEscapeMenu.Show() against gamepad B triggers.");

                // Diagnostic only: log what NavigateMenu sees each time D-pad is pressed, so a
                // bug report log can show whether nav is never called, called with no
                // CurrentWindow, or called with a window that has no registered Focusables.
                if (DebugLogging)
                {
                    var navigateMethod = AccessTools.Method(typeof(UIManager), nameof(UIManager.NavigateMenu));
                    var navigatePrefix = AccessTools.Method(typeof(MenuNavDiagnosticPatch), nameof(MenuNavDiagnosticPatch.Prefix));
                    harmony.Patch(navigateMethod, prefix: new HarmonyMethod(navigatePrefix));
                    Log.LogInfo("Controller Fix: Attached menu-navigation diagnostics to UIManager.NavigateMenu.");
                }

                // Navigation itself works (confirmed via NavDiag logs), but ParaButton.OnFocusGained
                // only plays a sound and fires a UnityEvent that isn't wired to any visual change in
                // this menu's prefab - so focus moves with no visible indicator. Add a generic Outline
                // highlight on focus gain/loss so any focused ParaButton is visibly marked, regardless
                // of what its own (possibly empty) ButtonFocused event does.
                var gainedMethod = AccessTools.Method(typeof(ParaButton), "OnFocusGained");
                var gainedPostfix = AccessTools.Method(typeof(ButtonFocusHighlightPatch), nameof(ButtonFocusHighlightPatch.GainedPostfix));
                harmony.Patch(gainedMethod, postfix: new HarmonyMethod(gainedPostfix));

                var lostMethod = AccessTools.Method(typeof(ParaButton), "OnFocusLost");
                var lostPostfix = AccessTools.Method(typeof(ButtonFocusHighlightPatch), nameof(ButtonFocusHighlightPatch.LostPostfix));
                harmony.Patch(lostMethod, postfix: new HarmonyMethod(lostPostfix));

                Log.LogInfo("Controller Fix: Added visible focus-highlight outline to ParaButton.");

                // Make gamepad focus reuse whatever IsMouseHovered visual (e.g. the main menu's
                // blue underline) is already configured on a button, for exact visual parity
                // instead of a second, different-looking highlight style.
                var isStateActiveMethod = AccessTools.PropertyGetter(typeof(ParaButtonAnimationBase), nameof(ParaButtonAnimationBase.IsStateActive));
                var isStateActivePostfix = AccessTools.Method(typeof(GamepadFocusVisualParityPatch), nameof(GamepadFocusVisualParityPatch.Postfix));
                harmony.Patch(isStateActiveMethod, postfix: new HarmonyMethod(isStateActivePostfix));
                Log.LogInfo("Controller Fix: Gamepad focus now reuses existing hover visuals.");

                // Same visual-feedback gap as ParaButton, but for stock Selectable controls
                // (sliders/toggles/input fields): selection works, but nothing renders it.
                var selOnSelect = AccessTools.Method(typeof(Selectable), "OnSelect", new[] { typeof(BaseEventData) });
                var selSelectPostfix = AccessTools.Method(typeof(SelectableFocusHighlightPatch), nameof(SelectableFocusHighlightPatch.SelectPostfix));
                harmony.Patch(selOnSelect, postfix: new HarmonyMethod(selSelectPostfix));

                var selOnDeselect = AccessTools.Method(typeof(Selectable), "OnDeselect", new[] { typeof(BaseEventData) });
                var selDeselectPostfix = AccessTools.Method(typeof(SelectableFocusHighlightPatch), nameof(SelectableFocusHighlightPatch.DeselectPostfix));
                harmony.Patch(selOnDeselect, postfix: new HarmonyMethod(selDeselectPostfix));
                Log.LogInfo("Controller Fix: Added visible focus-highlight outline to Selectable controls.");

                // UIInteractionsList (the whole interaction-menu panel/box) is itself a Focusable
                // (UIListItemBase : ParaToggle : ParaButton : Focusable) purely because it reuses
                // UIListItemBase for UIList's pooling API - it never overrides OnClick/focus
                // behavior. But it still registers to the same FocusableGroup as each individual
                // UIInteractionsListItem row inside it, so D-pad navigation randomly lands on the
                // whole box instead of a row (confirmed: user reported focus flipping between one
                // list item and the entire container). Unregister it right after it registers so
                // only the actual rows are ever navigable.
                var focusableOnEnable = AccessTools.Method(typeof(Focusable), "OnEnable");
                var skipContainerPostfix = AccessTools.Method(typeof(SkipListContainerFocusRegistrationPatch), nameof(SkipListContainerFocusRegistrationPatch.Postfix));
                harmony.Patch(focusableOnEnable, postfix: new HarmonyMethod(skipContainerPostfix));
                Log.LogInfo("Controller Fix: Excluded UIInteractionsList container from gamepad focus navigation.");

                // Gamepad A-presses on a focused element can be swallowed by Focusable.ClickUp's
                // hover check after the (virtual or real) mouse has ever hovered that element -
                // see GamepadFocusClickPatch for the _justEnabled mechanics.
                var clickUpMethod = AccessTools.Method(typeof(Focusable), nameof(Focusable.ClickUp));
                var clickUpPrefix = AccessTools.Method(typeof(GamepadFocusClickPatch), nameof(GamepadFocusClickPatch.Prefix));
                harmony.Patch(clickUpMethod, prefix: new HarmonyMethod(clickUpPrefix));
                Log.LogInfo("Controller Fix: Gamepad A-clicks on focused elements no longer blocked by stale hover state.");

                // InputManager.GetCursorPosition is the single choke point nearly every world
                // interaction system reads through (hover detection, item placement/rotation,
                // build-mode widgets, terrain tools, etc.) - it's hardcoded to ScreenCenterPosition
                // for gamepad, which is why players had to pan the camera to "aim". Override it
                // with our own right-stick-driven virtual cursor position instead.
                var getCursorPositionMethod = AccessTools.Method(typeof(InputManager), nameof(InputManager.GetCursorPosition));
                var getCursorPositionPostfix = AccessTools.Method(typeof(VirtualCursorPositionPatch), nameof(VirtualCursorPositionPatch.Postfix));
                harmony.Patch(getCursorPositionMethod, postfix: new HarmonyMethod(getCursorPositionPostfix));

                // With the virtual cursor off and a menu open, A-presses on focused menu items
                // also fired invisible world clicks at screen center - see
                // SuppressWorldClickInMenusPatch for the mechanics.
                var isPointerOverUIMethod = AccessTools.Method(typeof(InputManager), nameof(InputManager.IsPointerOverUI));
                var suppressWorldClickPostfix = AccessTools.Method(typeof(SuppressWorldClickInMenusPatch), nameof(SuppressWorldClickInMenusPatch.Postfix));
                harmony.Patch(isPointerOverUIMethod, postfix: new HarmonyMethod(suppressWorldClickPostfix));
                Log.LogInfo("Controller Fix: World clicks suppressed while a menu is open and the virtual cursor is off.");

                // CursorManager.LateUpdate unconditionally forces Cursor.visible = true every
                // frame, which would show the real OS pointer stuck wherever the physical mouse
                // last was, on top of our reticle. Force it off while our cursor is active.
                var cursorManagerLateUpdate = AccessTools.Method(typeof(CursorManager), "LateUpdate");
                var cursorManagerPrefix = AccessTools.Method(typeof(HideOSCursorPatch), nameof(HideOSCursorPatch.Prefix));
                harmony.Patch(cursorManagerLateUpdate, prefix: new HarmonyMethod(cursorManagerPrefix));

                Log.LogInfo("Controller Fix: Installed BG3-style virtual world cursor (toggle: left stick click).");

                // TranslationManager embeds keybinding hints into tooltip text via /{ActionName}
                // placeholders, resolved through KeyRebindingManager.GetBindingIndex(action,
                // isGamepad, ...). Many actions referenced this way were apparently only ever
                // authored/tested for keyboard and have no gamepad binding at all, so the gamepad
                // lookup returns -1 and the raw fallback text "*CANNOT FIND INPUT ACTION X*"
                // leaks straight into tooltips - something mouse+keyboard players would never see
                // (their lookup already succeeds) and gamepad players could never see either,
                // before this mod, since they couldn't freely hover items to trigger these
                // tooltips in the first place. Patch the single choke point: if the gamepad
                // lookup for an action fails, fall back to its keyboard binding instead of
                // leaving the placeholder text on screen.
                var getBindingIndexMethod = AccessTools.Method(typeof(KeyRebindingManager), nameof(KeyRebindingManager.GetBindingIndex));
                var getBindingIndexPostfix = AccessTools.Method(typeof(GamepadBindingFallbackPatch), nameof(GamepadBindingFallbackPatch.Postfix));
                harmony.Patch(getBindingIndexMethod, postfix: new HarmonyMethod(getBindingIndexPostfix));
                Log.LogInfo("Controller Fix: Added keyboard-binding fallback for tooltips with no gamepad binding.");

                // Y (the Catalog action) opens the build catalog but nothing dedicated closes it -
                // SetPlayerCatalogueModeEvent only ever shows it. Make Y a toggle on gamepad: if
                // the catalog is already open (player is in a build-ish state), route to
                // MessageTogglePlayerMainMode, which is the game's own live<->build switch, so
                // closing behaves exactly like clicking the Live Mode button. Keyboard behavior
                // is untouched (the prefix only diverts when the player is on gamepad).
                var catalogueModeMethod = AccessTools.Method(typeof(SetPlayerCatalogueModeEvent), "UpdateMessage");
                var catalogueTogglePrefix = AccessTools.Method(typeof(CatalogToggleParityPatch), nameof(CatalogToggleParityPatch.Prefix));
                harmony.Patch(catalogueModeMethod, prefix: new HarmonyMethod(catalogueTogglePrefix));
                Log.LogInfo("Controller Fix: Y now toggles the build catalog open/closed on gamepad.");

                // The bottom keybinding-tips bar (UIKeyBindingTipsItem.Init) hardcodes
                // isGamepad: false in its GetBindingIndex call, so it always renders keyboard
                // keys even mid-gamepad-session. Take over rendering when the player is on
                // gamepad and show the gamepad binding as a text label. (Text for now - the game
                // ships only a KeyboardAndMouseSprites TMP atlas, no controller glyph art, so
                // proper Xbox glyphs need an embedded CC0 sprite sheet as a follow-up step.)
                var tipsInitMethod = AccessTools.Method(typeof(UIKeyBindingTipsItem), nameof(UIKeyBindingTipsItem.Init));
                var tipsInitPrefix = AccessTools.Method(typeof(GamepadKeyBindingTipsPatch), nameof(GamepadKeyBindingTipsPatch.Prefix));
                harmony.Patch(tipsInitMethod, prefix: new HarmonyMethod(tipsInitPrefix));
                Log.LogInfo("Controller Fix: Keybinding tips bar now shows gamepad bindings when on controller.");

                // Tooltip /{ActionName} placeholders render Xbox glyph sprites on gamepad
                // instead of plain binding text (embedded Kenney CC0 atlas, see XboxGlyphs).
                var injectionMethod = AccessTools.Method(typeof(TranslationManager), "ActionInputKeyInjection");
                var injectionPrefix = AccessTools.Method(typeof(GamepadTooltipGlyphPatch), nameof(GamepadTooltipGlyphPatch.Prefix));
                harmony.Patch(injectionMethod, prefix: new HarmonyMethod(injectionPrefix));
                Log.LogInfo("Controller Fix: Tooltips now show Xbox button glyphs on controller.");

                // "No Snap" (Alt) / "Move Vertically" (Shift) on the triggers, contextually -
                // see ContextualTriggerModifiers for why the zoom suppression is scoped the way
                // it is.
                var altGetter = AccessTools.PropertyGetter(typeof(InputManager), "Alt");
                var shiftGetter = AccessTools.PropertyGetter(typeof(InputManager), "Shift");
                var onZoomMethod = AccessTools.Method(typeof(HybridPlayer), "OnZoom");
                harmony.Patch(altGetter, postfix: new HarmonyMethod(AccessTools.Method(typeof(ContextualTriggerModifiers), nameof(ContextualTriggerModifiers.AltPostfix))));
                harmony.Patch(shiftGetter, postfix: new HarmonyMethod(AccessTools.Method(typeof(ContextualTriggerModifiers), nameof(ContextualTriggerModifiers.ShiftPostfix))));
                harmony.Patch(onZoomMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(ContextualTriggerModifiers), nameof(ContextualTriggerModifiers.OnZoomPrefix))));
                Log.LogInfo("Controller Fix: LT = No Snap, RT = Move Vertically while placing items or drawing walls (trigger zoom suspended only then).");

                // Options menu: left/right on a focused settings slider adjusts its value
                // instead of navigating (works with UpdateOptionsFieldNavigation, which makes
                // the slider rows focusable in the first place).
                var groupNavigateMethod = AccessTools.Method(typeof(FocusableGroup), "Navigate");
                var sliderAdjustPrefix = AccessTools.Method(typeof(SliderAdjustNavigatePatch), nameof(SliderAdjustNavigatePatch.Prefix));
                harmony.Patch(groupNavigateMethod, prefix: new HarmonyMethod(sliderAdjustPrefix));
                Log.LogInfo("Controller Fix: Settings sliders are D-pad navigable and adjustable (left/right changes the value).");

                // While an item/wall is being carried the catalog window is still "open" but the
                // player is working in the world - stop menu nav from eating the D-pad and A.
                var uiNavigateMenuMethod = AccessTools.Method(typeof(UIManager), nameof(UIManager.NavigateMenu));
                var placingNavPrefix = AccessTools.Method(typeof(MenuNavSuppressedWhilePlacingPatch), nameof(MenuNavSuppressedWhilePlacingPatch.Prefix));
                harmony.Patch(uiNavigateMenuMethod, prefix: new HarmonyMethod(placingNavPrefix));
                Log.LogInfo("Controller Fix: Menu navigation suspended while carrying an item (D-pad rotates/switches floors, A places).");
            }
            catch (Exception e)
            {
                Log.LogError($"Controller Fix: Hook deployment failure: {e.Message}");
            }
        }

        void OnEnable()
        {
            if (!DebugLogging) return;
            Log?.LogInfo("Controller Fix: [Lifecycle] OnEnable called. " +
                $"scene='{gameObject.scene.name}' buildIndex={gameObject.scene.buildIndex} " +
                $"parent='{(gameObject.transform.parent == null ? "<none>" : gameObject.transform.parent.name)}' " +
                $"hideFlags={gameObject.hideFlags} activeInHierarchy={gameObject.activeInHierarchy}");
        }

        void OnDisable()
        {
            if (!DebugLogging) return;
            // gameObject is still valid here (this fires just before OnDestroy), so this is our
            // last chance to see what scene/parent it was in at the moment it got torn down.
            string sceneInfo;
            try
            {
                sceneInfo = $"scene='{gameObject.scene.name}' buildIndex={gameObject.scene.buildIndex} " +
                    $"parent='{(gameObject.transform.parent == null ? "<none>" : gameObject.transform.parent.name)}' " +
                    $"hideFlags={gameObject.hideFlags}";
            }
            catch (Exception e)
            {
                sceneInfo = $"<failed to read gameObject state: {e.Message}>";
            }
            Log?.LogInfo("Controller Fix: [Lifecycle] OnDisable called. " + sceneInfo + "\nStack:\n" + Environment.StackTrace);
        }

        void OnDestroy()
        {
            if (!DebugLogging) return;
            Log?.LogInfo("Controller Fix: [Lifecycle] OnDestroy called.\nStack:\n" + Environment.StackTrace);
        }

        void Start()
        {
            if (!DebugLogging) return;
            Log?.LogInfo("Controller Fix: [Lifecycle] Start called.");
        }

        // UIManager.NavigateMenu is only ever called from Back.cs's Update(), which loops over
        // PlayerManager.Instance.Players (household members) - at the main menu, before any save
        // is loaded, that list is empty, so NavigateMenu (and any Harmony patch on it) never runs
        // at all. Drive the main menu's own navigation independently every frame instead.
        private static string _lastMainMenuDiagState;

        private int _heartbeatFrameCount;

        void Update()
        {
            _heartbeatFrameCount++;
            if (DebugLogging && (_heartbeatFrameCount <= 5 || _heartbeatFrameCount % 180 == 0))
            {
                Log.LogInfo($"Controller Fix: [Heartbeat] Update() is running, frame {_heartbeatFrameCount}.");
            }

            // Each of these is independent and must run every frame regardless of what the
            // others do - they used to live in one try block where an early `return` (e.g. "not
            // in the main menu") skipped DPad gating/cursor updates entirely for the rest of the
            // frame, including during real gameplay. Keep them as separate top-level calls.
            UpdateMainMenuNavigation();
            UpdateDPadBuildActionGating();
            UpdateTimeStepping();
            UpdateCatalogScrollIntoView();
            UpdateOptionsFieldNavigation();
            UpdateVirtualCursor();
            ContextualTriggerModifiers.SuppressStaleZoom();
        }

        // D-pad left/right = step the game speed down/up, BG3/console-sim style:
        // pause <- normal <- fast <- fastest (left) and the reverse (right). Driven from code
        // rather than bindings because "one step slower/faster" needs the current speed, which
        // a static InputAction binding can't express. Uses the game's own message events
        // (MessageTogglePause / MessageSetTimeSpeed*) so audio cues and the UITime widget
        // behave exactly as if the keyboard shortcuts were pressed.
        private void UpdateTimeStepping()
        {
            try
            {
                var gamepad = Gamepad.current;
                if (gamepad == null || PlayerManager.Instance == null || SystemManager.Instance == null)
                {
                    return;
                }
                if (SavedGameManager.Instance == null || !SavedGameManager.Instance.HasLoadedSavedGame)
                {
                    return;
                }
                var hybridPlayer = PlayerManager.Instance.HybridPlayer1;
                if (hybridPlayer == null || !hybridPlayer.IsUsingGamePad)
                {
                    return;
                }
                // While a shoulder is held the D-pad press belongs to a combo binding
                // (LB+dpad = Undo/Redo/etc., RB+dpad = build tools), never to rotate/time.
                if (gamepad.leftShoulder.isPressed || gamepad.rightShoulder.isPressed)
                {
                    return;
                }

                bool stepSlower = gamepad.dpad.left.wasPressedThisFrame;
                bool stepFaster = gamepad.dpad.right.wasPressedThisFrame;
                if (!stepSlower && !stepFaster)
                {
                    return;
                }

                // D-pad left/right rotates instead of stepping time while an item is carried
                // (menu nav is suspended then, so the D-pad is free even with the catalog open)
                // or selected with no menu open. The game never wired ANY gamepad control to
                // SmallRotateItem - the shoulders only ever meant "switch menu tab" - and bare
                // LB/RB bindings would mis-fire a 45 whenever an LB+X 90 rotate starts.
                bool windowOpen = IsAnyBlockingWindowOpen(hybridPlayer);
                try
                {
                    var player = hybridPlayer.Player;
                    bool holdingItem = player?.ItemInPlacement != null;
                    bool selectedItem = player?.ItemSelected != null && !windowOpen;
                    if (holdingItem || selectedItem)
                    {
                        SystemManager.Instance.RegisterMessage(new MessageRotateItem
                        {
                            RotateLeft = stepSlower,
                            DoBigRotation = false,
                            PlayerIndex = player.PlayerIndex
                        });
                        return;
                    }
                }
                catch
                {
                    return; // Player not ready - don't fall through into time stepping blind
                }

                // While a menu is open the D-pad navigates it, not the clock.
                if (windowOpen)
                {
                    return;
                }

                // Mirror the game's own guard in UpdateKeyboardShortcuts: no time control while
                // something holds a pause override (cutscenes/storyboards).
                try
                {
                    if (hybridPlayer.Player != null && hybridPlayer.Player.HasPauseOverride())
                    {
                        return;
                    }
                }
                catch
                {
                    // Player not ready - skip this frame rather than risk fighting an override.
                    return;
                }

                if (stepSlower)
                {
                    if (ParaTime.IsPausedByPlayer)
                    {
                        return; // already at the bottom of the ladder
                    }
                    switch (ParaTime.TimeSpeedIndex)
                    {
                        case 0:
                            SystemManager.Instance.RegisterMessage(new MessageTogglePause());
                            break;
                        case 1:
                            SystemManager.Instance.RegisterMessage(new MessageSetTimeSpeedNormal());
                            break;
                        default:
                            SystemManager.Instance.RegisterMessage(new MessageSetTimeSpeedFast());
                            break;
                    }
                }
                else
                {
                    if (ParaTime.IsPausedByPlayer)
                    {
                        // SetTimeSpeed(0) also clears IsPausedByPlayer, so this both unpauses
                        // and lands on normal speed - the first rung above pause.
                        SystemManager.Instance.RegisterMessage(new MessageSetTimeSpeedNormal());
                        return;
                    }
                    switch (ParaTime.TimeSpeedIndex)
                    {
                        case 0:
                            SystemManager.Instance.RegisterMessage(new MessageSetTimeSpeedFast());
                            break;
                        case 1:
                            SystemManager.Instance.RegisterMessage(new MessageSetTimeSpeedFastest());
                            break;
                        // already at fastest - nothing above
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogError($"Controller Fix: Time stepping failed: {e.Message}\n{e.StackTrace}");
            }
        }

        // When D-pad focus lands on a catalog item that's scrolled out of the grid's viewport,
        // nothing scrolls it into sight (the game's FocusableGroup nav has no ScrollRect
        // awareness). Nudge the ScrollRect content the minimum distance to reveal the item.
        private Focusable _lastScrolledCatalogFocus;

        private void UpdateCatalogScrollIntoView()
        {
            try
            {
                var commonWindow = HybridReferences.Instance?.UIManagerCommon?.CurrentWindow;
                var perPlayerWindow = PlayerManager.Instance?.HybridPlayer1?.UIManager?.CurrentWindow;
                var catalog = (commonWindow as UIBuildModeCatalog) ?? (perPlayerWindow as UIBuildModeCatalog);
                if (catalog == null)
                {
                    _lastScrolledCatalogFocus = null;
                    return;
                }

                var focused = catalog.GetFocused(false);
                if (focused == null || ReferenceEquals(focused, _lastScrolledCatalogFocus))
                {
                    return;
                }
                _lastScrolledCatalogFocus = focused;
                if (!(focused is UIBuildModeItem))
                {
                    return;
                }

                var scroll = focused.GetComponentInParent<ScrollRect>();
                if (scroll == null || scroll.content == null)
                {
                    return;
                }
                var viewport = scroll.viewport != null ? scroll.viewport : scroll.GetComponent<RectTransform>();
                var vpCorners = new Vector3[4];
                viewport.GetWorldCorners(vpCorners);
                var itemCorners = new Vector3[4];
                ((RectTransform)focused.transform).GetWorldCorners(itemCorners);

                float aboveBy = itemCorners[1].y - vpCorners[1].y; // item pokes out the top
                float belowBy = vpCorners[0].y - itemCorners[0].y; // item pokes out the bottom
                if (aboveBy > 0f)
                {
                    scroll.content.position -= new Vector3(0f, aboveBy, 0f);
                }
                else if (belowBy > 0f)
                {
                    scroll.content.position += new Vector3(0f, belowBy, 0f);
                }
            }
            catch (Exception e)
            {
                Log.LogError($"Controller Fix: Catalog scroll-into-view failed: {e.Message}");
            }
        }

        // The options screen's right panel is inspector-generated: each row is a pooled
        // UIFieldGameObject whose widgets are stock Unity UI (Slider, Toggle, TMP_InputField),
        // not the game's Focusable system, so gamepad focus could never reach a slider. Fix, per
        // frame while the options window is open:
        //  1. Give every visible slider row a bare Focusable (all its virtuals are no-ops, so
        //     it's inert except for participating in focus/nav).
        //  2. Re-home every Focusable under the inspector (slider rows + the ParaButton/
        //     ParaToggle rows the prefabs already carry) to the window's own FocusableGroup -
        //     same nested-group trap as the build catalog.
        //  3. Drive the row's ImageHighlighted as the focus visual for slider rows (bare
        //     Focusables have no visuals; the game only uses ImageHighlighted in the mod-editor
        //     split view, never in the options menu).
        //  4. Scroll the focused row into the inspector's ScrollRect viewport.
        // Left/right value adjustment lives in SliderAdjustNavigatePatch.
        private Focusable _lastScrolledOptionsFocus;

        private void UpdateOptionsFieldNavigation()
        {
            try
            {
                var commonWindow = HybridReferences.Instance?.UIManagerCommon?.CurrentWindow;
                var perPlayerWindow = PlayerManager.Instance?.HybridPlayer1?.UIManager?.CurrentWindow;
                var options = (commonWindow as UIOptions) ?? (perPlayerWindow as UIOptions);
                if (options == null || options.UIInspector == null)
                {
                    _lastScrolledOptionsFocus = null;
                    return;
                }

                FocusableGroup windowGroup = options;
                var fieldGOs = options.UIInspector.GetComponentsInChildren<UIFieldGameObject>(includeInactive: false);
                var focused = windowGroup.GetFocused(isShoulderButton: false);
                foreach (var ufg in fieldGOs)
                {
                    bool isSliderRow = ufg.Slider != null && ufg.Slider.gameObject.activeInHierarchy
                        && ufg.UIField is UIFieldSlider;
                    var focusable = ufg.GetComponent<Focusable>();
                    if (isSliderRow)
                    {
                        if (focusable == null)
                        {
                            focusable = ufg.gameObject.AddComponent<Focusable>();
                        }
                        else if (!focusable.enabled)
                        {
                            focusable.enabled = true;
                        }
                    }
                    else if (focusable != null && focusable.enabled && focusable.GetType() == typeof(Focusable))
                    {
                        // Pooled row got reused for a non-slider field: retire our Focusable so
                        // focus can't land on an invisible row (disable unregisters it).
                        focusable.enabled = false;
                    }

                    // Row-level focus visual for EVERY row type: bare Focusables (sliders) have
                    // no visuals at all, and the toggle prefabs have no gamepad-focus animation
                    // either, so without this a focused toggle row looked like the D-pad press
                    // did nothing.
                    if (ufg.ImageHighlighted != null)
                    {
                        bool highlight = focused != null && focused.transform.IsChildOf(ufg.transform);
                        if (ufg.ImageHighlighted.activeSelf != highlight)
                        {
                            ufg.ImageHighlighted.SetActive(highlight);
                        }
                    }
                }

                // Re-home: Focusable.OnEnable registers to the NEAREST group ancestor, which for
                // inspector rows may be a nested group nothing ever drives. Move strays into the
                // window's group so the game's own nav includes them. (Rows re-register to the
                // nearest group every time pooling re-enables them, hence per-frame.)
                foreach (var f in options.UIInspector.GetComponentsInChildren<Focusable>(includeInactive: false))
                {
                    var groupField = Traverse.Create(f).Field("_focusableGroup");
                    var group = groupField.GetValue<FocusableGroup>();
                    if (!ReferenceEquals(group, windowGroup))
                    {
                        group?.UnregisterFocusable(f);
                        windowGroup.RegistrerFocusable(f); // sic - the game misspells it
                        groupField.SetValue(windowGroup);
                    }
                }

                // Scroll the focused row into view, once per focus change.
                if (focused == null || ReferenceEquals(focused, _lastScrolledOptionsFocus))
                {
                    return;
                }
                _lastScrolledOptionsFocus = focused;
                if (!focused.transform.IsChildOf(options.UIInspector.transform))
                {
                    return;
                }
                var scroll = focused.GetComponentInParent<ScrollRect>();
                if (scroll == null || scroll.content == null)
                {
                    return;
                }
                var viewport = scroll.viewport != null ? scroll.viewport : scroll.GetComponent<RectTransform>();
                var vpCorners = new Vector3[4];
                viewport.GetWorldCorners(vpCorners);
                var itemCorners = new Vector3[4];
                ((RectTransform)focused.transform).GetWorldCorners(itemCorners);
                float aboveBy = itemCorners[1].y - vpCorners[1].y;
                float belowBy = vpCorners[0].y - itemCorners[0].y;
                if (aboveBy > 0f)
                {
                    scroll.content.position -= new Vector3(0f, aboveBy, 0f);
                }
                else if (belowBy > 0f)
                {
                    scroll.content.position += new Vector3(0f, belowBy, 0f);
                }
            }
            catch (Exception e)
            {
                Log.LogError($"Controller Fix: Options field navigation failed: {e.Message}");
            }
        }

        private void UpdateMainMenuNavigation()
        {
            try
            {
                if (PlayerManager.Instance == null || UI.Instance == null)
                {
                    LogMainMenuDiagOnce("PlayerManager.Instance or UI.Instance is null");
                    return;
                }

                var hybridPlayer = PlayerManager.Instance.HybridPlayer1;
                if (hybridPlayer == null)
                {
                    LogMainMenuDiagOnce("HybridPlayer1 is null");
                    return;
                }
                if (!hybridPlayer.IsUsingGamePad)
                {
                    LogMainMenuDiagOnce($"IsUsingGamePad=false (currentControlScheme='{hybridPlayer.PlayerInput?.currentControlScheme}')");
                    return;
                }

                // UIMainMenu isn't tied to a specific player - it's shown before any household
                // exists - so it's likely registered under the shared/common UI (-1), not player 0.
                var mainMenuNeg1 = UI.GetOrNull<UIMainMenu>(-1);
                var mainMenu = mainMenuNeg1 ?? UI.GetOrNull<UIMainMenu>(0);
                if (mainMenu == null)
                {
                    LogMainMenuDiagOnce("UIMainMenu instance not found at playerIndex -1 or 0");
                    return;
                }
                if (!mainMenu.IsVisible)
                {
                    LogMainMenuDiagOnce($"UIMainMenu found (via index {(mainMenuNeg1 != null ? -1 : 0)}) but IsVisible=false");
                    return;
                }

                var perPlayerWindow = hybridPlayer.UIManager != null ? hybridPlayer.UIManager.CurrentWindow : null;
                var commonWindow = (HybridReferences.Instance != null && HybridReferences.Instance.UIManagerCommon != null)
                    ? HybridReferences.Instance.UIManagerCommon.CurrentWindow : null;

                // Don't steal navigation from a sub-window (e.g. New Game/Settings) that's
                // currently on top of the main menu - it should be driven by the normal
                // NavigateMenu path once household/back-button registration exists for it.
                if (perPlayerWindow != null || commonWindow != null)
                {
                    LogMainMenuDiagOnce($"Blocked by CurrentWindow (perPlayer='{perPlayerWindow?.GetType().Name}', common='{commonWindow?.GetType().Name}')");
                    return;
                }

                // Focusable.OnEnable() registers each button to the NEAREST FocusableGroup
                // ancestor via GetComponentInParent<FocusableGroup>(). UIContainer (which
                // MainPanel is) is itself a FocusableGroup, so the main menu buttons register to
                // MainPanel, not to the outer UIMainMenu window - confirmed by HasAnyFocusable
                // being false when driving navigation directly on mainMenu. Navigate MainPanel.
                var panel = mainMenu.MainPanel;
                if (panel == null)
                {
                    LogMainMenuDiagOnce("mainMenu.MainPanel is null");
                    return;
                }

                LogMainMenuDiagOnce($"Driving navigation on MainPanel (HasFocused={panel.HasFocused(false)}, HasAnyFocusable={panel.GetLeftMostFocusable(false) != null})");

                // Navigate()'s first directional press only primes default focus and returns
                // early (see FocusableGroup.Navigate) - nothing gets visibly focused until a
                // second press. Prime it ourselves every frame (no-op once something's focused)
                // so a default selection is visible as soon as the menu is driven by gamepad.
                panel.FocusDefaultFocusable(false);

                panel.NavigateMenuArrows(hybridPlayer);
                panel.NavigateMenuShoulders(hybridPlayer);
                panel.NavigateMenuDoClick(hybridPlayer);
            }
            catch (Exception e)
            {
                Log.LogError($"Controller Fix: Main menu navigation failed: {e.Message}\n{e.StackTrace}");
            }
        }

        private void LogMainMenuDiagOnce(string state)
        {
            if (!DebugLogging || state == _lastMainMenuDiagState)
            {
                return;
            }
            _lastMainMenuDiagState = state;
            Log.LogInfo($"Controller Fix: [MainMenuDiag] {state}");
        }

        private static string _lastDPadGateDiagState;

        // BuildWall/PipetteMode/SledgehammerMode/ToggleGrid are bound to the same physical D-pad
        // directions the FocusableGroup navigation system reads (ButtonDPadLeft/Right/Up/Down) for
        // moving focus in any open menu/interaction window. Both fire simultaneously, so opening
        // an in-game interaction menu while these are live can trigger a build tool by accident.
        // Disable them whenever a real modal-ish window is open; re-enable once nothing is.
        //
        // Originally used the private _visibleWindows list instead of CurrentWindow, on the
        // theory that non-back-button-registered windows would be invisible to a CurrentWindow
        // check. In practice this backfired badly: _visibleWindows during ordinary live-mode
        // play includes a pile of always-on HUD elements (UIGameBar, UIThoughtBubbles, UITime,
        // UICharacters, UIInteractionQueue, UINotifications, UISkillsInProgressAndUpcomingEvents,
        // UICharacterSubMenuBar, UIThoughts, ...) that are essentially ALWAYS present, so "any
        // window visible" was true almost permanently and permanently disabled build actions and
        // the virtual cursor. CurrentWindow (_visibleWindowsRegisteredForBackButton), confirmed
        // via [NavDiag] logs, correctly turns non-null only for real modal windows (UIInteractions,
        // UIEscapeMenu, loading/transition screens) and stays null for all of the above HUD noise.
        // Shared by DPad build-action gating and the virtual cursor (both need to know "is a
        // real menu/popup open").
        private bool IsAnyBlockingWindowOpen(HybridPlayer hybridPlayer)
        {
            var perPlayerWindow = hybridPlayer?.UIManager?.CurrentWindow;
            var commonWindow = HybridReferences.Instance?.UIManagerCommon?.CurrentWindow;

            LogDPadGateDiagOnce($"perPlayerWindow={perPlayerWindow?.GetType().Name ?? "null"}, commonWindow={commonWindow?.GetType().Name ?? "null"}");

            return perPlayerWindow != null || commonWindow != null;
        }

        // True while the player is actively carrying an item or drawing a wall/fence. The build
        // catalog stays CurrentWindow through all of that, so "a window is open" must NOT be
        // treated as "the player is in a menu" during placement - world input (A to place,
        // rotate, LT/RT modifiers, floor switching) has to keep flowing.
        internal static bool IsItemOrNodeInPlacement(HybridPlayer hybridPlayer)
        {
            try
            {
                var player = hybridPlayer?.Player;
                return player != null && (player.ItemInPlacement != null || player.NodeInPlacement != null);
            }
            catch
            {
                return false;
            }
        }

        private void UpdateDPadBuildActionGating()
        {
            if (Actions == null || PlayerManager.Instance == null)
            {
                return;
            }

            var hybridPlayer = PlayerManager.Instance.HybridPlayer1;
            bool anyWindowOpen = IsAnyBlockingWindowOpen(hybridPlayer);

            // Placement keeps the catalog window open but the player is working in the world -
            // floor switching (D-pad up/down) must stay live while carrying an item.
            bool shouldBeEnabled = !anyWindowOpen || IsItemOrNodeInPlacement(hybridPlayer);
            if (shouldBeEnabled == _dpadBuildActionsEnabled)
            {
                return;
            }
            _dpadBuildActionsEnabled = shouldBeEnabled;

            foreach (var actionName in DPadBuildActions)
            {
                var action = Actions.FindAction(actionName);
                if (action == null)
                {
                    continue;
                }
                if (shouldBeEnabled)
                {
                    action.Enable();
                }
                else
                {
                    action.Disable();
                }
            }
            if (DebugLogging)
            {
                Log.LogInfo($"Controller Fix: D-pad build actions {(shouldBeEnabled ? "enabled" : "disabled (a window is open)")}.");
            }
        }

        private void UpdateVirtualCursor()
        {
            try
            {
                if (PlayerManager.Instance == null)
                {
                    SetVirtualCursorEnabled(false, null);
                    return;
                }

                var hybridPlayer = PlayerManager.Instance.HybridPlayer1;
                if (hybridPlayer == null || !hybridPlayer.IsUsingGamePad)
                {
                    SetVirtualCursorEnabled(false, hybridPlayer);
                    return;
                }

                var gamepad = Gamepad.current;
                if (gamepad == null)
                {
                    SetVirtualCursorEnabled(false, hybridPlayer);
                    return;
                }

                // NOTE: the cursor deliberately stays available while menus/windows are open
                // (it used to force-disable via IsAnyBlockingWindowOpen). BG3-style, the cursor
                // and D-pad navigation coexist: the build catalog's item grid and other dense UI
                // are far more usable by pointing than by spatial focus-hopping, and the mouse
                // warp makes every menu behave exactly as it does for a mouse user.
                var toggleButton = GetCursorToggleButton(gamepad);
                if (toggleButton != null && toggleButton.wasPressedThisFrame)
                {
                    SetVirtualCursorEnabled(!VirtualCursorEnabled, hybridPlayer);
                }

                if (!VirtualCursorEnabled)
                {
                    return;
                }

                Vector2 stick = gamepad.rightStick.ReadValue();
                VirtualCursorPosition += stick * CfgCursorSpeed.Value * Time.unscaledDeltaTime;
                VirtualCursorPosition = new Vector2(
                    Mathf.Clamp(VirtualCursorPosition.x, 0f, Screen.width),
                    Mathf.Clamp(VirtualCursorPosition.y, 0f, Screen.height));

                UpdateCursorVisualPosition();

                // Several select/interact paths (UpdateSelect.cs) gate on InputManager.IsPointerOverUI,
                // which checks EventSystem.current.IsPointerOverGameObject() - driven by the real
                // OS mouse position via InputSystemUIInputModule, NOT by GetCursorPosition(). Same
                // story for the click-drag-distance check, which reads raw Input.mousePosition.
                // Patching GetCursorPosition alone left those reading stale, unmoving real-mouse
                // state, silently blocking every world click. Warp the real (now-hidden) OS cursor
                // to match so everything that reads mouse state agrees with the reticle.
                if (Mouse.current != null)
                {
                    Mouse.current.WarpCursorPosition(VirtualCursorPosition);
                }

                UpdateCursorUIClickHandling(gamepad);
            }
            catch (Exception e)
            {
                Log.LogError($"Controller Fix: Virtual cursor update failed: {e.Message}\n{e.StackTrace}");
            }
        }

        private UnityEngine.InputSystem.Controls.ButtonControl _cursorToggleButtonCache;
        private Gamepad _cursorToggleButtonGamepad;
        private string _cursorToggleButtonName;

        // Resolves the configured toggle control name on the current gamepad, cached until the
        // device or the config value changes. Falls back to leftStickPress on a bad name.
        private UnityEngine.InputSystem.Controls.ButtonControl GetCursorToggleButton(Gamepad gamepad)
        {
            string configured = CfgCursorToggleButton.Value;
            if (_cursorToggleButtonCache != null && _cursorToggleButtonGamepad == gamepad && _cursorToggleButtonName == configured)
            {
                return _cursorToggleButtonCache;
            }

            _cursorToggleButtonGamepad = gamepad;
            _cursorToggleButtonName = configured;
            _cursorToggleButtonCache = gamepad.TryGetChildControl<UnityEngine.InputSystem.Controls.ButtonControl>(configured);
            if (_cursorToggleButtonCache == null)
            {
                Log.LogWarning($"Controller Fix: Cursor ToggleButton '{configured}' not found on '{gamepad.displayName}', falling back to leftStickPress.");
                _cursorToggleButtonCache = gamepad.leftStickButton;
            }
            return _cursorToggleButtonCache;
        }

        private readonly List<RaycastResult> _uiRaycastResults = new List<RaycastResult>();
        private GameObject _cursorPointerDownTarget;

        // The mouse warp above already makes Unity's own EventSystem/InputSystemUIInputModule
        // hover any UI element (Enter/Exit) exactly like a real mouse would, for free - hover
        // works purely from mouse position. What's missing is an actual click: pressing gamepad
        // A isn't a real left-mouse-button press, so nothing dispatches PointerDown/Up/Click to
        // generic UI (a plain Button, a settings Selectable). Do that dispatch ourselves so any
        // UI element under the reticle behaves like it would under a real clicked mouse.
        //
        // Reads the raw Gamepad device (buttonSouth) rather than hybridPlayer.ButtonConfirm.
        // ButtonConfirm.Down/.Up are edge-triggered flags set by HybridPlayer's own Update(),
        // and Unity doesn't guarantee execution order between separate GameObjects' Update()
        // calls - reading them from our own, separate GameObject could intermittently observe a
        // stale or already-consumed edge, missing clicks unpredictably. The raw device state is
        // maintained centrally by the Input System itself, not per-script, so it's a more
        // reliable single source of truth for our own edge detection.
        private void UpdateCursorUIClickHandling(Gamepad gamepad)
        {
            if (EventSystem.current == null)
            {
                return;
            }

            var pointerData = new PointerEventData(EventSystem.current)
            {
                position = VirtualCursorPosition,
                button = PointerEventData.InputButton.Left,
            };
            _uiRaycastResults.Clear();
            EventSystem.current.RaycastAll(pointerData, _uiRaycastResults);
            GameObject hit = _uiRaycastResults.Count > 0 ? _uiRaycastResults[0].gameObject : null;
            if (_uiRaycastResults.Count > 0)
            {
                pointerData.pointerCurrentRaycast = _uiRaycastResults[0];
            }

            // The raycast usually lands on a leaf graphic (a Text label, an Image) that has no
            // pointer handlers of its own - the Button/Focusable lives on an ancestor. A plain
            // Execute(hit, ...) on the leaf silently does nothing, which is exactly why some UI
            // elements clicked fine (handler happened to be on the hit object) and others were
            // dead. Mirror what Unity's own input module does: ExecuteHierarchy for pointerDown
            // (bubbles up to the first ancestor that handles it) and GetEventHandler for click
            // (finds the ancestor click handler to compare and dispatch against).
            if (gamepad.buttonSouth.wasPressedThisFrame && hit != null)
            {
                pointerData.pointerPressRaycast = pointerData.pointerCurrentRaycast;
                var pressTarget = ExecuteEvents.ExecuteHierarchy(hit, pointerData, ExecuteEvents.pointerDownHandler);
                if (pressTarget == null)
                {
                    pressTarget = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit);
                }
                _cursorPointerDownTarget = pressTarget;
                pointerData.pointerPress = pressTarget;
            }

            if (gamepad.buttonSouth.wasReleasedThisFrame)
            {
                if (_cursorPointerDownTarget != null)
                {
                    pointerData.pointerPress = _cursorPointerDownTarget;
                    ExecuteEvents.Execute(_cursorPointerDownTarget, pointerData, ExecuteEvents.pointerUpHandler);
                }

                var clickTarget = hit != null ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit) : null;
                if (clickTarget != null && clickTarget == ExecuteEvents.GetEventHandler<IPointerClickHandler>(_cursorPointerDownTarget))
                {
                    ExecuteEvents.Execute(clickTarget, pointerData, ExecuteEvents.pointerClickHandler);
                }
                _cursorPointerDownTarget = null;
            }
        }

        private bool? _previousNeverAutoSwitchControlSchemes;

        private void SetVirtualCursorEnabled(bool enabled, HybridPlayer hybridPlayer)
        {
            if (VirtualCursorEnabled == enabled)
            {
                return;
            }
            VirtualCursorEnabled = enabled;

            // Right stick normally drives camera look (the "View" action) - disable it while
            // the cursor is active so moving the reticle doesn't also spin the camera.
            var viewAction = Actions?.FindAction("View");
            var playerInput = hybridPlayer?.PlayerInput;
            if (enabled)
            {
                viewAction?.Disable();
                // HybridPlayer.View is never actually read anywhere - the "View" action's own
                // callback (OnView) writes to a DIFFERENT field, MouseDelta, which is what
                // UpdateFreeCamera.cs actually uses for gamepad look rotation. Disabling the
                // action stops new OnView calls, but MouseDelta then stays stuck at its last
                // nonzero value forever (nothing left to zero it), so the camera kept applying
                // that same stale rotation delta every frame - a permanent spin. Zero the field
                // that's actually consumed.
                if (hybridPlayer != null)
                {
                    hybridPlayer.MouseDelta = Vector2.zero;
                }

                // WarpCursorPosition on the real mouse (below and every frame while active) is
                // itself indistinguishable from real mouse movement to PlayerInput's automatic
                // control-scheme detection - it was flipping currentControlScheme to "Keyboard"
                // every time we warped, which made hybridPlayer.IsUsingGamePad go false, which
                // made our own gating immediately disable the cursor again - a rapid on/off flicker.
                // Suspend auto-switching while the cursor owns the real mouse position.
                if (playerInput != null)
                {
                    _previousNeverAutoSwitchControlSchemes = playerInput.neverAutoSwitchControlSchemes;
                    playerInput.neverAutoSwitchControlSchemes = true;
                }

                VirtualCursorPosition = new Vector2(Screen.width / 2f, Screen.height / 2f);
                if (Mouse.current != null)
                {
                    Mouse.current.WarpCursorPosition(VirtualCursorPosition);
                }
                ShowCursorVisual();
            }
            else
            {
                viewAction?.Enable();
                if (playerInput != null && _previousNeverAutoSwitchControlSchemes.HasValue)
                {
                    playerInput.neverAutoSwitchControlSchemes = _previousNeverAutoSwitchControlSchemes.Value;
                    _previousNeverAutoSwitchControlSchemes = null;
                }
                HideCursorVisual();
            }

            Log.LogInfo($"Controller Fix: Virtual world cursor {(enabled ? "enabled" : "disabled")}.");
        }

        private void EnsureCursorVisualCreated()
        {
            if (_cursorVisualGO != null)
            {
                return;
            }

            var canvasGO = new GameObject("ControllerFix_CursorCanvas");
            canvasGO.hideFlags = HideFlags.HideAndDontSave;
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            canvasGO.AddComponent<CanvasScaler>();

            var imageGO = new GameObject("ControllerFix_CursorReticle");
            imageGO.transform.SetParent(canvasGO.transform, false);
            var image = imageGO.AddComponent<Image>();
            image.sprite = CreateReticleSprite();
            image.raycastTarget = false;
            image.rectTransform.sizeDelta = new Vector2(48f, 48f);

            _cursorVisualGO = canvasGO;
            _cursorVisualRect = image.rectTransform;
            _cursorVisualGO.SetActive(false);
        }

        private void ShowCursorVisual()
        {
            EnsureCursorVisualCreated();
            _cursorVisualGO.SetActive(true);
            UpdateCursorVisualPosition();
        }

        private void HideCursorVisual()
        {
            _cursorVisualGO?.SetActive(false);
        }

        private void UpdateCursorVisualPosition()
        {
            if (_cursorVisualRect != null)
            {
                _cursorVisualRect.position = new Vector3(VirtualCursorPosition.x, VirtualCursorPosition.y, 0f);
            }
        }

        // Procedurally generated dartboard-style reticle (outer ring + mid ring + center dot) -
        // no external art assets needed for a single-file mod.
        private static Sprite CreateReticleSprite()
        {
            if (_cachedReticleSprite != null)
            {
                return _cachedReticleSprite;
            }

            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear
            };
            var center = new Vector2(size / 2f, size / 2f);
            float outerRadius = size / 2f - 2f;
            float midRadius = outerRadius * 0.6f;
            const float ringThickness = 3f;
            const float dotRadius = 3f;
            var ringColor = new Color32(255, 215, 60, 235);

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    bool onOuterRing = dist <= outerRadius && dist >= outerRadius - ringThickness;
                    bool onMidRing = dist <= midRadius && dist >= midRadius - ringThickness;
                    bool onDot = dist <= dotRadius;
                    pixels[y * size + x] = (onOuterRing || onMidRing || onDot) ? ringColor : new Color32(0, 0, 0, 0);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();

            _cachedReticleSprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            return _cachedReticleSprite;
        }

        private void LogDPadGateDiagOnce(string state)
        {
            if (!DebugLogging || state == _lastDPadGateDiagState)
            {
                return;
            }
            _lastDPadGateDiagState = state;
            Log.LogInfo($"Controller Fix: [DPadGateDiag] {state}");
        }
    }

    public static class DestroyWatchPatch
    {
        public static void Prefix(UnityEngine.Object obj)
        {
            if (Plugin.OwnGameObject == null || obj == null)
            {
                return;
            }

            bool isUs = obj == Plugin.OwnGameObject
                || (obj is UnityEngine.Component c && c.gameObject == Plugin.OwnGameObject);

            if (isUs)
            {
                Plugin.Log.LogInfo($"Controller Fix: [DestroyWatch] Destroy() called on '{obj.GetType().Name}' (our GameObject). Stack:\n{Environment.StackTrace}");
            }
        }
    }

    public static class SetActiveWatchPatch
    {
        public static void Prefix(UnityEngine.GameObject __instance, bool value)
        {
            if (Plugin.OwnGameObject == null || __instance == null)
            {
                return;
            }

            if (__instance == Plugin.OwnGameObject)
            {
                Plugin.Log.LogInfo($"Controller Fix: [SetActiveWatch] SetActive({value}) called on our GameObject. Stack:\n{Environment.StackTrace}");
            }
        }
    }

    public static class SetParentWatchPatch
    {
        public static void Prefix(UnityEngine.Transform __instance, UnityEngine.Transform __0)
        {
            if (Plugin.OwnGameObject == null || __instance == null)
            {
                return;
            }

            if (__instance.gameObject == Plugin.OwnGameObject)
            {
                string parentName = __0 == null ? "<none>" : __0.name;
                Plugin.Log.LogInfo($"Controller Fix: [SetParentWatch] SetParent('{parentName}') called on our GameObject's transform. Stack:\n{Environment.StackTrace}");
            }
        }
    }

    public static class SkipListContainerFocusRegistrationPatch
    {
        private static bool _loggedCatalogItemGroup;

        public static void Postfix(Focusable __instance)
        {
            if (__instance is UIInteractionsList)
            {
                var listGroup = Traverse.Create(__instance).Field("_focusableGroup").GetValue<FocusableGroup>();
                listGroup?.UnregisterFocusable(__instance);
                return;
            }

            // Build catalog item thumbnails: the game only drives D-pad navigation on the
            // CurrentWindow's own FocusableGroup (UIManager.NavigateMenu), and Focusable.OnEnable
            // registers to the NEAREST group ancestor. The category buttons land on the catalog
            // window's group (hence they navigate fine), but the item grid registers to a nested
            // group nobody drives - so focus could never reach the actual items. Re-home them to
            // the catalog window's group so the same spatial navigation covers the whole window.
            if (__instance is UIBuildModeItem)
            {
                var catalog = __instance.GetComponentInParent<UIBuildModeCatalog>();
                if (catalog == null)
                {
                    return;
                }
                var groupField = Traverse.Create(__instance).Field("_focusableGroup");
                var group = groupField.GetValue<FocusableGroup>();
                if (!_loggedCatalogItemGroup)
                {
                    _loggedCatalogItemGroup = true;
                    string groupDesc = group == null ? "<null>" : $"{group.GetType().Name}:{group.gameObject.name}";
                    Plugin.Log.LogInfo($"Controller Fix: [CatalogNav] UIBuildModeItem registered to group '{groupDesc}'; " +
                        (ReferenceEquals(group, catalog) ? "already the catalog window group (re-homing not needed?)." : "re-homing to the catalog window group."));
                }
                if (!ReferenceEquals(group, catalog))
                {
                    group?.UnregisterFocusable(__instance);
                    catalog.RegistrerFocusable(__instance);
                    groupField.SetValue(catalog);
                }
            }
        }
    }

    public static class GamepadFocusClickPatch
    {
        // Focusable.ClickUp only fires OnClick when `_isHovered || (_justEnabled && IsPressed)`.
        // _justEnabled is cleared by ANY IsMouseHovered assignment after OnEnable - so once the
        // real/virtual mouse has hovered an element even briefly, a later gamepad A-press on it
        // (focused via D-pad, ClickDown sets IsPressed) silently does nothing. Mixing the
        // virtual cursor with D-pad focus makes this near-certain in the catalog grid. When the
        // click is gamepad-driven (focused + group in gamepad mode + pressed + not hovered),
        // restore _justEnabled so the click lands.
        public static void Prefix(Focusable __instance)
        {
            if (!__instance.IsPressed || !__instance.IsFocused)
            {
                return;
            }
            var traverse = Traverse.Create(__instance);
            var group = traverse.Field("_focusableGroup").GetValue<FocusableGroup>();
            bool hovered = traverse.Field("_isHovered").GetValue<bool>();
            if (group != null && group.IsUsingGamePad && !hovered)
            {
                traverse.Field("_justEnabled").SetValue(true);
            }
        }
    }

    // Runtime-built TMP sprite asset with Xbox controller button glyphs (Kenney Input Prompts,
    // CC0, embedded in the DLL as Resources/xbox_glyphs.png). The game ships only a
    // KeyboardAndMouseSprites TMP atlas, so gamepad hints could previously only ever be text.
    // Once registered with MaterialReferenceManager, any TMP text in the game can render
    // <sprite="XboxGlyphs" name="a"> etc.
    public static class XboxGlyphs
    {
        public const string AssetName = "XboxGlyphs";

        // Must match the order the sprites were packed into the atlas (5-column grid,
        // left-to-right then top-to-bottom) - see scratchpad make_atlas.ps1.
        private static readonly string[] SpriteNames =
        {
            "a", "b", "x", "y", "lb",
            "rb", "lt", "rt", "dpad_up", "dpad_down",
            "dpad_left", "dpad_right", "dpad", "menu", "view",
            "ls_press", "rs_press", "ls", "rs"
        };

        private const int Cell = 64;
        private const int Columns = 5;

        private static bool _initialized;
        private static bool _available;

        public static bool Available => EnsureLoaded();

        public static bool IsGamepadPath(string path)
        {
            return !string.IsNullOrEmpty(path)
                && (path.IndexOf("Gamepad", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("XInput", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // The game's KeyRebindingManager.GetBindingIndex matches simple bindings only when
        // binding.groups == "Gamepad" EXACTLY - which fails for the game's own native gamepad
        // bindings (their groups strings differ), so gamepad lookups for e.g. Cancel (B) or
        // SmallRotateItemLeft (LB) return -1 and fall back to keyboard. Find gamepad bindings
        // by control PATH instead, which is what actually determines the device.
        public static int FindGamepadBindingIndex(InputAction action)
        {
            if (action == null)
            {
                return -1;
            }
            for (int i = 0; i < action.bindings.Count; i++)
            {
                var b = action.bindings[i];
                if (b.isComposite)
                {
                    if (i + 1 < action.bindings.Count && action.bindings[i + 1].isPartOfComposite
                        && IsGamepadPath(action.bindings[i + 1].effectivePath))
                    {
                        return i;
                    }
                }
                else if (!b.isPartOfComposite && IsGamepadPath(b.effectivePath))
                {
                    return i;
                }
            }
            return -1;
        }

        // A few actions have native gamepad behavior hardcoded in game logic rather than bound
        // on the action itself (LB/RB rotate the selected item directly), so no binding-based
        // lookup can find them. Map those action names straight to the glyph that really works.
        public static bool TryGetHardcodedActionGlyph(string actionName, out string spriteName)
        {
            switch (actionName)
            {
                // The mod's rotate-while-carrying handling in UpdateTimeStepping - the game
                // itself never gave SmallRotateItem any gamepad control (LB/RB only ever
                // switched menu tabs, despite what these glyphs used to claim).
                case "SmallRotateItemLeft":
                    spriteName = "dpad_left";
                    return true;
                case "SmallRotateItemRight":
                    spriteName = "dpad_right";
                    return true;
                default:
                    spriteName = null;
                    return false;
            }
        }

        // Some tips rows don't reference an action at all - the game hardcodes a keyboard
        // sprite name ("escape", "leftclick", ...). Map the ones with a true controller
        // equivalent to a glyph; anything unmapped keeps the game's keyboard sprite.
        public static bool TryGetSpriteForKeyboardSpriteName(string keyboardSpriteName, out string spriteName)
        {
            switch (keyboardSpriteName)
            {
                case "escape":
                case "rightclick":
                    spriteName = "b"; // B = cancel/back everywhere on gamepad
                    return true;
                case "leftclick":
                    spriteName = "a"; // A = click/confirm (incl. the virtual cursor)
                    return true;
                case "alt":
                    // The "alt"/"shift" tips only ever appear in the exact contexts where
                    // ContextualTriggerModifiers makes the triggers act as those modifiers,
                    // so show whichever control is configured (keyboard sprite if disabled).
                    return TryGetSpriteForGamepadControlName(Plugin.CfgNoSnapHold.Value, out spriteName);
                case "shift":
                    return TryGetSpriteForGamepadControlName(Plugin.CfgMoveVerticallyHold.Value, out spriteName);
                default:
                    spriteName = null;
                    return false;
            }
        }

        // Maps a bare gamepad control name from config ("leftTrigger") to its glyph by running
        // it through the path mapper.
        private static bool TryGetSpriteForGamepadControlName(string controlName, out string spriteName)
        {
            spriteName = null;
            return !string.IsNullOrEmpty(controlName)
                && TryGetSpriteForPath("<Gamepad>/" + controlName, out spriteName);
        }

        // Maps an Input System control path (e.g. "<Gamepad>/buttonSouth", "<Gamepad>/dpad/up")
        // to a sprite name in the atlas. Returns false for anything non-gamepad (keyboard
        // fallback bindings keep their text label).
        public static bool TryGetSpriteForPath(string path, out string spriteName)
        {
            spriteName = null;
            if (!IsGamepadPath(path))
            {
                return false;
            }
            string p = path.ToLowerInvariant();

            if (p.Contains("dpad"))
            {
                if (p.EndsWith("/up")) spriteName = "dpad_up";
                else if (p.EndsWith("/down")) spriteName = "dpad_down";
                else if (p.EndsWith("/left")) spriteName = "dpad_left";
                else if (p.EndsWith("/right")) spriteName = "dpad_right";
                else spriteName = "dpad";
                return true;
            }
            if (p.EndsWith("buttonsouth")) { spriteName = "a"; return true; }
            if (p.EndsWith("buttoneast")) { spriteName = "b"; return true; }
            if (p.EndsWith("buttonwest")) { spriteName = "x"; return true; }
            if (p.EndsWith("buttonnorth")) { spriteName = "y"; return true; }
            if (p.EndsWith("leftshoulder")) { spriteName = "lb"; return true; }
            if (p.EndsWith("rightshoulder")) { spriteName = "rb"; return true; }
            if (p.EndsWith("lefttriggerbutton") || p.EndsWith("lefttrigger")) { spriteName = "lt"; return true; }
            if (p.EndsWith("righttriggerbutton") || p.EndsWith("righttrigger")) { spriteName = "rt"; return true; }
            if (p.EndsWith("leftstickpress")) { spriteName = "ls_press"; return true; }
            if (p.EndsWith("rightstickpress")) { spriteName = "rs_press"; return true; }
            if (p.EndsWith("leftstick")) { spriteName = "ls"; return true; }
            if (p.EndsWith("rightstick")) { spriteName = "rs"; return true; }
            if (p.EndsWith("/start")) { spriteName = "menu"; return true; }
            if (p.EndsWith("/select")) { spriteName = "view"; return true; }
            return false;
        }

        public static string GetSpriteMarkup(string spriteName)
        {
            return $"<sprite=\"{AssetName}\" name=\"{spriteName}\">";
        }

        // Renders the binding at bindingIndex as TMP sprite markup ("<sprite=...>" per control,
        // combos joined with '+'). Returns null when any involved control has no glyph (e.g. a
        // keyboard fallback binding) so the caller can keep its text label instead.
        public static string BuildMarkupForBinding(InputAction action, int bindingIndex)
        {
            if (!EnsureLoaded() || action == null || bindingIndex < 0 || bindingIndex >= action.bindings.Count)
            {
                return null;
            }

            var paths = new List<string>();
            var binding = action.bindings[bindingIndex];
            if (binding.isComposite)
            {
                for (int i = bindingIndex + 1; i < action.bindings.Count && action.bindings[i].isPartOfComposite; i++)
                {
                    paths.Add(action.bindings[i].effectivePath);
                }
            }
            else
            {
                paths.Add(binding.effectivePath);
            }
            if (paths.Count == 0)
            {
                return null;
            }

            var sb = new System.Text.StringBuilder();
            foreach (var path in paths)
            {
                if (!TryGetSpriteForPath(path, out var spriteName))
                {
                    return null;
                }
                if (sb.Length > 0)
                {
                    sb.Append("<size=60%>+</size>");
                }
                sb.Append($"<sprite=\"{AssetName}\" name=\"{spriteName}\">");
            }
            return sb.ToString();
        }

        private static bool EnsureLoaded()
        {
            if (_initialized)
            {
                return _available;
            }
            _initialized = true;
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                string resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("xbox_glyphs.png", StringComparison.OrdinalIgnoreCase));
                if (resourceName == null)
                {
                    Plugin.Log.LogError("Controller Fix: [Glyphs] Embedded resource xbox_glyphs.png not found.");
                    return false;
                }
                byte[] pngBytes;
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    pngBytes = new byte[stream.Length];
                    int read = 0;
                    while (read < pngBytes.Length)
                    {
                        int r = stream.Read(pngBytes, read, pngBytes.Length - read);
                        if (r <= 0) break;
                        read += r;
                    }
                }

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = AssetName,
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear
                };
                if (!ImageConversion.LoadImage(texture, pngBytes, false))
                {
                    Plugin.Log.LogError("Controller Fix: [Glyphs] Failed to decode glyph atlas PNG.");
                    return false;
                }

                var shader = Shader.Find("TextMeshPro/Sprite");
                if (shader == null)
                {
                    // Not all shaders survive build stripping; borrow whatever the game's own
                    // sprite asset (keyboard sprites) uses.
                    shader = TMP_Settings.defaultSpriteAsset?.material?.shader;
                }
                if (shader == null)
                {
                    Plugin.Log.LogError("Controller Fix: [Glyphs] No TMP sprite shader available.");
                    return false;
                }
                var material = new Material(shader)
                {
                    name = AssetName + " Material",
                    mainTexture = texture,
                    hideFlags = HideFlags.HideAndDontSave
                };

                var asset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
                asset.name = AssetName;
                asset.hideFlags = HideFlags.HideAndDontSave;
                asset.spriteSheet = texture;
                asset.material = material;
                asset.hashCode = TMP_TextUtilities.GetSimpleHashCode(asset.name);

                // Touch the backing fields directly: the public table properties have side
                // effects (getter can trigger UpdateLookupTables early) and null tables on a
                // fresh instance vary by TMP version. m_Version = "1.1.0" marks the asset as
                // already using the new glyph/character tables so TMP skips its upgrade path.
                var glyphTableField = AccessTools.Field(typeof(TMP_SpriteAsset), "m_SpriteGlyphTable");
                var charTableField = AccessTools.Field(typeof(TMP_SpriteAsset), "m_SpriteCharacterTable");
                var versionField = AccessTools.Field(typeof(TMP_SpriteAsset), "m_Version");

                var glyphTable = glyphTableField.GetValue(asset) as List<TMP_SpriteGlyph>;
                if (glyphTable == null)
                {
                    glyphTable = new List<TMP_SpriteGlyph>();
                    glyphTableField.SetValue(asset, glyphTable);
                }
                var charTable = charTableField.GetValue(asset) as List<TMP_SpriteCharacter>;
                if (charTable == null)
                {
                    charTable = new List<TMP_SpriteCharacter>();
                    charTableField.SetValue(asset, charTable);
                }
                versionField?.SetValue(asset, "1.1.0");

                for (int i = 0; i < SpriteNames.Length; i++)
                {
                    int col = i % Columns;
                    int row = i / Columns;
                    var glyph = new TMP_SpriteGlyph
                    {
                        index = (uint)i,
                        // BearingY at 85% of the cell sits the glyph on the baseline like a
                        // capital letter with a slight descender.
                        metrics = new GlyphMetrics(Cell, Cell, 0f, Cell * 0.85f, Cell),
                        // GlyphRect y counts from the BOTTOM of the texture.
                        glyphRect = new GlyphRect(col * Cell, texture.height - (row + 1) * Cell, Cell, Cell),
                        scale = 1f
                    };
                    glyphTable.Add(glyph);
                    var character = new TMP_SpriteCharacter(0xFFFE, glyph)
                    {
                        name = SpriteNames[i],
                        scale = 1f
                    };
                    charTable.Add(character);
                }

                asset.UpdateLookupTables();
                MaterialReferenceManager.AddSpriteAsset(asset);
                _available = true;
                Plugin.Log.LogInfo($"Controller Fix: [Glyphs] Registered '{AssetName}' TMP sprite asset ({SpriteNames.Length} glyphs).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Controller Fix: [Glyphs] Sprite asset creation failed: {e.Message}\n{e.StackTrace}");
            }
            return _available;
        }
    }

    // Every world-click path in UpdateSelect (and friends) is gated on
    // !InputManager.IsPointerOverUI. On gamepad with the virtual cursor OFF, the real (hidden)
    // mouse is parked at some stale position and GetCursorPosition falls back to screen center,
    // so pressing A to activate a FOCUSED menu element (e.g. a build catalog item) also fired an
    // invisible world click at screen center, selecting random background items. While a real
    // window is open and there's no cursor on screen, report the pointer as "over UI" so world
    // clicks are suppressed. Cursor on = mouse warped/accurate = untouched; no window open =
    // legacy camera-aim clicking still works.
    public static class SuppressWorldClickInMenusPatch
    {
        public static void Postfix(ref bool __result)
        {
            if (__result || Plugin.VirtualCursorEnabled)
            {
                return;
            }
            try
            {
                var hybridPlayer = PlayerManager.Instance?.HybridPlayer1;
                if (hybridPlayer == null || !hybridPlayer.IsUsingGamePad)
                {
                    return;
                }
                // While an item/wall is in placement the player is interacting with the WORLD
                // even though the catalog window is still "open" - forcing over-UI here broke
                // placing (A), rotate hold+drag, and Move Vertically, all gated on !IsPointerOverUI.
                if (Plugin.IsItemOrNodeInPlacement(hybridPlayer))
                {
                    return;
                }
                var window = hybridPlayer.UIManager?.CurrentWindow;
                if (window == null)
                {
                    window = HybridReferences.Instance?.UIManagerCommon?.CurrentWindow;
                }
                if (window != null)
                {
                    __result = true;
                }
            }
            catch
            {
                // Managers not ready - leave the original result alone.
            }
        }
    }

    // While an item/wall is in placement, the D-pad/left-stick/A belong to the world (rotate,
    // floors, place), not to the catalog window lurking behind it - without this, A would click
    // the still-focused catalog item (spawning another item) at the same time as placing, and
    // D-pad presses would scroll catalog focus around in the background. Back/B is handled
    // outside NavigateMenu, so canceling placement is unaffected.
    public static class MenuNavSuppressedWhilePlacingPatch
    {
        public static bool Prefix(HybridPlayer hybridPlayer)
        {
            try
            {
                if (hybridPlayer != null && hybridPlayer.IsUsingGamePad && Plugin.IsItemOrNodeInPlacement(hybridPlayer))
                {
                    return false;
                }
            }
            catch
            {
                // fall through to the original
            }
            return true;
        }
    }

    // Options-panel navigation overrides, prefixed on FocusableGroup.Navigate (the single
    // funnel for D-pad AND left-stick directional nav):
    //  - Left/right on a focused slider row adjusts the slider's value instead of moving focus
    //    (ints step exactly 1; decimals step 2% of the range, or one rounding increment if the
    //    field rounds coarser than that - otherwise the game's per-frame refresh would snap the
    //    value right back).
    //  - Up/down inside the settings panel walks rows strictly in visual (top-to-bottom) order.
    //    The game's spatial nav goes by angle-and-distance from the focused control's anchor,
    //    and the anchors are all over the place (toggles sit at the row's right edge, buttons in
    //    the middle), which made vertical movement skip rows. Stepping off either end falls back
    //    to spatial nav so focus can leave the panel (e.g. back to the category list).
    public static class SliderAdjustNavigatePatch
    {
        public static bool Prefix(FocusableGroup __instance, Vector2 direction, bool isShoulderButton)
        {
            try
            {
                if (isShoulderButton)
                {
                    return true;
                }
                var focused = __instance.GetFocused(isShoulderButton: false);
                if (focused == null)
                {
                    return true;
                }

                if (direction.x != 0f && direction.y == 0f)
                {
                    return !TryAdjustSlider(focused, direction.x);
                }
                if (direction.y != 0f && direction.x == 0f)
                {
                    return !TryNavigateOptionsRows(__instance, focused, direction.y);
                }
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static bool TryAdjustSlider(Focusable focused, float directionX)
        {
            var ufg = focused.GetComponent<UIFieldGameObject>();
            var sliderField = ufg?.UIField as UIFieldSlider;
            if (sliderField == null || ufg.Slider == null || !ufg.Slider.gameObject.activeInHierarchy)
            {
                return false;
            }

            float range = sliderField.MaxValue - sliderField.MinValue;
            float normStep;
            if (!sliderField.IsDecimalType)
            {
                normStep = range > 0f ? 1f / range : 1f;
            }
            else
            {
                normStep = 0.02f;
                if (sliderField.RoundDecimal > -1 && range > 0f)
                {
                    normStep = Mathf.Max(normStep, Mathf.Pow(10f, -sliderField.RoundDecimal) / range);
                }
            }
            // The slider itself is normalized 0..1 (SetValue maps the real value onto it);
            // assigning .value (with notify) runs the game's OnSliderValueChanged pipeline,
            // exactly like a mouse drag.
            ufg.Slider.value = Mathf.Clamp01(ufg.Slider.value + normStep * Mathf.Sign(directionX));
            return true;
        }

        private static readonly List<Focusable> RowsScratch = new List<Focusable>();

        private static bool TryNavigateOptionsRows(FocusableGroup group, Focusable focused, float directionY)
        {
            var options = group as UIOptions;
            if (options == null || options.UIInspector == null
                || !focused.transform.IsChildOf(options.UIInspector.transform))
            {
                return false;
            }

            // One nav stop per field row: the prefabs carry extra invisible ParaButtons
            // (unfold/search/etc.), and walking those made some transitions take two D-pad
            // presses. Keep the row's real control - the main toggle/button/slider handle -
            // and drop the rest.
            RowsScratch.Clear();
            var bestPerRow = new Dictionary<UIFieldGameObject, Focusable>();
            foreach (var f in options.UIInspector.GetComponentsInChildren<Focusable>(includeInactive: false))
            {
                if (!f.isActiveAndEnabled || !f.Interactable || f.IsShoulderButton)
                {
                    continue;
                }
                var row = f.GetComponentInParent<UIFieldGameObject>();
                if (row == null)
                {
                    RowsScratch.Add(f); // not part of a field row - keep as its own stop
                    continue;
                }
                if (!bestPerRow.TryGetValue(row, out var current) || RowRank(f, row) > RowRank(current, row))
                {
                    bestPerRow[row] = f;
                }
            }
            foreach (var f in bestPerRow.Values)
            {
                RowsScratch.Add(f);
            }
            RowsScratch.Sort((a, b) =>
            {
                int byY = b.transform.position.y.CompareTo(a.transform.position.y); // top first
                return byY != 0 ? byY : a.transform.position.x.CompareTo(b.transform.position.x);
            });

            // Focus may currently sit on a focusable we filtered out (e.g. spatial nav landed
            // on an unfold button) - resolve it to its row's representative before stepping.
            int index = RowsScratch.IndexOf(focused);
            if (index == -1)
            {
                var focusedRow = focused.GetComponentInParent<UIFieldGameObject>();
                if (focusedRow != null && bestPerRow.TryGetValue(focusedRow, out var representative))
                {
                    index = RowsScratch.IndexOf(representative);
                }
                if (index == -1)
                {
                    return false;
                }
            }
            int target = index + (directionY < 0f ? 1 : -1); // y=-1 means "down"
            if (target < 0 || target >= RowsScratch.Count)
            {
                return false; // off either end: let spatial nav carry focus out of the panel
            }
            group.SetFocused(RowsScratch[target]);
            return true;
        }

        // Which focusable represents a field row, when the prefab carries several: the slider
        // handle we synthesize > the player-facing toggle > the row's action button > anything
        // else (unfold/search/delete helpers).
        private static int RowRank(Focusable f, UIFieldGameObject row)
        {
            if (f.GetType() == typeof(Focusable)) return 3;
            if (f is ParaToggle) return 2;
            if (row.ButtonAction != null && ReferenceEquals(f, row.ButtonAction)) return 1;
            return 0;
        }
    }

    // "No Snap" (Alt) and "Move Vertically"/grid-divide (Shift) are keyboard hold-modifiers with
    // no gamepad equivalent. Nearly every check goes through the static InputManager.Alt/.Shift
    // property getters, so postfixes there can OR in a gamepad trigger - but LT/RT natively zoom
    // the camera, so the triggers only count as modifiers WHILE the player is manipulating
    // something the modifiers apply to (item in placement/resize/scale/rotation, or a wall/fence
    // node being drawn). During exactly that window, HybridPlayer.OnZoom is suppressed so a
    // trigger pull doesn't also zoom; outside it, triggers zoom as normal and Alt/Shift are
    // untouched. Known gap: a few legacy Input.GetKey(KeyCode.LeftAlt) call sites (chair-slot
    // snapping in UpdateMoveItem, paint-hover in UpdateHover) bypass InputManager and still
    // ignore the trigger.
    public static class ContextualTriggerModifiers
    {
        private static Gamepad _cachedGamepad;
        private static string _cachedNoSnapName, _cachedMoveVerticallyName;
        private static UnityEngine.InputSystem.Controls.ButtonControl _noSnapControl, _moveVerticallyControl;

        internal static bool Enabled =>
            !string.IsNullOrEmpty(Plugin.CfgNoSnapHold.Value) || !string.IsNullOrEmpty(Plugin.CfgMoveVerticallyHold.Value);

        // True while the player is manipulating something the Alt/Shift modifiers act on.
        internal static bool ContextActive()
        {
            try
            {
                var hybridPlayer = PlayerManager.Instance?.HybridPlayer1;
                if (hybridPlayer == null || !hybridPlayer.IsUsingGamePad)
                {
                    return false;
                }
                var player = hybridPlayer.Player;
                if (player == null)
                {
                    return false;
                }
                return player.ItemInPlacement != null || player.NodeInPlacement != null
                    || player.ItemInResize != null || player.ItemInScale != null
                    || player.ItemInRotation != null;
            }
            catch
            {
                return false; // managers not ready
            }
        }

        private static UnityEngine.InputSystem.Controls.ButtonControl Resolve(
            string configured, ref string cachedName, ref UnityEngine.InputSystem.Controls.ButtonControl cachedControl)
        {
            var gamepad = Gamepad.current;
            if (gamepad == null || string.IsNullOrEmpty(configured))
            {
                return null;
            }
            if (gamepad != _cachedGamepad)
            {
                _cachedGamepad = gamepad;
                _cachedNoSnapName = _cachedMoveVerticallyName = null;
                _noSnapControl = _moveVerticallyControl = null;
            }
            if (configured != cachedName)
            {
                cachedName = configured;
                cachedControl = gamepad.TryGetChildControl<UnityEngine.InputSystem.Controls.ButtonControl>(configured);
                if (cachedControl == null)
                {
                    Plugin.Log.LogWarning($"Controller Fix: Modifier control '{configured}' not found on '{gamepad.displayName}' - that modifier is disabled.");
                }
            }
            return cachedControl;
        }

        internal static bool NoSnapHeld()
        {
            var control = Resolve(Plugin.CfgNoSnapHold.Value, ref _cachedNoSnapName, ref _noSnapControl);
            return control != null && control.isPressed;
        }

        internal static bool MoveVerticallyHeld()
        {
            var control = Resolve(Plugin.CfgMoveVerticallyHold.Value, ref _cachedMoveVerticallyName, ref _moveVerticallyControl);
            return control != null && control.isPressed;
        }

        public static void AltPostfix(ref bool __result)
        {
            if (!__result && NoSnapHeld() && ContextActive())
            {
                __result = true;
            }
        }

        public static void ShiftPostfix(ref bool __result)
        {
            if (!__result && MoveVerticallyHeld() && ContextActive())
            {
                __result = true;
            }
        }

        // Block trigger zoom while the triggers double as modifiers. OnZoom only fires on value
        // CHANGES, so a stale nonzero Zoom.Value can survive entering the context (pull LT to
        // zoom, then grab an item mid-pull) - SuppressStaleZoom below, called every frame from
        // Plugin.Update, cleans that up.
        public static bool OnZoomPrefix(HybridPlayer __instance)
        {
            try
            {
                if (Enabled && __instance.IsUsingGamePad && ContextActive())
                {
                    __instance.Zoom.SetValue(0f);
                    return false;
                }
            }
            catch
            {
                // fall through to the original
            }
            return true;
        }

        internal static void SuppressStaleZoom()
        {
            try
            {
                if (!Enabled || !ContextActive())
                {
                    return;
                }
                var hybridPlayer = PlayerManager.Instance?.HybridPlayer1;
                if (hybridPlayer != null && hybridPlayer.Zoom.Value != 0f)
                {
                    hybridPlayer.Zoom.SetValue(0f);
                }
            }
            catch
            {
                // managers not ready
            }
        }
    }

    public static class VirtualCursorPositionPatch
    {
        public static void Postfix(ref Vector3 __result)
        {
            if (Plugin.VirtualCursorEnabled)
            {
                __result = new Vector3(Plugin.VirtualCursorPosition.x, Plugin.VirtualCursorPosition.y, 0f);
            }
        }
    }

    public static class HideOSCursorPatch
    {
        // The OS pointer should never be on screen while the controller is the active device -
        // not just while the virtual cursor is on (the reticle replaces it), but also when it's
        // off (a big idle arrow parked mid-screen is pure noise; the pointer comes back the
        // moment the player touches the mouse and the control scheme flips to Keyboard).
        //
        // This skips the original entirely when hiding, rather than pre-setting CursorIsVisible:
        // the original force-sets CursorIsVisible = true for windows flagged
        // ForceCursorVisibleUnlocked (clobbering any prefix value) and writes Cursor.visible
        // every frame - and a Postfix re-hiding it after that write is a visible same-frame
        // true->false toggle, which Windows renders as flicker (the bug this patch previously
        // had). One consistent write per frame, no fighting.
        public static bool Prefix(CursorManager __instance)
        {
            bool onGamepad = false;
            try
            {
                var hybridPlayer = PlayerManager.Instance?.HybridPlayer1;
                onGamepad = hybridPlayer != null && hybridPlayer.IsUsingGamePad;
            }
            catch
            {
                // PlayerManager not ready yet - let the game handle the cursor.
            }

            if (!onGamepad && !Plugin.VirtualCursorEnabled)
            {
                return true;
            }

            __instance.CursorIsVisible = false;
            __instance.MouseLockedInPlace = false;
            __instance.CursorLockMode = CursorLockMode.None;
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.None;
            return false;
        }
    }

    public static class GamepadKeyBindingTipsPatch
    {
        public static bool Prefix(UIKeyBindingTipsItem __instance, UIKeyBindingTips uiKeyBindingTips, BindingTipItemData data)
        {
            HybridPlayer hybridPlayer;
            try
            {
                hybridPlayer = PlayerManager.Instance?.GetHybridPlayer(uiKeyBindingTips.PlayerOwnerIndex);
            }
            catch
            {
                return true;
            }
            if (hybridPlayer == null || !hybridPlayer.IsUsingGamePad)
            {
                return true;
            }

            foreach (var rect in __instance.ContentSizeFittersToRefresh)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            }
            if (__instance.LabelActionName.Key == data.TranslationKey)
            {
                return false;
            }

            var action = hybridPlayer.PlayerInput.actions.FindAction(data.InputActionNameOrSpriteName);
            string label = data.InputActionNameOrSpriteName;
            string glyphMarkup = null;
            if (action != null)
            {
                // Path-based lookup: the game's GetBindingIndex misses its own native gamepad
                // bindings (groups-string mismatch), which left Cancel/SmallRotate rows showing
                // keyboard keys even though B/LB/RB work fine.
                int gamepadIndex = XboxGlyphs.FindGamepadBindingIndex(action);
                if (gamepadIndex != -1)
                {
                    glyphMarkup = XboxGlyphs.BuildMarkupForBinding(action, gamepadIndex);
                }
                if (glyphMarkup == null && XboxGlyphs.Available
                    && XboxGlyphs.TryGetHardcodedActionGlyph(action.name, out var hardcodedSprite))
                {
                    glyphMarkup = XboxGlyphs.GetSpriteMarkup(hardcodedSprite);
                }
                if (glyphMarkup == null)
                {
                    // Truly keyboard-only action: show the keyboard binding as a text label
                    // (GamepadBindingFallbackPatch makes this resolve the keyboard binding).
                    int bindingIndex = KeyRebindingManager.GetBindingIndex(action, isGamepad: true, 0, showError: false);
                    if (bindingIndex == -1)
                    {
                        return true;
                    }
                    label = action.GetBindingDisplayString(bindingIndex, out _, out _);
                    if (string.IsNullOrEmpty(label))
                    {
                        return true;
                    }
                }
            }
            else
            {
                // No action - the game hardcoded a keyboard sprite name for this row ("escape",
                // "alt", "leftclick", ...). Swap in the controller equivalent where one exists;
                // otherwise let the original render its keyboard sprite icon untouched.
                if (XboxGlyphs.Available && XboxGlyphs.TryGetSpriteForKeyboardSpriteName(label, out var mappedSprite))
                {
                    glyphMarkup = XboxGlyphs.GetSpriteMarkup(mappedSprite);
                }
                else
                {
                    return true;
                }
            }

            __instance.LabelActionName.Key = data.TranslationKey;
            __instance.LabelKeybinding.text = glyphMarkup ?? $"<size=75%><b>[{label}]</b></size>";
            return false;
        }
    }

    // Tooltip text runs /{ActionName} placeholders through TranslationManager.
    // ActionInputKeyInjection, which injects the plain display string of the current binding
    // ("A", "LB + D-Pad Left"). When the player is on gamepad and the binding maps to glyphs,
    // reimplement the substitution with <sprite> markup instead; otherwise let the original
    // (plus GamepadBindingFallbackPatch's keyboard fallback) do its thing.
    public static class GamepadTooltipGlyphPatch
    {
        public static bool Prefix(string translatedText, ref string __result)
        {
            try
            {
                if (translatedText == null || !translatedText.Contains("/{"))
                {
                    return true;
                }
                var hybridPlayer = PlayerManager.Instance?.GetHybridPlayer(0);
                if (hybridPlayer == null || !hybridPlayer.IsUsingGamePad || !XboxGlyphs.Available)
                {
                    return true;
                }

                string text = translatedText;
                int guard = 0;
                while (guard++ < 50)
                {
                    int start = text.IndexOf("/{", StringComparison.Ordinal);
                    if (start == -1)
                    {
                        break;
                    }
                    int end = text.IndexOf('}', start);
                    if (end == -1)
                    {
                        text = text.Replace("/{", "");
                        break;
                    }
                    string actionName = text.Substring(start + 2, end - start - 2);
                    string replacement = null;
                    var action = hybridPlayer.PlayerInput.actions.FindAction(actionName);
                    if (action == null)
                    {
                        replacement = "*CANNOT FIND INPUT ACTION " + actionName + "*";
                    }
                    else
                    {
                        // Path-based first: catches native gamepad bindings the game's own
                        // groups-string lookup misses (Cancel/B, SmallRotate/LB+RB, ...).
                        int gamepadIndex = XboxGlyphs.FindGamepadBindingIndex(action);
                        if (gamepadIndex != -1)
                        {
                            replacement = XboxGlyphs.BuildMarkupForBinding(action, gamepadIndex);
                        }
                        if (replacement == null
                            && XboxGlyphs.TryGetHardcodedActionGlyph(actionName, out var hardcodedSprite))
                        {
                            replacement = XboxGlyphs.GetSpriteMarkup(hardcodedSprite);
                        }
                        if (replacement == null)
                        {
                            // GetBindingIndex is postfixed with the keyboard fallback, so this
                            // never returns -1 for an action a keyboard player could use.
                            int bindingIndex = KeyRebindingManager.GetBindingIndex(action, isGamepad: true, 0, showError: false);
                            replacement = bindingIndex == -1
                                ? ""
                                : action.GetBindingDisplayString(bindingIndex, out _, out _);
                        }
                    }
                    text = text.Substring(0, start) + replacement + text.Substring(end + 1);
                }
                __result = text;
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Controller Fix: Tooltip glyph injection failed: {e.Message}");
                return true;
            }
        }
    }

    public static class CatalogToggleParityPatch
    {
        public static bool Prefix(MessageSetPlayerCatalogueMode message)
        {
            int playerIndex = message.PlayerIndex < 0 ? 0 : message.PlayerIndex;
            var hybridPlayer = PlayerManager.Instance?.GetHybridPlayer(playerIndex);
            if (hybridPlayer == null || !hybridPlayer.IsUsingGamePad)
            {
                return true;
            }

            var catalog = UI.GetOrNull<UIBuildModeCatalog>(0);
            var player = hybridPlayer.Player;
            if (catalog != null && catalog.IsVisible && player != null && player.State != GameStates.LiveMode)
            {
                SystemManager.Instance.RegisterMessage(new MessageTogglePlayerMainMode
                {
                    PlayerIndex = playerIndex
                });
                return false;
            }
            return true;
        }
    }

    public static class GamepadBindingFallbackPatch
    {
        public static void Postfix(InputAction action, bool isGamepad, int bindingIndexer, ref int __result)
        {
            if (isGamepad && __result == -1 && action != null)
            {
                __result = KeyRebindingManager.GetBindingIndex(action, false, bindingIndexer, showError: false);
            }
        }
    }

    public static class EscapeMenuGuardPatch
    {
        public static bool Prefix(UIContainer __instance)
        {
            if (__instance is not UIEscapeMenu)
            {
                return true;
            }

            var gamepad = Gamepad.current;
            var keyboard = Keyboard.current;
            // Back.Update() can delay the actual Show() call a frame or two behind the B press
            // (it checks IsDoingCancelableAction/StopAllSkippableStoryboards first), so a single-
            // frame wasPressedThisFrame check missed it intermittently. isPressed (held, not just
            // this frame) covers that gap.
            bool gamepadCancelHeld = gamepad != null && gamepad.buttonEast.isPressed;
            bool keyboardEscapeHeld = keyboard != null && keyboard.escapeKey.isPressed;

            if (gamepadCancelHeld && !keyboardEscapeHeld)
            {
                if (Plugin.DebugLogging)
                {
                    Plugin.Log.LogInfo("Controller Fix: Blocked B button from opening the pause menu.");
                }
                return false;
            }

            return true;
        }
    }

    public static class MenuNavDiagnosticPatch
    {
        private static UIWindow _lastWindow;

        public static void Prefix(UIManager __instance, HybridPlayer hybridPlayer)
        {
            var currentWindow = __instance.CurrentWindow;
            if (currentWindow != _lastWindow)
            {
                _lastWindow = currentWindow;
                string windowName = currentWindow == null ? "null" : currentWindow.GetType().Name;
                Plugin.Log.LogInfo($"Controller Fix: [NavDiag] CurrentWindow changed -> {windowName} (manager='{__instance.name}')");
            }

            if (currentWindow == null)
            {
                return;
            }

            LogIfPressed("DPadLeft", hybridPlayer.ButtonDPadLeft, currentWindow, hybridPlayer);
            LogIfPressed("DPadRight", hybridPlayer.ButtonDPadRight, currentWindow, hybridPlayer);
            LogIfPressed("DPadUp", hybridPlayer.ButtonDPadUp, currentWindow, hybridPlayer);
            LogIfPressed("DPadDown", hybridPlayer.ButtonDPadDown, currentWindow, hybridPlayer);
        }

        private static void LogIfPressed(string label, InputButton button, UIWindow currentWindow, HybridPlayer hybridPlayer)
        {
            if (!button.IntervalPressed)
            {
                return;
            }

            Plugin.Log.LogInfo($"Controller Fix: [NavDiag] {label}.IntervalPressed on window={currentWindow.GetType().Name}, "
                + $"IsUsingGamePad={hybridPlayer.IsUsingGamePad}, HasFocused={currentWindow.HasFocused(false)}, "
                + $"HasAnyFocusable={currentWindow.GetLeftMostFocusable(false) != null}");
        }
    }

    public static class ButtonFocusHighlightPatch
    {
        private static readonly Color HighlightColor = new Color(1f, 0.82f, 0.15f, 1f);

        public static void GainedPostfix(ParaButton __instance)
        {
            var outline = GetOrAddOutline(__instance);
            if (outline == null)
            {
                return;
            }

            outline.effectColor = HighlightColor;
            outline.effectDistance = new Vector2(3f, -3f);
            outline.enabled = true;
        }

        public static void LostPostfix(ParaButton __instance)
        {
            var graphic = FindGraphic(__instance);
            if (graphic == null)
            {
                return;
            }

            var outline = graphic.GetComponent<Outline>();
            if (outline != null)
            {
                outline.enabled = false;
            }
        }

        private static Outline GetOrAddOutline(ParaButton button)
        {
            // Outline is a BaseMeshEffect - it only renders if it's on the same GameObject as a
            // Graphic (Image/Text/TMP). Many ParaButtons are just a bare RectTransform with the
            // actual Image/Label as a child, so search children rather than assuming the button's
            // own GameObject carries the Graphic.
            var graphic = FindGraphic(button);
            if (graphic == null)
            {
                return null;
            }

            var outline = graphic.GetComponent<Outline>();
            if (outline == null)
            {
                outline = graphic.gameObject.AddComponent<Outline>();
            }
            return outline;
        }

        private static Graphic FindGraphic(ParaButton button)
        {
            var graphic = button.GetComponent<Graphic>();
            if (graphic != null)
            {
                return graphic;
            }
            return button.GetComponentInChildren<Graphic>();
        }
    }

    // ParaButtonAnimationBase.IsStateActive already has an IsGamepadFocused case (distinct from
    // IsMouseHovered), so the engine fully supports a separate gamepad-focus visual - but per
    // testing, the actual menu prefabs (main menu, pause menu) only ever wire up the
    // IsMouseHovered case (that's the blue-underline hover effect), never IsGamepadFocused. Rather
    // than invent a new visual, make gamepad focus satisfy the IsMouseHovered check too, so
    // whatever hover effect already exists (color swap, underline, etc.) plays for gamepad focus
    // as well, for free and with exact visual parity.
    public static class GamepadFocusVisualParityPatch
    {
        public static void Postfix(ParaButtonAnimationBase __instance, ref bool __result)
        {
            if (__result || __instance.StateToAnimate != ButtonStateToAnimate.IsMouseHovered || !__instance.Interactable)
            {
                return;
            }

            var focusable = __instance.GetComponent<Focusable>();
            if (focusable == null || !focusable.IsFocused)
            {
                return;
            }

            var group = focusable.GetComponentInParent<FocusableGroup>();
            if (group != null && group.IsUsingGamePad)
            {
                __result = true;
            }
        }
    }

    // Selecting a Slider/Toggle/InputField via EventSystem.SetSelectedGameObject works, but Unity's
    // Selectable only shows a visible highlight if its Transition is configured (ColorTint/
    // SpriteSwap/Animation) on that specific control - many of these settings prefabs apparently
    // have it set to None, same underlying gap as ParaButton's empty ButtonFocused event. Add the
    // same generic Outline highlight here via Selectable.OnSelect/OnDeselect.
    public static class SelectableFocusHighlightPatch
    {
        private static readonly Color HighlightColor = new Color(0.3f, 0.7f, 1f, 1f);

        public static void SelectPostfix(Selectable __instance)
        {
            var graphic = FindGraphic(__instance);
            if (graphic == null)
            {
                return;
            }

            var outline = graphic.GetComponent<Outline>();
            if (outline == null)
            {
                outline = graphic.gameObject.AddComponent<Outline>();
            }
            outline.effectColor = HighlightColor;
            outline.effectDistance = new Vector2(3f, -3f);
            outline.enabled = true;
        }

        public static void DeselectPostfix(Selectable __instance)
        {
            var graphic = FindGraphic(__instance);
            var outline = graphic != null ? graphic.GetComponent<Outline>() : null;
            if (outline != null)
            {
                outline.enabled = false;
            }
        }

        private static Graphic FindGraphic(Selectable selectable)
        {
            if (selectable.targetGraphic != null)
            {
                return selectable.targetGraphic;
            }
            var graphic = selectable.GetComponent<Graphic>();
            if (graphic != null)
            {
                return graphic;
            }
            return selectable.GetComponentInChildren<Graphic>();
        }
    }

    public class KeyBindingPatch
    {
        // Fires after the game loads its default configuration maps
        public static void LoadPostfix(object __instance)
        {
            try
            {
                var playerInput = PlayerManager.Instance.HybridPlayer1.GetComponent<PlayerInput>();
                var actions = playerInput.actions;
                Plugin.Actions = actions;

                // Without this, a modifier combo and a bare binding on the same physical button
                // BOTH fire: LB+X (LargeRotateItem) also triggered bare X (Delete), which
                // cancelled the item being placed. With shortcut consumption on, the Input System
                // groups bindings sharing a control and lets the more complex one (the composite)
                // consume the button event when it actually performs - bare bindings behave
                // exactly as before whenever the modifier isn't involved. (Verified against the
                // game's Input System: consumption only triggers on a performing multi-part
                // composite, so plain overlapping bindings are unaffected.)
                InputSystem.settings.shortcutKeysConsumeInput = true;
                Plugin.Log.LogInfo("Controller Fix: Enabled shortcut input consumption (combos suppress their bare-button siblings).");

                // All paths come from the BepInEx config file (BepInEx/config/
                // com.yourname.controllerfix.cfg), so players can remap any of these by editing
                // the file (or with the ConfigurationManager mod) - defaults match the values
                // that were previously hardcoded here.

                // Menu already binds <Gamepad>/start by default, but also binds <Gamepad>/select
                // as an alternate - narrow it to start only so it doesn't collide with TownMap,
                // which we bind to select below. Nothing reads the Menu action currently (see
                // SubscribeMenuToggle), so this alone doesn't make Start do anything by itself.
                SetGamepadBinding(actions, "Menu", Plugin.CfgMenu.Value);
                SubscribeMenuToggle(actions);

                // The B-opens-pause-menu bug is patched separately via EscapeMenuGuardPatch,
                // which targets the actual open call instead of touching the Cancel action
                // (Cancel/B needs to stay intact so it can still cancel in-progress build actions).

                // Build mode tools
                SetGamepadBinding(actions, "Catalog", Plugin.CfgCatalog.Value);
                SetGamepadBinding(actions, "BuildWall", Plugin.CfgBuildWall.Value);
                SetGamepadBinding(actions, "PipetteMode", Plugin.CfgPipetteMode.Value);
                SetGamepadBinding(actions, "SledgehammerMode", Plugin.CfgSledgehammerMode.Value);
                SetGamepadBinding(actions, "ToggleGrid", Plugin.CfgToggleGrid.Value);
                SetGamepadBinding(actions, "Delete", Plugin.CfgDelete.Value);
                // LargeRotateItem defaults to LB+X rather than rightStickPress - that button is
                // the virtual-cursor toggle, and a bare RS click with an item selected would
                // rotate it AND toggle the cursor at once. (X alone stays Delete.)
                SetGamepadBinding(actions, "LargeRotateItem", Plugin.CfgLargeRotateItem.Value);
                SetGamepadBinding(actions, "ToggleParaBuild", Plugin.CfgToggleParaBuild.Value);

                // General
                SetGamepadBinding(actions, "TownMap", Plugin.CfgTownMap.Value);
                SetGamepadBinding(actions, "PhotoMode", Plugin.CfgPhotoMode.Value);

                // Keyboard-only actions filled in from the binding audit. The D-pad combos are
                // window-gated via DPadBuildActions so they can't fire while a menu is open and
                // the D-pad is navigating. Bare-button overlap (e.g. Select alone = TownMap vs
                // RB+Select = FamilyTree) is resolved by the Input System's modifier-composite
                // priority, same as the game's own Tab vs Shift+Tab inspector bindings.
                SetGamepadBinding(actions, "Undo", Plugin.CfgUndo.Value);
                SetGamepadBinding(actions, "Redo", Plugin.CfgRedo.Value);
                SetGamepadBinding(actions, "QuickSave", Plugin.CfgQuickSave.Value);
                SetGamepadBinding(actions, "CenterCameraOnCharacter", Plugin.CfgCenterCameraOnCharacter.Value);
                SetGamepadBinding(actions, "PauseTime", Plugin.CfgPauseTime.Value);
                SetGamepadBinding(actions, "TimeSpeed0", Plugin.CfgTimeSpeed0.Value);
                SetGamepadBinding(actions, "TimeSpeed1", Plugin.CfgTimeSpeed1.Value);
                SetGamepadBinding(actions, "TimeSpeed2", Plugin.CfgTimeSpeed2.Value);
                SetGamepadBinding(actions, "FamilyTree", Plugin.CfgFamilyTree.Value);
                SetGamepadBinding(actions, "Calendar", Plugin.CfgCalendar.Value);
                SetGamepadBinding(actions, "LotMode", Plugin.CfgLotMode.Value);
                SetGamepadBinding(actions, "DuplicateItem", Plugin.CfgDuplicateItem.Value);

                // Note: SmallRotateItemLeft/Right are intentionally NOT bound here.
                // LB/RB already rotate the selected item natively - adding our own
                // binding on the same buttons double-fired the rotation.

                FixGamepadLookSensitivity(actions);
                SubscribeComboDiagnostics(actions);
                if (Plugin.DebugLogging)
                {
                    DumpGamepadBindingAudit(actions);
                }

                Plugin.Log.LogInfo("Controller Fix: Postfix complete.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Controller Fix: Postfix failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // Replaces any existing Gamepad-group bindings on the action with a single new one.
        // Uses ChangeBinding(i).Erase() (a real removal) rather than RemoveBindingOverride
        // (which only clears a runtime override and leaves the original binding active), and
        // is idempotent so re-running this patch (LoadAndApplyKeyRebindings can fire more than
        // once per session) never stacks duplicate bindings on the same button.
        //
        // "path1+path2" combos become a real OneModifier composite (same composite the game's
        // own keyboard rebind system builds for Ctrl+Z etc.). The old implementation passed the
        // raw "a+b" string to AddBinding, which is not a resolvable control path - every combo
        // binding was silently dead. An empty path just clears the gamepad bindings (the
        // GamepadBindingFallbackPatch keeps tooltips on the keyboard hint).
        private static void SetGamepadBinding(InputActionAsset actions, string actionName, string bindingPath)
        {
            var action = actions.FindAction(actionName);
            if (action == null)
            {
                Plugin.Log.LogWarning($"Controller Fix: Action '{actionName}' not found.");
                return;
            }

            // Collect first, erase back-to-front (indices shift on erase). Detect gamepad
            // bindings by control PATH, not groups: the game's native gamepad bindings carry
            // different groups strings (which is also why KeyRebindingManager's groups-based
            // lookup misses them), so a groups=="Gamepad" check left natives alive alongside
            // ours. Composite parents are identified by their first part; the game's keyboard
            // OneModifier composites are left alone.
            var toErase = new List<int>();
            for (int i = 0; i < action.bindings.Count; i++)
            {
                var b = action.bindings[i];
                if (b.isComposite)
                {
                    if (i + 1 < action.bindings.Count && action.bindings[i + 1].isPartOfComposite
                        && (action.bindings[i + 1].groups == "Gamepad" || XboxGlyphs.IsGamepadPath(action.bindings[i + 1].effectivePath)))
                    {
                        toErase.Add(i);
                    }
                }
                else if (!b.isPartOfComposite
                    && (b.groups == "Gamepad" || XboxGlyphs.IsGamepadPath(b.effectivePath)))
                {
                    toErase.Add(i);
                }
            }
            for (int i = toErase.Count - 1; i >= 0; i--)
            {
                // Erasing a composite parent also erases its parts.
                action.ChangeBinding(toErase[i]).Erase();
            }

            if (string.IsNullOrEmpty(bindingPath))
            {
                Plugin.Log.LogInfo($"Controller Fix: Cleared Gamepad bindings on '{actionName}' (configured empty).");
                return;
            }

            int plusIndex = bindingPath.IndexOf('+');
            if (plusIndex > 0)
            {
                string modifierPath = bindingPath.Substring(0, plusIndex);
                string buttonPath = bindingPath.Substring(plusIndex + 1);
                action.AddCompositeBinding("OneModifier")
                    .With("Modifier", modifierPath, "Gamepad")
                    .With("Binding", buttonPath, "Gamepad");
            }
            else
            {
                action.AddBinding(bindingPath, groups: "Gamepad");
            }
            Plugin.Log.LogInfo($"Controller Fix: Set '{actionName}' Gamepad binding -> '{bindingPath}'.");
        }

        // Temporary diagnostics: log when combo-bound actions actually perform, and from which
        // control - direct evidence of whether the OneModifier composites fire in-game.
        // (LoadAndApplyKeyRebindings re-runs, so unsubscribe stale handlers first.)
        private static readonly List<InputAction> _comboDiagActions = new List<InputAction>();

        private static void SubscribeComboDiagnostics(InputActionAsset actions)
        {
            foreach (var subscribed in _comboDiagActions)
            {
                subscribed.performed -= OnComboDiagPerformed;
            }
            _comboDiagActions.Clear();
            foreach (var name in new[] { "LargeRotateItem", "ToggleParaBuild", "BuildWall", "Undo", "Delete" })
            {
                var action = actions.FindAction(name);
                if (action != null)
                {
                    action.performed += OnComboDiagPerformed;
                    _comboDiagActions.Add(action);
                }
            }
        }

        private static void OnComboDiagPerformed(InputAction.CallbackContext ctx)
        {
            if (Plugin.DebugLogging)
            {
                Plugin.Log.LogInfo($"Controller Fix: [ComboDiag] '{ctx.action.name}' performed via '{ctx.control?.path}'.");
            }
        }

        private static InputAction _menuAction;

        // Nothing in the game reads HybridPlayer.ButtonMenu - it's populated by the "Menu"
        // action's own OnMenu SendMessage callback but never consumed, so Start currently does
        // nothing. Subscribe directly to the same InputAction the game already manages instead of
        // polling a gamepad every frame (LoadAndApplyKeyRebindings can re-run, so unsubscribe any
        // previous handler first to avoid firing the toggle twice per press).
        private static void SubscribeMenuToggle(InputActionAsset actions)
        {
            var menuAction = actions.FindAction("Menu");
            if (menuAction == null)
            {
                Plugin.Log.LogWarning("Controller Fix: Action 'Menu' not found, cannot wire up Start.");
                return;
            }

            if (_menuAction != null)
            {
                _menuAction.performed -= OnMenuPerformed;
            }
            _menuAction = menuAction;
            _menuAction.performed += OnMenuPerformed;
            Plugin.Log.LogInfo("Controller Fix: Subscribed to Menu action for Start-button pause toggle.");
        }

        private static void OnMenuPerformed(InputAction.CallbackContext ctx)
        {
            try
            {
                var device = ctx.control?.device;
                Plugin.Log.LogInfo($"Controller Fix: Menu action performed (control='{ctx.control?.displayName}', device='{device?.displayName}').");

                if (PlayerManager.Instance == null)
                {
                    Plugin.Log.LogWarning("Controller Fix: Menu performed but PlayerManager.Instance is null.");
                    return;
                }
                if (SavedGameManager.Instance == null || !SavedGameManager.Instance.HasLoadedSavedGame)
                {
                    Plugin.Log.LogInfo("Controller Fix: Menu performed but no saved game is loaded, ignoring.");
                    return;
                }

                foreach (var player in PlayerManager.Instance.Players)
                {
                    var escapeMenu = UI.Get<UIEscapeMenu>(player.PlayerIndex);
                    if (escapeMenu.IsVisible)
                    {
                        escapeMenu.Hide();
                        Plugin.Log.LogInfo($"Controller Fix: Hid pause menu for player {player.PlayerIndex}.");
                    }
                    else
                    {
                        escapeMenu.Show();
                        Plugin.Log.LogInfo($"Controller Fix: Showed pause menu for player {player.PlayerIndex}.");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Controller Fix: Menu toggle failed: {e.Message}\n{e.StackTrace}");
            }
        }

        // One-time diagnostic: log every action alongside its Keyboard-group and Gamepad-group
        // bindings, flagging actions that a keyboard player can use but a gamepad player cannot.
        // This is the ground truth for the "every PC binding needs a controller equivalent"
        // audit - the .inputactions asset isn't visible in the decompiled code, only at runtime.
        private static void DumpGamepadBindingAudit(InputActionAsset actions)
        {
            try
            {
                var report = new System.Text.StringBuilder("Controller Fix: [BindingAudit]\n");
                foreach (var action in actions)
                {
                    var keyboard = new List<string>();
                    var gamepad = new List<string>();
                    for (int i = 0; i < action.bindings.Count; i++)
                    {
                        var b = action.bindings[i];
                        string path = string.IsNullOrEmpty(b.overridePath) ? b.path : b.overridePath;
                        // Include the raw groups string: ground truth for why the game's
                        // groups-based lookups hit or miss a binding.
                        string entry = $"{path}(g:'{b.groups}')";
                        if (b.groups == "Gamepad" || path.Contains("<Gamepad>"))
                        {
                            gamepad.Add(entry);
                        }
                        else if (b.groups == "Keyboard" || path.Contains("<Keyboard>") || path.Contains("<Mouse>"))
                        {
                            keyboard.Add(entry);
                        }
                    }
                    string marker = (keyboard.Count > 0 && gamepad.Count == 0) ? " <-- NO GAMEPAD" : "";
                    report.AppendLine($"  {action.actionMap.name}/{action.name}: kb=[{string.Join(",", keyboard)}] gp=[{string.Join(",", gamepad)}]{marker}");
                }
                Plugin.Log.LogInfo(report.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Controller Fix: Binding audit failed: {e.Message}");
            }
        }

        private static void FixGamepadLookSensitivity(InputActionAsset actions)
        {
            var viewAction = actions["View"];
            if (viewAction == null)
            {
                Plugin.Log.LogWarning("Controller Fix: 'View' action not found.");
                return;
            }

            for (int i = 0; i < viewAction.bindings.Count; i++)
            {
                var b = viewAction.bindings[i];
                if (b.groups == "Gamepad" && b.path == "<Gamepad>/rightStick")
                {
                    viewAction.ApplyBindingOverride(i, new InputBinding
                    {
                        overrideProcessors = "scaleVector2(x=20,y=20)"
                    });
                    Plugin.Log.LogInfo($"Controller Fix: Applied sensitivity scale to View binding [{i}].");
                    return;
                }
            }

            Plugin.Log.LogWarning("Controller Fix: Gamepad rightStick binding not found on 'View' action.");
        }
    }
}