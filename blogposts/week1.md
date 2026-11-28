# MoveIt, Week 1: Furniture Assets, Prefabs and the AR Template

**Author:** Josip Brljevic

MoveIt is a mobile AR app where you can place furniture in your own room through the phone camera, to see if it fits before you buy it or move it. The goal for the first week was to get the furniture itself into the project, so that the rest of the app had something real to work with.

We started from Unity's **AR Mobile template**. Out of the box it already detects floors and walls, lets you tap on the floor to spawn an object, and lets you move, rotate and scale that object with your fingers. The only problem was that it spawned cubes and pyramids, so our job was to replace those with furniture. Before changing anything, we spent some time reading how the template places objects. When you tap the screen, a ray is cast against the detected planes, and the spawner gets back a position and rotation in the real world. ARCore tracks the room in real metres, so one unit in Unity is one metre in your room. This was an important thing to understand early, because it meant that our furniture had to be modelled at its real size, otherwise the app would give the wrong answer to "will it fit?".

The next step was finding models. We used two sources: the **IKEA 3D Assembly Dataset**, which has real IKEA products like the LACK table, EKET cabinets and BEKVÄM step stool at their true dimensions, and **Poly Haven**, which has free chairs, sofas, tables, beds and shelves. In total this gave us 91 models. Both sources use the glTF format, so we added the **glTFast** package to be able to import them into Unity.

The IKEA models were made for assembly instructions, so they came with every single screw and dowel. These parts are tiny, but they add a lot of geometry, which is not great on a phone that also has to run tracking and show the camera feed. We cleaned them up with glTF Transform by removing all hardware smaller than 2 cm, merging the meshes and resizing the textures to 1024 px.

One issue was that every model came in differently. Some had their pivot in the middle, some faced the wrong way, and the hierarchies were all different. Instead of fixing 91 prefabs by hand, we wrote an editor tool, `FurniturePrefabBuilder`, which can be run from the menu **MoveIt > Furniture > Build Prefabs**. It reads a `FurnitureSources.json` file with the name, category, source and license of each model, and builds every prefab with the same rules:

- the pivot is at the bottom centre, so the furniture stands on the floor where you tap,
- the scale is real-world size in metres,
- the front of the model faces forward,
- one `BoxCollider` covers the whole model, so it can be tapped and selected,
- a `FurnitureItem` component stores the id, display name, category and attribution.

The main part of the builder measures the bounds of the model and moves it so that its bottom centre sits on the prefab's origin:

```csharp
var bounds = GetRendererBounds(visual);
visual.transform.localPosition = new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);

var collider = root.AddComponent<BoxCollider>();
collider.center = new Vector3(0f, bounds.size.y / 2f, 0f);
collider.size = bounds.size;
```

If a model faces the wrong way or has the wrong size, you only change its `yaw` or `scale` in the JSON file and rebuild. The builder also fills a `FurnitureCatalog` asset with all the prefabs, sorted by category and name. This later made things easier for the rest of the team, because the furniture menu could be built from this one list, and the move and rotate controls could be added to the same kind of object every time. We also wrote down the source and license of every model in the furniture README, since we are using other people's work.

By the end of the week, the project had 91 real-size furniture prefabs that all followed the same rules. Looking back, setting these rules early was one of the best decisions in the project, because later features like wall snapping and the size outline on the floor could all rely on every piece of furniture being built the same way.
