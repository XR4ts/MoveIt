using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MoveIt.Furniture.Editor
{
    /// <summary>
    /// Builds a clean prefab for every model listed in FurnitureSources.json:
    /// pivot at the bottom centre, front facing +Z, unit scale on the root, one BoxCollider
    /// covering the model and a <see cref="FurnitureItem"/> with display and attribution data.
    /// Re-running overwrites the prefabs in place (GUIDs are kept), so fix orientation or scale
    /// by editing "yaw" / "scale" in FurnitureSources.json and rebuilding, not by hand.
    /// </summary>
    static class FurniturePrefabBuilder
    {
        const string k_Root = "Assets/Furniture";
        const string k_SourcesPath = k_Root + "/FurnitureSources.json";
        const string k_CatalogPath = k_Root + "/FurnitureCatalog.asset";

        [Serializable]
        class Source
        {
            public string id;
            public string displayName;
            public string category;
            public string source;
            public string author;
            public string license;
            public string url;
            public float yaw;
            public float scale;
        }

        [Serializable]
        class SourceList
        {
            public List<Source> items;
        }

        [MenuItem("MoveIt/Furniture/Build Prefabs")]
        static void BuildAll()
        {
            var json = AssetDatabase.LoadAssetAtPath<TextAsset>(k_SourcesPath);
            if (json == null)
            {
                Debug.LogError($"Furniture sources not found at {k_SourcesPath}");
                return;
            }

            var sources = JsonUtility.FromJson<SourceList>(json.text).items;
            var built = new List<FurnitureItem>();
            var skipped = new List<string>();
            var previewScene = EditorSceneManager.NewPreviewScene();

            try
            {
                for (var i = 0; i < sources.Count; i++)
                {
                    var source = sources[i];
                    if (EditorUtility.DisplayCancelableProgressBar("Building furniture prefabs", source.displayName, (float)i / sources.Count))
                        break;

                    var prefab = BuildPrefab(source, previewScene);
                    if (prefab != null)
                        built.Add(prefab);
                    else
                        skipped.Add(source.id);
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(previewScene);
                EditorUtility.ClearProgressBar();
            }

            UpdateCatalog(built);
            AssetDatabase.SaveAssets();

            Debug.Log($"Built {built.Count} furniture prefabs." +
                (skipped.Count > 0 ? $" Skipped (model missing or not imported): {string.Join(", ", skipped)}" : ""));
        }

        static FurnitureItem BuildPrefab(Source source, Scene previewScene)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(FindModelPath(source));
            if (model == null)
                return null;

            var root = new GameObject(source.id);
            SceneManager.MoveGameObjectToScene(root, previewScene);

            try
            {
                var visual = (GameObject)PrefabUtility.InstantiatePrefab(model, previewScene);
                visual.name = "Model";
                visual.transform.SetParent(root.transform, false);
                visual.transform.localRotation = Quaternion.Euler(0f, source.yaw, 0f);
                visual.transform.localScale = Vector3.one * (source.scale > 0f ? source.scale : 1f);

                // Move the model so the bottom centre of its bounds sits on the root pivot.
                var bounds = GetRendererBounds(visual);
                visual.transform.localPosition = new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);

                var collider = root.AddComponent<BoxCollider>();
                collider.center = new Vector3(0f, bounds.size.y / 2f, 0f);
                collider.size = bounds.size;

                var item = root.AddComponent<FurnitureItem>();
                item.Initialize(source.id, source.displayName, source.category,
                    $"{source.displayName} by {source.author} ({source.source}), {source.license}, {source.url}");

                var folder = $"{k_Root}/Prefabs/{source.source}";
                Directory.CreateDirectory(folder);
                var saved = PrefabUtility.SaveAsPrefabAsset(root, $"{folder}/{source.id}.prefab");
                return saved != null ? saved.GetComponent<FurnitureItem>() : null;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static string FindModelPath(Source source)
        {
            var folder = $"{k_Root}/Models/{source.source}/{source.id}";
            foreach (var extension in new[] { ".glb", ".gltf", ".fbx" })
            {
                var path = $"{folder}/{source.id}{extension}";
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        static Bounds GetRendererBounds(GameObject gameObject)
        {
            var renderers = gameObject.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return new Bounds(Vector3.zero, Vector3.zero);

            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1))
                bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        static void UpdateCatalog(List<FurnitureItem> items)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<FurnitureCatalog>(k_CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<FurnitureCatalog>();
                AssetDatabase.CreateAsset(catalog, k_CatalogPath);
            }

            catalog.SetItems(items.OrderBy(i => i.category).ThenBy(i => i.displayName).ToList());
            EditorUtility.SetDirty(catalog);
        }
    }
}
