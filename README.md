# MoveIt

Mobile AR app (Unity, AR Foundation / ARCore): scan the room, then place and move furniture around in it.

The Unity project is in [`MoveIt/`](MoveIt). Open that folder in Unity Hub with Unity 6000.3.7f1.

## Room-aware features

Scripts in [`MoveIt/Assets/AR/Scripts`](MoveIt/Assets/AR/Scripts). They're added at runtime to any scene with an `ObjectSpawner`; add the components to the scene yourself to change their settings.

- **Depth occlusion:** an `AROcclusionManager` on the AR camera uses ARCore's depth estimate, so real objects hide furniture behind them. On phones without a depth sensor (e.g. Galaxy S24) depth comes from camera motion, so move the phone a little before it kicks in.
- **Light estimation** (`RoomLightEstimation`): the scene's directional light and ambient light follow ARCore's estimate of the room's main light and ambient light.
- **Wall snapping** (`FurnitureWallSnap`, `RoomWalls`): let go of furniture within 30 cm of a detected wall and it turns square to the wall and slides flush against it; in a corner it snaps to both walls. Only vertical surfaces at least 1.1 m tall count as walls, so the fronts of dressers and beds don't.
- **Footprint and size** (`FurnitureFootprint`): while you hold an item, its footprint is outlined on the floor with its real size in cm. White = free, blue = will snap to a wall, red = overlaps a wall or other furniture.
- **Room scanning and surface dots** (`SurfaceDots`): while scanning, a bar shows the floor area and number of walls found and the template's dotted surface visuals stay on, until **Done scanning**; **Scan room** starts again. Afterwards the dots only show while you hold a piece of furniture, and they're always hidden while erasing.
- **Floor shadows** (`FloorShadows`, `ShadowCatcher.shader`): an invisible surface at floor height draws only the shadows of virtual furniture, so they fall on the real floor, in the direction of the estimated room light.
- **Help on demand** (`HelpOverlay`): the template's step-by-step onboarding is skipped; a **?** button opens a short help card.
- **Real-world scale** (`FurniturePlacementAssist`): pinch-to-scale is turned off for furniture, so every item keeps its true size.
- **Erase real objects** (`ObjectEraser`, `DepthObjectFitter`, `ErasedRegion`, `Assets/AR/Shaders/Erase.hlsl`): tap **Erase object**, then tap a real object. `DepthObjectFitter` reads ARCore's environment depth image, turns it into 3D points, removes floor and wall points, grows a region from the tapped point to separate the object, and fits an upright box around it: square to a nearby wall, otherwise to the tightest footprint, reaching back to the wall behind it since the camera can't see its back. Everything inside the box is erased live. If part of it still shows, **Adjust** opens controls to drag, twist, rotate and resize the box; without depth the box is placed where the tap meets the floor. The room around the object is modelled as the floor plus up to two walls (two in a corner). For each covered pixel, the shader finds which of these surfaces the view ray hits, then copies the camera image from the nearest point on the same surface that the box doesn't hide, mirrored across the edge, preferring points beside the object; a 12 cm strip of floor around it is replaced too, to remove its contact shadow. A depth pass replaces the object's occlusion depth, so furniture can be placed where it stood. Erased objects are held in place with AR anchors. **Undo erase** and **Show original** compare before and after. Needs the URP Opaque Texture (on in `URP-Performant`). Works best on plain floors and walls; patterns, rugs and hard shadows show seams.

## References

Third-party assets and code used in this project:

- **Furniture models:** [IKEA 3D Assembly Dataset](https://github.com/IKEA/IKEA3DAssemblyDataset) (CC BY-NC-SA 4.0, © Inter IKEA Systems B.V.) and [Poly Haven](https://polyhaven.com/models/furniture) (CC0). The full per-model list with authors is in [`MoveIt/Assets/Furniture/README.md`](MoveIt/Assets/Furniture/README.md).
- **glTF import:** [glTFast](https://docs.unity3d.com/Packages/com.unity.cloud.gltfast@6.1/manual/index.html) (Unity package `com.unity.cloud.gltfast`).
- **Mobile AR template:** Unity's AR Mobile project template and XR Interaction Toolkit samples.
