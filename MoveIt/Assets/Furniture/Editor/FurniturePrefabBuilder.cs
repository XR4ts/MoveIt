
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;
using Object = UnityEngine.Object;


namespace MoveIt.Furniture.Editor
{
    /// <summary>
    /// Builds furniture prefabs with a bottom-centre pivot,
    /// front facing +Z, a BoxCollider, interaction components,
    /// and FurnitureItem metadata.
    /// </summary>
    static class FurniturePrefabBuilder
    {
        const string k_Root = "Assets/Furniture";
        const string k_SourcesPath = k_Root + "/FurnitureSources.json";
        const string k_CatalogPath = k_Root + "/FurnitureCatalog.asset";

        static Queue<FurnitureItem> s_ThumbnailQueue;
        static FurnitureItem s_CurrentThumbnailItem;
        static int s_ThumbnailAttempts;

        const int k_MaxThumbnailAttempts = 300;
        const int k_ThumbnailResolution = 256;

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

            var sourceList = JsonUtility.FromJson<SourceList>(json.text);

            if (sourceList?.items == null)
            {
                Debug.LogError($"No furniture items found in {k_SourcesPath}");
                return;
            }

            var sources = sourceList.items;
            var built = new List<FurnitureItem>();
            var skipped = new List<string>();
            var previewScene = EditorSceneManager.NewPreviewScene();

            try
            {
                for (var i = 0; i < sources.Count; i++)
                {
                    var source = sources[i];

                    if (EditorUtility.DisplayCancelableProgressBar(
                        "Building furniture prefabs",
                        source.displayName,
                        (float)i / sources.Count))
                    {
                        break;
                    }

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

            // Generates thumbnails after all prefabs have been saved.
            QueueThumbnailGeneration(built);

            Debug.Log(
                $"Built {built.Count} furniture prefabs." +
                (skipped.Count > 0
                    ? $" Skipped (model missing or not imported): {string.Join(", ", skipped)}"
                    : ""));
        }

        static FurnitureItem BuildPrefab(Source source, Scene previewScene)
        {
            var modelPath = FindModelPath(source);
            var model = string.IsNullOrEmpty(modelPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);

            if (model == null)
                return null;

            var root = new GameObject(source.id);
            SceneManager.MoveGameObjectToScene(root, previewScene);

            try
            {
                var visual = (GameObject)PrefabUtility.InstantiatePrefab(
                    model, previewScene);

                visual.name = "Model";
                visual.transform.SetParent(root.transform, false);
                visual.transform.localRotation =
                    Quaternion.Euler(0f, source.yaw, 0f);
                visual.transform.localScale =
                    Vector3.one * (source.scale > 0f ? source.scale : 1f);

                // Move the model so its bottom-centre sits at the root pivot.
                var bounds = GetRendererBounds(visual);

                visual.transform.localPosition = new Vector3(
                    -bounds.center.x,
                    -bounds.min.y,
                    -bounds.center.z);

                // Collision shape for selection and interaction.
                var collider = root.AddComponent<BoxCollider>();
                collider.center = new Vector3(0f, bounds.size.y / 2f, 0f);
                collider.size = bounds.size;

                // Add the components needed for AR manipulation.
                AddInteractionComponents(root);

                // Preserve furniture catalog metadata.
                var item = root.AddComponent<FurnitureItem>();

                item.Initialize(
                    source.id,
                    source.displayName,
                    source.category,
                    $"{source.displayName} by {source.author} ({source.source}), {source.license}, {source.url}");


                var folder = $"{k_Root}/Prefabs/{source.source}";
                Directory.CreateDirectory(folder);

                var prefabPath = $"{folder}/{source.id}.prefab";
                var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

                return saved != null
                    ? saved.GetComponent<FurnitureItem>()
                    : null;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static void AddInteractionComponents(GameObject root)
        {
            // Rigidbody: match the working AR template.
            var body = root.GetComponent<Rigidbody>();

            if (body == null)
                body = root.AddComponent<Rigidbody>();

            body.useGravity = false;
            body.isKinematic = true;

            // XR interaction: allows the object to be selected and grabbed.
            if (root.GetComponent<XRGrabInteractable>() == null)
                root.AddComponent<XRGrabInteractable>();

            // AR transformation: enables movement, rotation and scaling.
            if (root.GetComponent<ARTransformer>() == null)
                root.AddComponent<ARTransformer>();
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
            var catalog =
                AssetDatabase.LoadAssetAtPath<FurnitureCatalog>(k_CatalogPath);

            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<FurnitureCatalog>();
                AssetDatabase.CreateAsset(catalog, k_CatalogPath);
            }

            catalog.SetItems(
                items.OrderBy(i => i.category)
                     .ThenBy(i => i.displayName)
                     .ToList());

            EditorUtility.SetDirty(catalog);
        }


        static void QueueThumbnailGeneration(List<FurnitureItem> items)
        {
            s_ThumbnailQueue = new Queue<FurnitureItem>(items);
            s_CurrentThumbnailItem = null;
            s_ThumbnailAttempts = 0;

            EditorApplication.update -= GenerateNextThumbnail;
            EditorApplication.update += GenerateNextThumbnail;

            Debug.Log($"Queued {items.Count} furniture thumbnails.");
        }

        static void GenerateNextThumbnail()
        {
            if (s_CurrentThumbnailItem == null)
            {
                if (s_ThumbnailQueue == null || s_ThumbnailQueue.Count == 0)
                {
                    EditorApplication.update -= GenerateNextThumbnail;

                    AssetDatabase.SaveAssets();

                    Debug.Log("Furniture thumbnail generation completed.");
                    return;
                }

                s_CurrentThumbnailItem = s_ThumbnailQueue.Dequeue();
                s_ThumbnailAttempts = 0;
            }

            var item = s_CurrentThumbnailItem;

            if (item == null)
            {
                s_CurrentThumbnailItem = null;
                return;
            }

            // Request Unity's own prefab preview.
            var preview = AssetPreview.GetAssetPreview(item.gameObject);

            if (preview == null)
            {
                s_ThumbnailAttempts++;

                if (s_ThumbnailAttempts >= k_MaxThumbnailAttempts)
                {
                    Debug.LogWarning(
                        $"Could not generate preview for {item.displayName}. Skipping.");

                    s_CurrentThumbnailItem = null;
                }

                return;
            }

            SaveThumbnail(item, preview);

            s_CurrentThumbnailItem = null;
        }

        static void SaveThumbnail(FurnitureItem item, Texture2D preview)
        {
            var folder = $"{k_Root}/Thumbnails";

            Directory.CreateDirectory(folder);

            var imagePath = $"{folder}/{item.id}.png";

            File.WriteAllBytes(imagePath, preview.EncodeToPNG());

            AssetDatabase.ImportAsset(
                imagePath,
                ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(imagePath) as TextureImporter;

            if (importer == null)
            {
                Debug.LogError($"Could not import thumbnail: {imagePath}");
                return;
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.maxTextureSize = k_ThumbnailResolution;
            importer.SaveAndReimport();

            var thumbnail = AssetDatabase.LoadAssetAtPath<Sprite>(imagePath);

            if (thumbnail == null)
            {
                Debug.LogError($"Could not load thumbnail sprite: {imagePath}");
                return;
            }

            // Update the saved prefab's FurnitureItem component.
            var prefabPath = AssetDatabase.GetAssetPath(item.gameObject);

            var prefabContents = PrefabUtility.LoadPrefabContents(prefabPath);

            try
            {
                var prefabItem = prefabContents.GetComponent<FurnitureItem>();

                if (prefabItem == null)
                {
                    Debug.LogError($"FurnitureItem missing from {prefabPath}");
                    return;
                }

                prefabItem.SetThumbnail(thumbnail);

                PrefabUtility.SaveAsPrefabAsset(
                    prefabContents,
                    prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabContents);
            }

            Debug.Log($"Generated thumbnail for {item.displayName}");
        }


    }
}
