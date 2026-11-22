using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace HS2_QuickToolDock
{
    [BepInPlugin(GUID, PluginName, PluginVersion)]
    [BepInProcess("StudioNEOV2.exe")]
    [BepInProcess("StudioNEOV2")]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.user.hs2.quicktooldock";
        public const string PluginName = "Studio Quick-Tool Dock";
        public const string PluginVersion = "1.3.0";

        internal static new ManualLogSource Logger;

        // Configuration entries
        private ConfigEntry<KeyboardShortcut> _hotkeyConfig;
        private ConfigEntry<KeyboardShortcut> _snapHotkeyConfig;
        private ConfigEntry<float> _posXConfig;
        private ConfigEntry<float> _posYConfig;
        private ConfigEntry<bool> _isCollapsedConfig;
        private ConfigEntry<bool> _isVisibleConfig;

        // Window state
        private const int WindowId = 748291;
        private Rect _windowRect;
        private const float DockWidth = 185f;
        private const float CollapsedHeight = 32f;
        private const float ExpandedHeight = 330f;

        // Custom smooth dragging
        private bool _isDragging = false;
        private Vector2 _dragClickOffset;

        // Undo snap history
        private struct SnapHistoryEntry
        {
            public Studio.GuideObject Guide;
            public Vector3 OldWorldPos;
            public int Kind;
        }
        private readonly List<SnapHistoryEntry> _lastSnapHistory = new List<SnapHistoryEntry>();

        // Feedback / Notification
        private string _statusMessage = "";
        private float _statusTimer = 0f;

        // Custom IMGUI Styling
        private bool _stylesInitialized = false;
        private GUIStyle _windowStyle;
        private GUIStyle _headerTitleStyle;
        private GUIStyle _collapseBtnStyle;
        private GUIStyle _toolBtnStyle;
        private GUIStyle _snapBtnStyle;
        private GUIStyle _undoBtnStyle;
        private GUIStyle _statusLabelStyle;
        private GUIStyle _footerStyle;

        private Texture2D _texWindowBg;
        private Texture2D _texBtnNormal;
        private Texture2D _texBtnHover;
        private Texture2D _texBtnActive;
        private Texture2D _texSnapNormal;
        private Texture2D _texSnapHover;
        private Texture2D _texSnapActive;
        private Texture2D _texCollapseNormal;
        private Texture2D _texCollapseHover;

        private void Awake()
        {
            Logger = base.Logger;

            _hotkeyConfig = Config.Bind(
                "General", 
                "ToggleHotkey", 
                new KeyboardShortcut(KeyCode.F8), 
                "Hotkey to show or hide the Quick-Tool Dock."
            );

            _snapHotkeyConfig = Config.Bind(
                "General", 
                "SnapHotkey", 
                new KeyboardShortcut(KeyCode.G, KeyCode.LeftAlt), 
                "Hotkey to snap selected character/prop to the ground or surface below."
            );

            _posXConfig = Config.Bind(
                "Window", 
                "PositionX", 
                25f, 
                "Screen X position of the floating dock."
            );

            _posYConfig = Config.Bind(
                "Window", 
                "PositionY", 
                65f, 
                "Screen Y position of the floating dock."
            );

            _isCollapsedConfig = Config.Bind(
                "Window", 
                "IsCollapsed", 
                false, 
                "Whether the dock is collapsed to a minimal titlebar."
            );

            _isVisibleConfig = Config.Bind(
                "Window", 
                "IsVisible", 
                true, 
                "Whether the dock is currently visible."
            );

            float initialHeight = _isCollapsedConfig.Value ? CollapsedHeight : ExpandedHeight;
            _windowRect = new Rect(_posXConfig.Value, _posYConfig.Value, DockWidth, initialHeight);

            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded! F8 = Toggle Dock, Alt+G = Snap to Surface.");
        }

        private void Update()
        {
            if (_hotkeyConfig.Value.IsDown())
            {
                _isVisibleConfig.Value = !_isVisibleConfig.Value;
                if (_isVisibleConfig.Value)
                    SetStatus("Dock Shown (F8)");
            }

            if (_snapHotkeyConfig.Value.IsDown())
            {
                ActionSnapToSurface();
            }
        }

        private void OnGUI()
        {
            if (!_isVisibleConfig.Value)
                return;

            InitStylesIfNeeded();

            Vector2 mouseScreenPos = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool isMouseOverDock = _windowRect.Contains(mouseScreenPos);

            // Prevent Studio NEO V2 camera rotation or scene selection while interacting with the dock
            if (isMouseOverDock)
            {
                if (Input.GetMouseButton(0) || Input.GetMouseButtonDown(0) || _isDragging)
                {
                    Input.ResetInputAxes();
                }
            }

            // Smooth custom dragging handler
            HandleDockDrag(mouseScreenPos);

            float targetHeight = _isCollapsedConfig.Value ? CollapsedHeight : ExpandedHeight;
            _windowRect.width = DockWidth;
            _windowRect.height = targetHeight;

            // Clamp within screen bounds
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Mathf.Max(0f, Screen.width - _windowRect.width));
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Mathf.Max(0f, Screen.height - _windowRect.height));

            _windowRect = GUI.Window(WindowId, _windowRect, DrawWindowContent, GUIContent.none, _windowStyle);
        }

        private void HandleDockDrag(Vector2 mouseScreenPos)
        {
            Event e = Event.current;
            Rect headerScreenRect = new Rect(_windowRect.x, _windowRect.y, _windowRect.width - 26f, 28f);

            if (e.rawType == EventType.MouseDown && e.button == 0)
            {
                if (headerScreenRect.Contains(mouseScreenPos))
                {
                    _isDragging = true;
                    _dragClickOffset = mouseScreenPos - new Vector2(_windowRect.x, _windowRect.y);
                }
            }
            else if (e.rawType == EventType.MouseUp && e.button == 0)
            {
                if (_isDragging)
                {
                    _isDragging = false;
                    SavePositionIfChanged();
                }
            }

            if (_isDragging)
            {
                if (Input.GetMouseButton(0))
                {
                    _windowRect.x = mouseScreenPos.x - _dragClickOffset.x;
                    _windowRect.y = mouseScreenPos.y - _dragClickOffset.y;
                }
                else
                {
                    _isDragging = false;
                    SavePositionIfChanged();
                }
            }
        }

        private void SavePositionIfChanged()
        {
            if (Mathf.Abs(_windowRect.x - _posXConfig.Value) > 1f || Mathf.Abs(_windowRect.y - _posYConfig.Value) > 1f)
            {
                _posXConfig.Value = _windowRect.x;
                _posYConfig.Value = _windowRect.y;
            }
        }

        private void DrawWindowContent(int id)
        {
            // Top Header Bar
            GUILayout.BeginHorizontal();
            GUILayout.Label("⚡ Quick Dock", _headerTitleStyle, GUILayout.Height(20));

            string collapseLabel = _isCollapsedConfig.Value ? "▲" : "▼";
            if (GUILayout.Button(collapseLabel, _collapseBtnStyle, GUILayout.Width(22), GUILayout.Height(18)))
            {
                _isCollapsedConfig.Value = !_isCollapsedConfig.Value;
            }
            GUILayout.EndHorizontal();

            if (!_isCollapsedConfig.Value)
            {
                GUILayout.Space(5);

                // Action Buttons
                if (GUILayout.Button("👁  Toggle UI", _toolBtnStyle, GUILayout.Height(28)))
                {
                    ActionToggleUI();
                }

                GUILayout.Space(2);
                if (GUILayout.Button("🔄  Toggle IK / FK", _toolBtnStyle, GUILayout.Height(28)))
                {
                    ActionToggleIKFK();
                }

                GUILayout.Space(2);
                if (GUILayout.Button("↺  Reset Rotation", _toolBtnStyle, GUILayout.Height(28)))
                {
                    ActionResetRotation();
                }

                GUILayout.Space(2);

                // Ground & Surface Snap row (Snap Button + Quick Undo Button)
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("⬇  Snap to Surface", _snapBtnStyle, GUILayout.Height(30)))
                {
                    ActionSnapToSurface();
                }

                GUI.enabled = _lastSnapHistory.Count > 0;
                if (GUILayout.Button("↶", _undoBtnStyle, GUILayout.Width(26), GUILayout.Height(30)))
                {
                    ActionUndoSnap();
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();

                GUILayout.Space(2);
                if (GUILayout.Button("🦴  Open HS2PE", _toolBtnStyle, GUILayout.Height(28)))
                {
                    ActionToggleHS2PE();
                }

                GUILayout.Space(2);
                if (GUILayout.Button("⏱  Open Timeline", _toolBtnStyle, GUILayout.Height(28)))
                {
                    ActionToggleTimeline();
                }

                GUILayout.Space(5);

                // Status Toast / Bottom Info
                if (Time.realtimeSinceStartup < _statusTimer && !string.IsNullOrEmpty(_statusMessage))
                {
                    GUILayout.Label(_statusMessage, _statusLabelStyle, GUILayout.Height(16));
                }
                else
                {
                    GUILayout.Label("Alt+G Snap | F8 Dock", _footerStyle, GUILayout.Height(16));
                }
            }

            // IMGUI drag window fallback
            GUI.DragWindow(new Rect(0, 0, DockWidth - 26, 28));
        }

        private void SetStatus(string message)
        {
            _statusMessage = message;
            _statusTimer = Time.realtimeSinceStartup + 2.5f;
        }

        #region Ground & Surface Snapping

        /// <summary>
        /// Snaps selected characters and props flush to whatever surface is below them (colliders or mesh triangles),
        /// or falls back to world floor (Y=0) if in mid-air.
        /// </summary>
        private void ActionSnapToSurface()
        {
            try
            {
                // Collect selected guide objects & controllers
                var targets = GetSelectedTargets();
                if (targets.Count == 0)
                {
                    SetStatus("Select an object or character first!");
                    return;
                }

                _lastSnapHistory.Clear();
                var equalsList = new List<Studio.GuideCommand.EqualsInfo>();
                var affectedKinds = new HashSet<int>();
                int snappedCount = 0;

                // Cache scene renderers once for fast triangle/AABB intersection across collider-less maps/items
                MeshRenderer[] sceneMeshRenderers = null;

                foreach (var target in targets)
                {
                    Studio.GuideObject guide = target.Guide;
                    Transform rootTransform = target.RootTransform;

                    if (guide == null || rootTransform == null)
                        continue;

                    // 1. Calculate true lowest Y point (min.y) and horizontal bounds from active renderers
                    if (!GetTrueBottomBounds(rootTransform, out float minY, out Bounds compositeBounds, out HashSet<Renderer> selfRenderers))
                    {
                        // Fallback if no renderers found
                        minY = rootTransform.position.y;
                        compositeBounds = new Bounds(rootTransform.position, Vector3.one * 0.1f);
                        selfRenderers = new HashSet<Renderer>();
                    }

                    // 2. Generate multi-point downward test rays across the object's footprint
                    Vector2[] testPoints = GetFootprintSamplePoints(compositeBounds);

                    // 3. Find highest ground surface below the object
                    float highestSurfaceY = float.MinValue;
                    bool surfaceFound = false;

                    foreach (var pt in testPoints)
                    {
                        Vector3 rayOrigin = new Vector3(pt.x, minY + 0.15f, pt.y);
                        float testMaxDistance = 80f; // search down to 80 units

                        // Layer A: Standard Unity Colliders
                        Ray ray = new Ray(rayOrigin, Vector3.down);
                        RaycastHit[] hits = Physics.RaycastAll(ray, testMaxDistance);
                        foreach (var hit in hits)
                        {
                            if (hit.collider == null) continue;
                            if (hit.collider.transform.IsChildOf(rootTransform)) continue; // ignore self
                            if (hit.point.y <= minY + 0.05f && hit.point.y > highestSurfaceY)
                            {
                                highestSurfaceY = hit.point.y;
                                surfaceFound = true;
                            }
                        }

                        // Layer B: MeshRenderer / MeshFilter collider-less maps & furniture
                        if (sceneMeshRenderers == null)
                        {
                            sceneMeshRenderers = FindObjectsOfType<MeshRenderer>();
                        }

                        foreach (var mr in sceneMeshRenderers)
                        {
                            if (mr == null || !mr.enabled || !mr.gameObject.activeInHierarchy) continue;
                            if (selfRenderers.Contains(mr)) continue; // ignore self
                            if (mr.transform.IsChildOf(rootTransform)) continue; // ignore self hierarchy

                            Bounds mb = mr.bounds;
                            // Fast AABB check on horizontal ray position
                            if (pt.x < mb.min.x || pt.x > mb.max.x || pt.y < mb.min.z || pt.y > mb.max.z)
                                continue;

                            // Skip meshes whose topmost point is lower than current best
                            if (mb.max.y <= highestSurfaceY)
                                continue;

                            // Skip meshes that are entirely above the object's bottom
                            if (mb.min.y > minY + 0.05f)
                                continue;

                            var mf = mr.GetComponent<MeshFilter>();
                            bool hitMesh = false;
                            float meshHitY = float.MinValue;

                            if (mf != null && mf.sharedMesh != null && mf.sharedMesh.isReadable)
                            {
                                hitMesh = RayIntersectsMeshFilter(pt, rayOrigin.y, mf, out meshHitY);
                            }

                            // If mesh was not readable or triangle test didn't hit, use bounding box top as flat planar surface
                            if (!hitMesh)
                            {
                                meshHitY = mb.max.y;
                                hitMesh = true;
                            }

                            if (hitMesh && meshHitY <= minY + 0.05f && meshHitY > highestSurfaceY)
                            {
                                highestSurfaceY = meshHitY;
                                surfaceFound = true;
                            }
                        }
                    }

                    // 4. Calculate final target surface Y (detected surface or default world floor Y=0)
                    float targetY = surfaceFound ? highestSurfaceY : 0f;
                    float deltaY = targetY - minY;

                    // If already resting flush on surface, skip
                    if (Mathf.Abs(deltaY) < 0.001f)
                        continue;

                    Vector3 currentPos = guide.transformTarget != null ? guide.transformTarget.position : guide.changeAmount.pos;
                    Vector3 newPos = currentPos + new Vector3(0f, deltaY, 0f);

                    // Apply snap via GuideObject.SetWorldPos
                    var equalsInfo = guide.SetWorldPos(newPos);
                    if (equalsInfo != null)
                    {
                        equalsList.Add(equalsInfo);
                    }

                    _lastSnapHistory.Add(new SnapHistoryEntry
                    {
                        Guide = guide,
                        OldWorldPos = currentPos,
                        Kind = target.Kind
                    });

                    affectedKinds.Add(target.Kind);
                    snappedCount++;
                }

                // Register with Studio's native UndoRedoManager
                if (equalsList.Count > 0)
                {
                    var cmd = new Studio.GuideCommand.MoveEqualsCommand(equalsList.ToArray());
                    Studio.UndoRedoManager.Instance?.Push(cmd);
                }

                // Update Studio coordinate textboxes
                foreach (var k in affectedKinds)
                {
                    try
                    {
                        Studio.Studio.Instance?.manipulatePanelCtrl?.UpdateInfo(k);
                    }
                    catch { }
                }

                if (snappedCount > 0)
                {
                    SetStatus($"Snapped {snappedCount} item(s) to surface!");
                }
                else
                {
                    SetStatus("Object is already flush with surface");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"ActionSnapToSurface error: {ex.Message}\n{ex.StackTrace}");
                SetStatus("Snap Error");
            }
        }

        /// <summary>
        /// Undoes the last snap operation, reverting objects to their previous positions.
        /// </summary>
        private void ActionUndoSnap()
        {
            try
            {
                if (_lastSnapHistory.Count == 0)
                {
                    SetStatus("Nothing to undo");
                    return;
                }

                var undoEquals = new List<Studio.GuideCommand.EqualsInfo>();
                var affectedKinds = new HashSet<int>();

                foreach (var entry in _lastSnapHistory)
                {
                    if (entry.Guide != null)
                    {
                        var eq = entry.Guide.SetWorldPos(entry.OldWorldPos);
                        if (eq != null)
                        {
                            undoEquals.Add(eq);
                        }
                        affectedKinds.Add(entry.Kind);
                    }
                }

                if (undoEquals.Count > 0)
                {
                    var cmd = new Studio.GuideCommand.MoveEqualsCommand(undoEquals.ToArray());
                    Studio.UndoRedoManager.Instance?.Push(cmd);
                }

                foreach (var k in affectedKinds)
                {
                    try
                    {
                        Studio.Studio.Instance?.manipulatePanelCtrl?.UpdateInfo(k);
                    }
                    catch { }
                }

                int count = _lastSnapHistory.Count;
                _lastSnapHistory.Clear();
                SetStatus($"Reverted snap for {count} item(s)");
            }
            catch (Exception ex)
            {
                Logger.LogError($"ActionUndoSnap error: {ex.Message}");
                SetStatus("Undo Snap Error");
            }
        }

        private struct TargetInfo
        {
            public Studio.GuideObject Guide;
            public Transform RootTransform;
            public int Kind;
        }

        private List<TargetInfo> GetSelectedTargets()
        {
            var result = new List<TargetInfo>();
            var seenKeys = new HashSet<int>();

            // 1. Check tree node controller selections (Characters, Items, Folders)
            var selectCtrls = Studio.Studio.Instance?.treeNodeCtrl?.selectObjectCtrl;
            if (selectCtrls != null && selectCtrls.Length > 0)
            {
                foreach (var ctrl in selectCtrls)
                {
                    if (ctrl == null || ctrl.guideObject == null) continue;
                    int key = ctrl.guideObject.dicKey;
                    if (seenKeys.Add(key))
                    {
                        Transform root = ctrl.guideObject.transformTarget;
                        if (ctrl is Studio.OCIChar ociChar && ociChar.charInfo != null)
                        {
                            root = ociChar.charInfo.gameObject.transform;
                        }
                        else if (ctrl is Studio.OCIItem ociItem && ociItem.objectItem != null)
                        {
                            root = ociItem.objectItem.transform;
                        }

                        result.Add(new TargetInfo
                        {
                            Guide = ctrl.guideObject,
                            RootTransform = root,
                            Kind = ctrl.kind
                        });
                    }
                }
            }

            // 2. Check active GuideObjectManager selections if tree selection was empty
            if (result.Count == 0)
            {
                var selectGuides = Studio.GuideObjectManager.Instance?.selectObjects;
                if (selectGuides != null && selectGuides.Length > 0)
                {
                    foreach (var guide in selectGuides)
                    {
                        if (guide == null) continue;
                        if (seenKeys.Add(guide.dicKey))
                        {
                            result.Add(new TargetInfo
                            {
                                Guide = guide,
                                RootTransform = guide.transformTarget != null ? guide.transformTarget : guide.transform,
                                Kind = -1
                            });
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Calculates the exact lowest Y point from all visual SkinnedMeshRenderer and MeshRenderer components.
        /// </summary>
        private static bool GetTrueBottomBounds(Transform root, out float minY, out Bounds compositeBounds, out HashSet<Renderer> selfRenderers)
        {
            minY = float.MaxValue;
            compositeBounds = new Bounds();
            selfRenderers = new HashSet<Renderer>();
            bool hasValidBounds = false;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                selfRenderers.Add(r);

                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                if (r.gameObject.name.IndexOf("Guide", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (r.bounds.size.sqrMagnitude < 0.0001f) continue;

                Bounds b = r.bounds;
                if (b.min.y < minY)
                    minY = b.min.y;

                if (!hasValidBounds)
                {
                    compositeBounds = b;
                    hasValidBounds = true;
                }
                else
                {
                    compositeBounds.Encapsulate(b);
                }
            }

            return hasValidBounds;
        }

        /// <summary>
        /// Generates sample test coordinates across the horizontal footprint of an object.
        /// </summary>
        private static Vector2[] GetFootprintSamplePoints(Bounds bounds)
        {
            Vector3 c = bounds.center;
            Vector3 ext = bounds.extents;

            return new[]
            {
                new Vector2(c.x, c.z),                                        // Center
                new Vector2(c.x - ext.x * 0.6f, c.z),                         // Left side / foot
                new Vector2(c.x + ext.x * 0.6f, c.z),                         // Right side / foot
                new Vector2(c.x, c.z - ext.z * 0.6f),                         // Front
                new Vector2(c.x, c.z + ext.z * 0.6f)                          // Back
            };
        }

        /// <summary>
        /// Tests a downward vertical ray against all triangles of a MeshFilter in world space.
        /// </summary>
        private static bool RayIntersectsMeshFilter(Vector2 point, float rayStartY, MeshFilter mf, out float hitY)
        {
            hitY = float.MinValue;
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) return false;

            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            Matrix4x4 l2w = mf.transform.localToWorldMatrix;
            bool hit = false;

            float px = point.x;
            float pz = point.y;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 v0 = l2w.MultiplyPoint3x4(vertices[triangles[i]]);
                Vector3 v1 = l2w.MultiplyPoint3x4(vertices[triangles[i + 1]]);
                Vector3 v2 = l2w.MultiplyPoint3x4(vertices[triangles[i + 2]]);

                // Quick bounding test for triangle
                float minX = Mathf.Min(v0.x, Mathf.Min(v1.x, v2.x));
                float maxX = Mathf.Max(v0.x, Mathf.Max(v1.x, v2.x));
                if (px < minX || px > maxX) continue;

                float minZ = Mathf.Min(v0.z, Mathf.Min(v1.z, v2.z));
                float maxZ = Mathf.Max(v0.z, Mathf.Max(v1.z, v2.z));
                if (pz < minZ || pz > maxZ) continue;

                // 2D Point-in-triangle edge test on XZ plane
                float e0 = (px - v0.x) * (v1.z - v0.z) - (pz - v0.z) * (v1.x - v0.x);
                float e1 = (px - v1.x) * (v2.z - v1.z) - (pz - v1.z) * (v2.x - v1.x);
                float e2 = (px - v2.x) * (v0.z - v2.z) - (pz - v2.z) * (v0.x - v2.x);

                bool hasNeg = (e0 < -0.0001f) || (e1 < -0.0001f) || (e2 < -0.0001f);
                bool hasPos = (e0 > 0.0001f) || (e1 > 0.0001f) || (e2 > 0.0001f);

                if (hasNeg && hasPos)
                    continue;

                // Compute triangle plane normal
                Vector3 normal = Vector3.Cross(v1 - v0, v2 - v0);
                if (Mathf.Abs(normal.y) < 0.0001f)
                    continue; // Skip vertical walls

                // Compute Y coordinate on the triangle plane
                float triY = v0.y - ((normal.x * (px - v0.x) + normal.z * (pz - v0.z)) / normal.y);
                if (triY <= rayStartY && triY > hitY)
                {
                    hitY = triY;
                    hit = true;
                }
            }

            return hit;
        }

        #endregion

        #region Studio Actions

        /// <summary>
        /// Toggles Studio NEO UI canvases (integrates with HideAllUI when available, or toggles canvases directly).
        /// </summary>
        private void ActionToggleUI()
        {
            try
            {
                if (Chainloader.PluginInfos.TryGetValue("keelhauled.hideallui", out var pInfo) && pInfo.Instance != null)
                {
                    var toggleMethod = pInfo.Instance.GetType().GetMethod(
                        "ToggleUI", 
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                    );

                    if (toggleMethod != null)
                    {
                        toggleMethod.Invoke(toggleMethod.IsStatic ? null : pInfo.Instance, null);
                        SetStatus("UI Toggled (HideAllUI)");
                        return;
                    }
                }

                var canvases = FindObjectsOfType<Canvas>();
                int toggled = 0;
                foreach (var canvas in canvases)
                {
                    if (canvas == null) continue;
                    string cName = canvas.gameObject.name;
                    if (cName.IndexOf("Studio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cName.IndexOf("Canvas", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cName.IndexOf("Custom", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        canvas.enabled = !canvas.enabled;
                        toggled++;
                    }
                }

                SetStatus(toggled > 0 ? "UI Canvases Toggled" : "No UI Canvas Found");
            }
            catch (Exception ex)
            {
                Logger.LogError($"ActionToggleUI error: {ex.Message}");
                SetStatus("Toggle UI Error");
            }
        }

        /// <summary>
        /// Toggles between Inverse Kinematics (IK) and Forward Kinematics (FK) on selected characters.
        /// </summary>
        private void ActionToggleIKFK()
        {
            try
            {
                if (Chainloader.PluginInfos.TryGetValue("certain.lsp.togglefkik", out var tfkInfo) && tfkInfo.Instance != null)
                {
                    var modeMethod = tfkInfo.Instance.GetType().GetMethod(
                        "ActiveKinematicMode", 
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                    );

                    if (modeMethod != null && modeMethod.GetParameters().Length == 0)
                    {
                        modeMethod.Invoke(modeMethod.IsStatic ? null : tfkInfo.Instance, null);
                        SetStatus("IK / FK Toggled (Sync)");
                        return;
                    }
                }

                var selectCtrls = Studio.Studio.Instance?.treeNodeCtrl?.selectObjectCtrl;
                if (selectCtrls == null || selectCtrls.Length == 0)
                {
                    SetStatus("Select a character first!");
                    return;
                }

                int charCount = 0;
                string newMode = "IK";

                foreach (var ctrl in selectCtrls)
                {
                    if (ctrl is Studio.OCIChar ociChar)
                    {
                        bool isFK = ociChar.IsFKGroup(Studio.OIBoneInfo.BoneGroup.Body);
                        bool targetFK = !isFK;
                        newMode = targetFK ? "FK" : "IK";

                        var boneGroups = new[]
                        {
                            Studio.OIBoneInfo.BoneGroup.Body,
                            Studio.OIBoneInfo.BoneGroup.RightArm,
                            Studio.OIBoneInfo.BoneGroup.LeftArm,
                            Studio.OIBoneInfo.BoneGroup.RightLeg,
                            Studio.OIBoneInfo.BoneGroup.LeftLeg
                        };

                        foreach (var bg in boneGroups)
                        {
                            ociChar.ActiveFK(bg, targetFK, true);
                            ociChar.ActiveIK(bg, !targetFK, true);
                        }
                        charCount++;
                    }
                }

                if (charCount > 0)
                    SetStatus($"Switched to {newMode} ({charCount} chara)");
                else
                    SetStatus("Selected object is not a character");
            }
            catch (Exception ex)
            {
                Logger.LogError($"ActionToggleIKFK error: {ex.Message}");
                SetStatus("IK/FK Toggle Error");
            }
        }

        /// <summary>
        /// Resets rotation (zeros out X, Y, Z) for currently selected studio objects or individual bones.
        /// </summary>
        private void ActionResetRotation()
        {
            try
            {
                int resetCount = 0;

                var selectedGuides = Studio.GuideObjectManager.Instance?.selectObjects;
                if (selectedGuides != null && selectedGuides.Length > 0)
                {
                    foreach (var guide in selectedGuides)
                    {
                        if (guide != null && guide.changeAmount != null)
                        {
                            guide.changeAmount.rot = Vector3.zero;
                            guide.changeAmount.onChangeRot?.Invoke();
                            resetCount++;
                        }
                    }
                }

                var selectedCtrls = Studio.Studio.Instance?.treeNodeCtrl?.selectObjectCtrl;
                if (selectedCtrls != null && selectedCtrls.Length > 0)
                {
                    foreach (var ctrl in selectedCtrls)
                    {
                        if (ctrl?.guideObject?.changeAmount != null)
                        {
                            ctrl.guideObject.changeAmount.rot = Vector3.zero;
                            ctrl.guideObject.changeAmount.onChangeRot?.Invoke();
                            resetCount++;
                        }

                        if (ctrl != null)
                        {
                            try
                            {
                                Studio.Studio.Instance?.manipulatePanelCtrl?.UpdateInfo(ctrl.kind);
                            }
                            catch { }
                        }
                    }
                }

                if (resetCount > 0)
                    SetStatus($"Rotation Reset ({resetCount} items)");
                else
                    SetStatus("Nothing selected to reset");
            }
            catch (Exception ex)
            {
                Logger.LogError($"ActionResetRotation error: {ex.Message}");
                SetStatus("Reset Rotation Error");
            }
        }

        /// <summary>
        /// Opens or toggles HS2PE (Pose Editor) window via HSPE.MainWindow.
        /// </summary>
        private void ActionToggleHS2PE()
        {
            try
            {
                if (Chainloader.PluginInfos.TryGetValue("com.joan6694.illusionplugins.poseeditor", out var peInfo) && peInfo.Instance != null)
                {
                    var peAsm = peInfo.Instance.GetType().Assembly;
                    var mwType = peAsm.GetType("HSPE.MainWindow");
                    if (mwType != null)
                    {
                        var selfField = mwType.GetField("_self", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        object mwInstance = selfField?.GetValue(null);

                        if (mwInstance == null)
                        {
                            mwInstance = UnityEngine.Object.FindObjectOfType(mwType);
                        }

                        if (mwInstance != null)
                        {
                            var showUIProp = mwType.GetProperty("ShowUI", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (showUIProp != null)
                            {
                                bool isShown = (bool)showUIProp.GetValue(mwInstance, null);
                                showUIProp.SetValue(mwInstance, !isShown, null);
                                SetStatus(isShown ? "HS2PE Closed" : "HS2PE Opened");
                                return;
                            }
                        }
                    }
                }

                SetStatus("HS2PE MainWindow not found");
            }
            catch (Exception ex)
            {
                Logger.LogError($"ActionToggleHS2PE error: {ex.Message}");
                SetStatus("HS2PE Toggle Error");
            }
        }

        /// <summary>
        /// Opens or toggles Timeline window.
        /// </summary>
        private void ActionToggleTimeline()
        {
            try
            {
                if (Chainloader.PluginInfos.TryGetValue("com.joan6694.illusionplugins.timeline", out var tlInfo) && tlInfo.Instance != null)
                {
                    var type = tlInfo.Instance.GetType();

                    var toggleMethod = type.GetMethod(
                        "ToggleUiVisible", 
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                    );

                    if (toggleMethod != null)
                    {
                        toggleMethod.Invoke(toggleMethod.IsStatic ? null : tlInfo.Instance, null);
                        SetStatus("Timeline Toggled");
                        return;
                    }

                    var prop = type.GetProperty(
                        "InterfaceVisible", 
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                    );

                    if (prop != null)
                    {
                        bool isShown = (bool)prop.GetValue(tlInfo.Instance, null);
                        prop.SetValue(tlInfo.Instance, !isShown, null);
                        SetStatus(isShown ? "Timeline Closed" : "Timeline Opened");
                        return;
                    }
                }

                SetStatus("Timeline plugin not found");
            }
            catch (Exception ex)
            {
                Logger.LogError($"ActionToggleTimeline error: {ex.Message}");
                SetStatus("Timeline Toggle Error");
            }
        }

        #endregion

        #region IMGUI Styling Initialization

        private void InitStylesIfNeeded()
        {
            if (_stylesInitialized)
                return;

            Color colBg = new Color(0.09f, 0.10f, 0.13f, 0.94f);        // Deep charcoal slate
            Color colBorder = new Color(0.24f, 0.28f, 0.38f, 0.95f);    // Crisp accent border
            Color colBtn = new Color(0.14f, 0.16f, 0.22f, 0.92f);       // Dark surface button
            Color colBtnHover = new Color(0.22f, 0.26f, 0.36f, 1.0f);   // Button hover highlight
            Color colBtnActive = new Color(0.23f, 0.51f, 0.96f, 1.0f);  // Blue accent press

            // Snap button custom accent palette (teal/emerald)
            Color colSnap = new Color(0.10f, 0.32f, 0.28f, 0.95f);
            Color colSnapHover = new Color(0.14f, 0.44f, 0.38f, 1.0f);
            Color colSnapActive = new Color(0.20f, 0.65f, 0.55f, 1.0f);

            _texWindowBg = MakeBorderedTex(12, 12, colBg, colBorder);
            _texBtnNormal = MakeBorderedTex(8, 8, colBtn, new Color(0.20f, 0.24f, 0.32f, 0.8f));
            _texBtnHover = MakeBorderedTex(8, 8, colBtnHover, new Color(0.35f, 0.42f, 0.56f, 1.0f));
            _texBtnActive = MakeBorderedTex(8, 8, colBtnActive, Color.white);

            _texSnapNormal = MakeBorderedTex(8, 8, colSnap, new Color(0.25f, 0.55f, 0.48f, 0.9f));
            _texSnapHover = MakeBorderedTex(8, 8, colSnapHover, new Color(0.38f, 0.75f, 0.65f, 1.0f));
            _texSnapActive = MakeBorderedTex(8, 8, colSnapActive, Color.white);

            _texCollapseNormal = MakeTex(4, 4, new Color(0.18f, 0.21f, 0.28f, 0.9f));
            _texCollapseHover = MakeTex(4, 4, new Color(0.28f, 0.34f, 0.46f, 1.0f));

            // Window Style
            _windowStyle = new GUIStyle(GUI.skin.window)
            {
                normal = { background = _texWindowBg, textColor = Color.white },
                onNormal = { background = _texWindowBg, textColor = Color.white },
                padding = new RectOffset(8, 8, 6, 6),
                border = new RectOffset(4, 4, 4, 4),
                contentOffset = Vector2.zero
            };

            // Title Style
            _headerTitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.58f, 0.77f, 1.0f, 1.0f) }
            };

            // Collapse Button Style
            _collapseBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 10,
                alignment = TextAnchor.MiddleCenter,
                normal = { background = _texCollapseNormal, textColor = new Color(0.8f, 0.85f, 0.9f, 1f) },
                hover = { background = _texCollapseHover, textColor = Color.white },
                active = { background = _texBtnActive, textColor = Color.white },
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 1, 0)
            };

            // Tool Action Button Style
            _toolBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                normal = { background = _texBtnNormal, textColor = new Color(0.92f, 0.94f, 0.98f, 1.0f) },
                hover = { background = _texBtnHover, textColor = Color.white },
                active = { background = _texBtnActive, textColor = Color.white },
                padding = new RectOffset(8, 6, 0, 0),
                margin = new RectOffset(0, 0, 1, 1)
            };

            // Snap Button Style (Accent Emerald Highlight)
            _snapBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                normal = { background = _texSnapNormal, textColor = new Color(0.70f, 0.96f, 0.88f, 1.0f) },
                hover = { background = _texSnapHover, textColor = Color.white },
                active = { background = _texSnapActive, textColor = Color.white },
                padding = new RectOffset(8, 6, 0, 0),
                margin = new RectOffset(0, 0, 1, 1)
            };

            // Undo Snap Mini Button Style
            _undoBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                normal = { background = _texBtnNormal, textColor = new Color(0.85f, 0.88f, 0.92f, 1.0f) },
                hover = { background = _texBtnHover, textColor = Color.white },
                active = { background = _texBtnActive, textColor = Color.white },
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(2, 0, 1, 1)
            };

            // Status Label Style
            _statusLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 10,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.32f, 0.88f, 0.62f, 1.0f) }
            };

            // Footer Hotkey Hint Style
            _footerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Normal,
                fontSize = 9,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.48f, 0.54f, 0.64f, 0.85f) }
            };

            _stylesInitialized = true;
        }

        private static Texture2D MakeTex(int width, int height, Color col)
        {
            Color[] pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; ++i)
                pixels[i] = col;

            Texture2D tex = new Texture2D(width, height);
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private static Texture2D MakeBorderedTex(int width, int height, Color bg, Color border)
        {
            Color[] pixels = new Color[width * height];
            for (int y = 0; y < height; ++y)
            {
                for (int x = 0; x < width; ++x)
                {
                    bool isBorder = (x == 0 || x == width - 1 || y == 0 || y == height - 1);
                    pixels[y * width + x] = isBorder ? border : bg;
                }
            }

            Texture2D tex = new Texture2D(width, height);
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        #endregion
    }
}
