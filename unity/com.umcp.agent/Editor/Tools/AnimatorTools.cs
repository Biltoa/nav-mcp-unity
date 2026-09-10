using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// Animator controllers, as one tool with an <c>action</c> discriminator.
    ///
    /// This is section 8.1's consolidation rule applied where it bites hardest. A state machine is
    /// one API with half a dozen verbs over the same three nouns — controller, layer, state — and
    /// the surface being replaced spends six separate tools and six separate schemas on it. One
    /// tool, one schema, one place to explain that a transition needs a condition parameter that
    /// already exists.
    /// </summary>
    internal static class AnimatorTools
    {
        [UnityTool(Skill = "animation", Id = "animator.controller",
            Summary = "Animator controllers: read, create, add states, parameters and transitions. action: info | create | addState | addParameter | addTransition | setDefault.",
            Mutating = true, Retry = RetryClass.Write,
            NoUndoReason = "AnimatorController edits are asset writes; Unity's animation editors do not register them with Undo.")]
        [Example("{ \"action\": \"info\", \"path\": \"Assets/Animation/Player.controller\" }")]
        [Example("{ \"action\": \"addState\", \"path\": \"Assets/Animation/Player.controller\", \"state\": \"Run\", \"clip\": \"Assets/Animation/Run.anim\" }")]
        [Example("{ \"action\": \"addTransition\", \"path\": \"Assets/Animation/Player.controller\", \"from\": \"Idle\", \"to\": \"Run\", \"parameter\": \"Speed\", \"greaterThan\": 0.1 }")]
        public static object Controller(
            [Doc("info | create | addState | addParameter | addTransition | setDefault")] string action,
            [Doc("Controller asset path, e.g. Assets/Animation/Player.controller")] string path,
            [Doc("State name, for addState / setDefault")] string state = null,
            [Doc("Clip asset path, for addState")] string clip = null,
            [Doc("Layer name. Defaults to the first layer.")] string layer = null,
            [Doc("Parameter name, for addParameter and addTransition conditions")] string parameter = null,
            [Doc("Parameter type: Float, Int, Bool, Trigger")] string parameterType = "Float",
            [Doc("Source state, for addTransition")] string from = null,
            [Doc("Destination state, for addTransition")] string to = null,
            [Doc("Condition: parameter greater than this (Float/Int)")] float? greaterThan = null,
            [Doc("Condition: parameter less than this (Float/Int)")] float? lessThan = null,
            [Doc("Condition: Bool parameter equals this, or Trigger when true")] bool? equals = null,
            [Doc("Transition duration in seconds (default 0.25)")] float duration = 0.25f)
        {
            var verb = Actions.Require(action,
                "info", "create", "addState", "addParameter", "addTransition", "setDefault");
            var assetPath = Resolve.AssetPath(path, "path");

            if (verb == "create")
            {
                if (AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath) != null)
                    throw new UmcpToolException("E_ALREADY_EXISTS", "A controller already exists at '" + assetPath + "'.",
                        "path", assetPath, null, "Use action:\"info\" to read it, or choose another path.");
                var folder = System.IO.Path.GetDirectoryName(assetPath).Replace('\\', '/');
                if (!AssetDatabase.IsValidFolder(folder)) AssetTools.CreateFolder(folder);
                var made = AnimatorController.CreateAnimatorControllerAtPath(assetPath);
                return new { created = assetPath, layers = made.layers.Length, defaultState = made.layers[0].stateMachine.defaultState == null ? null : made.layers[0].stateMachine.defaultState.name };
            }

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
            if (controller == null)
                throw new UmcpToolException("E_ASSET_NOT_FOUND", "No AnimatorController at '" + assetPath + "'.",
                    "path", assetPath,
                    AssetDatabase.FindAssets("t:AnimatorController").Take(5)
                        .Select(AssetDatabase.GUIDToAssetPath).ToArray(),
                    "action:\"create\" makes one.");

            var machine = MachineFor(controller, layer);

            switch (verb)
            {
                case "info":
                    return new
                    {
                        path = assetPath,
                        parameters = controller.parameters.Select(p => new { name = p.name, type = p.type.ToString() }).ToArray(),
                        layers = controller.layers.Select(l => new
                        {
                            name = l.name,
                            weight = l.defaultWeight,
                            defaultState = l.stateMachine.defaultState == null ? null : l.stateMachine.defaultState.name,
                            states = l.stateMachine.states.Select(s => new
                            {
                                name = s.state.name,
                                clip = s.state.motion == null ? null : AssetDatabase.GetAssetPath(s.state.motion),
                                speed = s.state.speed,
                                transitions = s.state.transitions.Select(t => new
                                {
                                    to = t.destinationState == null ? "<exit>" : t.destinationState.name,
                                    hasExitTime = t.hasExitTime,
                                    duration = t.duration,
                                    conditions = t.conditions.Select(c => c.parameter + " " + c.mode + " " + c.threshold).ToArray()
                                }).ToArray()
                            }).ToArray()
                        }).ToArray()
                    };

                case "addstate":
                    {
                        RequireName(state, "state");
                        if (machine.states.Any(s => s.state.name == state))
                            throw new UmcpToolException("E_ALREADY_EXISTS", "State '" + state + "' already exists on this layer.",
                                "state", state, machine.states.Select(s => s.state.name).ToArray(), null);

                        var added = machine.AddState(state);
                        if (!string.IsNullOrEmpty(clip))
                        {
                            var motion = AssetDatabase.LoadAssetAtPath<Motion>(Resolve.AssetPath(clip, "clip"));
                            if (motion == null)
                                throw new UmcpToolException("E_ASSET_NOT_FOUND", "No animation clip at '" + clip + "'.",
                                    "clip", clip,
                                    AssetDatabase.FindAssets("t:AnimationClip").Take(5).Select(AssetDatabase.GUIDToAssetPath).ToArray(), null);
                            added.motion = motion;
                        }
                        Save(controller);
                        return new { added = added.name, clip, isDefault = machine.defaultState == added, states = machine.states.Length };
                    }

                case "addparameter":
                    {
                        RequireName(parameter, "parameter");
                        if (controller.parameters.Any(p => p.name == parameter))
                            return new { added = false, note = "Parameter '" + parameter + "' already exists.", parameters = controller.parameters.Length };

                        AnimatorControllerParameterType type;
                        try { type = (AnimatorControllerParameterType)Enum.Parse(typeof(AnimatorControllerParameterType), parameterType, true); }
                        catch
                        {
                            throw new UmcpToolException("E_ARG_VALUE", "Unknown parameter type '" + parameterType + "'.",
                                "parameterType", parameterType, new[] { "Float", "Int", "Bool", "Trigger" }, null);
                        }

                        controller.AddParameter(parameter, type);
                        Save(controller);
                        return new { added = parameter, type = type.ToString(), parameters = controller.parameters.Length };
                    }

                case "addtransition":
                    {
                        RequireName(from, "from");
                        RequireName(to, "to");
                        var source = FindState(machine, from, "from");
                        var destination = FindState(machine, to, "to");

                        var transition = source.AddTransition(destination);
                        transition.duration = Mathf.Max(0f, duration);
                        transition.hasExitTime = greaterThan == null && lessThan == null && equals == null;

                        if (parameter != null)
                        {
                            var declared = controller.parameters.FirstOrDefault(p => p.name == parameter);
                            if (declared == null)
                                throw new UmcpToolException("E_PARAMETER_NOT_FOUND",
                                    "The controller has no parameter '" + parameter + "'.",
                                    "parameter", parameter,
                                    controller.parameters.Select(p => p.name).ToArray(),
                                    "Add it first with action:\"addParameter\".");

                            if (greaterThan != null) transition.AddCondition(AnimatorConditionMode.Greater, greaterThan.Value, parameter);
                            if (lessThan != null) transition.AddCondition(AnimatorConditionMode.Less, lessThan.Value, parameter);
                            if (equals != null)
                                transition.AddCondition(
                                    declared.type == AnimatorControllerParameterType.Trigger
                                        ? AnimatorConditionMode.If
                                        : (equals.Value ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot),
                                    0f, parameter);
                        }

                        Save(controller);
                        return new
                        {
                            added = source.name + " -> " + destination.name,
                            conditions = transition.conditions.Select(c => c.parameter + " " + c.mode + " " + c.threshold).ToArray(),
                            hasExitTime = transition.hasExitTime,
                            duration = transition.duration,
                            _hint = transition.conditions.Length == 0
                                ? "No conditions, so this transition fires on exit time alone."
                                : null
                        };
                    }

                case "setdefault":
                    {
                        RequireName(state, "state");
                        machine.defaultState = FindState(machine, state, "state");
                        Save(controller);
                        return new { defaultState = machine.defaultState.name };
                    }

                default:
                    throw new UmcpToolException("E_ARG_VALUE", "Unknown action '" + action + "'.",
                        "action", action,
                        new[] { "info", "create", "addState", "addParameter", "addTransition", "setDefault" }, null);
            }
        }

        static AnimatorStateMachine MachineFor(AnimatorController controller, string layer)
        {
            if (controller.layers.Length == 0)
                throw new UmcpToolException("E_NO_LAYERS", "This controller has no layers.", "layer", layer);
            if (string.IsNullOrEmpty(layer)) return controller.layers[0].stateMachine;

            var found = controller.layers.FirstOrDefault(l => l.name == layer);
            if (found == null)
                throw new UmcpToolException("E_LAYER_NOT_FOUND", "No layer named '" + layer + "'.",
                    "layer", layer, controller.layers.Select(l => l.name).ToArray(), null);
            return found.stateMachine;
        }

        static AnimatorState FindState(AnimatorStateMachine machine, string name, string param)
        {
            var found = machine.states.FirstOrDefault(s => s.state.name == name);
            if (found.state == null)
                throw new UmcpToolException("E_STATE_NOT_FOUND", "No state named '" + name + "' on this layer.",
                    param, name, machine.states.Select(s => s.state.name).ToArray(),
                    "Add it with action:\"addState\".");
            return found.state;
        }

        static void RequireName(string value, string param)
        {
            if (string.IsNullOrEmpty(value))
                throw new UmcpToolException("E_ARG_REQUIRED", "'" + param + "' is required for this action.", param);
        }

        static void Save(AnimatorController controller)
        {
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssetIfDirty(controller);
        }
    }
}
