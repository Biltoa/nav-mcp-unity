using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// The UI work that is not a screenshot problem: where a rect sits, how a group arranges its
    /// children, and how text is styled.
    ///
    /// uGUI and TextMeshPro live in assemblies this package deliberately does not reference — an
    /// agent that failed to compile in a project without uGUI would be worse than one that says
    /// "uGUI is not installed here" — so every uGUI type is reached through
    /// <see cref="Resolve.FindType"/> and driven by reflection. RectTransform is the exception:
    /// it is core engine, so <c>ui.rect</c> works in any project at all.
    /// </summary>
    internal static class UiLayoutTools
    {
        // ---------------------------------------------------------------- rects

        [UnityTool(Skill = "ui", Id = "ui.rect",
            Summary = "RectTransform anchors, pivot, size and position, including the anchor presets from the Inspector. action: info | set | anchor | stretch.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set UI rect")]
        [Example("{ \"action\": \"info\", \"target\": \"HUD/Health\" }")]
        [Example("{ \"action\": \"anchor\", \"target\": \"HUD/Health\", \"preset\": \"topLeft\", \"size\": [220, 48] }")]
        [Example("{ \"action\": \"stretch\", \"target\": \"HUD/Backdrop\", \"margin\": [16, 16, 16, 16] }")]
        public static object Rect(
            [Doc("info | set | anchor | stretch")] string action,
            [Doc("GameObject with a RectTransform")] string target,
            [Doc("Anchor preset: topLeft, top, topRight, left, center, right, bottomLeft, bottom, bottomRight, stretch, stretchTop, stretchBottom, stretchLeft, stretchRight")] string preset = null,
            [Doc("Size [width, height]")] float[] size = null,
            [Doc("Anchored position [x, y]")] float[] position = null,
            [Doc("Pivot [x, y], 0..1")] float[] pivot = null,
            [Doc("Stretch margins [left, top, right, bottom]")] float[] margin = null)
        {
            var verb = Actions.Require(action, "info", "set", "anchor", "stretch");
            var go = Resolve.GameObject(target, "target");
            var rect = go.GetComponent<RectTransform>();
            if (rect == null)
                throw new UmcpToolException("E_NOT_A_RECT", "'" + go.name + "' has no RectTransform, so it is not a UI element.",
                    "target", target, null,
                    "UI objects live under a Canvas. gameobject.create with a parent under one, or setup.uiScreen for a whole screen.");

            if (verb == "info")
                return Describe(rect);

            Undo.RecordObject(rect, "Set UI rect");

            switch (verb)
            {
                case "anchor":
                {
                    if (string.IsNullOrEmpty(preset))
                        throw new UmcpToolException("E_BAD_ARG", "anchor needs a preset.", "preset", null, PresetNames, null);
                    ApplyPreset(rect, preset);
                    break;
                }

                case "stretch":
                {
                    ApplyPreset(rect, "stretch");
                    // Inset from each edge, which is what "stretch with a margin" means in the
                    // Inspector and what offsetMin/offsetMax actually store. The sign on the max
                    // side is inverted, which is the detail everybody gets wrong by hand.
                    var m = margin ?? new float[] { 0, 0, 0, 0 };
                    if (m.Length != 4)
                        throw new UmcpToolException("E_BAD_ARG", "margin is [left, top, right, bottom].",
                            "margin", string.Join(",", m), null, null);
                    rect.offsetMin = new Vector2(m[0], m[3]);
                    rect.offsetMax = new Vector2(-m[2], -m[1]);
                    break;
                }

                case "set":
                    break;

                default:
                    throw new UmcpToolException("E_BAD_ARG", "Unknown action '" + action + "'.",
                        "action", action, new[] { "info", "set", "anchor", "stretch" }, null);
            }

            if (pivot != null && pivot.Length >= 2) rect.pivot = new Vector2(pivot[0], pivot[1]);

            // Size after the preset, because a stretched axis has no size of its own: writing
            // sizeDelta on a stretched axis sets the *inset*, not the width, which is why "I set
            // the width to 220 and it filled the screen" happens.
            if (size != null && size.Length >= 2 && verb != "stretch")
                rect.sizeDelta = new Vector2(size[0], size[1]);

            if (position != null && position.Length >= 2)
                rect.anchoredPosition = new Vector2(position[0], position[1]);

            EditorUtility.SetDirty(rect);
            return Describe(rect);
        }

        // ---------------------------------------------------------------- layout groups

        [UnityTool(Skill = "ui", Id = "ui.layout",
            Summary = "Layout groups and fitters: arrange children horizontally, vertically or in a grid, and size a container to its content. action: info | horizontal | vertical | grid | fit | element | remove.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set UI layout")]
        [Example("{ \"action\": \"vertical\", \"target\": \"Menu/Buttons\", \"spacing\": 12, \"padding\": [16, 16, 16, 16] }")]
        [Example("{ \"action\": \"grid\", \"target\": \"Inventory/Slots\", \"cellSize\": [96, 96], \"spacing\": 8 }")]
        [Example("{ \"action\": \"fit\", \"target\": \"Tooltip\", \"vertical\": \"preferred\" }")]
        public static object Layout(
            [Doc("info | horizontal | vertical | grid | fit | element | remove")] string action,
            [Doc("GameObject to arrange (the container, not the children)")] string target,
            [Doc("Gap between children")] float spacing = 0f,
            [Doc("Padding [left, top, right, bottom]")] float[] padding = null,
            [Doc("Child alignment: UpperLeft … MiddleCenter … LowerRight")] string alignment = "UpperLeft",
            [Doc("Grid cell size [width, height]")] float[] cellSize = null,
            [Doc("Stretch children along the layout axis")] bool expand = false,
            [Doc("ContentSizeFitter horizontal mode: unconstrained | preferred | minimum")] string horizontal = null,
            [Doc("ContentSizeFitter vertical mode: unconstrained | preferred | minimum")] string vertical = null,
            [Doc("LayoutElement preferred size [width, height]; -1 leaves an axis alone")] float[] preferred = null,
            [Doc("LayoutElement flexible weight [horizontal, vertical]")] float[] flexible = null)
        {
            var verb = Actions.Require(action,
                "info", "horizontal", "vertical", "grid", "fit", "element", "remove");
            var go = Resolve.GameObject(target, "target");

            if (verb == "info") return LayoutInfo(go);

            if (verb == "remove")
            {
                var removed = new List<string>();
                foreach (var name in new[] { "UnityEngine.UI.HorizontalLayoutGroup", "UnityEngine.UI.VerticalLayoutGroup",
                                             "UnityEngine.UI.GridLayoutGroup", "UnityEngine.UI.ContentSizeFitter",
                                             "UnityEngine.UI.LayoutElement" })
                {
                    var type = Resolve.FindType(name);
                    if (type == null) continue;
                    var existing = go.GetComponent(type);
                    if (existing == null) continue;
                    Undo.DestroyObjectImmediate(existing);
                    removed.Add(name.Substring(name.LastIndexOf('.') + 1));
                }
                return new { target = go.name, removed = removed.ToArray() };
            }

            switch (verb)
            {
                case "horizontal":
                case "vertical":
                {
                    var typeName = verb == "horizontal"
                        ? "UnityEngine.UI.HorizontalLayoutGroup"
                        : "UnityEngine.UI.VerticalLayoutGroup";
                    var group = Require(go, typeName);

                    Set(group, "spacing", spacing);
                    Set(group, "childAlignment", ParseEnum(group, "childAlignment", alignment));
                    Set(group, "padding", MakePadding(padding));
                    // Control *and* expand: a layout group that controls size but does not expand
                    // leaves children at zero width, which reads as "the buttons vanished".
                    Set(group, "childControlWidth", true);
                    Set(group, "childControlHeight", true);
                    Set(group, "childForceExpandWidth", verb == "horizontal" && expand);
                    Set(group, "childForceExpandHeight", verb == "vertical" && expand);
                    break;
                }

                case "grid":
                {
                    var group = Require(go, "UnityEngine.UI.GridLayoutGroup");
                    if (cellSize != null && cellSize.Length >= 2)
                        Set(group, "cellSize", new Vector2(cellSize[0], cellSize[1]));
                    Set(group, "spacing", new Vector2(spacing, spacing));
                    Set(group, "childAlignment", ParseEnum(group, "childAlignment", alignment));
                    Set(group, "padding", MakePadding(padding));
                    break;
                }

                case "fit":
                {
                    var fitter = Require(go, "UnityEngine.UI.ContentSizeFitter");
                    if (horizontal != null) Set(fitter, "horizontalFit", ParseFit(fitter, horizontal));
                    if (vertical != null) Set(fitter, "verticalFit", ParseFit(fitter, vertical));
                    break;
                }

                case "element":
                {
                    var element = Require(go, "UnityEngine.UI.LayoutElement");
                    if (preferred != null && preferred.Length >= 2)
                    {
                        if (preferred[0] >= 0) Set(element, "preferredWidth", preferred[0]);
                        if (preferred[1] >= 0) Set(element, "preferredHeight", preferred[1]);
                    }
                    if (flexible != null && flexible.Length >= 2)
                    {
                        Set(element, "flexibleWidth", flexible[0]);
                        Set(element, "flexibleHeight", flexible[1]);
                    }
                    break;
                }

                default:
                    throw new UmcpToolException("E_BAD_ARG", "Unknown action '" + action + "'.",
                        "action", action,
                        new[] { "info", "horizontal", "vertical", "grid", "fit", "element", "remove" }, null);
            }

            EditorUtility.SetDirty(go);
            return LayoutInfo(go);
        }

        // ---------------------------------------------------------------- text

        [UnityTool(Skill = "ui", Id = "ui.text",
            Summary = "Read and style UI text, TextMeshPro or legacy: content, size, colour, alignment, wrapping, auto-size. action: info | set.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set UI text")]
        [Example("{ \"action\": \"info\", \"target\": \"HUD/Score\" }")]
        [Example("{ \"action\": \"set\", \"target\": \"HUD/Score\", \"text\": \"0\", \"fontSize\": 36, \"color\": [1, 1, 1, 1], \"alignment\": \"center\" }")]
        public static object Text(
            [Doc("info | set")] string action,
            [Doc("GameObject carrying a TMP or uGUI text component")] string target,
            [Doc("The string to display")] string text = null,
            [Doc("Font size in points")] float? fontSize = null,
            [Doc("Colour [r, g, b, a], 0..1")] float[] color = null,
            [Doc("left | center | right")] string alignment = null,
            [Doc("Wrap long lines")] bool? wrap = null,
            [Doc("Shrink text to fit its rect")] bool? autoSize = null)
        {
            var verb = Actions.Require(action, "info", "set");
            var go = Resolve.GameObject(target, "target");
            var component = FindText(go);
            if (component == null)
                throw new UmcpToolException("E_NO_TEXT_COMPONENT",
                    "'" + go.name + "' has no TextMeshPro or uGUI Text component.",
                    "target", target,
                    go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).ToArray(),
                    "setup.uiScreen creates labels with the right component for this project.");

            var tmp = component.GetType().FullName.StartsWith("TMPro.");

            if (verb == "info") return TextInfo(component, tmp);

            Undo.RecordObject(component, "Set UI text");

            if (text != null) Set(component, "text", text);
            if (fontSize.HasValue) Set(component, "fontSize", tmp ? (object)fontSize.Value : (object)(int)fontSize.Value);
            if (color != null && color.Length >= 3)
                Set(component, "color", new Color(color[0], color[1], color[2], color.Length > 3 ? color[3] : 1f));

            if (alignment != null)
            {
                // TMP and uGUI disagree on the enum entirely — TextAlignmentOptions versus
                // TextAnchor — so the tool takes three words and maps them per component.
                if (tmp) Set(component, "alignment", ParseNamedEnum(component, "alignment", TmpAlignment(alignment)));
                else Set(component, "alignment", ParseNamedEnum(component, "alignment", UguiAlignment(alignment)));
            }

            if (wrap.HasValue)
            {
                if (tmp) Set(component, "enableWordWrapping", wrap.Value);
                else Set(component, "horizontalOverflow", ParseNamedEnum(component, "horizontalOverflow", wrap.Value ? "Wrap" : "Overflow"));
            }

            if (autoSize.HasValue)
            {
                if (tmp) Set(component, "enableAutoSizing", autoSize.Value);
                else Set(component, "resizeTextForBestFit", autoSize.Value);
            }

            EditorUtility.SetDirty(component);
            return TextInfo(component, tmp);
        }

        // ---------------------------------------------------------------- helpers

        static readonly string[] PresetNames =
        {
            "topLeft", "top", "topRight", "left", "center", "right",
            "bottomLeft", "bottom", "bottomRight",
            "stretch", "stretchTop", "stretchBottom", "stretchLeft", "stretchRight"
        };

        static void ApplyPreset(RectTransform rect, string preset)
        {
            Vector2 min, max, pivot;
            switch ((preset ?? "").ToLowerInvariant())
            {
                case "topleft":      min = new Vector2(0, 1); max = new Vector2(0, 1); pivot = new Vector2(0, 1); break;
                case "top":          min = new Vector2(.5f, 1); max = new Vector2(.5f, 1); pivot = new Vector2(.5f, 1); break;
                case "topright":     min = new Vector2(1, 1); max = new Vector2(1, 1); pivot = new Vector2(1, 1); break;
                case "left":         min = new Vector2(0, .5f); max = new Vector2(0, .5f); pivot = new Vector2(0, .5f); break;
                case "center":       min = new Vector2(.5f, .5f); max = new Vector2(.5f, .5f); pivot = new Vector2(.5f, .5f); break;
                case "right":        min = new Vector2(1, .5f); max = new Vector2(1, .5f); pivot = new Vector2(1, .5f); break;
                case "bottomleft":   min = new Vector2(0, 0); max = new Vector2(0, 0); pivot = new Vector2(0, 0); break;
                case "bottom":       min = new Vector2(.5f, 0); max = new Vector2(.5f, 0); pivot = new Vector2(.5f, 0); break;
                case "bottomright":  min = new Vector2(1, 0); max = new Vector2(1, 0); pivot = new Vector2(1, 0); break;
                case "stretch":      min = Vector2.zero; max = Vector2.one; pivot = new Vector2(.5f, .5f); break;
                case "stretchtop":   min = new Vector2(0, 1); max = new Vector2(1, 1); pivot = new Vector2(.5f, 1); break;
                case "stretchbottom":min = new Vector2(0, 0); max = new Vector2(1, 0); pivot = new Vector2(.5f, 0); break;
                case "stretchleft":  min = new Vector2(0, 0); max = new Vector2(0, 1); pivot = new Vector2(0, .5f); break;
                case "stretchright": min = new Vector2(1, 0); max = new Vector2(1, 1); pivot = new Vector2(1, .5f); break;
                default:
                    throw new UmcpToolException("E_BAD_ARG", "Unknown anchor preset '" + preset + "'.",
                        "preset", preset, PresetNames, null);
            }

            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.pivot = pivot;
        }

        static object Describe(RectTransform rect)
        {
            var stretchedX = Mathf.Abs(rect.anchorMax.x - rect.anchorMin.x) > 0.0001f;
            var stretchedY = Mathf.Abs(rect.anchorMax.y - rect.anchorMin.y) > 0.0001f;
            return new
            {
                target = rect.name,
                path = Resolve.Path(rect),
                anchorMin = Vec.Arr(rect.anchorMin),
                anchorMax = Vec.Arr(rect.anchorMax),
                pivot = Vec.Arr(rect.pivot),
                anchoredPosition = Vec.Arr(rect.anchoredPosition),
                size = Vec.Arr(rect.rect.size),
                // Said out loud because sizeDelta means "inset" on a stretched axis, and a caller
                // reading size back after setting it deserves to know which it got.
                stretched = new { x = stretchedX, y = stretchedY },
                offsetMin = Vec.Arr(rect.offsetMin),
                offsetMax = Vec.Arr(rect.offsetMax)
            };
        }

        static object LayoutInfo(GameObject go)
        {
            var found = new List<object>();
            foreach (var name in new[] { "UnityEngine.UI.HorizontalLayoutGroup", "UnityEngine.UI.VerticalLayoutGroup",
                                         "UnityEngine.UI.GridLayoutGroup", "UnityEngine.UI.ContentSizeFitter",
                                         "UnityEngine.UI.LayoutElement" })
            {
                var type = Resolve.FindType(name);
                if (type == null) continue;
                var component = go.GetComponent(type);
                if (component == null) continue;

                found.Add(new
                {
                    component = type.Name,
                    spacing = Read(component, "spacing"),
                    padding = Read(component, "padding") is RectOffset o
                        ? new[] { o.left, o.top, o.right, o.bottom }
                        : null,
                    cellSize = Read(component, "cellSize"),
                    alignment = Read(component, "childAlignment")?.ToString(),
                    horizontalFit = Read(component, "horizontalFit")?.ToString(),
                    verticalFit = Read(component, "verticalFit")?.ToString()
                });
            }

            var rect = go.GetComponent<RectTransform>();
            return new
            {
                target = go.name,
                children = go.transform.childCount,
                layout = found.ToArray(),
                size = rect != null ? Vec.Arr(rect.rect.size) : null
            };
        }

        static object TextInfo(Component component, bool tmp)
        {
            var colour = Read(component, "color");
            return new
            {
                target = component.gameObject.name,
                component = component.GetType().Name,
                kind = tmp ? "TextMeshPro" : "uGUI",
                text = Read(component, "text") as string,
                fontSize = Read(component, "fontSize"),
                color = colour is Color c ? Vec.Arr(c) : null,
                alignment = Read(component, "alignment")?.ToString(),
                autoSize = Read(component, tmp ? "enableAutoSizing" : "resizeTextForBestFit")
            };
        }

        static Component FindText(GameObject go)
        {
            foreach (var name in new[] { "TMPro.TextMeshProUGUI", "TMPro.TextMeshPro", "UnityEngine.UI.Text" })
            {
                var type = Resolve.FindType(name);
                if (type == null) continue;
                var component = go.GetComponent(type);
                if (component != null) return component;
            }
            return null;
        }

        /// <summary>Get the component, adding it if the object does not have it yet.</summary>
        static Component Require(GameObject go, string typeName)
        {
            var type = Resolve.FindType(typeName);
            if (type == null)
                throw new UmcpToolException("E_UGUI_MISSING",
                    typeName.Substring(typeName.LastIndexOf('.') + 1) + " is not available: this project does not have uGUI installed.",
                    "target", go.name, null,
                    "Add com.unity.ugui to the project, or build the UI with whatever system it does use.");

            var existing = go.GetComponent(type);
            if (existing != null) { Undo.RecordObject(existing, "Set UI layout"); return existing; }
            return Undo.AddComponent(go, type);
        }

        static RectOffset MakePadding(float[] padding)
        {
            var p = padding ?? new float[] { 0, 0, 0, 0 };
            if (p.Length != 4)
                throw new UmcpToolException("E_BAD_ARG", "padding is [left, top, right, bottom].",
                    "padding", string.Join(",", p), null, null);
            return new RectOffset((int)p[0], (int)p[2], (int)p[1], (int)p[3]);
        }

        static object ParseFit(Component fitter, string mode)
        {
            switch ((mode ?? "").ToLowerInvariant())
            {
                case "unconstrained": return ParseNamedEnum(fitter, "horizontalFit", "Unconstrained");
                case "preferred": return ParseNamedEnum(fitter, "horizontalFit", "PreferredSize");
                case "minimum": case "min": return ParseNamedEnum(fitter, "horizontalFit", "MinSize");
                default:
                    throw new UmcpToolException("E_BAD_ARG", "Fit mode must be unconstrained, preferred or minimum.",
                        "fit", mode, new[] { "unconstrained", "preferred", "minimum" }, null);
            }
        }

        static string TmpAlignment(string a)
        {
            switch ((a ?? "").ToLowerInvariant())
            {
                case "left": return "Left";
                case "center": case "centre": return "Center";
                case "right": return "Right";
                default:
                    throw new UmcpToolException("E_BAD_ARG", "alignment must be left, center or right.",
                        "alignment", a, new[] { "left", "center", "right" }, null);
            }
        }

        static string UguiAlignment(string a)
        {
            switch ((a ?? "").ToLowerInvariant())
            {
                case "left": return "MiddleLeft";
                case "center": case "centre": return "MiddleCenter";
                case "right": return "MiddleRight";
                default:
                    throw new UmcpToolException("E_BAD_ARG", "alignment must be left, center or right.",
                        "alignment", a, new[] { "left", "center", "right" }, null);
            }
        }

        // ---------------------------------------------------------------- reflection

        static object ParseEnum(Component component, string property, string value)
        {
            return ParseNamedEnum(component, property, value);
        }

        static object ParseNamedEnum(Component component, string property, string value)
        {
            var prop = component.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null) return null;
            try { return Enum.Parse(prop.PropertyType, value, true); }
            catch
            {
                throw new UmcpToolException("E_BAD_ARG",
                    "'" + value + "' is not a valid " + prop.PropertyType.Name + ".",
                    property, value, Enum.GetNames(prop.PropertyType).Take(12).ToArray(), null);
            }
        }

        static void Set(Component component, string property, object value)
        {
            if (value == null) return;
            var prop = component.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanWrite) return;
            try { prop.SetValue(component, value, null); }
            catch (Exception e)
            {
                throw new UmcpToolException("E_SET_FAILED",
                    "Could not set " + property + " on " + component.GetType().Name + ": " + e.Message,
                    property, value.ToString(), null, null);
            }
        }

        static object Read(Component component, string property)
        {
            var prop = component.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanRead) return null;
            try
            {
                var value = prop.GetValue(component, null);
                if (value is Vector2 v) return Vec.Arr(v);
                return value;
            }
            catch { return null; }
        }
    }
}
