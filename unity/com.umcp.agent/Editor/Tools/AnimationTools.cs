using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// Animation clips, blend trees and layers — the three things `animator.controller` could not
    /// do, each as one tool with an <c>action</c> discriminator.
    ///
    /// Before these, "add a walk-to-run blend" fell through to code mode: correct, but it costs a
    /// compile and returns a paragraph where a tool returns a fact. The split between the three is
    /// the asset boundary, not a taxonomy — a clip is a file, a blend tree lives inside a
    /// controller, and a layer is a property of one.
    /// </summary>
    internal static class AnimationTools
    {
        // ---------------------------------------------------------------- clips

        [UnityTool(Skill = "animation", Id = "animation.clip",
            Summary = "Animation clips: read, create, set curves and events, sample a value. action: info | create | setCurve | addEvent | sample.",
            Mutating = true, Retry = RetryClass.Write,
            NoUndoReason = "Clip edits are asset writes; Unity's animation window does not register them with Undo either.")]
        [Example("{ \"action\": \"info\", \"path\": \"Assets/Animation/Run.anim\" }")]
        [Example("{ \"action\": \"create\", \"path\": \"Assets/Animation/Bob.anim\", \"loop\": true }")]
        [Example("{ \"action\": \"setCurve\", \"path\": \"Assets/Animation/Bob.anim\", \"property\": \"m_LocalPosition.y\", \"times\": [0, 0.5, 1], \"values\": [0, 0.4, 0] }")]
        public static object Clip(
            [Doc("info | create | setCurve | addEvent | sample")] string action,
            [Doc("Clip asset path, e.g. Assets/Animation/Run.anim")] string path,
            [Doc("Relative transform path inside the animated hierarchy. Empty means the animated object itself.")] string target = "",
            [Doc("Animated property, e.g. m_LocalPosition.y, m_LocalScale.x, material._Color.r")] string property = null,
            [Doc("Component type the property belongs to (default Transform)")] string component = "Transform",
            [Doc("Keyframe times in seconds")] float[] times = null,
            [Doc("Keyframe values, one per time")] float[] values = null,
            [Doc("Loop the clip (create)")] bool loop = false,
            [Doc("Frame rate (create, default 60)")] float frameRate = 60f,
            [Doc("Function name, for addEvent")] string function = null,
            [Doc("Event or sample time in seconds")] float time = 0f,
            [Doc("String argument for the event")] string stringArgument = null)
        {
            var verb = Actions.Require(action, "info", "create", "setCurve", "addEvent", "sample");
            var assetPath = Resolve.AssetPath(path, "path");

            if (verb == "create")
            {
                if (AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath) != null)
                    throw new UmcpToolException("E_ALREADY_EXISTS", "A clip already exists at '" + assetPath + "'.",
                        "path", assetPath, null, "Use action:\"info\" to read it, or choose another path.");

                var folder = System.IO.Path.GetDirectoryName(assetPath).Replace('\\', '/');
                if (!AssetDatabase.IsValidFolder(folder)) AssetTools.CreateFolder(folder);

                var made = new AnimationClip { frameRate = frameRate };
                if (loop)
                {
                    // Looping lives in the clip's *import* settings, not on the clip object, and
                    // setting `wrapMode` alone does nothing for an Animator — the state loops only
                    // when loopTime is set in the serialized settings. This is the single most
                    // common "my animation does not loop" cause.
                    var settings = AnimationUtility.GetAnimationClipSettings(made);
                    settings.loopTime = true;
                    AnimationUtility.SetAnimationClipSettings(made, settings);
                }
                AssetDatabase.CreateAsset(made, assetPath);
                AssetDatabase.SaveAssets();
                return new { created = assetPath, frameRate, loop };
            }

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
            if (clip == null)
                throw new UmcpToolException("E_ASSET_NOT_FOUND", "No AnimationClip at '" + assetPath + "'.",
                    "path", assetPath,
                    AssetDatabase.FindAssets("t:AnimationClip").Take(5)
                        .Select(AssetDatabase.GUIDToAssetPath).ToArray(),
                    "assets.find with filter \"t:AnimationClip\" lists what exists.");

            switch (verb)
            {
                case "info":
                {
                    var bindings = AnimationUtility.GetCurveBindings(clip);
                    var settings = AnimationUtility.GetAnimationClipSettings(clip);
                    var curves = bindings.Take(Bounds.Limit(40)).Select(b => new
                    {
                        target = b.path,
                        type = b.type != null ? b.type.Name : null,
                        property = b.propertyName,
                        keys = AnimationUtility.GetEditorCurve(clip, b) is AnimationCurve c ? c.length : 0
                    }).ToArray();
                    return new
                    {
                        path = assetPath,
                        length = clip.length,
                        frameRate = clip.frameRate,
                        loop = settings.loopTime,
                        legacy = clip.legacy,
                        empty = clip.empty,
                        curves,
                        curveCount = bindings.Length,
                        curvesReturned = curves.Length,
                        _truncated = bindings.Length > curves.Length,
                        events = clip.events.Select(e => new { e.functionName, e.time, e.stringParameter }).ToArray()
                    };
                }

                case "setcurve":
                {
                    if (string.IsNullOrEmpty(property))
                        throw new UmcpToolException("E_BAD_ARG", "setCurve needs a property.", "property", null, null,
                            "For example m_LocalPosition.y on a Transform.");
                    if (times == null || values == null || times.Length == 0 || times.Length != values.Length)
                        throw new UmcpToolException("E_BAD_ARG",
                            "times and values must be the same non-zero length; got " +
                            (times == null ? 0 : times.Length) + " and " + (values == null ? 0 : values.Length) + ".",
                            "times", null, null, "One value per time.");

                    var type = Resolve.ComponentType(component ?? "Transform", "component");
                    var curve = new AnimationCurve();
                    for (int i = 0; i < times.Length; i++) curve.AddKey(times[i], values[i]);
                    // Smooth by default: linear tangents on a hand-made curve look like a bug to
                    // anyone who opens the clip afterwards.
                    for (int i = 0; i < curve.length; i++) curve.SmoothTangents(i, 0f);

                    clip.SetCurve(target ?? "", type, property, curve);
                    EditorUtility.SetDirty(clip);
                    AssetDatabase.SaveAssets();

                    return new { path = assetPath, target = target ?? "", property, keys = curve.length, length = clip.length };
                }

                case "addevent":
                {
                    if (string.IsNullOrEmpty(function))
                        throw new UmcpToolException("E_BAD_ARG", "addEvent needs a function name.", "function", null, null,
                            "The method must exist on a component of the animated GameObject, or the event does nothing at runtime.");

                    var list = clip.events.ToList();
                    list.Add(new AnimationEvent { functionName = function, time = time, stringParameter = stringArgument ?? "" });
                    AnimationUtility.SetAnimationEvents(clip, list.ToArray());
                    EditorUtility.SetDirty(clip);
                    AssetDatabase.SaveAssets();

                    return new { path = assetPath, added = function, time, events = list.Count };
                }

                case "sample":
                {
                    // Reading a curve's value at a time, without Play mode: the only way to check
                    // "does this animation actually reach 1.0" from a tool call.
                    var results = new List<object>();
                    foreach (var b in AnimationUtility.GetCurveBindings(clip))
                    {
                        if (!string.IsNullOrEmpty(property) && b.propertyName != property) continue;
                        var c = AnimationUtility.GetEditorCurve(clip, b);
                        if (c == null) continue;
                        results.Add(new { target = b.path, property = b.propertyName, value = c.Evaluate(time) });
                        if (results.Count >= Bounds.Limit(40)) break;
                    }
                    return new { path = assetPath, time, samples = results.ToArray() };
                }

                default:
                    throw new UmcpToolException("E_BAD_ARG", "Unknown action '" + action + "'.",
                        "action", action, new[] { "info", "create", "setCurve", "addEvent", "sample" }, null);
            }
        }

        // ---------------------------------------------------------------- blend trees

        [UnityTool(Skill = "animation", Id = "animation.blendTree",
            Summary = "Blend trees inside an Animator controller: create one as a state, add motions, read it. action: info | create | addMotion.",
            Mutating = true, Retry = RetryClass.Write,
            NoUndoReason = "Controller edits are asset writes; Unity's Animator window does not register them with Undo.")]
        [Example("{ \"action\": \"create\", \"path\": \"Assets/Animation/Player.controller\", \"state\": \"Locomotion\", \"parameter\": \"Speed\" }")]
        [Example("{ \"action\": \"addMotion\", \"path\": \"Assets/Animation/Player.controller\", \"state\": \"Locomotion\", \"clip\": \"Assets/Animation/Run.anim\", \"threshold\": 1 }")]
        public static object BlendTree(
            [Doc("info | create | addMotion")] string action,
            [Doc("Controller asset path")] string path,
            [Doc("State name that holds the blend tree")] string state,
            [Doc("Blend parameter name (create). Must already exist on the controller.")] string parameter = null,
            [Doc("Second blend parameter, for 2D trees")] string parameterY = null,
            [Doc("1D | 2DSimpleDirectional | 2DFreeformDirectional | 2DFreeformCartesian")] string blendType = "1D",
            [Doc("Layer name. Defaults to the first layer.")] string layer = null,
            [Doc("Clip asset path, for addMotion")] string clip = null,
            [Doc("Threshold for a 1D tree")] float threshold = 0f,
            [Doc("Position for a 2D tree, [x, y]")] float[] position = null)
        {
            var verb = Actions.Require(action, "info", "create", "addMotion");
            var assetPath = Resolve.AssetPath(path, "path");
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
            if (controller == null)
                throw new UmcpToolException("E_ASSET_NOT_FOUND", "No AnimatorController at '" + assetPath + "'.",
                    "path", assetPath, null, "animator.controller with action:\"create\" makes one.");

            var machine = LayerMachine(controller, layer);

            if (verb == "create")
            {
                if (string.IsNullOrEmpty(parameter))
                    throw new UmcpToolException("E_BAD_ARG", "A blend tree needs a blend parameter.", "parameter", null,
                        controller.parameters.Select(p => p.name).ToArray(),
                        "Add it first with animator.controller action:\"addParameter\".");

                // Refusing an undeclared parameter rather than creating one: a blend tree bound to
                // a parameter that nothing sets looks like it works and never blends.
                if (!controller.parameters.Any(p => p.name == parameter))
                    throw new UmcpToolException("E_PARAM_NOT_FOUND",
                        "The controller has no parameter '" + parameter + "'.",
                        "parameter", parameter, controller.parameters.Select(p => p.name).ToArray(),
                        "animator.controller action:\"addParameter\" declares it.");

                if (machine.states.Any(s => s.state.name == state))
                    throw new UmcpToolException("E_ALREADY_EXISTS", "The layer already has a state called '" + state + "'.",
                        "state", state, null, "Use action:\"addMotion\" to add clips to it.");

                var tree = new BlendTree
                {
                    name = state,
                    blendParameter = parameter,
                    blendType = ParseBlendType(blendType),
                    useAutomaticThresholds = false
                };
                if (!string.IsNullOrEmpty(parameterY)) tree.blendParameterY = parameterY;

                // The tree has to be an asset inside the controller, or it is lost on reload.
                AssetDatabase.AddObjectToAsset(tree, controller);
                var added = machine.AddState(state);
                added.motion = tree;

                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
                return new { created = state, parameter, blendType = tree.blendType.ToString(), path = assetPath };
            }

            var holder = machine.states.FirstOrDefault(s => s.state.name == state).state;
            if (holder == null)
                throw new UmcpToolException("E_STATE_NOT_FOUND", "No state called '" + state + "' in this layer.",
                    "state", state, machine.states.Select(s => s.state.name).ToArray(), null);

            var blend = holder.motion as BlendTree;
            if (blend == null)
                throw new UmcpToolException("E_NOT_A_BLEND_TREE", "'" + state + "' holds a clip, not a blend tree.",
                    "state", state, null, "Create the tree as its own state with action:\"create\".");

            if (verb == "info")
                return new
                {
                    state,
                    parameter = blend.blendParameter,
                    parameterY = blend.blendParameterY,
                    blendType = blend.blendType.ToString(),
                    motions = blend.children.Select(c => new
                    {
                        motion = c.motion != null ? AssetDatabase.GetAssetPath(c.motion) : null,
                        threshold = c.threshold,
                        position = new[] { c.position.x, c.position.y }
                    }).ToArray()
                };

            if (verb == "addmotion")
            {
                var motion = AssetDatabase.LoadAssetAtPath<AnimationClip>(Resolve.AssetPath(clip, "clip"));
                if (motion == null)
                    throw new UmcpToolException("E_ASSET_NOT_FOUND", "No AnimationClip at '" + clip + "'.",
                        "clip", clip, null, "animation.clip action:\"create\" makes an empty one.");

                var children = blend.children.ToList();
                var child = new ChildMotion { motion = motion, timeScale = 1f, threshold = threshold };
                if (position != null && position.Length >= 2) child.position = new Vector2(position[0], position[1]);
                children.Add(child);
                // Ordered by threshold: Unity blends between *adjacent* children, so an unsorted
                // 1D tree blends between the wrong pair and looks like the clips are wrong.
                // Automatic thresholds silently replace every caller-supplied value with a
                // normalised 0..1 sequence when the children array is assigned.
                if (blend.blendType == BlendTreeType.Simple1D)
                    blend.useAutomaticThresholds = false;
                blend.children = blend.blendType == BlendTreeType.Simple1D
                    ? children.OrderBy(c => c.threshold).ToArray()
                    : children.ToArray();

                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
                return new { state, added = AssetDatabase.GetAssetPath(motion), threshold, motions = blend.children.Length };
            }

            throw new UmcpToolException("E_BAD_ARG", "Unknown action '" + action + "'.",
                "action", action, new[] { "info", "create", "addMotion" }, null);
        }

        // ---------------------------------------------------------------- layers

        [UnityTool(Skill = "animation", Id = "animation.layer",
            Summary = "Animator layers and avatar masks: list, add, set weight and blending. action: list | add | setWeight | setMask.",
            Mutating = true, Retry = RetryClass.Write,
            NoUndoReason = "Controller edits are asset writes; Unity's Animator window does not register them with Undo.")]
        [Example("{ \"action\": \"list\", \"path\": \"Assets/Animation/Player.controller\" }")]
        [Example("{ \"action\": \"add\", \"path\": \"Assets/Animation/Player.controller\", \"layer\": \"UpperBody\", \"weight\": 1, \"additive\": false }")]
        public static object Layer(
            [Doc("list | add | setWeight | setMask")] string action,
            [Doc("Controller asset path")] string path,
            [Doc("Layer name")] string layer = null,
            [Doc("Layer weight 0..1")] float weight = 1f,
            [Doc("Additive blending instead of Override")] bool additive = false,
            [Doc("Avatar mask asset path, for setMask")] string mask = null)
        {
            var verb = Actions.Require(action, "list", "add", "setWeight", "setMask");
            var assetPath = Resolve.AssetPath(path, "path");
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
            if (controller == null)
                throw new UmcpToolException("E_ASSET_NOT_FOUND", "No AnimatorController at '" + assetPath + "'.",
                    "path", assetPath, null, null);

            if (verb == "list")
                return new
                {
                    path = assetPath,
                    layers = controller.layers.Select((l, i) => new
                    {
                        index = i,
                        name = l.name,
                        weight = l.defaultWeight,
                        blending = l.blendingMode.ToString(),
                        mask = l.avatarMask != null ? AssetDatabase.GetAssetPath(l.avatarMask) : null,
                        states = l.stateMachine != null ? l.stateMachine.states.Length : 0
                    }).ToArray()
                };

            if (string.IsNullOrEmpty(layer))
                throw new UmcpToolException("E_BAD_ARG", action + " needs a layer name.", "layer", null,
                    controller.layers.Select(l => l.name).ToArray(), null);

            if (verb == "add")
            {
                if (controller.layers.Any(l => l.name == layer))
                    throw new UmcpToolException("E_ALREADY_EXISTS", "The controller already has a layer called '" + layer + "'.",
                        "layer", layer, null, "Use action:\"setWeight\" to change it.");

                controller.AddLayer(layer);

                // AddLayer returns void and the array is a copy, so the new layer has to be read
                // back, edited and written whole. Editing controller.layers[i] in place is the
                // classic no-op here.
                var all = controller.layers;
                var made = all[all.Length - 1];
                made.defaultWeight = Mathf.Clamp01(weight);
                made.blendingMode = additive ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override;
                all[all.Length - 1] = made;
                controller.layers = all;

                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
                return new { added = layer, index = all.Length - 1, weight = made.defaultWeight, blending = made.blendingMode.ToString() };
            }

            var index = Array.FindIndex(controller.layers, l => l.name == layer);
            if (index < 0)
                throw new UmcpToolException("E_LAYER_NOT_FOUND", "No layer called '" + layer + "'.",
                    "layer", layer, controller.layers.Select(l => l.name).ToArray(), null);

            var layers = controller.layers;
            var entry = layers[index];

            if (verb == "setweight")
            {
                entry.defaultWeight = Mathf.Clamp01(weight);
                entry.blendingMode = additive ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override;
            }
            else if (verb == "setmask")
            {
                var maskAsset = string.IsNullOrEmpty(mask)
                    ? null
                    : AssetDatabase.LoadAssetAtPath<AvatarMask>(Resolve.AssetPath(mask, "mask"));
                if (!string.IsNullOrEmpty(mask) && maskAsset == null)
                    throw new UmcpToolException("E_ASSET_NOT_FOUND", "No AvatarMask at '" + mask + "'.",
                        "mask", mask, null, "Create one in the Project window, or pass no mask to clear it.");
                entry.avatarMask = maskAsset;
            }
            else
                throw new UmcpToolException("E_BAD_ARG", "Unknown action '" + action + "'.",
                    "action", action, new[] { "list", "add", "setWeight", "setMask" }, null);

            layers[index] = entry;
            controller.layers = layers;
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new
            {
                layer,
                index,
                weight = entry.defaultWeight,
                blending = entry.blendingMode.ToString(),
                mask = entry.avatarMask != null ? AssetDatabase.GetAssetPath(entry.avatarMask) : null
            };
        }

        // ---------------------------------------------------------------- shared

        static AnimatorStateMachine LayerMachine(AnimatorController controller, string layer)
        {
            var index = string.IsNullOrEmpty(layer)
                ? 0
                : Array.FindIndex(controller.layers, l => l.name == layer);

            if (index < 0)
                throw new UmcpToolException("E_LAYER_NOT_FOUND", "No layer called '" + layer + "'.",
                    "layer", layer, controller.layers.Select(l => l.name).ToArray(), null);

            return controller.layers[index].stateMachine;
        }

        static BlendTreeType ParseBlendType(string s)
        {
            switch ((s ?? "1D").ToLowerInvariant())
            {
                case "1d": return BlendTreeType.Simple1D;
                case "2dsimpledirectional": return BlendTreeType.SimpleDirectional2D;
                case "2dfreeformdirectional": return BlendTreeType.FreeformDirectional2D;
                case "2dfreeformcartesian": return BlendTreeType.FreeformCartesian2D;
                case "direct": return BlendTreeType.Direct;
                default:
                    throw new UmcpToolException("E_BAD_ARG", "Unknown blendType '" + s + "'.",
                        "blendType", s,
                        new[] { "1D", "2DSimpleDirectional", "2DFreeformDirectional", "2DFreeformCartesian", "Direct" }, null);
            }
        }
    }
}
