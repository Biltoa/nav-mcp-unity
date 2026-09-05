using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// <c>ui.layoutReport</c>: the UI defects that are geometric, and therefore checkable without
    /// looking at a screenshot — elements off the canvas, elements with no size, overlapping
    /// siblings, text below the contrast threshold, and content outside the safe area.
    ///
    /// uGUI types live in an assembly this package does not reference, so <c>Graphic</c> colours
    /// and text sizes are read by reflection. The alternative — referencing UnityEngine.UI —
    /// would make the agent fail to compile in a project that does not use uGUI.
    /// </summary>
    internal static class UiTools
    {
        [UnityTool(Skill = "ui", Id = "ui.layoutReport",
            Summary = "Geometric UI problems on a canvas: off-screen rects, zero-size elements, overlapping siblings, low-contrast text, unsafe-area content.",
            Retry = RetryClass.Read, Cost = Cost.Moderate)]
        [Example("{ }")]
        [Example("{ \"canvas\": \"HUD\", \"checks\": [\"offscreen\", \"contrast\"] }")]
        public static object LayoutReport(
            [Doc("Canvas name or path. Defaults to every canvas in the open scenes.")] string canvas = null,
            [Doc("Which checks: offscreen, zerosize, overlap, contrast, safearea. Default all.")] string[] checks = null,
            [Doc("Safe-area inset in pixels to assume, for notch devices (default 0)")] float safeAreaInset = 0f,
            [Doc("Maximum findings (default 50)")] int limit = 50)
        {
            int cap = Bounds.Limit(limit);
            var wanted = new HashSet<string>(checks == null || checks.Length == 0
                ? new[] { "offscreen", "zerosize", "overlap", "contrast", "safearea" }
                : checks.Select(c => (c ?? "").ToLowerInvariant()));

            var canvases = Resolve.AllGameObjects()
                .Select(go => go.GetComponent<Canvas>())
                .Where(c => c != null && c.isRootCanvas)
                .ToArray();

            if (!string.IsNullOrEmpty(canvas))
            {
                var target = Resolve.GameObject(canvas, "canvas");
                var c = target.GetComponent<Canvas>();
                if (c == null)
                    throw new UmcpToolException("E_NOT_A_CANVAS", "'" + target.name + "' has no Canvas component.",
                        "canvas", canvas,
                        canvases.Select(x => x.name).ToArray(),
                        "Omit the argument to report on every canvas.");
                canvases = new[] { c };
            }

            if (canvases.Length == 0)
                return new { canvases = 0, findings = new object[0], _hint = "No canvases in the open scenes." };

            var findings = new List<object>();
            var reports = new List<object>();

            foreach (var c in canvases)
            {
                var canvasRect = c.GetComponent<RectTransform>();
                if (canvasRect == null) continue;
                var bounds = WorldRect(canvasRect);

                var elements = c.GetComponentsInChildren<RectTransform>(false)
                    .Where(r => r != canvasRect)
                    .ToArray();

                foreach (var rect in elements)
                {
                    if (findings.Count >= cap) break;
                    var path = Resolve.Path(rect.transform);
                    var r = WorldRect(rect);

                    if (wanted.Contains("zerosize") && (r.width <= 0.01f || r.height <= 0.01f))
                        findings.Add(new
                        {
                            severity = "warning",
                            code = "UI_ZERO_SIZE",
                            subject = path,
                            detail = "Rect is " + r.width.ToString("0.#") + " x " + r.height.ToString("0.#") + ".",
                            fix = "Give it a size, or let a layout group drive it."
                        });

                    if (wanted.Contains("offscreen") && !bounds.Overlaps(r))
                        findings.Add(new
                        {
                            severity = "warning",
                            code = "UI_OFFSCREEN",
                            subject = path,
                            detail = "Rect sits entirely outside the canvas (" + Describe(r) + " vs canvas " + Describe(bounds) + ").",
                            fix = "Check its anchors — an element anchored to a corner and offset in pixels leaves the screen at other resolutions."
                        });

                    if (wanted.Contains("safearea") && safeAreaInset > 0f)
                    {
                        var safe = new Rect(bounds.x + safeAreaInset, bounds.y + safeAreaInset,
                                            bounds.width - safeAreaInset * 2, bounds.height - safeAreaInset * 2);
                        if (bounds.Overlaps(r) && !safe.Contains(new Vector2(r.xMin, r.yMin)))
                            findings.Add(new
                            {
                                severity = "info",
                                code = "UI_OUTSIDE_SAFE_AREA",
                                subject = path,
                                detail = "Extends into the " + safeAreaInset.ToString("0") + " px inset reserved for notches and home indicators.",
                                fix = "Parent it under a safe-area container."
                            });
                    }

                    if (wanted.Contains("contrast"))
                    {
                        var contrast = TextContrast(rect.gameObject);
                        if (contrast != null && contrast.Value < 4.5f)
                            findings.Add(new
                            {
                                severity = "warning",
                                code = "UI_LOW_CONTRAST",
                                subject = path,
                                detail = "Text contrast ratio against its nearest background is " +
                                         contrast.Value.ToString("0.0") + ":1.",
                                fix = "WCAG AA wants 4.5:1 for body text, 3:1 for large text. Darken the text or lighten the background."
                            });
                    }
                }

                if (wanted.Contains("overlap"))
                    foreach (var f in Overlaps(elements, cap - findings.Count)) findings.Add(f);

                reports.Add(new
                {
                    canvas = Resolve.Path(c.transform),
                    renderMode = c.renderMode.ToString(),
                    referenceResolution = ReferenceResolution(c),
                    elements = elements.Length
                });
            }

            var list = findings.Take(cap).ToArray();
            return new
            {
                canvases = reports.ToArray(),
                count = list.Length,
                findings = list,
                _truncated = findings.Count > list.Length,
                _hint = list.Length == 0 ? "No geometric problems found." :
                    "Geometry only: this says nothing about whether the layout looks good, only about whether it is broken."
            };
        }

        /// <summary>Siblings whose rects overlap. Only siblings: a child overlapping its parent is the normal case.</summary>
        static IEnumerable<object> Overlaps(RectTransform[] elements, int budget)
        {
            if (budget <= 0) yield break;
            var byParent = elements.Where(e => e.parent != null).GroupBy(e => e.parent);
            int found = 0;

            foreach (var group in byParent)
            {
                var siblings = group.Where(IsGraphic).ToArray();
                for (int i = 0; i < siblings.Length; i++)
                    for (int j = i + 1; j < siblings.Length; j++)
                    {
                        var a = WorldRect(siblings[i]);
                        var b = WorldRect(siblings[j]);
                        if (!a.Overlaps(b)) continue;

                        var overlap = Rect.MinMaxRect(
                            Mathf.Max(a.xMin, b.xMin), Mathf.Max(a.yMin, b.yMin),
                            Mathf.Min(a.xMax, b.xMax), Mathf.Min(a.yMax, b.yMax));
                        float area = overlap.width * overlap.height;
                        float smaller = Mathf.Min(a.width * a.height, b.width * b.height);
                        if (smaller <= 0f || area / smaller < 0.5f) continue;   // a nudge is not an overlap

                        found++;
                        yield return new
                        {
                            severity = "info",
                            code = "UI_OVERLAP",
                            subject = Resolve.Path(siblings[i].transform),
                            detail = "Overlaps sibling '" + siblings[j].name + "' by " +
                                     (area / smaller * 100f).ToString("0") + "% of the smaller rect.",
                            fix = "Intentional for backgrounds and badges; a defect when both are interactive."
                        };
                        if (found >= budget) yield break;
                    }
            }
        }

        static bool IsGraphic(RectTransform rect)
        {
            foreach (var component in rect.GetComponents<Component>())
            {
                if (component == null) continue;
                var t = component.GetType();
                while (t != null)
                {
                    if (t.FullName == "UnityEngine.UI.Graphic") return true;
                    t = t.BaseType;
                }
            }
            return false;
        }

        /// <summary>
        /// Contrast ratio between a text element's colour and the nearest opaque ancestor
        /// background, by the WCAG relative-luminance formula. Null when the object is not text or
        /// there is nothing to compare it against — an unknown is reported as unknown.
        /// </summary>
        static float? TextContrast(GameObject go)
        {
            Color? textColor = null;
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null) continue;
                var name = component.GetType().FullName;
                bool isText = name == "UnityEngine.UI.Text" ||
                              name == "TMPro.TextMeshProUGUI" ||
                              name == "TMPro.TextMeshPro";
                if (!isText) continue;

                var colorProp = component.GetType().GetProperty("color", BindingFlags.Public | BindingFlags.Instance);
                if (colorProp == null) continue;
                try { textColor = (Color)colorProp.GetValue(component, null); }
                catch { }
                break;
            }
            if (textColor == null) return null;

            var parent = go.transform.parent;
            while (parent != null)
            {
                var background = OpaqueColor(parent.gameObject);
                if (background != null) return Ratio(textColor.Value, background.Value);
                parent = parent.parent;
            }
            return null;
        }

        static Color? OpaqueColor(GameObject go)
        {
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null) continue;
                var t = component.GetType();
                bool graphic = false;
                var walk = t;
                while (walk != null) { if (walk.FullName == "UnityEngine.UI.Graphic") { graphic = true; break; } walk = walk.BaseType; }
                if (!graphic) continue;

                var colorProp = t.GetProperty("color", BindingFlags.Public | BindingFlags.Instance);
                if (colorProp == null) continue;
                try
                {
                    var c = (Color)colorProp.GetValue(component, null);
                    if (c.a >= 0.9f) return c;
                }
                catch { }
            }
            return null;
        }

        static float Ratio(Color a, Color b)
        {
            float la = Luminance(a), lb = Luminance(b);
            float lighter = Mathf.Max(la, lb), darker = Mathf.Min(la, lb);
            return (lighter + 0.05f) / (darker + 0.05f);
        }

        static float Luminance(Color c)
        {
            return 0.2126f * Channel(c.r) + 0.7152f * Channel(c.g) + 0.0722f * Channel(c.b);
        }

        static float Channel(float v)
        {
            return v <= 0.03928f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
        }

        static Rect WorldRect(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
        }

        static string Describe(Rect r)
        {
            return "x " + r.xMin.ToString("0") + ".." + r.xMax.ToString("0") +
                   ", y " + r.yMin.ToString("0") + ".." + r.yMax.ToString("0");
        }

        static object ReferenceResolution(Canvas canvas)
        {
            foreach (var component in canvas.GetComponents<Component>())
            {
                if (component == null) continue;
                if (component.GetType().FullName != "UnityEngine.UI.CanvasScaler") continue;
                var prop = component.GetType().GetProperty("referenceResolution");
                if (prop == null) continue;
                try
                {
                    var v = (Vector2)prop.GetValue(component, null);
                    return new { x = v.x, y = v.y };
                }
                catch { return null; }
            }
            return null;
        }
    }
}
