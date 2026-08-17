using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AceLand.Injection.Editor.Graph
{
    internal sealed class InjectionGraphWindow : EditorWindow
    {
        private const string LIVE_KEY = "AceLand.Injection.Graph.Live";
        private const string AUTO_KEY = "AceLand.Injection.Graph.AutoRefresh";
        private const string AUTO_SCAN_KEY = "AceLand.Injection.Graph.AutoScan";
        private const string PKG_KEY = "AceLand.Injection.Graph.GroupByPackage";
        private const string COLLAPSE_PFX = "AceLand.Injection.Graph.Collapsed.";
        private const string SEARCH_CONTROL = "AceLand.Injection.Graph.Search";
        private const double HIERARCHY_DEBOUNCE = 0.4;
        private static readonly bool VERTICAL_CENTER = false;

        // ── layout metrics ──
        private const float GROUP_WIDTH  = 420f;
        private const float GROUP_HEADER = 38f;
        private const float GROUP_PAD    = 14f;
        private const float GROUP_GAP    = 72f;
        private const float NODE_HEIGHT  = 72f;
        private const float NODE_GAP     = 0f;
        private const float ACCENT_WIDTH = 8f;
        private const float INSPECTOR_WIDTH = 336f;
        private const float MARGIN = 26f;
        private const float GROUP_STACK_GAP  = 26f;
        private const float EMPTY_BODY_HEIGHT = 30f;

        // ── sub-group (band) metrics ──
        private const float BAND_HEADER    = 21f;
        private const float BAND_TITLE_GAP = 5f;
        private const float BAND_GAP       = 9f;
        private const float ARROW_W        = 20f;
        private const float ARROW_BOX      = 18f;
        private const int   BAND_MIN_NODES = 10;      // don't band a short column
        private const string UNGROUPED     = "Project";

        private InjectionGraph _graph;
        private Vector2 _pan = new(MARGIN, MARGIN);
        private float _zoom = 1f;
        private string _filter = "";
        private bool _showConsumers = true;
        private bool _errorsOnly;
        private bool _live;
        private bool _autoRefresh;              // poll while live is active
        private double _nextAutoRefresh;
        private int _pendingScanRetries;

        private bool _autoScan;                 // rescan on hierarchy change (edit mode)
        private bool _hierarchyDirty;
        private double _hierarchyDirtyAt;
        private bool _scanning;                 // reentrancy guard

        private bool _groupByPackage;

        private GraphNode  _selected;
        private GraphGroup _selectedGroup;
        private GraphNode  _hover;
        private GraphGroup _hoverGroup;
        private Vector2 _inspectorScroll;

        private bool LiveActive => _live && Application.isPlaying;
        private bool Filtering => !string.IsNullOrEmpty(_filter) || _errorsOnly;

        /// <summary>A horizontal band inside a group: optional header + its nodes.</summary>
        private sealed class Band
        {
            public string Key;                      // stable id for collapse state; null = not collapsible
            public string Label;                    // null = no header drawn (flat group)
            public Rect   Rect;                     // header rect, WORLD space
            public int    Total;                    // registrations before filtering
            public readonly List<GraphNode> Nodes = new();
            public bool   Collapsed;
        }

        private readonly Dictionary<string, List<Band>> _bands = new();

        [MenuItem("Tools/AceLand/Injection/Graph %#g")]
        public static void Open()
        {
            var window = GetWindow<InjectionGraphWindow>("Injection Graph");
            window.minSize = new Vector2(980, 540);
            window.Show();
            window.Scan();
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded() => GraphOrigin.ClearAllCaches();

        private void OnEnable()
        {
            wantsMouseMove = true;
            _live = EditorPrefs.GetBool(LIVE_KEY, true);
            _autoRefresh = EditorPrefs.GetBool(AUTO_KEY, true);
            _autoScan = EditorPrefs.GetBool(AUTO_SCAN_KEY, true);
            _groupByPackage = EditorPrefs.GetBool(PKG_KEY, true);
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorPrefs.SetBool(LIVE_KEY, _live);
            EditorPrefs.SetBool(AUTO_KEY, _autoRefresh);
            EditorPrefs.SetBool(AUTO_SCAN_KEY, _autoScan);
            EditorPrefs.SetBool(PKG_KEY, _groupByPackage);
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnHierarchyChange()
        {
            if (!_autoScan) return;
            if (_scanning) return;                  // our own installers touched the hierarchy
            if (Application.isPlaying) return;      // live mode already polls; spawns would thrash

            _hierarchyDirty = true;
            _hierarchyDirtyAt = EditorApplication.timeSinceStartup + HIERARCHY_DEBOUNCE;
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.ExitingPlayMode:
                case PlayModeStateChange.EnteredEditMode:
                    ClearGraph();                              // drop refs to dying objects
                    break;

                case PlayModeStateChange.EnteredPlayMode:
                    ClearGraph();
                    if (_live) _pendingScanRetries = 12;       // wait for scopes to Awake
                    break;
            }
            Repaint();
        }

        private void ClearGraph()
        {
            _graph = null;
            _selected = null;
            _selectedGroup = null;
            _hover = null;
            _hoverGroup = null;
            _bands.Clear();
            _pendingScanRetries = 0;
        }

        private void OnEditorUpdate()
        {
            if (_pendingScanRetries > 0)
            {
                _pendingScanRetries--;
                if (DI.IsGlobalBuilt || _pendingScanRetries == 0)
                {
                    _pendingScanRetries = 0;
                    Scan();
                }
                return;
            }

            // coalesced hierarchy rescan
            if (_hierarchyDirty && EditorApplication.timeSinceStartup >= _hierarchyDirtyAt)
            {
                _hierarchyDirty = false;
                Scan(preserveView: true, clearCaches: false);
                return;
            }

            if (!_autoRefresh || !LiveActive) return;
            if (EditorApplication.timeSinceStartup < _nextAutoRefresh) return;

            _nextAutoRefresh = EditorApplication.timeSinceStartup + 1.0;
            Scan(preserveView: true, clearCaches: false);
        }

        // ══════════════════════════════════════════════════════ GUI

        private void OnGUI()
        {
            DrawToolbar();

            var top = EditorStyles.toolbar.fixedHeight;
            var canvas = new Rect(0, top, position.width - INSPECTOR_WIDTH, position.height - top);
            var inspector = new Rect(canvas.xMax, top, INSPECTOR_WIDTH, position.height - top);

            if (_graph == null)
            {
                EditorGUI.DrawRect(canvas, GraphStyles.CanvasBg);
                var help = new Rect(canvas.x + 28, canvas.y + 28, canvas.width - 56, 132);
                EditorGUI.HelpBox(help,
                    Application.isPlaying
                        ? (_live
                            ? "Press Refresh to read the running containers."
                            : "Live is off — Refresh analyses the loaded scenes statically.\n" +
                              "Turn Live on to inspect the actual running containers.")
                        : (_live
                            ? "Live is armed. Refresh analyses the scene statically until you enter Play mode."
                            : "Press Refresh to analyse the active scene.\n\n" +
                              "Turn Live on to read the running containers once you enter Play mode."),
                    MessageType.Info);
                DrawInspector(inspector);
                return;
            }

            HandleInput(canvas);
            DrawCanvas(canvas);
            DrawInspector(inspector);
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                // ── always enabled ──
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(62)))
                    Scan();

                // ── Live: off / armed / active ──
                var liveStyle = new GUIStyle(EditorStyles.toolbarButton);
                if (_live)
                    liveStyle.normal.textColor = LiveActive
                        ? GraphStyles.LiveDot                          // green: reading live containers
                        : new Color(0.70f, 0.70f, 0.74f);              // grey: armed, waiting for Play

                var liveLabel = LiveActive ? "◉ Live" : "Live";
                var liveTip = LiveActive
                    ? "Reading the running containers."
                    : _live
                        ? "Armed — switches to live data when you enter Play mode."
                        : "Analyse scene assets statically.";

                var live = GUILayout.Toggle(_live, new GUIContent(liveLabel, liveTip),
                                            liveStyle, GUILayout.Width(54));
                if (live != _live)
                {
                    _live = live;
                    EditorPrefs.SetBool(LIVE_KEY, _live);
                    if (_graph != null) Scan(preserveView: true);
                }

                // ── auto-refresh, only meaningful while live ──
                using (new EditorGUI.DisabledScope(!LiveActive))
                {
                    var auto = GUILayout.Toggle(_autoRefresh,
                        new GUIContent("⟳", "Re-scan every second while live."),
                        EditorStyles.toolbarButton, GUILayout.Width(26));
                    if (auto != _autoRefresh)
                    {
                        _autoRefresh = auto;
                        EditorPrefs.SetBool(AUTO_KEY, _autoRefresh);
                    }
                }

                // ── auto-scan is an EDIT mode feature, so it is enabled when not playing ──
                using (new EditorGUI.DisabledScope(Application.isPlaying))
                {
                    var autoScan = GUILayout.Toggle(_autoScan,
                        new GUIContent("⤾", "Rescan automatically when the hierarchy changes (edit mode)."),
                        EditorStyles.toolbarButton, GUILayout.Width(26));
                    if (autoScan != _autoScan)
                    {
                        _autoScan = autoScan;
                        EditorPrefs.SetBool(AUTO_SCAN_KEY, _autoScan);
                    }
                }

                using (new EditorGUI.DisabledScope(_graph == null))
                {
                    if (GUILayout.Button("Frame All", EditorStyles.toolbarButton, GUILayout.Width(70)))
                        FrameAll();

                    GUILayout.Space(10);
                    GUILayout.Label("Zoom", EditorStyles.miniLabel, GUILayout.Width(36));
                    _zoom = GUILayout.HorizontalSlider(_zoom, 0.3f, 2f, GUILayout.Width(150));

                    GUILayout.Space(10);
                    var consumers = GUILayout.Toggle(_showConsumers, "Consumers",
                        EditorStyles.toolbarButton, GUILayout.Width(76));
                    if (consumers != _showConsumers)
                    {
                        _showConsumers = consumers;
                        Layout();
                    }

                    var byPkg = GUILayout.Toggle(_groupByPackage,
                        new GUIContent("Pkg", "Group registrations by originating package."),
                        EditorStyles.toolbarButton, GUILayout.Width(38));
                    if (byPkg != _groupByPackage)
                    {
                        _groupByPackage = byPkg;
                        EditorPrefs.SetBool(PKG_KEY, _groupByPackage);
                        Layout();
                        Repaint();
                    }

                    if (GUILayout.Button(new GUIContent("⊟", "Collapse all package bands."),
                                         EditorStyles.toolbarButton, GUILayout.Width(24)))
                        SetAllBands(true);
                    if (GUILayout.Button(new GUIContent("⊞", "Expand all bands and scopes."),
                                         EditorStyles.toolbarButton, GUILayout.Width(24)))
                        SetAllBands(false);
                }

                GUILayout.FlexibleSpace();

                DrawSearchField(190f);

                var issues = _graph?.IssueCount ?? 0;
                var issueStyle = new GUIStyle(EditorStyles.toolbarButton);
                if ((_graph?.ErrorCount ?? 0) > 0) issueStyle.normal.textColor = GraphStyles.ErrorHue;
                else if (issues > 0) issueStyle.normal.textColor = new Color(0.95f, 0.78f, 0.42f);

                if (GUILayout.Button($"Issues ({issues})", issueStyle, GUILayout.Width(84)))
                {
                    _errorsOnly = !_errorsOnly;
                    Layout();
                    FocusFirstIssue();
                }

                var statusStyle = new GUIStyle(EditorStyles.miniLabel);
                if (LiveActive) statusStyle.normal.textColor = GraphStyles.LiveDot;
                GUILayout.Label(StatusLabel, statusStyle, GUILayout.Width(60));
            }
        }

        private string StatusLabel
        {
            get
            {
                if (LiveActive) return _autoRefresh ? "live ⟳" : "live";
                return _live ? "armed" : "static";
            }
        }

        // ══════════════════════════════════════════════════════ search field

        private static GUIStyle _cancelStyle, _cancelEmptyStyle;

        private static GUIStyle FindStyle(params string[] names)
        {
            foreach (var n in names)
            {
                var s = GUI.skin.FindStyle(n);
                if (s != null) return s;
            }
            return EditorStyles.miniButton;   // ugly, but still clickable
        }

        // style names differ by Unity version — the old one is famously misspelled
        private static GUIStyle CancelStyle => _cancelStyle ??=
            FindStyle("ToolbarSearchCancelButton", "ToolbarSeachCancelButton");
        private static GUIStyle CancelEmptyStyle => _cancelEmptyStyle ??=
            FindStyle("ToolbarSearchCancelButtonEmpty", "ToolbarSeachCancelButtonEmpty");

        private void DrawSearchField(float width)
        {
            const float CANCEL_W = 15f;

            var rect = GUILayoutUtility.GetRect(width, EditorGUIUtility.singleLineHeight,
                                                EditorStyles.toolbarSearchField, GUILayout.Width(width));
            var fieldRect  = new Rect(rect.x, rect.y, rect.width - CANCEL_W, rect.height);
            var cancelRect = new Rect(rect.xMax - CANCEL_W, rect.y, CANCEL_W, rect.height);

            if (Event.current.type == EventType.KeyDown &&
                Event.current.keyCode == KeyCode.Escape &&
                GUI.GetNameOfFocusedControl() == SEARCH_CONTROL)
            {
                ClearFilter();
                Event.current.Use();
                return;
            }

            GUI.SetNextControlName(SEARCH_CONTROL);
            EditorGUI.BeginChangeCheck();
            var next = EditorGUI.TextField(fieldRect, _filter, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck()) ApplyFilter(next);

            if (!string.IsNullOrEmpty(_filter))
            {
                if (GUI.Button(cancelRect, GUIContent.none, CancelStyle)) ClearFilter();
                EditorGUIUtility.AddCursorRect(cancelRect, MouseCursor.Arrow);
            }
            else if (Event.current.type == EventType.Repaint)
            {
                GUI.Label(cancelRect, GUIContent.none, CancelEmptyStyle);
            }
        }

        private void ApplyFilter(string next)
        {
            if (next == _filter) return;
            _filter = next;
            Layout();                       // ← the line that kills the gaps
            Repaint();
        }

        private void ClearFilter()
        {
            _filter = "";
            GUIUtility.keyboardControl = 0;  // else the field keeps painting stale text
            Layout();
            Repaint();
        }

        // ══════════════════════════════════════════════════════ collapse state

        private static string GroupCollapseKey(GraphGroup g) => g.Id + "/#group";
        private static bool IsCollapsed(string key) => SessionState.GetBool(COLLAPSE_PFX + key, false);
        private static void SetCollapsed(string key, bool value) => SessionState.SetBool(COLLAPSE_PFX + key, value);

        private List<Band> Bands(GraphGroup g)
            => g != null && _bands.TryGetValue(g.Id, out var b) ? b : null;

        /// <summary>A collapsed group still opens itself when a filter has hits inside it.</summary>
        private bool IsGroupCollapsed(GraphGroup group, List<Band> bands)
        {
            if (!IsCollapsed(GroupCollapseKey(group))) return false;
            if (Filtering && bands != null && bands.Any(b => b.Nodes.Count > 0)) return false;
            return true;
        }

        private void Toggle(string key)
        {
            SetCollapsed(key, !IsCollapsed(key));
            Layout();
            Repaint();
        }

        private void SetAllBands(bool collapsed)
        {
            foreach (var pair in _bands)
                foreach (var band in pair.Value)
                    if (band.Key != null && band.Label != null) SetCollapsed(band.Key, collapsed);

            if (!collapsed && _graph != null)
                foreach (var group in _graph.Groups) SetCollapsed(GroupCollapseKey(group), false);

            Layout();
            Repaint();
        }

        // ══════════════════════════════════════════════════════ scan

        private void Scan(bool preserveView = false, bool clearCaches = true)
        {
            if (_scanning) return;
            _scanning = true;
            try
            {
                if (clearCaches) GraphOrigin.ClearCaches();

                var previousNodeId = _selected?.Id;
                var previousGroupId = _selectedGroup?.Id;
                var previousPan = _pan;
                var previousZoom = _zoom;
                var hadGraph = _graph != null;

                _graph = LiveActive
                    ? InjectionGraphBuilder.FromRuntime()
                    : InjectionGraphBuilder.FromLoadedScenes(DescribeLoadedScenes());

                _hover = null;
                _hoverGroup = null;
                Layout();

                if (preserveView && hadGraph)
                {
                    _pan = previousPan;
                    _zoom = previousZoom;
                    _selected = previousNodeId != null ? _graph.Find(previousNodeId) : null;
                    _selectedGroup = previousGroupId != null ? _graph.FindGroup(previousGroupId) : null;
                }
                else
                {
                    _selected = null;
                    _selectedGroup = null;
                    FrameAll();
                }

                _hierarchyDirty = false;         // this scan already covers it
                Repaint();
            }
            finally { _scanning = false; }
        }

        private static string DescribeLoadedScenes()
        {
            var names = new List<string>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                names.Add(string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name);
            }
            return names.Count == 0 ? "no scene" : string.Join(" + ", names);
        }

        // ══════════════════════════════════════════════════════ filtering + banding

        private bool PassesFilter(GraphNode node)
        {
            if (!_showConsumers && node.Kind == NodeKind.Consumer) return false;
            if (_errorsOnly && !node.HasError) return false;
            if (string.IsNullOrEmpty(_filter)) return true;

            if (Matches(node)) return true;

            // keep an installer on screen when one of its registrations matched,
            // otherwise a filtered column loses the thing that explains it
            return node.Kind == NodeKind.Installer
                   && _graph.ProvidedBy(node.Id).Any(Matches);

            bool Matches(GraphNode n)
            {
                var haystack = n.Title + " " + n.Subtitle + " " + n.Origin + " " +
                               string.Join(" ", n.Contracts);
                return haystack.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static string BandLabel(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return UNGROUPED;

            var s = origin.Replace('\\', '/');

            if (s.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring("Packages/".Length);
                var slash = s.IndexOf('/');
                if (slash > 0) s = s.Substring(0, slash);
                return s;
            }

            if (s.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return UNGROUPED;
            return s;
        }

        private string SubgroupKey(GraphNode node)
        {
            // The installer's origin, not the node's: contracts often live in a separate
            // abstractions assembly from the implementation, which would split one package
            // across two bands. The installer owns the registration.
            if (node.InstallerNodeIds.Count > 0)
            {
                var installer = _graph.Find(node.PrimaryInstallerId);
                if (installer != null && !string.IsNullOrEmpty(installer.Origin))
                    return BandLabel(installer.Origin);
            }
            return BandLabel(node.Origin);
        }

        private List<Band> BuildBands(GraphGroup group)
        {
            var matches = OrderedNodes(group).Where(PassesFilter).ToList();

            var eligible = _groupByPackage
                           && group.IsScope
                           && group.Nodes.Count(n => n.Kind == NodeKind.Registration) >= BAND_MIN_NODES;

            if (!eligible) return Flat(matches);

            var bands = new List<Band>();

            var installers = matches.Where(n => n.Kind == NodeKind.Installer).ToList();
            if (installers.Count > 0)
                bands.Add(MakeBand(group, "installers", "Installers", installers,
                                   group.Nodes.Count(n => n.Kind == NodeKind.Installer)));

            var totals = group.Nodes
                .Where(n => n.Kind != NodeKind.Installer)
                .GroupBy(SubgroupKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var packages = matches
                .Where(n => n.Kind != NodeKind.Installer)
                .GroupBy(SubgroupKey, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => string.Equals(g.Key, UNGROUPED, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            // one band is just noise — fall back to flat
            if (packages.Count <= 1 && installers.Count == 0) return Flat(matches);

            foreach (var pkg in packages)
                bands.Add(MakeBand(group, pkg.Key, pkg.Key, pkg.ToList(),
                                   totals.TryGetValue(pkg.Key, out var t) ? t : pkg.Count()));

            return bands;

            static List<Band> Flat(List<GraphNode> nodes)
            {
                var band = new Band { Key = null, Label = null, Total = nodes.Count };
                band.Nodes.AddRange(nodes);
                return new List<Band> { band };
            }
        }

        private Band MakeBand(GraphGroup group, string key, string label,
                              List<GraphNode> nodes, int total)
        {
            var id = group.Id + "/" + key;
            var collapsed = IsCollapsed(id);

            // a collapsed band must not swallow search hits, or search looks broken.
            // the user's own choice is untouched in SessionState and returns when the filter clears.
            if (collapsed && Filtering && nodes.Count > 0) collapsed = false;

            var band = new Band { Key = id, Label = label, Total = total, Collapsed = collapsed };
            band.Nodes.AddRange(nodes);
            return band;
        }

        // ══════════════════════════════════════════════════════ layout

        private void Layout()
        {
            if (_graph == null) return;

            // nothing keeps a stale position: every rect is re-earned each pass
            foreach (var n in _graph.Nodes) n.Rect = Rect.zero;
            _bands.Clear();

            // ── assign columns from depth ──
            var scopes = _graph.Groups.Where(g => g.IsScope).ToList();
            var maxDepth = scopes.Count > 0 ? scopes.Max(g => g.Depth) : -1;
            foreach (var g in scopes) g.Column = g.Depth;

            var next = maxDepth + 1;
            var unresolved = _graph.FindGroup("group:unresolved");
            if (unresolved != null) unresolved.Column = next++;
            var consumers = _graph.FindGroup("group:consumers");
            if (consumers != null) consumers.Column = next;

            var visible = _graph.Groups
                .Where(g => g.Id != "group:consumers" || _showConsumers)
                .ToList();

            foreach (var g in visible) _bands[g.Id] = BuildBands(g);

            // ── measure with the same routine that places, so they cannot drift ──
            var measured = visible.ToDictionary(
                g => g.Id,
                g => LayoutGroupBody(g, _bands[g.Id], IsGroupCollapsed(g, _bands[g.Id]), 0, 0, place: false));

            foreach (var g in visible) g.Rect = new Rect(0, 0, GROUP_WIDTH, measured[g.Id]);

            var columns = visible.GroupBy(g => g.Column).OrderBy(c => c.Key).ToList();

            var heights = columns
                .Select(c => c.Sum(g => measured[g.Id]) + (c.Count() - 1) * GROUP_STACK_GAP)
                .ToList();
            var tallest = heights.Count > 0 ? heights.Max() : 0f;

            // ── place ──
            float x = 0;
            for (var i = 0; i < columns.Count; i++)
            {
                // meatier groups on top; empty ones drop to the bottom where they're easy to spot
                var column = columns[i]
                    .OrderByDescending(g => g.Nodes.Count)
                    .ThenBy(g => g.Title, StringComparer.Ordinal)
                    .ThenBy(g => g.Id, StringComparer.Ordinal)
                    .ToList();

                var y = VERTICAL_CENTER
                    ? (tallest - heights[i]) * 0.5f
                    : 0;

                foreach (var group in column)
                {
                    var bands = _bands[group.Id];
                    var height = LayoutGroupBody(group, bands, IsGroupCollapsed(group, bands),
                                                 x, y, place: true);

                    group.Rect = new Rect(x, y, GROUP_WIDTH, height);
                    group.HeaderRect = new Rect(x, y, GROUP_WIDTH, GROUP_HEADER);

                    y += height + GROUP_STACK_GAP;
                }

                x += GROUP_WIDTH + GROUP_GAP;
            }

            // ── groups excluded this pass must not keep last pass's rect ──
            foreach (var g in _graph.Groups.Except(visible))
            {
                g.Rect = Rect.zero;
                g.HeaderRect = Rect.zero;
            }
        }

        /// <summary>Places (or just measures) one group's bands. Returns total group height.</summary>
        private float LayoutGroupBody(GraphGroup group, List<Band> bands, bool collapsed,
                                     float x, float y, bool place)
        {
            if (collapsed)
            {
                if (place)
                    foreach (var band in bands)
                    {
                        band.Rect = Rect.zero;
                        foreach (var n in band.Nodes) n.Rect = Rect.zero;
                    }
                return GROUP_HEADER;
            }

            var innerX = x + GROUP_PAD;
            var innerW = GROUP_WIDTH - GROUP_PAD * 2f;
            var top = y + GROUP_HEADER + GROUP_PAD;
            var cursor = top;

            for (var i = 0; i < bands.Count; i++)
            {
                var band = bands[i];
                if (i > 0) cursor += BAND_GAP;

                if (band.Label != null)
                {
                    if (place) band.Rect = new Rect(innerX, cursor, innerW, BAND_HEADER);
                    cursor += BAND_HEADER + BAND_TITLE_GAP;
                }
                else if (place) band.Rect = Rect.zero;

                if (band.Collapsed)
                {
                    if (place)
                        foreach (var n in band.Nodes) n.Rect = Rect.zero;
                    continue;
                }

                foreach (var n in band.Nodes)
                {
                    if (place) n.Rect = new Rect(innerX, cursor, innerW, NODE_HEIGHT);
                    cursor += NODE_HEIGHT + NODE_GAP;
                }
                if (band.Nodes.Count > 0) cursor -= NODE_GAP;
            }

            var body = cursor - top;
            if (body <= 0f) body = EMPTY_BODY_HEIGHT;
            return GROUP_HEADER + GROUP_PAD + body + GROUP_PAD;
        }

        private static IEnumerable<GraphNode> OrderedNodes(GraphGroup group)
            => group.Nodes
                .OrderBy(n => n.Kind == NodeKind.Installer ? 0 : 1)
                .ThenByDescending(n => n.HasError)
                .ThenBy(n => n.Title, StringComparer.Ordinal)
                .ThenBy(n => n.Id, StringComparer.Ordinal);

        private void FrameAll()
        {
            if (_graph == null || _graph.Groups.Count == 0) return;

            var rects = _graph.Groups.Where(g => g.Rect.width > 0).Select(g => g.Rect).ToList();
            if (rects.Count == 0) return;

            var bounds = rects.Aggregate((a, b) => Rect.MinMaxRect(
                Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin),
                Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax)));

            var view = new Vector2(position.width - INSPECTOR_WIDTH - MARGIN * 2f,
                                   position.height - EditorStyles.toolbar.fixedHeight - MARGIN * 2f);

            _zoom = Mathf.Clamp(Mathf.Min(view.x / bounds.width, view.y / bounds.height), 0.3f, 1f);
            _pan = new Vector2(MARGIN - bounds.xMin * _zoom, MARGIN - bounds.yMin * _zoom);
            Repaint();
        }

        private void FocusFirstIssue()
        {
            var errorNode = _graph?.Nodes.FirstOrDefault(n => n.HasError && n.Rect.width > 0);
            Rect focus;

            if (errorNode != null) { SelectNode(errorNode); focus = errorNode.Rect; }
            else
            {
                var group = _graph?.Groups.FirstOrDefault(g => (g.IsWarning || g.IsError) && g.Rect.width > 0);
                if (group == null) return;
                SelectGroup(group);
                focus = group.Rect;
            }

            var view = new Vector2(position.width - INSPECTOR_WIDTH, position.height);
            _pan = new Vector2(view.x * 0.4f - focus.center.x * _zoom,
                view.y * 0.4f - focus.center.y * _zoom);
            Repaint();
        }

        // ══════════════════════════════════════════════════════ canvas

        private void DrawCanvas(Rect canvas)
        {
            GUI.BeginClip(canvas);
            try
            {
                var clip = new Rect(0, 0, canvas.width, canvas.height);
                GraphStyles.DrawGrid(clip, _pan, _zoom);

                foreach (var group in _graph.Groups) DrawGroup(group);

                var visible = new Dictionary<string, GraphNode>();
                foreach (var node in Visible()) visible[node.Id] = node;

                DrawEdges(visible);

                foreach (var node in visible.Values) DrawNode(node);
            }
            finally { GUI.EndClip(); }
        }

        private void DrawGroup(GraphGroup group)
        {
            if (group.Rect.width <= 0) return;

            var rect = new Rect(ToScreen(group.Rect.position), group.Rect.size * _zoom);
            var palette = GraphStyles.Get(group.ColorIndex, group.IsError);
            var bands = Bands(group) ?? new List<Band>();
            var groupCollapsed = IsGroupCollapsed(group, bands);

            EditorGUI.DrawRect(rect, palette.GroupFill);

            // header band — brighter, and highlights on hover to advertise clickability
            var headerRect = new Rect(rect.x, rect.y, rect.width, GROUP_HEADER * _zoom);
            var headerTint = new Color(palette.Accent.r, palette.Accent.g, palette.Accent.b,
                                       group == _hoverGroup ? 0.20f : 0.10f);
            EditorGUI.DrawRect(headerRect, headerTint);

            GraphStyles.Outline(rect, group == _selectedGroup
                ? GraphStyles.SelectedRing
                : group.IsWarning ? new Color(0.95f, 0.75f, 0.35f, 0.55f)
                                  : palette.GroupBorder,
                group == _selectedGroup ? 2f : 1.5f);

            // ── foldout arrow: its own hit target, so header clicks still select ──
            var arrowWorld = ArrowRect(group);
            var arrowRect = new Rect(ToScreen(arrowWorld.position), arrowWorld.size * _zoom);

            if (Event.current.type == EventType.Repaint)
            {
                EditorGUIUtility.AddCursorRect(arrowRect, MouseCursor.Link);
                if (group.Target != null) EditorGUIUtility.AddCursorRect(headerRect, MouseCursor.Link);
            }

            if (Event.current.type != EventType.Repaint || _zoom < 0.4f) return;

            var pad = GROUP_PAD * _zoom;
            var headerX = rect.x + pad + ARROW_W * _zoom;
            var header = new Rect(headerX, rect.y + pad * 0.55f,
                                  rect.width - (headerX - rect.x) - pad, GROUP_HEADER * _zoom * 0.55f);

            var titleStyle = GraphStyles.GroupTitle;
            titleStyle.normal.textColor = palette.Header;
            titleStyle.fontSize = Mathf.RoundToInt(12f * Mathf.Clamp(_zoom, 0.8f, 1.3f));

            var arrowStyle = new GUIStyle(titleStyle) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(arrowRect, groupCollapsed ? "▸" : "▾", arrowStyle);

            var shown = bands.Sum(b => b.Nodes.Where(n => n.Kind != NodeKind.Installer)
                                              .Sum(n => Mathf.Max(1, n.MergeCount)));
            var total = group.Nodes.Where(n => n.Kind != NodeKind.Installer)
                                   .Sum(n => Mathf.Max(1, n.MergeCount));

            var countText = Filtering && shown != total ? $"({shown} / {total})" : $"({total})";
            var groupTitle = (group.IsWarning ? "⚠ " : "") + $"{group.Title}  {countText}";
            GUI.Label(header, GraphStyles.Fit(groupTitle, titleStyle, header.width), titleStyle);

            var subStyle = GraphStyles.GroupSub;
            subStyle.normal.textColor = palette.SubHeader;
            subStyle.fontSize = Mathf.RoundToInt(11f * Mathf.Clamp(_zoom, 0.8f, 1.3f));

            var titleWidth = titleStyle.CalcSize(new GUIContent(groupTitle)).x;
            var subRect = new Rect(header.x + titleWidth + 10f * _zoom, header.y,
                                   header.width - titleWidth - 10f * _zoom, header.height);
            GUI.Label(subRect, GraphStyles.Fit("· " + group.Subtitle, subStyle, subRect.width), subStyle);

            if (groupCollapsed) return;

            // ── band headers ──
            foreach (var band in bands)
            {
                if (band.Label == null || band.Rect.width <= 0) continue;

                var bandRect = new Rect(ToScreen(band.Rect.position), band.Rect.size * _zoom);
                EditorGUI.DrawRect(bandRect, new Color(palette.Accent.r, palette.Accent.g,
                                                       palette.Accent.b, 0.07f));
                EditorGUI.DrawRect(new Rect(bandRect.x, bandRect.yMax - 1f, bandRect.width, 1f),
                                   new Color(1f, 1f, 1f, 0.05f));
                EditorGUIUtility.AddCursorRect(bandRect, MouseCursor.Link);

                var bandStyle = GraphStyles.GroupSub;
                bandStyle.normal.textColor = palette.SubHeader;
                bandStyle.fontSize = Mathf.RoundToInt(10f * Mathf.Clamp(_zoom, 0.8f, 1.3f));

                var bandCount = Filtering && band.Nodes.Count != band.Total
                    ? $"({band.Nodes.Count} / {band.Total})"
                    : $"({band.Total})";

                var label = $"{(band.Collapsed ? "▸" : "▾")}  {band.Label}  {bandCount}";
                var textRect = new Rect(bandRect.x + 6f * _zoom, bandRect.y,
                                        bandRect.width - 12f * _zoom, bandRect.height);
                GUI.Label(textRect, GraphStyles.Fit(label, bandStyle, textRect.width), bandStyle);
            }

            // ── placeholder when nothing survives ──
            if (bands.All(b => b.Nodes.Count == 0))
            {
                var body = new Rect(rect.x + pad, rect.y + (GROUP_HEADER + GROUP_PAD) * _zoom,
                                    rect.width - pad * 2f, EMPTY_BODY_HEIGHT * _zoom);
                var hint = GraphStyles.NodeSub;
                hint.fontSize = Mathf.RoundToInt(10f * Mathf.Clamp(_zoom, 0.8f, 1.3f));
                hint.alignment = TextAnchor.MiddleCenter;
                GUI.Label(body, Filtering ? "no matches" : "no registrations", hint);
                hint.alignment = TextAnchor.MiddleLeft;          // shared style — restore
            }
        }

        private static Rect ArrowRect(GraphGroup group)
            => new(group.Rect.x + 6f,
                   group.Rect.y + (GROUP_HEADER - ARROW_BOX) * 0.5f,
                   ARROW_BOX, ARROW_BOX);

        private void DrawNode(GraphNode node)
        {
            if (node.Rect.width <= 0) return;

            var rect = new Rect(ToScreen(node.Rect.position), node.Rect.size * _zoom);
            var group = _graph.GroupOf(node);
            var palette = GraphStyles.Get(group?.ColorIndex ?? 0, node.HasError || (group?.IsError ?? false));
            var isInstaller = node.Kind == NodeKind.Installer;

            // ── background, once ──
            EditorGUI.DrawRect(rect, node == _hover
                ? GraphStyles.NodeBgHover
                : isInstaller ? GraphStyles.InstallerBg : GraphStyles.NodeBg);

            GraphStyles.Outline(rect, GraphStyles.NodeOutline);

            var accent = isInstaller ? GraphStyles.InstallerAccent : palette.Accent;
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, ACCENT_WIDTH * _zoom, rect.height), accent);

            if (node == _selected)
                GraphStyles.Outline(rect, GraphStyles.SelectedRing, 2f);

            if (Event.current.type != EventType.Repaint || _zoom < 0.45f) return;

            // ── text ──
            var padX = 11f * _zoom;
            var inner = new Rect(rect.x + ACCENT_WIDTH * _zoom + padX, rect.y + 9f * _zoom,
                                 rect.width - ACCENT_WIDTH * _zoom - padX * 2f, rect.height - 18f * _zoom);

            var scale = Mathf.Clamp(_zoom, 0.8f, 1.3f);
            var titleStyle = GraphStyles.NodeTitle;
            titleStyle.fontSize = Mathf.RoundToInt(12f * scale);
            var subStyle = GraphStyles.NodeSub;
            subStyle.fontSize = Mathf.RoundToInt(10f * scale);

            var lineH = inner.height * 0.5f;
            var titleRect = new Rect(inner.x, inner.y, inner.width, lineH);

            if (node.IsInstantiated && !isInstaller)
            {
                var dot = new Rect(inner.xMax - 8f * _zoom, inner.y + lineH * 0.5f - 3f * _zoom,
                                   6f * _zoom, 6f * _zoom);
                var partial = node.MergeCount > 1 && node.InstantiatedCount < node.MergeCount;
                GraphStyles.Dot(dot, partial
                    ? Color.Lerp(GraphStyles.LiveDot, GraphStyles.NodeBg, 0.45f)
                    : GraphStyles.LiveDot);
                titleRect.width -= 14f * _zoom;
            }

            var prefix = node.Kind switch
            {
                NodeKind.Installer  => "⚙ ",
                NodeKind.Consumer   => "▸ ",
                NodeKind.Unresolved => "✖ ",
                _                   => "● "
            };

            var titleText = node.MergeCount > 1 ? $"{node.Title}  ×{node.MergeCount}" : node.Title;
            GUI.Label(titleRect, GraphStyles.Fit(prefix + titleText, titleStyle, titleRect.width), titleStyle);

            // ── subtitle: contracts appended ONCE ──
            var sub = node.Subtitle;
            if (isInstaller)
                sub = node.ProvidedCount == 0
                    ? "installer · no registrations"
                    : $"installer · {node.ProvidedCount} registration(s)";
            else if (node.Kind == NodeKind.Registration && node.Contracts.Count > 0)
                sub += "  ·  as " + string.Join(", ", node.Contracts);

            var subRect = new Rect(inner.x, inner.y + lineH, inner.width, lineH);
            GUI.Label(subRect, GraphStyles.Fit(sub, subStyle, subRect.width,
                fromLeft: node.Kind == NodeKind.Consumer), subStyle);
        }

        private void DrawEdges(Dictionary<string, GraphNode> visible)
        {
            if (Event.current.type != EventType.Repaint) return;

            var focus = BuildFocusSet();

            Handles.BeginGUI();
            DrawEdgePass(visible, focus, active: false);      // dim underneath
            DrawEdgePass(visible, focus, active: true);       // bright on top
            Handles.EndGUI();
        }

        private void DrawEdgePass(Dictionary<string, GraphNode> visible, HashSet<string> focus, bool active)
        {
            foreach (var edge in _graph.Edges)
            {
                if (edge.Kind is EdgeKind.Provides or EdgeKind.Installs) continue;

                var isActive = focus.Count > 0 &&
                               (focus.Contains(edge.FromId) || focus.Contains(edge.ToId));
                if (isActive != active) continue;

                Vector2 a, b;
                if (edge.Kind == EdgeKind.ScopeParent)
                {
                    var from = _graph.FindGroup(edge.FromId);
                    var to = _graph.FindGroup(edge.ToId);
                    if (from == null || to == null || from.Rect.width <= 0 || to.Rect.width <= 0) continue;
                    Anchors(from.Rect, to.Rect, out a, out b);
                }
                else
                {
                    if (!visible.TryGetValue(edge.FromId, out var from)) continue;
                    if (!visible.TryGetValue(edge.ToId, out var to)) continue;
                    Anchors(from.Rect, to.Rect, out a, out b);
                }

                var hue = edge.Kind switch
                {
                    EdgeKind.Missing     => new Color(0.90f, 0.35f, 0.35f),
                    EdgeKind.ScopeParent => new Color(0.58f, 0.58f, 0.64f),
                    EdgeKind.Deferred    => new Color(0.58f, 0.78f, 1f),
                    EdgeKind.Collection  => new Color(0.96f, 0.82f, 0.42f),
                    _                    => new Color(0.46f, 0.76f, 0.52f)
                };

                var color = active ? hue : GraphStyles.DimEdge(hue);
                var width = GraphStyles.ScaleWidth(GraphStyles.WidthFor(edge.Kind), _zoom);
                if (active) width *= GraphStyles.EdgeActiveBoost;

                var reach = Mathf.Min(80f, Mathf.Abs(a.x - b.x) * 0.5f + 22f);
                var pull = new Vector2(a.x > b.x ? -reach : reach, 0);

                GraphStyles.DrawEdge(a, b, a + pull, b - pull, color, width, backing: active);

                if (_zoom > 0.55f)
                    Arrow(b, b - pull, GraphStyles.ArrowSize * Mathf.Clamp(_zoom, 0.7f, 1.4f), color);
            }
        }

        private void Anchors(Rect from, Rect to, out Vector2 a, out Vector2 b)
        {
            var fromRight = from.center.x > to.center.x;
            a = ToScreen(new Vector2(fromRight ? from.xMin : from.xMax, from.center.y));
            b = ToScreen(new Vector2(fromRight ? to.xMax : to.xMin, to.center.y));
        }

        private static void Arrow(Vector2 tip, Vector2 from, float size, Color color)
        {
            var dir = (tip - from).normalized;
            if (dir.sqrMagnitude < 0.001f) return;
            var perp = new Vector2(-dir.y, dir.x) * (size * 0.45f);
            Handles.color = color;
            Handles.DrawAAConvexPolygon(
                new Vector3(tip.x, tip.y),
                new Vector3(tip.x - dir.x * size + perp.x, tip.y - dir.y * size + perp.y),
                new Vector3(tip.x - dir.x * size - perp.x, tip.y - dir.y * size - perp.y));
        }

        // ══════════════════════════════════════════════════════ inspector

        private void DrawInspector(Rect panel)
        {
            EditorGUI.DrawRect(panel, GraphStyles.PanelBg);
            EditorGUI.DrawRect(new Rect(panel.x, panel.y, 1f, panel.height), GraphStyles.PanelBorder);

            GUILayout.BeginArea(new Rect(panel.x + 14, panel.y + 12, panel.width - 28, panel.height - 24));
            _inspectorScroll = EditorGUILayout.BeginScrollView(_inspectorScroll);

            GUILayout.Label("Inspector", GraphStyles.PanelHeader);
            GUILayout.Space(6);

            if (_selectedGroup != null) DrawGroupInspector(_selectedGroup);
            else if (_selected != null) DrawNodeInspector(_selected);
            else GUILayout.Label("Select a scope header or a node.", GraphStyles.PanelSub);

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawGroupInspector(GraphGroup group)
        {
            var palette = GraphStyles.Get(group.ColorIndex, group.IsError);

            var panelTitle = GraphStyles.PanelTitle;
            panelTitle.normal.textColor = group.IsError ? GraphStyles.ErrorHue
                                        : group.IsWarning ? new Color(0.95f, 0.78f, 0.42f)
                                        : palette.Header;
            GUILayout.Label(group.Title, panelTitle);
            GUILayout.Label(group.ComponentTypeName ?? group.Subtitle, GraphStyles.PanelSub);

            GUILayout.Space(10); Separator(); GUILayout.Space(8);

            var installers = group.Nodes.Where(n => n.Kind == NodeKind.Installer).ToList();
            var registrations = group.Nodes.Where(n => n.Kind == NodeKind.Registration).ToList();

            Row("Kind", group.IsScope ? "Scope" : "Group");

            if (group.IsScope)
            {
                Row("Depth", group.Depth >= int.MaxValue - 1 ? "—" : group.Depth.ToString());
                Row("Parent", _graph.ParentGroupOf(group)?.Title ?? "—");
            }

            Row("Installers", installers.Count.ToString());
            Row("Registrations", registrations.Sum(n => Mathf.Max(1, n.MergeCount)).ToString());

            if (group.Target is LifetimeScope scope)
            {
                Row("Inject Target", scope.InjectionTargetMode.ToString());
                Row("Injects", group.InjectedCount.ToString());
                Row("Persistent", scope.IsPersistent ? "yes" : "no");
            }

            var bands = Bands(group);
            if (bands != null && bands.Count > 1)
            {
                GUILayout.Space(10); Separator(); GUILayout.Space(8);
                GUILayout.Label($"Packages ({bands.Count(b => b.Label != null)})", GraphStyles.Section);

                foreach (var band in bands.Where(b => b.Label != null))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var collapsed = IsCollapsed(band.Key);
                        if (GUILayout.Button(collapsed ? "▸" : "▾", GraphStyles.PanelSub, GUILayout.Width(14)))
                            Toggle(band.Key);

                        GUILayout.Label(band.Label, GraphStyles.Key);
                        GUILayout.FlexibleSpace();
                        GUILayout.Label(band.Total.ToString(), GraphStyles.Value, GUILayout.Width(34));
                    }
                }
            }

            if (group.Notes.Count > 0)
            {
                GUILayout.Space(8);
                foreach (var note in group.Notes)
                    EditorGUILayout.HelpBox(note.Text, note.Kind switch
                    {
                        NoteKind.Error   => MessageType.Error,
                        NoteKind.Warning => MessageType.Warning,
                        _                => MessageType.Info
                    });
            }

            if (installers.Count > 0)
            {
                GUILayout.Space(10); Separator(); GUILayout.Space(8);
                GUILayout.Label($"Installers ({installers.Count})", GraphStyles.Section);
                foreach (var i in installers) Link(i);
            }

            if (registrations.Count > 0)
            {
                GUILayout.Space(10); Separator(); GUILayout.Space(8);
                GUILayout.Label($"Provides ({registrations.Count})", GraphStyles.Section);
                foreach (var r in registrations.Take(14)) Link(r);
                if (registrations.Count > 14)
                    GUILayout.Label($"  … +{registrations.Count - 14}", GraphStyles.PanelSub);
            }

            GUILayout.Space(14);

            var target = SelectTarget.From(group);

            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(!target.HasType))
            {
                if (GUILayout.Button("Ping", GUILayout.Height(22))) PingScript(target);
                if (GUILayout.Button("Open", GUILayout.Height(22))) OpenScript(target);
            }

            using (new EditorGUI.DisabledScope(!target.HasSceneObject))
                if (GUILayout.Button("Select in Hierarchy", GUILayout.Height(22)))
                    TrySelectInHierarchy(target);
        }

        private void DrawNodeInspector(GraphNode node)
        {
            var group = _graph?.GroupOf(node);
            var palette = GraphStyles.Get(group?.ColorIndex ?? 0, node.HasError);

            var panelTitle = GraphStyles.PanelTitle;
            panelTitle.normal.textColor = node.HasError ? GraphStyles.ErrorHue : palette.Header;
            GUILayout.Label(node.Title, panelTitle);

            // origin OR namespace, then namespace only if origin took the first line
            if (!string.IsNullOrEmpty(node.Origin))
            {
                var style = GraphStyles.PanelSub;
                style.normal.textColor = GraphOrigin.IsPackage(node.Origin)
                    ? new Color(0.58f, 0.70f, 0.88f)
                    : new Color(0.55f, 0.57f, 0.60f);
                GUILayout.Label(node.Origin, style);
                style.normal.textColor = new Color(0.55f, 0.57f, 0.60f);

                if (!string.IsNullOrEmpty(node.Namespace))
                    GUILayout.Label(node.Namespace, GraphStyles.PanelSub);
            }
            else if (!string.IsNullOrEmpty(node.Namespace))
            {
                GUILayout.Label(node.Namespace, GraphStyles.PanelSub);
            }

            GUILayout.Space(10); Separator(); GUILayout.Space(8);

            Row("Scope", group?.Title ?? "—");
            Row("Kind", node.Kind.ToString());

            switch (node.Kind)
            {
                case NodeKind.Registration:
                    Row("Lifetime", node.Subtitle);
                    Row("Contracts", node.Contracts.Count > 0 ? string.Join(", ", node.Contracts) : "self");

                    if (node.InstallerNodeIds.Count == 1)
                        Row("Installer", _graph.Find(node.PrimaryInstallerId)?.Title ?? "—");
                    else if (node.InstallerNodeIds.Count > 1)
                        Row("Installers", node.InstallerNodeIds.Count.ToString());

                    if (node.MergeCount > 1)
                    {
                        Row("Registrations", node.MergeCount.ToString());
                        Row("State", $"{node.InstantiatedCount} of {node.MergeCount} instantiated");
                    }
                    else Row("State", node.IsInstantiated ? "Instantiated" : "Declared");
                    break;

                case NodeKind.Consumer:
                    Row("Path", node.Subtitle);
                    if (!string.IsNullOrEmpty(node.ScenePath))
                        Row("Scene", System.IO.Path.GetFileNameWithoutExtension(node.ScenePath));
                    break;

                case NodeKind.Unresolved:
                    Row("Status", "Not registered");
                    break;

                case NodeKind.Installer:
                    Row("Registrations", node.ProvidedCount.ToString());
                    if (node.Target != null)
                        Row("Asset", node.Target is Component ? "MonoInstaller" : "ScriptableObject");
                    break;
            }

            if (node.Details.Count > 0)
            {
                GUILayout.Space(6);
                foreach (var detail in node.Details.Take(8)) Row("", detail);
            }

            // ── notices, after the facts ──
            if (node.MergeCount > 1 && !node.Subtitle.Contains("#"))
            {
                GUILayout.Space(8);
                EditorGUILayout.HelpBox(
                    $"{node.MergeCount} unkeyed registrations of the same contract. " +
                    "Direct injection resolves the LAST one; only IEnumerable<T> sees them all.\n" +
                    "Add .WithId(...) to select individually, or inject IEnumerable<T> into a catalog service.",
                    MessageType.Info);
            }

            if (node.Kind == NodeKind.Unresolved)
            {
                GUILayout.Space(8);
                EditorGUILayout.HelpBox(
                    $"Nothing registers {node.Title}.\nAdd it in an installer, or mark the injection " +
                    "site [Inject(Optional = true)].", MessageType.Error);
            }

            if (node.Kind == NodeKind.Installer)
            {
                var provided = _graph.ProvidedBy(node.Id).ToArray();

                GUILayout.Space(10); Separator(); GUILayout.Space(8);
                GUILayout.Label($"Provides ({provided.Length})", GraphStyles.Section);

                if (provided.Length == 0)
                    EditorGUILayout.HelpBox("This installer registered nothing. Dead code, or an early return.",
                        MessageType.Warning);
                else
                    foreach (var p in provided.Take(14)) Link(p);
            }

            var dependencies = _graph!.DependenciesOf(node.Id).ToArray();
            var consumers = _graph!.ConsumersOf(node.Id).ToArray();

            GUILayout.Space(10); Separator(); GUILayout.Space(8);

            GUILayout.Label($"Depends On ({dependencies.Length})", GraphStyles.Section);
            if (dependencies.Length == 0)
                GUILayout.Label("  —", GraphStyles.PanelSub);
            else
            {
                foreach (var dependency in dependencies.Take(12)) Link(dependency);
                if (dependencies.Length > 12)
                    GUILayout.Label($"  … +{dependencies.Length - 12}", GraphStyles.PanelSub);
            }

            GUILayout.Space(8);

            GUILayout.Label($"Dependents ({consumers.Length})", GraphStyles.Section);
            if (consumers.Length == 0)
                GUILayout.Label("  —", GraphStyles.PanelSub);
            else
            {
                foreach (var consumer in consumers.Take(12)) Link(consumer);
                if (consumers.Length > 12)
                    GUILayout.Label($"  … +{consumers.Length - 12}", GraphStyles.PanelSub);
            }

            GUILayout.Space(14);

            var target = SelectTarget.From(node);

            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(!target.HasType))
            {
                if (GUILayout.Button("Ping", GUILayout.Height(22))) PingScript(target);
                if (GUILayout.Button("Open", GUILayout.Height(22))) OpenScript(target);
            }

            using (new EditorGUI.DisabledScope(!target.HasSceneObject))
                if (GUILayout.Button("Select in Hierarchy", GUILayout.Height(22)))
                    TrySelectInHierarchy(target);
        }

        private static void Separator()
        {
            var rect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, GraphStyles.Separator);
        }

        private static void Row(string key, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(key, GraphStyles.Key, GUILayout.Width(78));
                GUILayout.Label(value, GraphStyles.Value);
            }
        }

        private void Link(GraphNode node)
        {
            var label = node.Kind switch
            {
                NodeKind.Installer  => "   ⚙ " + node.Title,
                NodeKind.Unresolved => "   ✖ " + node.Title,
                NodeKind.Consumer   => "   ▸ " + node.Title,
                _                   => "   ● " + node.Title
            };

            var style = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                normal = { textColor = node.HasError
                    ? GraphStyles.ErrorHue
                    : new Color(0.62f, 0.78f, 0.95f) }
            };

            if (GUILayout.Button(label, style))
            {
                SelectNode(node);
                Repaint();
            }

            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
        }

        // ══════════════════════════════════════════════════════ input

        private void HandleInput(Rect canvas)
        {
            var e = Event.current;
            if (!canvas.Contains(e.mousePosition)) return;

            var local = e.mousePosition - canvas.position;

            switch (e.type)
            {
                case EventType.ScrollWheel:
                {
                    var before = (local - _pan) / _zoom;
                    _zoom = Mathf.Clamp(_zoom * (1f - e.delta.y * 0.03f), 0.3f, 2.5f);
                    _pan = local - before * _zoom;
                    e.Use(); Repaint();
                    break;
                }

                case EventType.MouseDrag when e.button == 2 || (e.button == 0 && e.alt):
                    _pan += e.delta;
                    e.Use(); Repaint();
                    break;

                case EventType.MouseMove:
                {
                    var node = NodeAt(local);
                    var group = node == null ? GroupAt(local) : null;
                    if (node != _hover || group != _hoverGroup)
                    {
                        _hover = node; _hoverGroup = group;
                        Repaint();
                    }
                    break;
                }

                case EventType.MouseDown when e.button == 0:
                {
                    // foldouts first — they sit on top of the header and band strips
                    var arrow = ArrowAt(local);
                    if (arrow != null) { Toggle(GroupCollapseKey(arrow)); e.Use(); break; }

                    var (_, band) = BandAt(local);
                    if (band?.Key != null) { Toggle(band.Key); e.Use(); break; }

                    var node = NodeAt(local);
                    if (node != null)
                    {
                        SelectNode(node);
                        if (e.clickCount == 2) OpenScript(SelectTarget.From(node));
                    }
                    else
                    {
                        var group = GroupAt(local);
                        if (group != null)
                        {
                            SelectGroup(group);
                            if (e.clickCount == 2) TrySelectInHierarchy(SelectTarget.From(group));
                        }
                        else ClearSelection();
                    }
                    e.Use(); Repaint();
                    break;
                }

                case EventType.MouseDown when e.button == 1:
                {
                    var (bandGroup, band) = BandAt(local);
                    if (band?.Key != null) { ShowBandMenu(bandGroup, band); e.Use(); break; }

                    var node = NodeAt(local);
                    if (node != null) { SelectNode(node); ShowNodeMenu(node); }
                    else
                    {
                        var group = GroupAt(local);
                        if (group == null) break;
                        SelectGroup(group);
                        ShowGroupMenu(group);
                    }
                    e.Use(); Repaint();
                    break;
                }
            }
        }

        private GraphNode NodeAt(Vector2 local)
        {
            var world = (local - _pan) / _zoom;
            return Visible().LastOrDefault(n => n.Rect.width > 0 && n.Rect.Contains(world));
        }

        private GraphGroup GroupAt(Vector2 local)
        {
            if (_graph == null) return null;
            var world = (local - _pan) / _zoom;
            return _graph.Groups.LastOrDefault(g => g.Rect.width > 0 && g.Rect.Contains(world));
        }

        private GraphGroup ArrowAt(Vector2 local)
        {
            if (_graph == null) return null;
            var world = (local - _pan) / _zoom;

            return _graph.Groups.LastOrDefault(g => g.Rect.width > 0 && ArrowRect(g).Contains(world));
        }

        private (GraphGroup group, Band band) BandAt(Vector2 local)
        {
            if (_graph == null) return (null, null);
            var world = (local - _pan) / _zoom;

            foreach (var group in _graph.Groups)
            {
                if (group.Rect.width <= 0 || !group.Rect.Contains(world)) continue;

                var bands = Bands(group);
                if (bands == null) continue;

                foreach (var band in bands)
                    if (band.Label != null && band.Rect.width > 0 && band.Rect.Contains(world))
                        return (group, band);
            }
            return (null, null);
        }

        private IEnumerable<GraphNode> Visible()
            => _graph == null
                ? Enumerable.Empty<GraphNode>()
                : _graph.Nodes.Where(n => n.Rect.width > 0);

        private Vector2 ToScreen(Vector2 world) => world * _zoom + _pan;

        // ══════════════════════════════════════════════════════ actions

        private void SelectNode(GraphNode node)   { _selected = node; _selectedGroup = null; }
        private void SelectGroup(GraphGroup group) { _selectedGroup = group; _selected = null; }
        private void ClearSelection()              { _selected = null; _selectedGroup = null; }

        private void ShowBandMenu(GraphGroup group, Band band)
        {
            var menu = new GenericMenu();
            var collapsed = IsCollapsed(band.Key);

            menu.AddItem(new GUIContent(collapsed ? "Expand" : "Collapse"), false, () => Toggle(band.Key));

            menu.AddItem(new GUIContent("Collapse Others"), false, () =>
            {
                var bands = Bands(group);
                if (bands == null) return;
                foreach (var other in bands.Where(b => b.Key != null && b.Label != null))
                    SetCollapsed(other.Key, other.Key != band.Key);
                Layout();
                Repaint();
            });

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Filter to This Package"), false, () =>
            {
                ApplyFilter(band.Label);
            });
            menu.AddItem(new GUIContent("Copy Package Name"), false, () => Copy(band.Label));
            menu.AddItem(new GUIContent("Copy Registrations"), false, () =>
                Copy(string.Join("\n", band.Nodes
                    .Where(n => n.Kind == NodeKind.Registration)
                    .Select(RegisterSnippet))));

            menu.ShowAsContext();
        }

        private void ShowGroupMenu(GraphGroup group)
        {
            var menu = new GenericMenu();
            var target = SelectTarget.From(group);

            AddScriptMenuItems(menu, target);

            if (target.HasSceneObject)
                menu.AddItem(new GUIContent("Select in Hierarchy"), false, () => TrySelectInHierarchy(target));
            else
                menu.AddDisabledItem(new GUIContent("Select in Hierarchy"));

            menu.AddSeparator("");
            menu.AddItem(new GUIContent(IsCollapsed(GroupCollapseKey(group)) ? "Expand Scope" : "Collapse Scope"),
                false, () => Toggle(GroupCollapseKey(group)));

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Copy Scope Name"), false, () => Copy(group.Title));

            if (!string.IsNullOrEmpty(group.ObjectPath))
                menu.AddItem(new GUIContent("Copy Hierarchy Path"), false, () => Copy(group.ObjectPath));

            if (group.Nodes.Any(n => n.Kind == NodeKind.Registration))
                menu.AddItem(new GUIContent("Copy All Registrations"), false, () =>
                    Copy(string.Join("\n", OrderedNodes(group)
                        .Where(n => n.Kind == NodeKind.Registration)
                        .Select(RegisterSnippet))));

            if (!string.IsNullOrEmpty(group.Origin))
            {
                menu.AddSeparator("");
                menu.AddDisabledItem(new GUIContent($"Origin: {group.Origin}"));
            }

            menu.ShowAsContext();
        }

        private void ShowNodeMenu(GraphNode node)
        {
            var menu = new GenericMenu();
            var target = SelectTarget.From(node);

            AddScriptMenuItems(menu, target);

            if (target.HasSceneObject)
                menu.AddItem(new GUIContent("Select in Hierarchy"), false, () => TrySelectInHierarchy(target));
            else
                menu.AddDisabledItem(new GUIContent("Select in Hierarchy"));

            // ── copy ──
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Copy Type Name"), false,
                () => Copy(node.TypeFullName ?? node.Title));

            if (node.Kind == NodeKind.Registration || node.Kind == NodeKind.Unresolved)
                menu.AddItem(new GUIContent("Copy Register Snippet"), false,
                    () => Copy(RegisterSnippet(node)));

            if (node.Kind != NodeKind.Consumer)
                menu.AddItem(new GUIContent("Copy Inject Snippet"), false,
                    () => Copy(InjectSnippet(node)));

            if (!string.IsNullOrEmpty(node.ObjectPath))
                menu.AddItem(new GUIContent("Copy Hierarchy Path"), false, () => Copy(node.ObjectPath));

            // ── navigate ──
            var group = _graph?.GroupOf(node);
            if (group != null || node.Kind == NodeKind.Registration)
            {
                menu.AddSeparator("");

                if (group != null)
                    menu.AddItem(new GUIContent($"Select Scope '{group.Title}'"), false,
                        () => { SelectGroup(group); Repaint(); });

                if (node.Kind == NodeKind.Registration)
                    menu.AddItem(new GUIContent("Filter to Consumers"), false, () =>
                    {
                        _showConsumers = true;
                        ApplyFilter(node.Title);
                    });
            }

            // ── origin footer ──
            if (!string.IsNullOrEmpty(node.Origin))
            {
                menu.AddSeparator("");
                menu.AddDisabledItem(new GUIContent($"Origin: {node.Origin}"));
            }

            menu.ShowAsContext();
        }

        private void Copy(string text)
        {
            EditorGUIUtility.systemCopyBuffer = text;
            ShowNotification(new GUIContent("Copied"));
        }

        private MonoScript ResolveScript(SelectTarget target)
            => GraphOrigin.FindScript(target.ResolvedType, target.TypeFullName, target.Target);

        private static void Ping(MonoScript script)
        {
            Selection.activeObject = script;
            EditorGUIUtility.PingObject(script);
        }

        private static void Open(ScriptTarget hit)
        {
            if (hit.Script == null) return;

            if (hit.Line > 0) AssetDatabase.OpenAsset(hit.Script, hit.Line);
            else AssetDatabase.OpenAsset(hit.Script);
        }

        private void PingScript(SelectTarget target)
        {
            if (!ScriptLocator.TryFindBest(target.ResolvedType, target.TypeFullName, target.Target, out var hit))
            {
                NotifyNoSource(target);
                return;
            }

            Ping(hit.Script);
        }

        private void OpenScript(SelectTarget target)
        {
            if (!ScriptLocator.TryFindBest(target.ResolvedType, target.TypeFullName, target.Target, out var hit))
            {
                NotifyNoSource(target);
                return;
            }

            Open(hit);
        }

        private void NotifyNoSource(SelectTarget target)
        {
            var name = GraphOrigin.ShortName(target.TypeFullName ?? target.Title);
            ShowNotification(new GUIContent($"No source found for {name}"));
            if (target.Target != null) EditorGUIUtility.PingObject(target.Target);
        }

        private static string RegisterSnippet(GraphNode node)
        {
            var impl = GraphOrigin.ShortName(node.TypeFullName ?? node.Title);

            switch (node.Kind)
            {
                case NodeKind.Unresolved:
                    return $"builder.Register<{node.Title}, /* Impl */>(Lifetime.Singleton);";

                case NodeKind.Registration when node.Contracts.Count == 1:
                    return $"builder.Register<{node.Contracts[0]}, {impl}>(Lifetime.{Lifetime(node)});";

                case NodeKind.Registration when node.Contracts.Count > 1:
                    return $"builder.Register<{impl}>(Lifetime.{Lifetime(node)})\n" +
                           "       .AsImplementedInterfaces();";

                case NodeKind.Registration:
                    return $"builder.Register<{impl}>(Lifetime.{Lifetime(node)});";

                default:
                    return $"builder.Register<{impl}>(Lifetime.Singleton);";
            }

            static string Lifetime(GraphNode n)
            {
                var head = n.Subtitle?.Split('·')[0].Trim();
                return string.IsNullOrEmpty(head) ? "Singleton" : head;
            }
        }

        private static string InjectSnippet(GraphNode node)
        {
            var contract = node.Kind == NodeKind.Registration && node.Contracts.Count > 0
                ? node.Contracts[0]
                : node.Title;

            var field = "_" + char.ToLowerInvariant(contract[0]) + contract.Substring(1);

            if (contract.Length > 1 && contract[0] == 'I' && char.IsUpper(contract[1]))
                field = "_" + char.ToLowerInvariant(contract[1]) + contract.Substring(2);

            return $"[Inject] private {contract} {field};";
        }

        private void TrySelectInHierarchy(SelectTarget target)
        {
            if (target.Target != null) { Focus(target.Target); return; }

            if (string.IsNullOrEmpty(target.ObjectPath))
            {
                ShowNotification(new GUIContent("No scene object"));
                return;
            }

            // edit-mode scans reload scenes, so live references are gone — reopen if needed
            if (!string.IsNullOrEmpty(target.ScenePath) && !IsSceneOpen(target.ScenePath))
            {
                if (!EditorUtility.DisplayDialog("Open Scene",
                        $"'{System.IO.Path.GetFileName(target.ScenePath)}' is not open.\nOpen it now?",
                        "Open", "Cancel")) return;

                if (!UnityEditor.SceneManagement.EditorSceneManager
                        .SaveCurrentModifiedScenesIfUserWantsTo()) return;

                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(target.ScenePath);
            }

            var go = FindByPath(target.ObjectPath);
            if (go == null)
            {
                ShowNotification(new GUIContent("Not found — press Refresh"));
                return;
            }

            Object resolved = go;
            if (!string.IsNullOrEmpty(target.ComponentTypeName))
            {
                var component = go.GetComponents<Component>()
                    .FirstOrDefault(c => c != null && c.GetType().FullName == target.ComponentTypeName);
                if (component != null) resolved = component;
            }

            Focus(resolved);
        }

        private static void Focus(Object target)
        {
            var go = target is Component c ? c.gameObject : target as GameObject;
            Selection.activeObject = go != null ? go : target;
            EditorGUIUtility.PingObject(Selection.activeObject);
        }

        private static bool IsSceneOpen(string path)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).path == path) return true;
            return false;
        }

        private static GameObject FindByPath(string path)
        {
            var parts = path.Split('/');
            if (parts.Length == 0) return null;

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != parts[0]) continue;
                    var current = root.transform;
                    var ok = true;
                    for (var p = 1; p < parts.Length; p++)
                    {
                        var next = current.Find(parts[p]);
                        if (next == null) { ok = false; break; }
                        current = next;
                    }
                    if (ok) return current.gameObject;
                }
            }
            return null;
        }

        private readonly struct SelectTarget
        {
            public readonly string Title, ScenePath, ObjectPath, ComponentTypeName, TypeFullName, Origin;
            public readonly Type ResolvedType;
            public readonly Object Target;

            private SelectTarget(string title, string scenePath, string objectPath, string componentTypeName,
                string typeFullName, string origin, Type resolvedType, Object target)
            {
                Title = title; ScenePath = scenePath; ObjectPath = objectPath;
                ComponentTypeName = componentTypeName; TypeFullName = typeFullName;
                Origin = origin; ResolvedType = resolvedType; Target = target;
            }

            public bool HasSceneObject => Target != null || !string.IsNullOrEmpty(ObjectPath);
            public bool HasType => ResolvedType != null || !string.IsNullOrEmpty(TypeFullName);

            public static SelectTarget From(GraphNode n) => new(
                n.Title, n.ScenePath, n.ObjectPath, n.ComponentTypeName,
                n.TypeFullName ?? n.ComponentTypeName, n.Origin, n.ResolvedType, n.Target);

            public static SelectTarget From(GraphGroup g) => new(
                g.Title, g.ScenePath, g.ObjectPath, g.ComponentTypeName,
                g.TypeFullName ?? g.ComponentTypeName, g.Origin, g.ResolvedType, g.Target);
        }

        private HashSet<string> BuildFocusSet()
        {
            var focus = new HashSet<string>();

            var node = _selected ?? _hover;
            if (node != null)
            {
                focus.Add(node.Id);

                if (node.Kind == NodeKind.Installer)
                    foreach (var provided in _graph.ProvidedBy(node.Id)) focus.Add(provided.Id);
                else
                    foreach (var installerId in node.InstallerNodeIds) focus.Add(installerId);
            }

            var group = _selectedGroup ?? _hoverGroup;
            if (group != null)
            {
                focus.Add(group.Id);
                foreach (var member in group.Nodes) focus.Add(member.Id);
            }

            return focus;
        }

        private void AddScriptMenuItems(GenericMenu menu, SelectTarget target)
        {
            var scripts = GraphOrigin.FindScripts(target.ResolvedType, target.TypeFullName, target.Target);

            if (scripts.Length == 0)
            {
                menu.AddDisabledItem(new GUIContent("Ping Script"));
                menu.AddDisabledItem(new GUIContent("Open Script"));
                return;
            }

            if (scripts.Length == 1)
            {
                var only = scripts[0];
                menu.AddItem(new GUIContent("Ping Script"), false, () => Ping(only.Script));
                menu.AddItem(new GUIContent("Open Script"), false, () => Open(only));
                return;
            }

            foreach (var s in scripts)
            {
                var captured = s;
                var suffix = captured.FromPackage ? "  (package)" : "";
                menu.AddItem(new GUIContent($"Ping/{captured.Label}{suffix}"), false, () => Ping(captured.Script));
                menu.AddItem(new GUIContent($"Open/{captured.Label}{suffix}"), false, () => Open(captured));
            }
        }
    }
}