# MoveIt

Mobile AR app (Unity, AR Foundation / ARCore): scan the room, then place and move furniture around in it.

The Unity project is in [`MoveIt/`](MoveIt). Open that folder in Unity Hub with Unity 6000.3.7f1.

## Room-aware features

Scripts in [`MoveIt/Assets/AR/Scripts`](MoveIt/Assets/AR/Scripts). They're added at runtime to any scene with an `ObjectSpawner`; add the components to the scene yourself to change their settings.

- **Depth occlusion:** an `AROcclusionManager` on the AR camera uses ARCore's depth estimate, so real objects hide furniture behind them. On phones without a depth sensor (e.g. Galaxy S24) depth comes from camera motion, so move the phone a little before it kicks in.
- **Light estimation** (`RoomLightEstimation`): the scene's directional light and ambient light follow ARCore's estimate of the room's main light and ambient light.
- **Wall snapping** (`FurnitureWallSnap`, `RoomWalls`): let go of furniture within 30 cm of a detected wall and it turns square to the wall and slides flush against it; in a corner it snaps to both walls.
- **Footprint and size** (`FurnitureFootprint`): while you hold an item, its footprint is outlined on the floor with its real size in cm. White = free, blue = will snap to a wall, red = overlaps a wall or other furniture.
- **Real-world scale** (`FurniturePlacementAssist`): pinch-to-scale is turned off for furniture, so every item keeps its true size.

## References

Third-party assets and code used in this project:

- **Furniture models:** [IKEA 3D Assembly Dataset](https://github.com/IKEA/IKEA3DAssemblyDataset) (CC BY-NC-SA 4.0, © Inter IKEA Systems B.V.) and [Poly Haven](https://polyhaven.com/models/furniture) (CC0). The full per-model list with authors is in [`MoveIt/Assets/Furniture/README.md`](MoveIt/Assets/Furniture/README.md).
- **glTF import:** [glTFast](https://docs.unity3d.com/Packages/com.unity.cloud.gltfast@6.1/manual/index.html) (Unity package `com.unity.cloud.gltfast`).
- **Mobile AR template:** Unity's AR Mobile project template and XR Interaction Toolkit samples.
