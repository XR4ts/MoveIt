
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MoveIt.Furniture.Editor
{
    public static class FurniturePreviewTest
    {
        static GameObject s_Target;
        static int s_Attempts;

        [MenuItem("MoveIt/Furniture/Test Selected Model Preview")]
        static void TestPreview()
        {
            string assetPath = AssetDatabase.GetAssetPath(
                Selection.activeObject);

            s_Target = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);

            if (s_Target == null)
            {
                Debug.LogError(
                    "Select an imported furniture model in the Project window first.");
                return;
            }

            s_Attempts = 0;

            Debug.Log($"Requesting preview for: {s_Target.name}");

            EditorApplication.update -= CheckPreview;
            EditorApplication.update += CheckPreview;

            AssetPreview.GetAssetPreview(s_Target);
        }

        static void CheckPreview()
        {
            s_Attempts++;

            Texture2D preview = AssetPreview.GetAssetPreview(s_Target);

            if (preview != null)
            {
                EditorApplication.update -= CheckPreview;

                const string folder = "Assets/Furniture/Thumbnails";

                if (!AssetDatabase.IsValidFolder(folder))
                    AssetDatabase.CreateFolder(
                        "Assets/Furniture", "Thumbnails");

                string path = $"{folder}/preview_test.png";

                File.WriteAllBytes(path, preview.EncodeToPNG());
                AssetDatabase.ImportAsset(path);

                Debug.Log($"Preview saved successfully: {path}");
                return;
            }

            if (s_Attempts > 300)
            {
                EditorApplication.update -= CheckPreview;
                Debug.LogError(
                    "Unity did not produce an AssetPreview after 300 updates.");
            }
        }
    }
}
