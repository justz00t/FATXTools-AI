using System.Collections.Generic;
using System.Linq;

namespace FATX.Reference
{
    /// <summary>
    /// The reference sets loaded in this process. Their learned file types
    /// are used by every file carver run.
    /// </summary>
    public static class ReferenceLibrary
    {
        private static readonly List<ReferenceSet> loaded = new List<ReferenceSet>();

        public static IReadOnlyList<ReferenceSet> Loaded
        {
            get { lock (loaded) return loaded.ToList(); }
        }

        public static void Add(ReferenceSet set)
        {
            lock (loaded)
            {
                loaded.RemoveAll(s => s.Name == set.Name && s.SourceFolder == set.SourceFolder);
                loaded.Add(set);
            }
        }

        public static void Clear()
        {
            lock (loaded) loaded.Clear();
        }

        /// <summary>
        /// The file types learned by the loaded sets (null when none is loaded).
        /// </summary>
        public static List<TypeSignature> CarverSignatures
        {
            get
            {
                lock (loaded)
                {
                    var types = loaded.SelectMany(s => s.Types).ToList();
                    return types.Count == 0 ? null : types;
                }
            }
        }
    }
}
