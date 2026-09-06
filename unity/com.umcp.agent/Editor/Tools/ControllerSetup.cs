using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// <c>setup.thirdPersonController</c> — the composite from section 8.3 that needs a script.
    ///
    /// It is **two-phase, deliberately**. A controller needs behaviour, behaviour needs a
    /// MonoBehaviour, and writing a `.cs` file triggers a compile and a domain reload — after which
    /// the type exists and can be attached, and not one instruction before. Pretending otherwise
    /// would mean either attaching a type that does not exist yet, or blocking an Editor tick until
    /// a compile finishes, which every health check in this system reads as a wedged Editor.
    ///
    /// So: the first call writes the script and asks for the recompile; the second call builds the
    /// rig. The result of the first says exactly that, and the daemon holds and replays anything
    /// queued behind the reload, so "call it twice" costs one extra round trip and no lost work.
    /// </summary>
    internal static class ControllerSetup
    {
        public const string TypeName = "UmcpThirdPersonController";
        const string DefaultFolder = "Assets/Scripts/Generated";

        [UnityTool(Skill = "gameobject", Id = "setup.thirdPersonController",
            Summary = "Create a third-person controller: character, camera rig and movement script. Two calls — the first writes the script, the second builds the rig after the reload.",
            Mutating = true, Retry = RetryClass.Compile, Cost = Cost.Expensive, Undo = "Set up third-person controller")]
        [Example("{ \"name\": \"Player\" }")]
        [Example("{ \"name\": \"Player\", \"position\": [0, 1, 0], \"moveSpeed\": 6 }")]
        public static object ThirdPersonController(
            [Doc("Name for the character object")] string name = "Player",
            [Doc("World position [x, y, z] (default [0, 1, 0])")] float[] position = null,
            [Doc("Walk speed in m/s (default 4)")] float moveSpeed = 4f,
            [Doc("Sprint speed in m/s (default 7)")] float sprintSpeed = 7f,
            [Doc("Jump height in metres (default 1.2)")] float jumpHeight = 1.2f,
            [Doc("Where the generated script goes")] string scriptFolder = "Assets/Scripts/Generated")
        {
            // This one writes a file, so confinement is not a nicety: an unchecked "../"
            // here would write C# anywhere the Editor process can reach.
            var folder = Resolve.AssetPath(
                string.IsNullOrEmpty(scriptFolder) ? DefaultFolder : scriptFolder, "scriptFolder").TrimEnd('/');
            var scriptPath = folder + "/" + TypeName + ".cs";
            var type = FindType(TypeName);

            // ---- phase one: the script has to exist and be compiled before anything can use it.
            if (type == null)
            {
                bool wrote = false;
                if (!File.Exists(Absolute(scriptPath)))
                {
                    if (!AssetDatabase.IsValidFolder(folder)) AssetTools.CreateFolder(folder);
                    File.WriteAllText(Absolute(scriptPath), ScriptSource);
                    AssetDatabase.ImportAsset(scriptPath, ImportAssetOptions.ForceSynchronousImport);
                    wrote = true;
                }

                UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();

                return new
                {
                    phase = "compiling",
                    scriptCreated = wrote,
                    script = scriptPath,
                    nextStep = "Call setup.thirdPersonController again with the same arguments once the reload finishes; " +
                               "the second call builds the rig.",
                    _hint = wrote
                        ? "The script was written and a recompile requested."
                        : "The script exists but its type is not loaded yet — a compile is pending or failed. Check compile.errors."
                };
            }

            // ---- phase two: the type exists, so build the thing.
            var where = position == null || position.Length != 3 ? new Vector3(0f, 1f, 0f) : Vec.V3(position, "position");
            if (Resolve.TryGameObject(name) != null)
                throw new UmcpToolException("E_ALREADY_EXISTS",
                    "A GameObject named '" + name + "' already exists.", "name", name, null,
                    "Pick another name, or delete the existing object first.");

            var root = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(root, "Set up third-person controller");
            root.transform.position = where;

            var controller = root.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.3f;
            controller.center = new Vector3(0f, 0.9f, 0f);

            // A visible body, so the thing is not an invisible capsule nobody can find in the scene.
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            var bodyCollider = body.GetComponent<Collider>();
            if (bodyCollider != null) UnityEngine.Object.DestroyImmediate(bodyCollider);   // the CharacterController is the collider

            var pivot = new GameObject("CameraPivot");
            pivot.transform.SetParent(root.transform, false);
            pivot.transform.localPosition = new Vector3(0f, 1.6f, 0f);

            var behaviour = root.AddComponent(type);
            SetField(behaviour, "cameraPivot", pivot.transform);
            SetField(behaviour, "moveSpeed", moveSpeed);
            SetField(behaviour, "sprintSpeed", sprintSpeed);
            SetField(behaviour, "jumpHeight", jumpHeight);

            var camera = BuildCamera(pivot.transform, root.transform, out var cameraKind, out var note);

            return new
            {
                phase = "built",
                character = Resolve.Path(root.transform),
                script = scriptPath,
                components = new[] { "CharacterController", TypeName },
                cameraPivot = Resolve.Path(pivot.transform),
                camera,
                cameraKind,
                note,
                _hint = "Movement runs in Play mode only. This tool never enters Play mode — press Play yourself to test it."
            };
        }

        /// <summary>
        /// Cinemachine when the project has it, a plain camera when it does not — and, either way,
        /// the user's existing Main Camera is left alone. Two enabled cameras with no explicit
        /// depth is a rendering coin toss, and silently disabling somebody's main camera is worse.
        /// </summary>
        static string BuildCamera(Transform pivot, Transform follow, out string kind, out string note)
        {
            var vcamType = FindType("Unity.Cinemachine.CinemachineCamera")
                        ?? FindType("Cinemachine.CinemachineVirtualCamera");

            if (vcamType != null)
            {
                var go = new GameObject("PlayerCamera");
                Undo.RegisterCreatedObjectUndo(go, "Set up third-person controller");
                go.transform.position = pivot.position - follow.forward * 4f + Vector3.up * 0.5f;

                var vcam = go.AddComponent(vcamType);
                SetProperty(vcam, "Follow", pivot);
                SetProperty(vcam, "LookAt", pivot);

                var main = Camera.main;
                var brainType = FindType("Unity.Cinemachine.CinemachineBrain") ?? FindType("Cinemachine.CinemachineBrain");
                bool brainAdded = false;
                if (main != null && brainType != null && main.GetComponent(brainType) == null)
                {
                    main.gameObject.AddComponent(brainType);
                    brainAdded = true;
                }

                kind = "cinemachine";
                note = main == null
                    ? "No Main Camera in the scene; add one with a CinemachineBrain for the virtual camera to drive."
                    : brainAdded ? "A CinemachineBrain was added to the Main Camera so this virtual camera can drive it."
                                 : "The Main Camera already has a CinemachineBrain.";
                return Resolve.Path(go.transform);
            }

            var plain = new GameObject("PlayerCamera");
            Undo.RegisterCreatedObjectUndo(plain, "Set up third-person controller");
            plain.transform.SetParent(pivot, false);
            plain.transform.localPosition = new Vector3(0f, 0.5f, -4f);
            plain.transform.localRotation = Quaternion.Euler(8f, 0f, 0f);

            var camera = plain.AddComponent<Camera>();
            var existing = Camera.main;
            if (existing != null)
            {
                camera.enabled = false;
                kind = "plain (disabled)";
                note = "The scene already has a Main Camera, so this one was created disabled rather than " +
                       "competing with it. Enable it, or disable the other, when you want to use it.";
            }
            else
            {
                plain.tag = "MainCamera";
                kind = "plain";
                note = "No Main Camera existed, so this one was tagged MainCamera.";
            }
            return Resolve.Path(plain.transform);
        }

        static void SetField(Component component, string field, object value)
        {
            var f = component.GetType().GetField(field);
            if (f != null) { try { f.SetValue(component, value); } catch { } }
        }

        static void SetProperty(Component component, string member, object value)
        {
            var type = component.GetType();
            var prop = type.GetProperty(member);
            if (prop != null && prop.CanWrite) { try { prop.SetValue(component, value, null); return; } catch { } }
            var field = type.GetField(member);
            if (field != null) { try { field.SetValue(component, value); } catch { } }
        }

        static Type FindType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type found;
                try { found = assembly.GetType(name, false) ?? assembly.GetTypes().FirstOrDefault(t => t.Name == name); }
                catch { continue; }
                if (found != null) return found;
            }
            return null;
        }

        static string Absolute(string assetPath) =>
            Path.Combine(UmcpSettings.ProjectRoot, assetPath).Replace('\\', '/');

        /// <summary>
        /// The generated controller. It compiles against either input system, because a project's
        /// input choice is not something a tool gets to make: <c>ENABLE_INPUT_SYSTEM</c> and
        /// <c>ENABLE_LEGACY_INPUT_MANAGER</c> are both defined by Unity, and both branches are
        /// written out so the file works wherever it lands.
        /// </summary>
        const string ScriptSource = @"// Generated by the Unity MCP Tool (setup.thirdPersonController).
// Safe to edit: it is a normal script in your project and nothing regenerates it.
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[RequireComponent(typeof(CharacterController))]
public class UmcpThirdPersonController : MonoBehaviour
{
    [Header(""Movement"")]
    public float moveSpeed = 4f;
    public float sprintSpeed = 7f;
    public float jumpHeight = 1.2f;
    public float gravity = -19.62f;
    public float turnSmoothing = 0.08f;

    [Header(""Look"")]
    public Transform cameraPivot;
    public float lookSensitivity = 0.12f;
    public float minPitch = -35f;
    public float maxPitch = 65f;

    CharacterController _controller;
    Vector3 _velocity;
    float _turnVelocity;
    float _pitch;

    void Awake()
    {
        _controller = GetComponent<CharacterController>();
    }

    void Update()
    {
        Look(ReadLook());
        Move(ReadMove(), ReadSprint(), ReadJump());
    }

    void Look(Vector2 delta)
    {
        if (cameraPivot == null || delta == Vector2.zero) return;

        transform.Rotate(Vector3.up, delta.x * lookSensitivity, Space.World);
        _pitch = Mathf.Clamp(_pitch - delta.y * lookSensitivity, minPitch, maxPitch);
        cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
    }

    void Move(Vector2 input, bool sprinting, bool jumped)
    {
        bool grounded = _controller.isGrounded;
        if (grounded && _velocity.y < 0f) _velocity.y = -2f;   // keep it pinned to the ground

        Vector3 wish = new Vector3(input.x, 0f, input.y);
        if (wish.sqrMagnitude > 1f) wish.Normalize();

        if (wish.sqrMagnitude > 0.0001f)
        {
            float target = Mathf.Atan2(wish.x, wish.z) * Mathf.Rad2Deg + transform.eulerAngles.y;
            float angle = Mathf.SmoothDampAngle(transform.eulerAngles.y, target, ref _turnVelocity, turnSmoothing);
            Vector3 direction = Quaternion.Euler(0f, target, 0f) * Vector3.forward;
            _controller.Move(direction * (sprinting ? sprintSpeed : moveSpeed) * Time.deltaTime);
            transform.rotation = Quaternion.Euler(0f, angle, 0f);
        }

        if (jumped && grounded) _velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);

        _velocity.y += gravity * Time.deltaTime;
        _controller.Move(_velocity * Time.deltaTime);
    }

    // ---- input, whichever system this project uses -------------------------------------------

    Vector2 ReadMove()
    {
#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            float x = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
            float y = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
            return new Vector2(x, y);
        }
        var pad = Gamepad.current;
        if (pad != null) return pad.leftStick.ReadValue();
        return Vector2.zero;
#else
        return new Vector2(Input.GetAxisRaw(""Horizontal""), Input.GetAxisRaw(""Vertical""));
#endif
    }

    Vector2 ReadLook()
    {
#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (mouse != null && Mouse.current.rightButton.isPressed) return mouse.delta.ReadValue();
        var pad = Gamepad.current;
        if (pad != null) return pad.rightStick.ReadValue() * 4f;
        return Vector2.zero;
#else
        if (!Input.GetMouseButton(1)) return Vector2.zero;
        return new Vector2(Input.GetAxis(""Mouse X""), Input.GetAxis(""Mouse Y"")) * 10f;
#endif
    }

    bool ReadSprint()
    {
#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        return keyboard != null && keyboard.leftShiftKey.isPressed;
#else
        return Input.GetKey(KeyCode.LeftShift);
#endif
    }

    bool ReadJump()
    {
#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        return keyboard != null && keyboard.spaceKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.Space);
#endif
    }
}
";
    }
}
