using UnityEditor;
using UnityEngine;

namespace AceLand.Injection.Editor.Graph
{
    internal static class GraphStyles
    {
        // ── canvas ──
        public static readonly Color CanvasBg   = new(0.169f, 0.169f, 0.176f);
        public static readonly Color GridFine   = new(1f, 1f, 1f, 0.022f);
        public static readonly Color GridCoarse = new(1f, 1f, 1f, 0.045f);
        public const float GridStep = 16f;
        public const int GridCoarseEvery = 5;

        // ── docked inspector ──
        public static readonly Color PanelBg     = new(0.216f, 0.216f, 0.224f);
        public static readonly Color PanelBorder = new(0.118f, 0.118f, 0.125f);
        public static readonly Color Separator   = new(1f, 1f, 1f, 0.57f);

        // ── nodes ──
        public static readonly Color NodeBg       = new(0.239f, 0.243f, 0.255f);
        public static readonly Color NodeBgHover  = new(0.286f, 0.294f, 0.310f);
        public static readonly Color NodeOutline  = new(1f, 1f, 1f, 0.56f);
        public static readonly Color SelectedRing = Color.white;
        public static readonly Color LiveDot      = new(0.42f, 0.84f, 0.50f);
        public static readonly Color ErrorHue     = new(0.90f, 0.35f, 0.35f);

        // ── edges ──
        public const float EdgeWidth           = 3.2f;   // default connection
        public const float EdgeWidthCollection = 4.4f;   // IEnumerable<T> fan-out
        public const float EdgeWidthMissing    = 3.6f;   // unresolved — should shout
        public const float EdgeWidthScope      = 2.4f;   // parent chain — background info
        public const float ArrowSize           = 11f;
        public const float EdgeZoomScale       = 0.55f;  // 0 = fixed px, 1 = fully zoom-scaled

        private const int BEZIER_SEGMENTS = 20;
        private static readonly Vector3[] bezierBuffer = new Vector3[BEZIER_SEGMENTS + 1];

        public static float WidthFor(EdgeKind kind) => kind switch
        {
            EdgeKind.Collection  => EdgeWidthCollection,
            EdgeKind.Missing     => EdgeWidthMissing,
            EdgeKind.ScopeParent => EdgeWidthScope,
            _                    => EdgeWidth
        };

        /// <summary>Partial zoom scaling: lines stay visible when zoomed out, get heavier when zoomed in.</summary>
        public static float ScaleWidth(float width, float zoom)
            => width * Mathf.Lerp(1f, zoom, EdgeZoomScale);

        public struct Palette
        {
            public Color Accent, Header, SubHeader, GroupFill, GroupBorder;
        }

        private static readonly Color[] hues =
        {
            new(0.36f, 0.60f, 0.86f),   // blue   — global / depth 0
            new(0.42f, 0.75f, 0.48f),   // green  — depth 1
            new(0.68f, 0.51f, 0.86f),   // violet — depth 2
            new(0.93f, 0.66f, 0.35f),   // amber  — depth 3
            new(0.36f, 0.78f, 0.78f),   // teal
            new(0.90f, 0.52f, 0.65f),   // pink
        };

        public static Palette Get(int index, bool error = false)
        {
            var hue = error ? ErrorHue : hues[((index % hues.Length) + hues.Length) % hues.Length];
            return new Palette
            {
                Accent      = hue,
                Header      = Color.Lerp(hue, Color.white, 0.28f),
                SubHeader   = Color.Lerp(hue, Color.white, 0.58f),
                GroupFill   = new Color(hue.r, hue.g, hue.b, 0.075f),
                GroupBorder = new Color(hue.r, hue.g, hue.b, 0.34f),
            };
        }

        // ── text ──
        private static GUIStyle _groupTitle, _groupSub, _nodeTitle, _nodeSub, _key, _value,
                        _panelHeader, _panelTitle, _panelSub, _section;

        public static GUIStyle GroupTitle => _groupTitle ??= new GUIStyle(EditorStyles.label)
        {
            fontStyle = FontStyle.Bold, fontSize = 12,
            padding = new RectOffset(0, 0, 0, 0), clipping = TextClipping.Clip
        };

        public static GUIStyle GroupSub => _groupSub ??= new GUIStyle(EditorStyles.label)
        {
            fontSize = 11, padding = new RectOffset(0, 0, 0, 0), clipping = TextClipping.Clip
        };

        public static GUIStyle NodeTitle => _nodeTitle ??= new GUIStyle(EditorStyles.label)
        {
            fontStyle = FontStyle.Bold, fontSize = 12,
            normal = { textColor = new Color(0.93f, 0.94f, 0.95f) },
            padding = new RectOffset(0, 0, 0, 0),
            alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip
        };

        public static GUIStyle NodeSub => _nodeSub ??= new GUIStyle(EditorStyles.label)
        {
            fontSize = 10,
            normal = { textColor = new Color(0.58f, 0.60f, 0.64f) },
            padding = new RectOffset(0, 0, 0, 0),
            alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip
        };

        public static GUIStyle Key => _key ??= new GUIStyle(EditorStyles.label)
        {
            fontSize = 11, normal = { textColor = new Color(0.60f, 0.62f, 0.65f) }
        };

        public static GUIStyle Value => _value ??= new GUIStyle(EditorStyles.label)
        {
            fontSize = 11, normal = { textColor = new Color(0.86f, 0.87f, 0.89f) },
            clipping = TextClipping.Clip
        };

        public static GUIStyle PanelHeader => _panelHeader ??= new GUIStyle(EditorStyles.label)
        {
            fontStyle = FontStyle.Bold, fontSize = 12,
            normal = { textColor = new Color(0.88f, 0.89f, 0.91f) }
        };

        public static GUIStyle PanelTitle => _panelTitle ??= new GUIStyle(EditorStyles.label)
        {
            fontSize = 13, normal = { textColor = new Color(0.93f, 0.94f, 0.95f) },
            clipping = TextClipping.Clip
        };

        public static GUIStyle PanelSub => _panelSub ??= new GUIStyle(EditorStyles.label)
        {
            fontSize = 10, normal = { textColor = new Color(0.55f, 0.57f, 0.60f) },
            clipping = TextClipping.Clip
        };

        public static GUIStyle Section => _section ??= new GUIStyle(EditorStyles.label)
        {
            fontStyle = FontStyle.Bold, fontSize = 11,
            normal = { textColor = new Color(0.78f, 0.80f, 0.83f) }
        };

        // ── helpers ──

        public static void DrawGrid(Rect clip, Vector2 pan, float zoom)
        {
            EditorGUI.DrawRect(clip, CanvasBg);
            if (Event.current.type != EventType.Repaint) return;

            var step = GridStep * zoom;
            if (step < 7f) return;                      // too dense to be useful

            Handles.BeginGUI();
            DrawLines(clip, pan, step, GridFine);
            DrawLines(clip, pan, step * GridCoarseEvery, GridCoarse);
            Handles.EndGUI();
        }

        private static void DrawLines(Rect clip, Vector2 pan, float step, Color color)
        {
            if (step < 4f) return;
            Handles.color = color;

            var startX = Mathf.Repeat(pan.x, step);
            for (float x = startX; x < clip.width; x += step)
                Handles.DrawLine(new Vector3(x, 0), new Vector3(x, clip.height));

            var startY = Mathf.Repeat(pan.y, step);
            for (float y = startY; y < clip.height; y += step)
                Handles.DrawLine(new Vector3(0, y), new Vector3(clip.width, y));
        }
        
        /// <summary>
        /// Crisp bezier. DrawBezier's null-texture path renders washed out at any width;
        /// a sampled poly-line with a dark backing pass stays solid and readable.
        /// </summary>
        public static void DrawEdge(Vector2 a, Vector2 b, Vector2 tangentA, Vector2 tangentB,
            Color color, float width)
        {
            for (int i = 0; i <= BEZIER_SEGMENTS; i++)
            {
                var t = i / (float)BEZIER_SEGMENTS;
                var u = 1f - t;
                var p = a * (u * u * u) + tangentA * (3f * u * u * t) +
                        tangentB * (3f * u * t * t) + b * (t * t * t);
                bezierBuffer[i] = new Vector3(p.x, p.y, 0f);
            }

            Handles.color = new Color(0f, 0f, 0f, 0.5f);          // contrast backing
            Handles.DrawAAPolyLine(width + 2.5f, bezierBuffer);

            Handles.color = color;
            Handles.DrawAAPolyLine(width, bezierBuffer);
        }

        public static void Outline(Rect rect, Color color, float thickness = 1.5f)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        public static void Dot(Rect rect, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;
            Handles.BeginGUI();
            Handles.color = color;
            Handles.DrawSolidDisc(rect.center, Vector3.forward, rect.width * 0.5f);
            Handles.EndGUI();
        }

        /// <summary>Truncates with an ellipsis. fromLeft keeps the tail (good for hierarchy paths).</summary>
        public static string Fit(string text, GUIStyle style, float width, bool fromLeft = false)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (style.CalcSize(new GUIContent(text)).x <= width) return text;

            const string ell = "…";
            if (fromLeft)
            {
                for (int i = 1; i < text.Length; i++)
                {
                    var candidate = ell + text.Substring(i);
                    if (style.CalcSize(new GUIContent(candidate)).x <= width) return candidate;
                }
            }
            else
            {
                for (int i = text.Length - 1; i > 0; i--)
                {
                    var candidate = text.Substring(0, i) + ell;
                    if (style.CalcSize(new GUIContent(candidate)).x <= width) return candidate;
                }
            }
            return ell;
        }
    }
}