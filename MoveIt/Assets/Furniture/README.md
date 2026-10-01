# Furniture assets

Placeable furniture for MoveIt. Every model has a prefab in `Prefabs/`, built by **MoveIt > Furniture > Build Prefabs** from the models in `Models/` and the metadata in `FurnitureSources.json`.

## Prefab conventions

These hold for every prefab, so placement and controls code can rely on them:

- **Pivot** at the bottom centre of the model: place the prefab at an AR plane hit pose and it stands on the floor.
- **Real-world scale** in metres, with scale `1,1,1` on the root.
- **Front faces +Z** (Unity forward).
- **One `BoxCollider`** on the root covering the whole model, for tap/selection raycasts.
- **`FurnitureItem`** component on the root with `id`, `displayName`, `category`, `attribution` and `size`.
- **`FurnitureCatalog.asset`** lists all prefabs, sorted by category and name, for the selector UI (`catalog.items`, `GetByCategory`, `GetById`).

To fix a model that faces the wrong way or has the wrong size, set `yaw` (degrees) or `scale` on its entry in `FurnitureSources.json` and rebuild. Don't edit the prefabs by hand; a rebuild overwrites them.

## Adding a model

1. Put it in `Models/<Source>/<id>/<id>.glb` (or `.gltf` / `.fbx`).
2. Add an entry to `FurnitureSources.json` with its source, author, license and URL.
3. Run **MoveIt > Furniture > Build Prefabs**.
4. Add it to the list below.

## Sources and licenses

### IKEA 3D Assembly Dataset

Copyright © Inter IKEA Systems B.V. 2021. Licensed under [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/) (see `Models/IKEA/LICENSE.md`). Source: <https://github.com/IKEA/IKEA3DAssemblyDataset>. IKEA keeps all rights to the product designs; the license covers the 3D data only, for non-commercial use.

**Modified for mobile:** hardware smaller than 2 cm (screws, cam locks, dowels) removed, part transforms baked into the vertices, meshes merged, and textures resized to 1024 px JPEG with [glTF Transform](https://gltf-transform.dev). Under ShareAlike, these modified files are released under the same CC BY-NC-SA 4.0 license.

| Model | Product | Category |
|---|---|---|
| `LACK_30449908_55x55` | [LACK side table](https://www.ikea.com/nl/en/p/lack-side-table-white-30449908/) | Tables |
| `EKET_70332124_35x25x35` | [EKET cabinet](https://www.ikea.com/nl/en/p/eket-cabinet-white-70332124/) | Storage |
| `EKET_00333947_70x35x35` | [EKET cabinet with 2 drawers](https://www.ikea.com/nl/en/p/eket-cabinet-with-2-drawers-white-00333947/) | Storage |
| `BEKVAM_30178884` | [BEKVÄM step stool](https://www.ikea.com/pt/en/p/bekvaem-step-stool-black-30178884/) | Seating |
| `BEKVAM_40463852` | [BEKVÄM step stool (EU)](https://www.ikea.com/nl/en/p/bekvaem-step-stool-black-40463852/) | Seating |
| `DALFRED_60155602` | [DALFRED bar stool](https://www.ikea.com/nl/en/p/dalfred-bar-stool-black-60155602/) | Seating |

### Poly Haven

All Poly Haven models are [CC0](https://polyhaven.com/license) (public domain); attribution is not required but given here. Downloaded as 1k glTF from <https://polyhaven.com/models/furniture>, unmodified.

| Model | Name | Author | Category |
|---|---|---|---|
| `GothicBed_01` | [Gothic Bed 01](https://polyhaven.com/a/GothicBed_01) | Kirill Sannikov | Beds |
| `old_bed_frame` | [Old Bed Frame](https://polyhaven.com/a/old_bed_frame) | Luca B | Beds |
| `vintage_day_bed` | [Vintage Day Bed](https://polyhaven.com/a/vintage_day_bed) | Aron Łyczek | Beds |
| `chinese_screen_panels` | [Chinese Screen Panels](https://polyhaven.com/a/chinese_screen_panels) | Kirill Sannikov | Ornaments |
| `stone_fire_pit` | [Stone Fire Pit](https://polyhaven.com/a/stone_fire_pit) | Sebastian Platen | Outdoor Structures |
| `ArmChair_01` | [Arm Chair 01](https://polyhaven.com/a/ArmChair_01) | Kirill Sannikov | Seating |
| `bar_chair_round_01` | [Bar Chair Round 01](https://polyhaven.com/a/bar_chair_round_01) | Dairon Sanchez | Seating |
| `BarberShopChair_01` | [Barber Shop Chair 01](https://polyhaven.com/a/BarberShopChair_01) | Fernando Quinn | Seating |
| `chinese_armchair` | [Chinese Armchair](https://polyhaven.com/a/chinese_armchair) | Kirill Sannikov | Seating |
| `chinese_sofa` | [Chinese Sofa](https://polyhaven.com/a/chinese_sofa) | Kirill Sannikov | Seating |
| `chinese_stool` | [Chinese Stool](https://polyhaven.com/a/chinese_stool) | Kirill Sannikov | Seating |
| `dining_chair_02` | [Dining Chair 02](https://polyhaven.com/a/dining_chair_02) | James Ray Cock | Seating |
| `folding_wooden_stool` | [Folding Wooden Stool](https://polyhaven.com/a/folding_wooden_stool) | Ulan Cabanilla | Seating |
| `gallinera_chair` | [Gallinera Chair](https://polyhaven.com/a/gallinera_chair) | Ulan Cabanilla | Seating |
| `GreenChair_01` | [Green Chair 01](https://polyhaven.com/a/GreenChair_01) | Kirill Sannikov | Seating |
| `metal_stool_01` | [Metal Stool 01](https://polyhaven.com/a/metal_stool_01) | Ulan Cabanilla | Seating |
| `metal_stool_02` | [Metal Stool 02](https://polyhaven.com/a/metal_stool_02) | Ulan Cabanilla | Seating |
| `metal_stool_03` | [Metal Stool 03](https://polyhaven.com/a/metal_stool_03) | Flo Tasser | Seating |
| `mid_century_lounge_chair` | [Mid Century Lounge Chair](https://polyhaven.com/a/mid_century_lounge_chair) | Kuutti Siitonen | Seating |
| `modern_arm_chair_01` | [Modern Arm Chair 01](https://polyhaven.com/a/modern_arm_chair_01) | Vibrant Nordic | Seating |
| `modular_street_seating` | [Modular Street Seating](https://polyhaven.com/a/modular_street_seating) | Stuart Attenborrow | Seating |
| `Ottoman_01` | [Ottoman 01](https://polyhaven.com/a/Ottoman_01) | Caspian Fortune | Seating |
| `outdoor_table_chair_set_01` | [Outdoor Table Chair Set 01](https://polyhaven.com/a/outdoor_table_chair_set_01) | James Ray Cock | Seating |
| `painted_wooden_bench` | [Painted Wooden Bench](https://polyhaven.com/a/painted_wooden_bench) | Kirill Sannikov | Seating |
| `painted_wooden_chair_01` | [Painted Wooden Chair 01](https://polyhaven.com/a/painted_wooden_chair_01) | Kuutti Siitonen | Seating |
| `painted_wooden_chair_02` | [Painted Wooden Chair 02](https://polyhaven.com/a/painted_wooden_chair_02) | Kirill Sannikov | Seating |
| `painted_wooden_sofa` | [Painted Wooden Sofa](https://polyhaven.com/a/painted_wooden_sofa) | Kirill Sannikov | Seating |
| `painted_wooden_stool` | [Painted Wooden Stool](https://polyhaven.com/a/painted_wooden_stool) | Kirill Sannikov | Seating |
| `plastic_monobloc_chair_01` | [Plastic Monobloc Chair 01](https://polyhaven.com/a/plastic_monobloc_chair_01) | Kuutti Siitonen | Seating |
| `Rockingchair_01` | [Rockingchair 01](https://polyhaven.com/a/Rockingchair_01) | Jorge Camacho | Seating |
| `SchoolChair_01` | [School Chair 01](https://polyhaven.com/a/SchoolChair_01) | Ethan Place | Seating |
| `Sofa_01` | [Sofa 01](https://polyhaven.com/a/Sofa_01) | Kirill Sannikov | Seating |
| `sofa_02` | [Sofa 02](https://polyhaven.com/a/sofa_02) | Kirill Sannikov | Seating |
| `sofa_03` | [Sofa 03](https://polyhaven.com/a/sofa_03) | Fran Calvente | Seating |
| `WoodenChair_01` | [Wooden Chair 01](https://polyhaven.com/a/WoodenChair_01) | Jake Mobley | Seating |
| `wooden_stool_01` | [Wooden Stool 01](https://polyhaven.com/a/wooden_stool_01) | Kuutti Siitonen | Seating |
| `wooden_stool_02` | [Wooden Stool 02](https://polyhaven.com/a/wooden_stool_02) | Kuutti Siitonen | Seating |
| `chinese_cabinet` | [Chinese Cabinet](https://polyhaven.com/a/chinese_cabinet) | Kirill Sannikov | Storage |
| `chinese_commode` | [Chinese Commode](https://polyhaven.com/a/chinese_commode) | Kirill Sannikov | Storage |
| `CoffeeCart_01` | [Coffee Cart 01](https://polyhaven.com/a/CoffeeCart_01) | Joe Seabuhr | Storage |
| `drawer_cabinet` | [Drawer Cabinet](https://polyhaven.com/a/drawer_cabinet) | Ulan Cabanilla | Storage |
| `GothicCabinet_01` | [Gothic Cabinet 01](https://polyhaven.com/a/GothicCabinet_01) | Kirill Sannikov | Storage |
| `GothicCommode_01` | [Gothic Commode 01](https://polyhaven.com/a/GothicCommode_01) | Kirill Sannikov | Storage |
| `industrial_storage_cart` | [Industrial Storage Cart](https://polyhaven.com/a/industrial_storage_cart) | Jule Bielitz | Storage |
| `modern_wooden_cabinet` | [Modern Wooden Cabinet](https://polyhaven.com/a/modern_wooden_cabinet) | Patrik Pangerl | Storage |
| `painted_wooden_cabinet` | [Painted Wooden Cabinet](https://polyhaven.com/a/painted_wooden_cabinet) | Kirill Sannikov | Storage |
| `painted_wooden_cabinet_02` | [Painted Wooden Cabinet 02](https://polyhaven.com/a/painted_wooden_cabinet_02) | Kirill Sannikov | Storage |
| `painted_wooden_shelves` | [Painted Wooden Shelves](https://polyhaven.com/a/painted_wooden_shelves) | Kirill Sannikov | Storage |
| `Shelf_01` | [Shelf 01](https://polyhaven.com/a/Shelf_01) | Gabriel Radić | Storage |
| `steel_frame_shelves_01` | [Steel Frame Shelves 01](https://polyhaven.com/a/steel_frame_shelves_01) | James Ray Cock | Storage |
| `steel_frame_shelves_02` | [Steel Frame Shelves 02](https://polyhaven.com/a/steel_frame_shelves_02) | James Ray Cock | Storage |
| `steel_frame_shelves_03` | [Steel Frame Shelves 03](https://polyhaven.com/a/steel_frame_shelves_03) | Ulan Cabanilla | Storage |
| `tool_cart` | [Tool Cart](https://polyhaven.com/a/tool_cart) | Savva Zakharov | Storage |
| `vintage_cabinet_01` | [Vintage Cabinet 01](https://polyhaven.com/a/vintage_cabinet_01) | Rico Cilliers | Storage |
| `vintage_wooden_drawer_01` | [Vintage Wooden Drawer 01](https://polyhaven.com/a/vintage_wooden_drawer_01) | James Ray Cock | Storage |
| `wooden_bookshelf_worn` | [Wooden Bookshelf Worn](https://polyhaven.com/a/wooden_bookshelf_worn) | Ulan Cabanilla | Storage |
| `wooden_display_shelves_01` | [Wooden Display Shelves 01](https://polyhaven.com/a/wooden_display_shelves_01) | James Ray Cock | Storage |
| `WoodenTable_03` | [Wooden Table 03](https://polyhaven.com/a/WoodenTable_03) | Gabriel Radić | Storage |
| `worn_metal_rack` | [Worn Metal Rack](https://polyhaven.com/a/worn_metal_rack) | Luca B | Storage |
| `chinese_console_table` | [Chinese Console Table](https://polyhaven.com/a/chinese_console_table) | Kirill Sannikov | Tables |
| `chinese_tea_table` | [Chinese Tea Table](https://polyhaven.com/a/chinese_tea_table) | Kirill Sannikov | Tables |
| `ClassicConsole_01` | [Classic Console 01](https://polyhaven.com/a/ClassicConsole_01) | Kirill Sannikov | Tables |
| `ClassicNightstand_01` | [Classic Nightstand 01](https://polyhaven.com/a/ClassicNightstand_01) | Kirill Sannikov | Tables |
| `CoffeeTable_01` | [Coffee Table 01](https://polyhaven.com/a/CoffeeTable_01) | Fernando Quinn | Tables |
| `coffee_table_round_01` | [Coffee Table Round 01](https://polyhaven.com/a/coffee_table_round_01) | Ulan Cabanilla | Tables |
| `dining_table` | [Dining Table](https://polyhaven.com/a/dining_table) | Aron Łyczek | Tables |
| `gallinera_table` | [Gallinera Table](https://polyhaven.com/a/gallinera_table) | Ulan Cabanilla | Tables |
| `gothic_coffee_table` | [Gothic Coffee Table](https://polyhaven.com/a/gothic_coffee_table) | Ulan Cabanilla | Tables |
| `industrial_coffee_table` | [Industrial Coffee Table](https://polyhaven.com/a/industrial_coffee_table) | Ulan Cabanilla | Tables |
| `metal_office_desk` | [Metal Office Desk](https://polyhaven.com/a/metal_office_desk) | Ulan Cabanilla | Tables |
| `modern_coffee_table_01` | [Modern Coffee Table 01](https://polyhaven.com/a/modern_coffee_table_01) | Amin | Tables |
| `modern_coffee_table_02` | [Modern Coffee Table 02](https://polyhaven.com/a/modern_coffee_table_02) | Amin | Tables |
| `painted_wooden_nightstand` | [Painted Wooden Nightstand](https://polyhaven.com/a/painted_wooden_nightstand) | Kirill Sannikov | Tables |
| `painted_wooden_table` | [Painted Wooden Table](https://polyhaven.com/a/painted_wooden_table) | Kirill Sannikov | Tables |
| `round_wooden_table_01` | [Round Wooden Table 01](https://polyhaven.com/a/round_wooden_table_01) | Ulan Cabanilla | Tables |
| `round_wooden_table_02` | [Round Wooden Table 02](https://polyhaven.com/a/round_wooden_table_02) | Ulan Cabanilla | Tables |
| `SchoolDesk_01` | [School Desk 01](https://polyhaven.com/a/SchoolDesk_01) | Ethan Place | Tables |
| `side_table_01` | [Side Table 01](https://polyhaven.com/a/side_table_01) | James Ray Cock | Tables |
| `side_table_tall_01` | [Side Table Tall 01](https://polyhaven.com/a/side_table_tall_01) | James Ray Cock | Tables |
| `small_wooden_table_01` | [Small Wooden Table 01](https://polyhaven.com/a/small_wooden_table_01) | Ulan Cabanilla | Tables |
| `wooden_picnic_table` | [Wooden Picnic Table](https://polyhaven.com/a/wooden_picnic_table) | Ulan Cabanilla | Tables |
| `WoodenTable_01` | [Wooden Table 01](https://polyhaven.com/a/WoodenTable_01) | Ethan Place | Tables |
| `WoodenTable_02` | [Wooden Table 02](https://polyhaven.com/a/WoodenTable_02) | Fran Calvente | Tables |
| `wooden_table_02` | [Wooden Table 02](https://polyhaven.com/a/wooden_table_02) | Serhii Khromov | Tables |
| `ornate_mirror_01` | [Ornate Mirror 01](https://polyhaven.com/a/ornate_mirror_01) | James Ray Cock | Wall Decor |
