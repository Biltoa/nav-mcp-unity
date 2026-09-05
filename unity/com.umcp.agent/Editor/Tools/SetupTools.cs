using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// Composite tools (section 8.3): sequences an agent repeats, shipped as one tool and one
    /// Editor tick.
    ///
    /// The bar for adding one is deliberately high. A composite is worth it when the sequence is
    /// long, when the order matters, and when getting it slightly wrong produces something that
    /// *looks* right — a canvas with no EventSystem takes clicks nowhere, and nothing about the
    /// scene says so. It is not worth it as a shortcut for two calls an agent can already make.
    /// </summary>
    internal static class SetupTools
    {
        [UnityTool(Skill = "ui", Id = "setup.uiScreen",
            Summary = "Create a working UI screen: canvas, scaler, raycaster, EventSystem if missing, a root panel, and the elements you name.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set up UI screen", Cost = Cost.Moderate)]
        [Example("{ \"name\": \"PauseMenu\", \"elements\": [\"text:Paused\", \"button:Resume\", \"button:Quit\"] }")]
        public static object UiScreen(
            [Doc("Screen name; the canvas takes it")] string name,
            [Doc("Elements, each \"kind:Label\" where kind is text, button, image or panel")] string[] elements = null,
            [Doc("Reference resolution [width, height] (default [1920, 1080])")] float[] referenceResolution = null,
            [Doc("Sort order, when several canvases overlap")] int sortOrder = 0)
        {
            if (string.IsNullOrEmpty(name))
                throw new UmcpToolException("E_ARG_REQUIRED", "A screen name is required.", "name");

            var ui = UiTypes();
            if (ui == null)
                throw new UmcpToolException("E_PACKAGE_MISSING",
                    "uGUI (UnityEngine.UI) is not available in this project.", "name", name, null,
                    "Install com.unity.ugui, or build the screen with UI Toolkit instead.");

            var reference = referenceResolution == null || referenceResolution.Length != 2
                ? new Vector2(1920, 1080)
                : new Vector2(referenceResolution[0], referenceResolution[1]);

            var created = new List<string>();

            // The canvas.
            var canvasGo = new GameObject(name, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(canvasGo, "Set up UI screen");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;

            var scaler = canvasGo.AddComponent(ui.CanvasScaler);
            Set(scaler, "uiScaleMode", 1);                    // ScaleWithScreenSize
            Set(scaler, "referenceResolution", reference);
            Set(scaler, "matchWidthOrHeight", 0.5f);
            canvasGo.AddComponent(ui.GraphicRaycaster);
            created.Add(name);

            // The EventSystem. A canvas without one looks completely correct and receives no
            // input at all, which is the single most common "my button does nothing" cause.
            bool eventSystemCreated = false;
            if (ui.EventSystem != null && UnityEngine.Object.FindFirstObjectByType(ui.EventSystem) == null)
            {
                var esGo = new GameObject("EventSystem");
                Undo.RegisterCreatedObjectUndo(esGo, "Set up UI screen");
                esGo.AddComponent(ui.EventSystem);
                if (ui.InputModule != null) esGo.AddComponent(ui.InputModule);
                eventSystemCreated = true;
                created.Add("EventSystem");
            }

            // A full-screen root panel, so anchoring is sane for everything inside it.
            var panel = NewRect("Panel", canvasGo.transform);
            Stretch(panel);
            var panelImage = panel.gameObject.AddComponent(ui.Image);
            Set(panelImage, "color", new Color(0f, 0f, 0f, 0.5f));
            created.Add(name + "/Panel");

            var made = new List<object>();
            float y = 220f;
            foreach (var spec in elements ?? new string[0])
            {
                var parts = (spec ?? "").Split(new[] { ':' }, 2);
                var kind = parts[0].Trim().ToLowerInvariant();
                var label = parts.Length > 1 ? parts[1].Trim() : kind;

                var element = NewRect(label, panel);
                element.anchorMin = element.anchorMax = new Vector2(0.5f, 0.5f);
                element.pivot = new Vector2(0.5f, 0.5f);
                element.anchoredPosition = new Vector2(0, y);
                element.sizeDelta = new Vector2(400, 80);
                y -= 100f;

                switch (kind)
                {
                    case "text":
                        AddText(element.gameObject, ui, label);
                        break;

                    case "button":
                        {
                            var image = element.gameObject.AddComponent(ui.Image);
                            Set(image, "color", new Color(0.2f, 0.4f, 0.9f, 1f));
                            element.gameObject.AddComponent(ui.Button);
                            var textGo = NewRect("Label", element);
                            Stretch(textGo);
                            AddText(textGo.gameObject, ui, label);
                            break;
                        }

                    case "image":
                        element.gameObject.AddComponent(ui.Image);
                        break;

                    case "panel":
                        {
                            var image = element.gameObject.AddComponent(ui.Image);
                            Set(image, "color", new Color(1f, 1f, 1f, 0.1f));
                            break;
                        }

                    default:
                        UnityEngine.Object.DestroyImmediate(element.gameObject);
                        throw new UmcpToolException("E_ARG_VALUE",
                            "Unknown element kind '" + kind + "' in \"" + spec + "\".",
                            "elements", spec, new[] { "text", "button", "image", "panel" },
                            "Elements look like \"button:Resume\".");
                }

                made.Add(new { kind, name = label, path = Resolve.Path(element) });
                created.Add(Resolve.Path(element));
            }

            return new
            {
                canvas = Resolve.Path(canvasGo.transform),
                eventSystemCreated,
                referenceResolution = new[] { reference.x, reference.y },
                elements = made.ToArray(),
                created = created.ToArray(),
                _hint = "ui.layoutReport checks the result for off-screen, zero-size and low-contrast problems."
            };
        }

        [UnityTool(Skill = "lighting", Id = "setup.litInterior",
            Summary = "Light an interior: a key light, two fills, and a reflection probe, all sized and placed from the room's own bounds.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set up interior lighting", Cost = Cost.Moderate)]
        [Example("{ \"room\": \"Level/Kitchen\" }")]
        public static object LitInterior(
            [Doc("The room object; its renderer bounds decide placement")] string room,
            [Doc("Key light intensity (default 1.2)")] float intensity = 1.2f,
            [Doc("Colour temperature in kelvin (default 4000, warm interior)")] float kelvin = 4000f,
            [Doc("Bake mode: Realtime, Mixed or Baked (default Mixed)")] string bakeMode = "Mixed")
        {
            var target = Resolve.GameObject(room, "room");
            var renderers = target.GetComponentsInChildren<Renderer>(false);
            if (renderers.Length == 0)
                throw new UmcpToolException("E_NO_BOUNDS",
                    "'" + target.name + "' has no renderers, so there are no bounds to light.",
                    "room", room, null,
                    "Point this at the geometry, not at an empty parent.");

            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);

            LightmapBakeType bake;
            try { bake = (LightmapBakeType)Enum.Parse(typeof(LightmapBakeType), bakeMode, true); }
            catch
            {
                throw new UmcpToolException("E_ARG_VALUE", "Unknown bake mode '" + bakeMode + "'.",
                    "bakeMode", bakeMode, new[] { "Realtime", "Mixed", "Baked" }, null);
            }

            var parent = new GameObject(target.name + "_Lighting");
            Undo.RegisterCreatedObjectUndo(parent, "Set up interior lighting");
            parent.transform.position = bounds.center;

            var made = new List<object>();

            // Key: high, slightly off-centre, angled down. Centred lights flatten a room.
            var key = MakeLight(parent.transform, "Key",
                bounds.center + new Vector3(bounds.extents.x * 0.35f, bounds.extents.y * 0.8f, bounds.extents.z * 0.35f),
                LightType.Point, intensity, kelvin, Mathf.Max(bounds.extents.magnitude, 1f) * 1.4f, bake);
            made.Add(Describe(key));

            // Fills at a third of the key, on the opposite side, to lift the shadows without
            // flattening: two lights of equal intensity read as no lighting at all.
            var fillA = MakeLight(parent.transform, "Fill_A",
                bounds.center + new Vector3(-bounds.extents.x * 0.5f, bounds.extents.y * 0.5f, -bounds.extents.z * 0.2f),
                LightType.Point, intensity * 0.35f, kelvin + 500f, Mathf.Max(bounds.extents.magnitude, 1f), bake);
            made.Add(Describe(fillA));

            var fillB = MakeLight(parent.transform, "Fill_B",
                bounds.center + new Vector3(bounds.extents.x * 0.1f, bounds.extents.y * 0.4f, -bounds.extents.z * 0.6f),
                LightType.Point, intensity * 0.25f, kelvin - 300f, Mathf.Max(bounds.extents.magnitude, 1f), bake);
            made.Add(Describe(fillB));

            var probeGo = new GameObject("ReflectionProbe");
            probeGo.transform.SetParent(parent.transform, true);
            probeGo.transform.position = bounds.center;
            var probe = probeGo.AddComponent<ReflectionProbe>();
            probe.size = bounds.size;
            probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Baked;
            probe.boxProjection = true;

            return new
            {
                root = Resolve.Path(parent.transform),
                room = Resolve.Path(target.transform),
                bounds = new { center = Vec.Arr(bounds.center), size = Vec.Arr(bounds.size) },
                lights = made.ToArray(),
                reflectionProbe = Resolve.Path(probeGo.transform),
                bakeMode = bake.ToString(),
                _hint = bake == LightmapBakeType.Realtime
                    ? null
                    : "Mixed and Baked lights need a bake to look right: lighting.bake with action:\"start\"."
            };
        }

        // ---------------------------------------------------------------- helpers

        static Light MakeLight(Transform parent, string name, Vector3 position, LightType type,
                               float intensity, float kelvin, float range, LightmapBakeType bake)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, true);
            go.transform.position = position;
            var light = go.AddComponent<Light>();
            light.type = type;
            light.intensity = intensity;
            light.range = range;
            light.useColorTemperature = true;
            light.colorTemperature = kelvin;
            light.lightmapBakeType = bake;
            light.shadows = LightShadows.Soft;
            return light;
        }

        static object Describe(Light light)
        {
            return new
            {
                path = Resolve.Path(light.transform),
                type = light.type.ToString(),
                intensity = light.intensity,
                kelvin = light.colorTemperature,
                range = light.range,
                bake = light.lightmapBakeType.ToString()
            };
        }

        static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        static void AddText(GameObject go, UiTypeSet ui, string content)
        {
            // TextMeshPro when the project has it, uGUI Text when it does not. Choosing the one
            // that is present beats hard-coding either and failing in half of all projects.
            if (ui.TmpText != null)
            {
                var tmp = go.AddComponent(ui.TmpText);
                Set(tmp, "text", content);
                Set(tmp, "fontSize", 36f);
                Set(tmp, "alignment", 514);   // TextAlignmentOptions.Center
                return;
            }

            var text = go.AddComponent(ui.Text);
            Set(text, "text", content);
            Set(text, "fontSize", 28);
            Set(text, "alignment", TextAnchor.MiddleCenter);
            Set(text, "color", Color.white);
        }

        static void Set(Component component, string member, object value)
        {
            if (component == null) return;
            var type = component.GetType();
            var prop = type.GetProperty(member);
            if (prop != null && prop.CanWrite)
            {
                try
                {
                    var converted = prop.PropertyType.IsEnum && value is int
                        ? Enum.ToObject(prop.PropertyType, value)
                        : value;
                    prop.SetValue(component, converted, null);
                    return;
                }
                catch { }
            }
            var field = type.GetField(member);
            if (field == null) return;
            try { field.SetValue(component, value); } catch { }
        }

        sealed class UiTypeSet
        {
            public Type CanvasScaler, GraphicRaycaster, Image, Button, Text, EventSystem, InputModule, TmpText;
        }

        static UiTypeSet UiTypes()
        {
            var set = new UiTypeSet
            {
                CanvasScaler = Find("UnityEngine.UI.CanvasScaler"),
                GraphicRaycaster = Find("UnityEngine.UI.GraphicRaycaster"),
                Image = Find("UnityEngine.UI.Image"),
                Button = Find("UnityEngine.UI.Button"),
                Text = Find("UnityEngine.UI.Text"),
                EventSystem = Find("UnityEngine.EventSystems.EventSystem"),
                InputModule = Find("UnityEngine.InputSystem.UI.InputSystemUIInputModule")
                              ?? Find("UnityEngine.EventSystems.StandaloneInputModule"),
                TmpText = Find("TMPro.TextMeshProUGUI")
            };
            return set.CanvasScaler == null || set.Image == null ? null : set;
        }

        static Type Find(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = assembly.GetType(fullName, false); }
                catch { continue; }
                if (type != null) return type;
            }
            return null;
        }
    }
}
