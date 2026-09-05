#nullable disable
namespace Umcp.Agent
{
    /// <summary>
    /// The reconcile hash, defined once and compiled into both the Unity package and the daemon.
    ///
    /// A cache that silently lies is worse than no cache, so the daemon periodically asks the
    /// Editor for subtree hashes and compares them against its mirror. That comparison is only
    /// meaningful if both sides hash identically — which is why this is a shared file with no
    /// Unity types in it rather than two implementations that agree today.
    ///
    /// FNV-1a: not cryptographic, and it does not need to be. It needs to be deterministic across
    /// two runtimes (Mono in the Editor, CoreCLR in the daemon), order-sensitive for sibling
    /// ordering, and cheap enough to run over a whole scene.
    /// </summary>
    public static class MirrorHash
    {
        const uint Offset = 2166136261;
        const uint Prime = 16777619;

        public static uint Start() { return Offset; }

        public static uint Mix(uint hash, string s)
        {
            if (s == null) return Mix(hash, (byte)0xFF);
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                hash = Mix(hash, (byte)(c & 0xFF));
                hash = Mix(hash, (byte)((c >> 8) & 0xFF));
            }
            return Mix(hash, (byte)0);
        }

        public static uint Mix(uint hash, int value)
        {
            hash = Mix(hash, (byte)(value & 0xFF));
            hash = Mix(hash, (byte)((value >> 8) & 0xFF));
            hash = Mix(hash, (byte)((value >> 16) & 0xFF));
            hash = Mix(hash, (byte)((value >> 24) & 0xFF));
            return hash;
        }

        public static uint Mix(uint hash, uint value) { return Mix(hash, unchecked((int)value)); }

        public static uint Mix(uint hash, byte b)
        {
            unchecked
            {
                hash ^= b;
                hash *= Prime;
            }
            return hash;
        }

        /// <summary>
        /// Hash of one node's own identity — deliberately excluding its instance id, which is not
        /// stable across a domain reload and would make every reconcile after a recompile report
        /// drift that is not there.
        /// </summary>
        public static uint Node(string name, int siblingIndex, int flags, string tag, string layer, string components)
        {
            var h = Start();
            h = Mix(h, name);
            h = Mix(h, siblingIndex);
            h = Mix(h, flags);
            h = Mix(h, tag);
            h = Mix(h, layer);
            h = Mix(h, components);
            return h;
        }

        /// <summary>Fold a child's hash into its parent's, in sibling order.</summary>
        public static uint Fold(uint parent, uint child) { return Mix(parent, child); }
    }
}
