using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// Particle systems and VFX Graph.
    ///
    /// The surface being replaced spends **41 tools** on VFX Graph alone — `vfx_add_block`,
    /// `vfx_add_context`, `vfx_add_operator`, `vfx_connect_slots`, and so on — which is a node
    /// editor rebuilt one schema at a time. That is the wrong shape twice over: it is enormous, and
    /// a model cannot hold a graph's structure through a keyhole of single-node calls anyway.
    ///
    /// The position taken here: **read the graph, and set its exposed parameters.** Exposed
    /// parameters are the interface a VFX artist deliberately publishes, and they are what a scene
    /// actually needs changed. Authoring the graph itself belongs to a human in the graph editor,
    /// or to `unity_script` for the rare programmatic case — not to forty wrappers.
    /// </summary>
    internal static class EffectTools
    {
        // ---------------------------------------------------------------- particles

        [UnityTool(Skill = "effects", Id = "particles.info",
            Summary = "Read a ParticleSystem: emission, shape, lifetime, speed, size, colour, and which modules are on.",
            Retry = RetryClass.Read)]
        [Example("{ \"target\": \"Fire\" }")]
        public static object ParticlesInfo([Doc("GameObject with a ParticleSystem: path, name or #id")] string target)
        {
            var system = Require(target);
            var main = system.main;
            var emission = system.emission;
            var shape = system.shape;

            return new
            {
                path = Resolve.Path(system.transform),
                playing = system.isPlaying,
                duration = main.duration,
                looping = main.loop,
                prewarm = main.prewarm,
                startLifetime = Describe(main.startLifetime),
                startSpeed = Describe(main.startSpeed),
                startSize = Describe(main.startSize),
                startColor = Vec.Arr(main.startColor.color),
                gravityModifier = Describe(main.gravityModifier),
                maxParticles = main.maxParticles,
                simulationSpace = main.simulationSpace.ToString(),
                emission = new
                {
                    enabled = emission.enabled,
                    rateOverTime = Describe(emission.rateOverTime),
                    rateOverDistance = Describe(emission.rateOverDistance),
                    bursts = emission.burstCount
                },
                shape = new { enabled = shape.enabled, type = shape.shapeType.ToString(), radius = shape.radius, angle = shape.angle },
                modules = new
                {
                    velocityOverLifetime = system.velocityOverLifetime.enabled,
                    colorOverLifetime = system.colorOverLifetime.enabled,
                    sizeOverLifetime = system.sizeOverLifetime.enabled,
                    rotationOverLifetime = system.rotationOverLifetime.enabled,
                    noise = system.noise.enabled,
                    collision = system.collision.enabled,
                    trails = system.trails.enabled,
                    subEmitters = system.subEmitters.enabled
                },
                renderer = RendererInfo(system)
            };
        }

        [UnityTool(Skill = "effects", Id = "particles.set",
            Summary = "Set the common ParticleSystem properties: rate, lifetime, speed, size, colour, looping, max particles.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set particle properties")]
        [Example("{ \"target\": \"Fire\", \"rateOverTime\": 40, \"startLifetime\": 2.5 }")]
        public static object ParticlesSet(
            [Doc("GameObject with a ParticleSystem")] string target,
            [Doc("Emission rate per second")] float? rateOverTime = null,
            [Doc("Particle lifetime in seconds")] float? startLifetime = null,
            [Doc("Initial speed")] float? startSpeed = null,
            [Doc("Initial size")] float? startSize = null,
            [Doc("Start colour [r, g, b, a], 0-1")] float[] startColor = null,
            [Doc("Gravity modifier")] float? gravityModifier = null,
            [Doc("Loop the system")] bool? looping = null,
            [Doc("Hard cap on live particles")] int? maxParticles = null)
        {
            var system = Require(target);
            Undo.RecordObject(system, "Set particle properties");

            var main = system.main;
            var emission = system.emission;
            var changed = new System.Collections.Generic.List<string>();

            if (rateOverTime != null) { emission.rateOverTime = rateOverTime.Value; changed.Add("rateOverTime"); }
            if (startLifetime != null) { main.startLifetime = startLifetime.Value; changed.Add("startLifetime"); }
            if (startSpeed != null) { main.startSpeed = startSpeed.Value; changed.Add("startSpeed"); }
            if (startSize != null) { main.startSize = startSize.Value; changed.Add("startSize"); }
            if (gravityModifier != null) { main.gravityModifier = gravityModifier.Value; changed.Add("gravityModifier"); }
            if (looping != null) { main.loop = looping.Value; changed.Add("looping"); }
            if (maxParticles != null) { main.maxParticles = Mathf.Max(1, maxParticles.Value); changed.Add("maxParticles"); }
            if (startColor != null)
            {
                if (startColor.Length != 4)
                    throw new UmcpToolException("E_ARG_SHAPE", "'startColor' needs 4 numbers: [r, g, b, a].",
                        "startColor", "[" + startColor.Length + " values]", null, "For example: [1, 0.5, 0, 1]");
                main.startColor = new Color(startColor[0], startColor[1], startColor[2], startColor[3]);
                changed.Add("startColor");
            }

            EditorUtility.SetDirty(system);

            if (changed.Count == 0)
                return new { changed = new string[0], _hint = "Nothing was passed to change. particles.info reads the current values." };

            return new
            {
                path = Resolve.Path(system.transform),
                changed = changed.ToArray(),
                _hint = "Curves and gradients are not settable here — only constants. Use unity_script for a curve."
            };
        }

        static ParticleSystem Require(string target)
        {
            var go = Resolve.GameObject(target, "target");
            var system = go.GetComponent<ParticleSystem>();
            if (system == null)
                throw new UmcpToolException("E_COMPONENT_NOT_FOUND",
                    "'" + go.name + "' has no ParticleSystem.", "target", target,
                    go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).Take(6).ToArray(),
                    "scene.query with [has:ParticleSystem] lists the systems in the scene.");
            return system;
        }

        static object Describe(ParticleSystem.MinMaxCurve curve)
        {
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant: return curve.constant;
                case ParticleSystemCurveMode.TwoConstants: return new { min = curve.constantMin, max = curve.constantMax };
                default: return curve.mode.ToString();   // a curve; a single number would be a lie
            }
        }

        static object RendererInfo(ParticleSystem system)
        {
            var renderer = system.GetComponent<ParticleSystemRenderer>();
            if (renderer == null) return null;
            return new
            {
                mode = renderer.renderMode.ToString(),
                material = renderer.sharedMaterial == null ? null : renderer.sharedMaterial.name,
                shader = renderer.sharedMaterial == null || renderer.sharedMaterial.shader == null
                    ? null : renderer.sharedMaterial.shader.name,
                sortingLayer = renderer.sortingLayerName,
                sortingOrder = renderer.sortingOrder
            };
        }

        // ---------------------------------------------------------------- vfx graph

        [UnityTool(Skill = "effects", Id = "vfx.info",
            Summary = "Read a VisualEffect component: its asset, its exposed parameters and their current values.",
            Retry = RetryClass.Read)]
        [Example("{ \"target\": \"Sparks\" }")]
        public static object VfxInfo([Doc("GameObject with a VisualEffect component. Omit to list them all.")] string target = null)
        {
            var type = VisualEffectType();
            if (type == null)
                throw new UmcpToolException("E_PACKAGE_MISSING",
                    "The Visual Effect Graph package is not installed in this project.", "target", target, null,
                    "Install com.unity.visualeffectgraph, or use particles.* for the built-in system.");

            if (string.IsNullOrEmpty(target))
            {
                var all = Resolve.AllGameObjects()
                    .SelectMany(go => go.GetComponents<Component>())
                    .Where(c => c != null && type.IsInstanceOfType(c))
                    .Select(c => new { path = Resolve.Path(c.transform), asset = AssetName(c) })
                    .Take(50).ToArray();
                return new { count = all.Length, effects = all };
            }

            var component = ComponentOn(target, type);
            return new
            {
                path = Resolve.Path(component.transform),
                asset = AssetName(component),
                parameters = ExposedParameters(component),
                _hint = "vfx.set changes an exposed parameter. Graph structure is edited in the VFX Graph window."
            };
        }

        [UnityTool(Skill = "effects", Id = "vfx.set",
            Summary = "Set one exposed parameter on a VisualEffect: float, int, bool, Vector2/3/4 or colour.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set VFX parameter")]
        [Example("{ \"target\": \"Sparks\", \"parameter\": \"Rate\", \"value\": 250 }")]
        public static object VfxSet(
            [Doc("GameObject with a VisualEffect component")] string target,
            [Doc("Exposed parameter name, exactly as the graph publishes it")] string parameter,
            [Doc("Numeric value, for float, int or bool parameters")] float? value = null,
            [Doc("Vector or colour value, 2-4 numbers")] float[] vector = null)
        {
            var type = VisualEffectType();
            if (type == null)
                throw new UmcpToolException("E_PACKAGE_MISSING",
                    "The Visual Effect Graph package is not installed in this project.", "target", target);

            var component = ComponentOn(target, type);
            if (string.IsNullOrEmpty(parameter))
                throw new UmcpToolException("E_ARG_REQUIRED", "A parameter name is required.", "parameter");

            Undo.RecordObject(component, "Set VFX parameter");

            // Exposed parameters are typed, and the graph is the authority on which type each one
            // is. Ask it, rather than guessing from the shape of the value that was passed.
            if (Invoke<bool>(component, "HasFloat", parameter) && value != null)
                Invoke<object>(component, "SetFloat", parameter, value.Value);
            else if (Invoke<bool>(component, "HasInt", parameter) && value != null)
                Invoke<object>(component, "SetInt", parameter, Mathf.RoundToInt(value.Value));
            else if (Invoke<bool>(component, "HasBool", parameter) && value != null)
                Invoke<object>(component, "SetBool", parameter, value.Value != 0f);
            else if (Invoke<bool>(component, "HasVector3", parameter) && vector != null && vector.Length >= 3)
                Invoke<object>(component, "SetVector3", parameter, new Vector3(vector[0], vector[1], vector[2]));
            else if (Invoke<bool>(component, "HasVector2", parameter) && vector != null && vector.Length >= 2)
                Invoke<object>(component, "SetVector2", parameter, new Vector2(vector[0], vector[1]));
            else if (Invoke<bool>(component, "HasVector4", parameter) && vector != null && vector.Length >= 4)
                Invoke<object>(component, "SetVector4", parameter, new Vector4(vector[0], vector[1], vector[2], vector[3]));
            else
                throw new UmcpToolException("E_PARAMETER_NOT_FOUND",
                    "'" + parameter + "' is not an exposed parameter of this effect, or the value's shape does not match its type.",
                    "parameter", parameter,
                    ExposedParameters(component).Select(p => p.ToString()).Take(8).ToArray(),
                    "vfx.info lists the exposed parameters and their types.");

            EditorUtility.SetDirty(component);
            return new { path = Resolve.Path(component.transform), parameter, set = true };
        }

        static object[] ExposedParameters(Component component)
        {
            // VisualEffectAsset publishes its exposed properties through the component's
            // VFXExposedProperty enumeration; the API differs between package versions, so this
            // reads what is there and reports nothing rather than inventing a shape.
            var list = new System.Collections.Generic.List<object>();
            var method = component.GetType().GetMethod("GetExposedProperties", BindingFlags.Public | BindingFlags.Instance);
            if (method == null) return list.ToArray();

            var parameterType = method.GetParameters().FirstOrDefault();
            if (parameterType == null) return list.ToArray();

            try
            {
                var itemType = parameterType.ParameterType.GetGenericArguments().FirstOrDefault();
                if (itemType == null) return list.ToArray();
                var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(itemType);
                var target = Activator.CreateInstance(listType);
                method.Invoke(component, new[] { target });

                foreach (var item in (System.Collections.IEnumerable)target)
                {
                    var nameField = itemType.GetField("name");
                    var typeField = itemType.GetField("type");
                    list.Add(new
                    {
                        name = nameField == null ? null : nameField.GetValue(item) as string,
                        type = typeField == null ? null : Convert.ToString(typeField.GetValue(item))
                    });
                }
            }
            catch { /* a package version we do not know: report nothing, not a guess */ }

            return list.ToArray();
        }

        static Component ComponentOn(string target, Type type)
        {
            var go = Resolve.GameObject(target, "target");
            var component = go.GetComponents<Component>().FirstOrDefault(c => c != null && type.IsInstanceOfType(c));
            if (component == null)
                throw new UmcpToolException("E_COMPONENT_NOT_FOUND",
                    "'" + go.name + "' has no VisualEffect component.", "target", target, null,
                    "vfx.info with no target lists every VisualEffect in the open scenes.");
            return component;
        }

        static string AssetName(Component component)
        {
            var prop = component.GetType().GetProperty("visualEffectAsset");
            var asset = prop == null ? null : prop.GetValue(component, null) as UnityEngine.Object;
            return asset == null ? null : AssetDatabase.GetAssetPath(asset);
        }

        static T Invoke<T>(Component component, string method, params object[] args)
        {
            var found = component.GetType().GetMethod(method, args.Select(a => a.GetType()).ToArray());
            if (found == null) return default(T);
            try { return (T)found.Invoke(component, args); }
            catch { return default(T); }
        }

        static Type VisualEffectType()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = assembly.GetType("UnityEngine.VFX.VisualEffect", false); }
                catch { continue; }
                if (type != null) return type;
            }
            return null;
        }
    }
}
