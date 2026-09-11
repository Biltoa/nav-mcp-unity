using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// Timeline and Cinemachine — both optional packages, both read by reflection.
    ///
    /// What is exposed is deliberately narrow: **read the structure, and change the one value that
    /// decides behaviour.** For Cinemachine that value is priority, which is what actually selects
    /// the live camera; for Timeline it is nothing at all, because a timeline is authored in its
    /// own window and a tool that half-edits one produces a track nobody can reason about.
    ///
    /// This is the same judgement as the VFX tools: forty wrappers around a node editor is not an
    /// API, it is a schema bill.
    /// </summary>
    internal static class CinematicTools
    {
        // ---------------------------------------------------------------- timeline

        [UnityTool(Skill = "cinematics", Id = "timeline.info",
            Summary = "Read the Timelines in the scene: directors, their assets, tracks, clips and bindings.",
            Retry = RetryClass.Read, Cost = Cost.Moderate)]
        [Example("{ }")]
        [Example("{ \"target\": \"CutsceneDirector\" }")]
        public static object TimelineInfo(
            [Doc("A GameObject with a PlayableDirector. Omit to list every director.")] string target = null,
            [Doc("Maximum clips per track (default 20)")] int limit = 20)
        {
            var directorType = Find("UnityEngine.Playables.PlayableDirector");
            if (directorType == null)
                throw new UmcpToolException("E_PACKAGE_MISSING",
                    "PlayableDirector is not available in this project.", "target", target, null,
                    "Timeline needs com.unity.timeline.");

            var directors = (string.IsNullOrEmpty(target)
                    ? Resolve.AllGameObjects()
                    : new[] { Resolve.GameObject(target, "target") })
                .SelectMany(go => go.GetComponents<Component>())
                .Where(c => c != null && directorType.IsInstanceOfType(c))
                .ToArray();

            if (directors.Length == 0)
                return new
                {
                    count = 0,
                    returned = 0,
                    directors = new object[0],
                    _truncated = false,
                    _hint = string.IsNullOrEmpty(target)
                        ? "No PlayableDirector in the open scenes."
                        : "'" + target + "' has no PlayableDirector."
                };

            int cap = Bounds.Limit(limit);
            var rows = directors.Take(20).Select(director =>
            {
                var asset = Read(director, "playableAsset") as UnityEngine.Object;
                return new
                {
                    path = Resolve.Path(director.transform),
                    asset = asset == null ? null : AssetDatabase.GetAssetPath(asset),
                    duration = Read(director, "duration"),
                    playOnAwake = Read(director, "playOnAwake"),
                    wrapMode = Convert.ToString(Read(director, "extrapolationMode")),
                    tracks = asset == null ? new object[0] : Tracks(asset, cap)
                };
            }).ToArray();

            return new
            {
                count = directors.Length,
                returned = rows.Length,
                directors = rows,
                _truncated = directors.Length > rows.Length
            };
        }

        static object[] Tracks(UnityEngine.Object asset, int cap)
        {
            // TimelineAsset.GetOutputTracks() returns IEnumerable<TrackAsset>; both types live in
            // the Timeline package, so everything here is reflective and tolerant of a version
            // that does not have a member.
            var method = asset.GetType().GetMethod("GetOutputTracks", BindingFlags.Public | BindingFlags.Instance);
            if (method == null) return new object[0];

            object result;
            try { result = method.Invoke(asset, null); }
            catch { return new object[0]; }
            if (result is not IEnumerable tracks) return new object[0];

            return tracks.Cast<object>().Where(t => t != null).Take(cap).Select(track =>
            {
                var clips = Read(track, "GetClips") as IEnumerable;
                var clipMethod = track.GetType().GetMethod("GetClips", BindingFlags.Public | BindingFlags.Instance);
                if (clips == null && clipMethod != null)
                {
                    try { clips = clipMethod.Invoke(track, null) as IEnumerable; } catch { }
                }

                var clipRows = clips == null ? new object[0] : clips.Cast<object>().Take(cap).Select(clip => (object)new
                {
                    name = Convert.ToString(Read(clip, "displayName")),
                    start = Read(clip, "start"),
                    duration = Read(clip, "duration")
                }).ToArray();

                return (object)new
                {
                    name = Convert.ToString(Read(track, "name")),
                    type = track.GetType().Name,
                    muted = Read(track, "muted"),
                    clips = clipRows,
                    clipCount = clipRows.Length
                };
            }).ToArray();
        }

        // ---------------------------------------------------------------- cinemachine

        [UnityTool(Skill = "cinematics", Id = "cinemachine.cameras",
            Summary = "List the Cinemachine virtual cameras with their priority, follow and look-at targets, and which one is live.",
            Retry = RetryClass.Read)]
        [Example("{ }")]
        public static object Cameras()
        {
            var cameras = VirtualCameras();
            if (cameras.Length == 0 && BrainType() == null)
                throw new UmcpToolException("E_PACKAGE_MISSING",
                    "Cinemachine is not installed in this project.", null, null, null,
                    "Install com.unity.cinemachine, or use the Camera component directly.");

            var brain = Resolve.AllGameObjects()
                .SelectMany(go => go.GetComponents<Component>())
                .FirstOrDefault(c => c != null && BrainType() != null && BrainType().IsInstanceOfType(c));

            var live = brain == null ? null : Read(brain, "ActiveVirtualCamera");
            var liveName = live == null ? null : Convert.ToString(Read(live, "Name") ?? Read(live, "name"));

            return new
            {
                brain = brain == null ? null : Resolve.Path(brain.transform),
                liveCamera = liveName,
                cameras = cameras.Select(c => new
                {
                    path = Resolve.Path(c.transform),
                    priority = Priority(c),
                    follow = Name(Read(c, "Follow")),
                    lookAt = Name(Read(c, "LookAt")),
                    enabled = (c as Behaviour) == null ? true : ((Behaviour)c).enabled,
                    type = c.GetType().Name
                }).ToArray(),
                _hint = brain == null
                    ? "No CinemachineBrain on any camera: virtual cameras do nothing without one."
                    : "Priority decides which camera is live. cinemachine.setPriority changes it."
            };
        }

        [UnityTool(Skill = "cinematics", Id = "cinemachine.setPriority",
            Summary = "Set a Cinemachine virtual camera's priority, which is what decides the live camera.",
            Mutating = true, Retry = RetryClass.Write, Undo = "Set camera priority")]
        [Example("{ \"target\": \"CM vcam Follow\", \"priority\": 20 }")]
        public static object SetPriority(
            [Doc("The virtual camera GameObject")] string target,
            [Doc("New priority; higher wins")] int priority)
        {
            if (BrainType() == null)
                throw new UmcpToolException("E_PACKAGE_MISSING",
                    "Cinemachine is not installed in this project.", null, null, null,
                    "Install com.unity.cinemachine, or use the Camera component directly.");

            var go = Resolve.GameObject(target, "target");
            var camera = go.GetComponents<Component>()
                .FirstOrDefault(c => c != null && IsVirtualCamera(c.GetType()));

            if (camera == null)
                throw new UmcpToolException("E_COMPONENT_NOT_FOUND",
                    "'" + go.name + "' has no Cinemachine virtual camera component.", "target", target,
                    VirtualCameras().Select(c => c.name).Take(6).ToArray(),
                    "cinemachine.cameras lists the virtual cameras in the scene.");

            Undo.RecordObject(camera, "Set camera priority");
            var before = Read(camera, "Priority");

            // Cinemachine 2 exposes `int Priority` as a property. Cinemachine 3 exposes a
            // *field* of type PrioritySettings, whose Enabled flag gates the value entirely —
            // writing Value alone sets a number the brain then ignores, which looks exactly like
            // a tool that worked and a camera that did not switch.
            var property = camera.GetType().GetProperty("Priority");
            var field = camera.GetType().GetField("Priority");
            var memberType = property != null && property.CanWrite ? property.PropertyType
                           : field != null ? field.FieldType
                           : null;

            if (memberType == null)
                throw new UmcpToolException("E_NOT_SUPPORTED",
                    "This Cinemachine version does not expose a writable Priority on " + camera.GetType().Name + ".",
                    "target", target, null,
                    "Set it in the Inspector, or use unity_script for this version's API.");

            var value = memberType == typeof(int) ? (object)priority : Wrap(memberType, priority);
            if (value == null)
                throw new UmcpToolException("E_NOT_SUPPORTED",
                    "Could not build a Priority value of type " + memberType.Name + ".",
                    "priority", priority.ToString());

            if (property != null && property.CanWrite) property.SetValue(camera, value, null);
            else field.SetValue(camera, value);
            EditorUtility.SetDirty(camera);

            return new
            {
                path = Resolve.Path(camera.transform),
                priority,
                previous = Convert.ToString(before),
                readBack = Priority(camera),
                _hint = "The brain re-evaluates immediately; the highest priority enabled camera becomes live."
            };
        }

        static object Wrap(Type type, int priority)
        {
            try
            {
                var instance = Activator.CreateInstance(type);
                var enabled = type.GetField("Enabled");
                if (enabled != null) enabled.SetValue(instance, true);   // otherwise the value is inert

                var field = type.GetField("Value") ?? type.GetField("m_Value");
                if (field != null) { field.SetValue(instance, priority); return instance; }
                var prop = type.GetProperty("Value");
                if (prop != null && prop.CanWrite) { prop.SetValue(instance, priority, null); return instance; }
            }
            catch { }
            return null;
        }

        /// <summary>The priority as a number, whichever generation of the API holds it.</summary>
        static object Priority(Component camera)
        {
            var raw = Read(camera, "Priority");
            if (raw == null) return null;
            if (raw is int) return raw;

            var value = Read(raw, "Value");
            var enabled = Read(raw, "Enabled");
            return new
            {
                value = value ?? 0,
                enabled = enabled ?? true,
                _note = enabled is bool && !(bool)enabled ? "default priority; Enabled is false" : null
            };
        }

        static Component[] VirtualCameras() =>
            Resolve.AllGameObjects()
                .SelectMany(go => go.GetComponents<Component>())
                .Where(c => c != null && IsVirtualCamera(c.GetType()))
                .ToArray();

        static bool IsVirtualCamera(Type type)
        {
            // Cinemachine 2 and 3 name their base types differently; matching the base class by
            // name covers both without referencing either package.
            while (type != null)
            {
                if (type.Name is "CinemachineVirtualCameraBase" or "CinemachineVirtualCamera" or "CinemachineCamera") return true;
                type = type.BaseType;
            }
            return false;
        }

        static Type BrainType() => Find("Unity.Cinemachine.CinemachineBrain") ?? Find("Cinemachine.CinemachineBrain");

        static string Name(object value)
        {
            var o = value as UnityEngine.Object;
            return o == null ? null : o.name;
        }

        static object Read(object target, string member)
        {
            if (target == null) return null;
            var type = target.GetType();
            var prop = type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null) { try { return prop.GetValue(target, null); } catch { return null; } }
            var field = type.GetField(member, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) { try { return field.GetValue(target); } catch { return null; } }
            return null;
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
