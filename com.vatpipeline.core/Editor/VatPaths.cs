using UnityEditor;

namespace VatPipeline.EditorTools
{
    /// <summary>
    /// Folder helper. Inlined into the package so it carries no dependency on any host
    /// project's own setup utilities.
    /// </summary>
    public static class VatPaths
    {
        /// <summary>Creates every missing folder along an "Assets/a/b/c" path.</summary>
        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
