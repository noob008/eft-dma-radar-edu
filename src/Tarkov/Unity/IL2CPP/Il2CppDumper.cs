using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using eft_dma_radar.Common.DMA.ScatterAPI;
using eft_dma_radar.Common.Misc;
using SDK;

namespace eft_dma_radar.Tarkov.Unity.IL2CPP
{
    public static class Il2CppDumper
    {
        // ── IL2CPP struct field offsets ──────────────────────────────────────────
        private const uint K_Name        = 0x10;   // char*    Il2CppClass::name
        private const uint K_Namespace   = 0x18;   // char*    Il2CppClass::namespaze
        private const uint K_Fields      = 0x80;   // FieldInfo*  (direct array)
        private const uint K_Methods     = 0x98;   // MethodInfo** (array of pointers)
        private const uint K_MethodCount = 0x120;  // uint16
        private const uint K_FieldCount  = 0x124;  // uint16

        private const uint FI_Name       = 0x00;   // char*    FieldInfo::name
        private const uint FI_Offset     = 0x18;   // int32    FieldInfo::offset  (signed!)
        private const uint FI_Stride     = 0x20;   // sizeof(FieldInfo)

        private const uint MI_Pointer    = 0x00;   // void*    MethodInfo::methodPointer
        private const uint MI_Name       = 0x18;   // char*    MethodInfo::name

        private const int  MaxClasses    = 80_000;
        private const int  MaxNameLen    = 256;

        // ── Scatter-read raw structs ─────────────────────────────────────────────

        /// <summary>
        /// Contiguous name + namespace pointers read from Il2CppClass at offset 0x10.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ClassNamePtrs
        {
            public ulong NamePtr;      // 0x10  char* name
            public ulong NamespacePtr; // 0x18  char* namespaze
        }

        /// <summary>
        /// Raw FieldInfo entry (0x20 bytes stride). Only the fields we need are mapped.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 0x20)]
        private struct RawFieldInfo
        {
            [FieldOffset(0x00)] public ulong NamePtr; // char* name
            [FieldOffset(0x18)] public int   Offset;  // int32 offset (signed!)
        }

        /// <summary>
        /// Raw MethodInfo header. We read 0x20 bytes starting at the MethodInfo address
        /// to capture the method pointer (0x00) and name pointer (0x18).
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 0x20)]
        private struct RawMethodInfo
        {
            [FieldOffset(0x00)] public ulong MethodPointer; // void* methodPointer
            [FieldOffset(0x18)] public ulong NamePtr;       // char* name
        }

        // ── Schema ───────────────────────────────────────────────────────────────

        private enum FieldKind { Normal, MethodRva }

        private readonly struct SchemaField
        {
            public readonly string    Il2CppName; // name as it appears in IL2CPP metadata
            public readonly string    CsName;     // name to emit in the output struct
            public readonly FieldKind Kind;
            public SchemaField(string il2cpp, string cs, FieldKind kind = FieldKind.Normal)
            { Il2CppName = il2cpp; CsName = cs; Kind = kind; }
        }

        private sealed class SchemaClass
        {
            public readonly string        Il2CppName; // plain class name used for name-based lookup
            public readonly string        CsName;     // struct name in generated output
            public readonly bool          IsStatic;   // emit as static class (singleton statics)
            public readonly SchemaField[] Fields;
            /// <summary>
            /// When non-null, resolves the class directly via
            ///   tablePtr + TypeIndex * 8
            /// without name-string matching. Required for obfuscated EFT classes.
            /// Obtain from Offsets.Special or by scanning the type table offline.
            /// MDToken → TypeIndex: (mdToken &amp; 0x00FFFFFF) - 1
            /// </summary>
            public readonly uint? TypeIndex;

            public SchemaClass(string il2cpp, string cs, bool isStatic, SchemaField[] fields, uint? typeIndex)
            { Il2CppName = il2cpp; CsName = cs; IsStatic = isStatic; Fields = fields; TypeIndex = typeIndex; }
        }

        // Shorthand helpers
        private static SchemaField F(string il2cpp, string cs = null)
            => new(il2cpp, cs ?? il2cpp, FieldKind.Normal);
        private static SchemaField M(string il2cpp, string cs = null)
            => new(il2cpp, cs ?? (il2cpp + "_RVA"), FieldKind.MethodRva);

        /// <param name="il2cpp">Plain IL2CPP class name (only used for name-based fallback).</param>
        /// <param name="f">Fields / methods to dump.</param>
        /// <param name="cs">Output struct name (defaults to il2cpp).</param>
        /// <param name="s">Emit as static class.</param>
        /// <param name="ti">
        /// TypeIndex for direct O(1) lookup.
        /// Set this for any class whose name is obfuscated in EFT (\uXXXX).
        /// Obtain via: (MDToken &amp; 0x00FFFFFF) - 1, or from Offsets.Special.
        /// Leave 0 to use name-based lookup (only reliable for non-obfuscated classes).
        /// </param>
        private static SchemaClass C(string il2cpp, SchemaField[] f, string cs = null, bool s = false, uint ti = 0)
            => new(il2cpp, cs ?? il2cpp, s, f, ti == 0 ? null : ti);

        private static SchemaClass[] BuildSchema() =>
        [
            // TarkovApplication
            C("TarkovApplication", [F("_menuOperation")]),

            // MainMenuShowOperation
            C("MainMenuShowOperation", [F("_afkMonitor"), F("_preloaderUI"), F("_profile")]),

            // PreloaderUI
            C("PreloaderUI", [F("_sessionIdText"), F("_alphaVersionLabel")]),

            // AFKMonitor → AfkMonitor
            C("AFKMonitor", [F("_afkTimeout", "Delay")], cs: "AfkMonitor"),

            // GameWorld (base fields)
            C("GameWorld", [
                F("GameDateTime"),
                F("<SynchronizableObjectLogicProcessor>k__BackingField", "SynchronizableObjectLogicProcessor"),
            ]),

            // GameWorld → ClientLocalGameWorld (extended fields from same IL2CPP class)
            C("GameWorld", [
                F("<BtrController>k__BackingField", "BtrController"),
                F("<TransitController>k__BackingField", "TransitController"),
                F("<ExfiltrationController>k__BackingField", "ExfilController"),
                F("<ClientShellingController>k__BackingField", "ClientShellingController"),
                F("<LocationId>k__BackingField", "LocationId"),
                F("LootList"),
                F("RegisteredPlayers"),
                F("BorderZones"),
                F("MainPlayer"),
                F("_world", "World"),
                F("<SynchronizableObjectLogicProcessor>k__BackingField", "SynchronizableObjectLogicProcessor"),
                F("Grenades"),
            ], cs: "ClientLocalGameWorld"),

            // TransitController
            C("TransitController", [F("pointsById", "TransitPoints")]),

            // ArtilleryShellingControllerClient → ClientShellingController
            C("ArtilleryShellingControllerClient", [F("ActiveClientProjectiles")], cs: "ClientShellingController"),

            // World_2 → WorldController
            C("World_2", [F("_interactables", "Interactables")], cs: "WorldController"),

            // WorldInteractiveObject → Interactable
            C("WorldInteractiveObject", [F("KeyId"), F("Id"), F("_doorState")], cs: "Interactable"),

            // ArtilleryProjectileClient
            C("ArtilleryProjectileClient", [F("_targetPosition", "Position"), F("_flyOn", "IsActive")]),

            // TransitPoint
            C("TransitPoint", [F("parameters")]),

            // TransitParameters
            C("TransitParameters", [F("id"), F("active"), F("name"), F("description"), F("target"), F("location")]),

            // SynchronizableObject
            C("SynchronizableObject", [F("Type")]),

            // SynchronizableObjectLogicProcessor
            C("SynchronizableObjectLogicProcessor", [F("_activeSynchronizableObjects")]),

            // TripwireSynchronizableObject
            C("TripwireSynchronizableObject", [
                F("<GrenadeTemplateId>k__BackingField", "GrenadeTemplateId"),
                F("_tripwireState"),
                F("<FromPosition>k__BackingField", "FromPosition"),
                F("<ToPosition>k__BackingField", "ToPosition"),
            ]),

            // BtrController
            C("BtrController", [F("<BtrView>k__BackingField", "BtrView")]),

            // BTRView
            C("BTRView", [F("turret"), F("_previousPosition")]),

            // BTRTurretView
            C("BTRTurretView", [F("_bot", "AttachedBot")]),

            // EffectsController
            C("EffectsController", [
                F("_effectsPrefab"),
                F("FastVineteFlicker"),
                F("<RainScreenDrops>k__BackingField", "RainScreenDrops"),
                F("<ScreenWater>k__BackingField", "ScreenWater"),
                F("_vignette"),
                F("_doubleVision"),
                F("_hueFocus"),
                F("_radialBlur"),
                F("_sharpen"),
                F("_lowhHealthBlend"),
                F("_bloodlossBlend"),
                F("_wiggle"),
                F("_motionBluer"),
                F("_bloodOnScreen"),
                F("_grenadeFlash"),
                F("_eyeBurn"),
                F("_blur"),
                F("_dof"),
                F("_effectAccumulators"),
                F("_sharpenAccumulator"),
                F("_radialBlurAccumulator"),
                F("_chromaticAberration"),
                F("_thermalVision"),
                F("_frostbiteEffect"),
            ]),

            // FrostbiteEffect
            C("FrostbiteEffect", [F("_opacity")]),

            // NightVision
            C("NightVision", [F("_on")]),

            // ThermalVision
            C("ThermalVision", [
                F("_material", "Material"), F("On"), F("IsNoisy"), F("IsFpsStuck"), F("IsMotionBlurred"),
                F("IsGlitch"), F("IsPixelated"), F("ChromaticAberrationThermalShift"),
                F("UnsharpRadiusBlur"), F("UnsharpBias"),
            ]),

            // HealthInfo → HealthController
            C("HealthInfo", [F("Energy"), F("Hydration")], cs: "HealthController"),

            // ExfiltrationController → ExfilController
            C("ExfiltrationController", [
                F("<ExfiltrationPoints>k__BackingField", "ExfiltrationPointArray"),
                F("<ScavExfiltrationPoints>k__BackingField", "ScavExfiltrationPointArray"),
                F("<SecretExfiltrationPoints>k__BackingField", "SecretExfiltrationPointArray"),
            ], cs: "ExfilController"),

            // ExfiltrationPoint → Exfil
            C("ExfiltrationPoint", [F("_status"), F("Settings"), F("EligibleEntryPoints")], cs: "Exfil"),

            // ScavExfiltrationPoint → ScavExfil
            C("ScavExfiltrationPoint", [F("EligibleIds")], cs: "ScavExfil"),

            // ExitTriggerSettings → ExfilSettings
            C("ExitTriggerSettings", [F("Name")], cs: "ExfilSettings"),

            // Grenade (fields from Grenade class)
            C("Grenade", [F("<WeaponSource>k__BackingField", "WeaponSource")], cs: "Grenade"),

            // Throwable (fields from Throwable class → same output struct Grenade)
            C("Throwable", [F("_isDestroyed", "IsDestroyed")], cs: "Grenade"),

            // Player
            C("Player", [
                F("_characterController"),
                F("<MovementContext>k__BackingField", "MovementContext"),
                F("_playerBody"),
                F("<ProceduralWeaponAnimation>k__BackingField", "ProceduralWeaponAnimation"),
                F("_animators"),
                F("EnabledAnimators"),
                F("Corpse"),
                F("<Location>k__BackingField", "Location"),
                F("<InteractableObject>k__BackingField", "InteractableObject"),
                F("<Profile>k__BackingField", "Profile"),
                F("Physical"),
                F("<AIData>k__BackingField", "AIData"),
                F("_healthController"),
                F("_inventoryController"),
                F("_handsController"),
                F("<InteractionRayOriginOnStartOperation>k__BackingField", "InteractionRayOriginOnStartOperation"),
                F("<InteractionRayDirectionOnStartOperation>k__BackingField", "InteractionRayDirectionOnStartOperation"),
                F("<IsYourPlayer>k__BackingField", "IsYourPlayer"),
                F("<VoipID>k__BackingField", "VoipID"),
                F("<PlayerId>k__BackingField", "Id"),
                F("<GameWorld>k__BackingField", "GameWorld"),
            ]),

            // ObservedPlayerView
            C("ObservedPlayerView", [
                F("<ObservedPlayerController>k__BackingField", "ObservedPlayerController"),
                F("<Voice>k__BackingField", "Voice"),
                F("<VisibleToCameraType>k__BackingField", "VisibleToCameraType"),
                F("<GroupId>k__BackingField", "GroupID"),
                F("<Side>k__BackingField", "Side"),
                F("<IsAI>k__BackingField", "IsAI"),
                F("<NickName>k__BackingField", "NickName"),
                F("<AccountId>k__BackingField", "AccountId"),
                F("<PlayerBody>k__BackingField", "PlayerBody"),
                F("<Id>k__BackingField", "Id"),
                F("<VoipID>k__BackingField", "VoipId"),
            ]),

            // ObservedPlayerController
            C("ObservedPlayerController", [
                F("<InventoryController>k__BackingField", "InventoryController"),
                F("<PlayerView>k__BackingField", "Player"),
                F("<InfoContainer>k__BackingField", "InfoContainer"),
                F("<MovementController>k__BackingField", "MovementController"),
                F("<HealthController>k__BackingField", "HealthController"),
                F("<HandsController>k__BackingField", "HandsController"),
            ]),

            // ObservedPlayerStateContext → ObservedMovementController
            C("ObservedPlayerStateContext", [
                F("<Rotation>k__BackingField", "Rotation"),
                F("_velocity", "Velocity"),
            ], cs: "ObservedMovementController"),

            // ObservedPlayerHandsController → ObservedHandsController
            C("ObservedPlayerHandsController", [
                F("_item", "ItemInHands"),
                F("_bundleAnimationBones", "BundleAnimationBones"),
            ], cs: "ObservedHandsController"),

            // BundleAnimationBones → BundleAnimationBonesController
            C("BundleAnimationBones", [
                F("<ProceduralWeaponAnimation>k__BackingField", "ProceduralWeaponAnimationObs"),
            ], cs: "BundleAnimationBonesController"),

            // ProceduralWeaponAnimation → ProceduralWeaponAnimationObs (observed _isAiming)
            C("ProceduralWeaponAnimation", [
                F("_isAiming", "_isAimingObs"),
            ], cs: "ProceduralWeaponAnimationObs"),

            // ObservedPlayerHealthController → ObservedHealthController
            C("ObservedPlayerHealthController", [
                F("_player", "Player"),
                F("_playerCorpse", "PlayerCorpse"),
                F("HealthStatus"),
            ], cs: "ObservedHealthController"),

            // ProceduralWeaponAnimation (main)
            C("ProceduralWeaponAnimation", [
                F("<ShotNeedsFovAdjustments>k__BackingField", "ShotNeedsFovAdjustments"),
                F("Breath"),
                F("PositionZeroSum"),
                F("Shootingg"),
                F("_aimingSpeed"),
                F("_isAiming"),
                F("_optics"),
                F("_shotDirection"),
                F("Mask"),
                F("HandsContainer"),
                F("_fovCompensatoryDistance"),
            ]),

            // PlayerSpring → HandsContainer
            C("PlayerSpring", [
                F("CameraOffset"),
                F("HandsRotation"),
                F("CameraRotation"),
                F("CameraPosition"),
            ], cs: "HandsContainer"),

            // SightNBone
            C("SightNBone", [F("Mod")]),

            // ShotEffector
            C("ShotEffector", [F("NewShotRecoil")]),

            // PlayerStateContainer
            C("PlayerStateContainer", [F("Name"), F("StateFullNameHash")]),

            // NewRecoilShotEffect → NewShotRecoil
            C("NewRecoilShotEffect", [F("IntensitySeparateFactors")], cs: "NewShotRecoil"),

            // VisorEffect
            C("VisorEffect", [F("Intensity")]),

            // TOD_Time
            C("TOD_Time", [F("LockCurrentTime")]),

            // TOD_CycleParameters
            C("TOD_CycleParameters", [F("Hour")]),

            // TOD_ImageEffect → TOD_Scattering
            C("TOD_ImageEffect", [F("_sky", "Sky")], cs: "TOD_Scattering"),

            // TOD_Sky
            C("TOD_Sky", [
                F("<Cycle>k__BackingField", "Cycle"),
                F("<Components>k__BackingField", "TOD_Components"),
            ]),

            // TOD_Components
            C("TOD_Components", [F("<Time>k__BackingField", "TOD_Time")]),

            // Profile
            C("Profile", [
                F("Id"), F("AccountId"), F("Info"), F("Inventory"), F("Skills"),
                F("TaskConditionCounters"), F("QuestsData"), F("WishlistManager"), F("Stats"),
            ]),

            // WishlistManager
            C("WishlistManager", [F("_userItems", "Items")]),

            // ProfileInfo → PlayerInfo
            C("ProfileInfo", [
                F("Nickname"), F("EntryPoint"), F("<Side>k__BackingField", "Side"), F("RegistrationDate"),
                F("GroupId"), F("<Settings>k__BackingField", "Settings"), F("MemberCategory"), F("_experience", "Experience"),
            ], cs: "PlayerInfo"),

            // SkillManager
            C("SkillManager", [
                F("StrengthBuffJumpHeightInc"), F("StrengthBuffThrowDistanceInc"),
                F("MagDrillsLoadSpeed"), F("MagDrillsUnloadSpeed"),
                F("RaidLoadedAmmoAction"), F("RaidUnloadedAmmoAction"),
            ]),

            // FloatBuff → SkillValueContainer
            C("FloatBuff", [F("Value")], cs: "SkillValueContainer"),

            // QuestStatusData → QuestData
            C("QuestStatusData", [F("Id"), F("Status"), F("CompletedConditions"), F("Template")], cs: "QuestData"),

            // CompletedConditionsCollection
            C("CompletedConditionsCollection", [
                F("_backendData", "BackendData"),
                F("_localChanges", "LocalChanges"),
            ]),

            // QuestTemplate
            C("QuestTemplate", [
                F("<Conditions>k__BackingField", "Conditions"),
                F("_questName", "Name"),
            ]),

            // ItemHandsController
            C("ItemHandsController", [F("_item", "Item")]),

            // FirearmController
            C("FirearmController", [F("Fireport"), F("COI", "TotalCenterOfImpact"), F("WeaponLn")]),

            // ClientFirearmController (fields from ClientFirearmController + inherited FirearmController)
            C("FirearmController", [F("WeaponLn")], cs: "ClientFirearmController"),
            C("ClientFirearmController", [F("LastShotId", "ShotIndex")], cs: "ClientFirearmController"),

            // MovementContext
            C("MovementContext", [
                F("_player", "Player"),
                F("_rotation"),
                F("PlantState"),
                F("<CurrentState>k__BackingField", "CurrentState"),
                F("_states"),
                F("_movementStates"),
                F("_tilt"),
                F("_physicalCondition"),
                F("_speedLimitIsDirty"),
                F("<StateSpeedLimit>k__BackingField", "StateSpeedLimit"),
                F("<StateSprintSpeedLimit>k__BackingField", "StateSprintSpeedLimit"),
                F("_lookDirection"),
                F("<WalkInertia>k__BackingField", "WalkInertia"),
                F("<SprintBrakeInertia>k__BackingField", "SprintBrakeInertia"),
                F("_poseInertia"),
                F("_currentPoseInertia"),
                F("_inertiaAppliedTime"),
            ]),

            // MovementState (from MovementState class)
            C("MovementState", [F("StickToGround"), F("PlantTime")], cs: "MovementState"),

            // BaseMovementState (from BaseMovementState class → same output)
            C("BaseMovementState", [F("Name"), F("AnimatorStateHash"), F("AuthoritySpeed")], cs: "MovementState"),

            // MovePlayerState (from MovePlayerState class → same output)
            C("MovePlayerState", [F("_velocity"), F("_velocity2")], cs: "MovementState"),

            // InventoryController
            C("InventoryController", [F("<Inventory>k__BackingField", "Inventory")]),

            // Inventory
            C("Inventory", [F("Equipment"), F("QuestRaidItems"), F("QuestStashItems"), F("Stash")]),

            // Stash
            C("Stash", [F("_grid", "Grids")]),

            // CompoundItem → Stash (Slots from CompoundItem, same output Stash)
            C("CompoundItem", [F("Slots")], cs: "Stash"),

            // CompoundItem → Equipment
            C("CompoundItem", [F("Grids"), F("Slots")], cs: "Equipment"),

            // BarterOther → BarterOtherOffsets
            C("BarterOther", [F("Dogtag")], cs: "BarterOtherOffsets"),

            // DogtagComponent
            C("DogtagComponent", [
                F("GroupId"), F("AccountId"), F("ProfileId"), F("Nickname"),
                F("Side"), F("Level"), F("Time"), F("Status"), F("KillerAccountId"),
                F("KillerProfileId"), F("KillerName"), F("WeaponName"), F("CarriedByGroupMember"),
            ]),

            // Grid → Grids
            C("Grid", [F("<ItemCollection>k__BackingField", "ContainedItems")], cs: "Grids"),

            // GridItemCollection → GridContainedItems
            C("GridItemCollection", [F("ItemsList", "Items")], cs: "GridContainedItems"),

            // Slot
            C("Slot", [
                F("<ContainedItem>k__BackingField", "ContainedItem"),
                F("<ID>k__BackingField", "ID"),
                F("Required"),
            ]),

            // LootItem → InteractiveLootItem
            C("LootItem", [F("_item", "Item")], cs: "InteractiveLootItem"),

            // Skeleton → DizSkinningSkeleton
            C("Skeleton", [F("_values")], cs: "DizSkinningSkeleton"),

            // LootableContainer (fields from LootableContainer class)
            C("LootableContainer", [F("ItemOwner"), F("Template")], cs: "LootableContainer"),

            // WorldInteractiveObject (fields inherited → same output LootableContainer)
            C("WorldInteractiveObject", [
                F("<InteractingPlayer>k__BackingField", "InteractingPlayer"),
            ], cs: "LootableContainer"),

            // ItemController → LootableContainerItemOwner
            C("ItemController", [F("<RootItem>k__BackingField", "RootItem")], cs: "LootableContainerItemOwner"),

            // Item → LootItem
            C("Item", [
                F("StackObjectsCount"), F("Version"), F("Components"), F("<Template>k__BackingField", "Template"), F("<SpawnedInSession>k__BackingField", "SpawnedInSession"),
            ], cs: "LootItem"),

            // CompoundItem → LootItemMod
            C("CompoundItem", [F("Grids"), F("Slots")], cs: "LootItemMod"),

            // Grid → Grid
            C("Grid", [F("<ItemCollection>k__BackingField", "ItemCollection")], cs: "Grid"),

            // GridItemCollection → GridItemCollection
            C("GridItemCollection", [F("ItemsList")], cs: "GridItemCollection"),

            // Weapon → LootItemWeapon
            C("Weapon", [
                F("FireMode"),
                F("<Chambers>k__BackingField", "Chambers"),
                F("_magSlotCache"),
            ], cs: "LootItemWeapon"),

            // LevelSettings
            C("LevelSettings", [F("AmbientMode"), F("EquatorColor"), F("GroundColor")]),

            // SlotView_2 → PlayerBodySubclass
            C("SlotView_2", [F("Dresses")], cs: "PlayerBodySubclass"),

            // Dress
            C("Dress", [F("Renderers")]),

            // EFTHardSettings (singleton with TypeIndex)
            C("EFTHardSettings", [
                F("POSE_CHANGING_SPEED"),
                F("_instance"),
                F("MED_EFFECT_USING_PANEL"),
                F("MOUSE_LOOK_HORIZONTAL_LIMIT"),
                F("MOUSE_LOOK_LIMIT_IN_AIMING_COEF"),
                F("MOUSE_LOOK_VERTICAL_LIMIT"),
                F("ABOVE_OR_BELOW"),
                F("ABOVE_OR_BELOW_STAIRS"),
                F("AIM_PROCEDURAL_INTENSITY"),
                F("AIR_CONTROL_BACK_DIR"),
                F("AIR_CONTROL_NONE_OR_ORT_DIR"),
                F("AIR_CONTROL_SAME_DIR"),
                F("AIR_LERP"),
                F("AIR_MIN_SPEED"),
                F("DecelerationSpeed"),
                F("WEAPON_OCCLUSION_LAYERS"),
                F("DOOR_RAYCAST_DISTANCE"),
                F("LOOT_RAYCAST_DISTANCE"),
            ], s: true, ti: Offsets.Special.EFTHardSettings_TypeIndex),

            // GPUInstancerManager (singleton with TypeIndex)
            C("GPUInstancerManager", [
                F("Instance"),
                F("runtimeDataList"),
            ], s: true, ti: Offsets.Special.GPUInstancerManager_TypeIndex),

            // ClientBackendSession
            C("ClientBackendSession", [F("<BackEndConfig>k__BackingField", "BackEndConfig")]),

            // FireModeComponent
            C("FireModeComponent", [F("FireMode")]),

            // MagazineTemplate → LootItemMagazine
            C("MagazineTemplate", [F("Cartridges"), F("LoadUnloadModifier")], cs: "LootItemMagazine"),

            // Item → MagazineClass
            C("Item", [F("StackObjectsCount")], cs: "MagazineClass"),

            // StackSlot
            C("StackSlot", [F("_items"), F("MaxCount")]),

            // ItemTemplate
            C("ItemTemplate", [F("Name"), F("ShortName"), F("<_id>k__BackingField", "_id"), F("Weight"), F("QuestItem")]),

            // ModTemplate
            C("ModTemplate", [F("Velocity")]),

            // AmmoTemplate
            C("AmmoTemplate", [
                F("InitialSpeed"), F("BallisticCoeficient"), F("BulletMassGram"), F("BulletDiameterMilimeters"),
            ]),

            // WeaponTemplate
            C("WeaponTemplate", [
                F("Velocity"), F("AllowJam"), F("AllowFeed"), F("AllowMisfire"), F("AllowSlide"),
            ]),

            // PlayerBody
            C("PlayerBody", [
                F("SkeletonRootJoint"), F("BodySkins"), F("_bodyRenderers"), F("SlotViews"), F("PointOfView"),
            ]),

            // InventoryBlur
            C("InventoryBlur", [F("_blurCount"), F("_upsampleTexDimension")]),

            // Physical
            C("PhysicalBase", [
                F("Overweight"), F("WalkOverweight"), F("WalkSpeedLimit"), F("Inertia"),
                F("Stamina"), F("Oxygen"), F("BaseOverweightLimits"), F("SprintOverweightLimits"),
                F("PreviousWeight"), F("SprintAcceleration"), F("PreSprintAcceleration"),
                F("_encumbered"), F("_overEncumbered"), F("SprintOverweight"), F("<BerserkRestorationFactor>k__BackingField", "BerserkRestorationFactor"),
            ], cs: "Physical"),

            // Stamina → PhysicalValue
            C("Stamina", [F("Current")], cs: "PhysicalValue"),

            // BreathEffector
            C("BreathEffector", [F("Intensity")]),

            // OpticCameraManager
            C("OpticCameraManager", [F("<Camera>k__BackingField", "Camera"), F("<CurrentOpticSight>k__BackingField", "CurrentOpticSight")]),

            // GPUInstancerRuntimeData
            C("GPUInstancerRuntimeData", [F("instanceBounds")]),

            // CameraManager → EFTCameraManager
            C("CameraManager", [
                F("<OpticCameraManager>k__BackingField", "OpticCameraManager"),
                F("<Camera>k__BackingField", "Camera"),
                M("get_Instance_RVA", "GetInstance_RVA"),
            ], cs: "EFTCameraManager"),

            // SightComponent
            C("SightComponent", [
                F("_template"), F("ScopesSelectedModes"), F("SelectedScope"), F("ScopeZoomValue"),
            ]),

            // SightModTemplate → SightInterface
            C("SightModTemplate", [F("Zooms")], cs: "SightInterface"),

            // WeatherController (instance fields + static Instance, with TypeIndex)
            C("WeatherController", [F("Instance"), F("WeatherDebug")], s: true, ti: Offsets.Special.WeatherController_TypeIndex),

            // WeatherDebug
            C("WeatherDebug", [
                F("CloudDensity"), F("Fog"), F("LightningThunderProbability"),
                F("Rain"), F("WindMagnitude"), F("isEnabled"),
            ]),
        ];

        // ── IL2CPP bootstrap resolution ─────────────────────────────────────────

        /// <summary>
        /// Candidate signatures for locating the TypeInfoTable global store.
        /// Each entry: (signature, rel32 offset from match start, instruction length for RIP calc, description).
        /// All patterns target a <c>mov [rip+xxxx], rax</c> or <c>lea reg, [rip+xxxx]</c>
        /// instruction that references the <c>s_Il2CppMetadataRegistration→typeInfoTable</c> global.
        /// </summary>
        private static readonly (string Sig, int RelOffset, int InstrLen, string Desc)[] TypeInfoTableSigs =
        [
            // Pattern 1 (strict): mov [rip+xxxx], rax; mov rax, [rip+yyyy]; mov edx/ecx, [rax+0x30]
            ("48 89 05 ? ? ? ? 48 8B 05 ? ? ? ? 8B 50", 3, 7, "mov [rip+rel32],rax; mov rax,[rip+rel32]; mov edx,[rax+30]"),

            // Pattern 2 (relaxed tail): mov [rip+xxxx], rax; mov rax, [rip+yyyy]
            ("48 89 05 ? ? ? ? 48 8B 05 ? ? ? ?", 3, 7, "mov [rip+rel32],rax; mov rax,[rip+rel32]"),

            // Pattern 3: mov [rip+xxxx], rax; followed by xor ecx,ecx (common in newer IL2CPP builds)
            ("48 89 05 ? ? ? ? 33 C9", 3, 7, "mov [rip+rel32],rax; xor ecx,ecx"),

            // Pattern 4: mov [rip+xxxx], rax; followed by any mov reg,imm or test
            ("48 89 05 ? ? ? ? 48 85", 3, 7, "mov [rip+rel32],rax; test reg,reg"),
        ];

        /// <summary>
        /// Signature-scans GameAssembly.dll for the TypeInfoTable global and
        /// updates <see cref="Offsets.Special.TypeInfoTableRva"/> at runtime.
        /// Tries multiple signature patterns and validates the result by probing
        /// the resolved table for plausible class pointers.
        /// Falls back to the hardcoded value in SDK.cs if all strategies fail.
        /// </summary>
        private static bool ResolveTypeInfoTableRva(ulong gaBase)
        {
            XMLogging.WriteLine("[Il2CppDumper] Scanning for TypeInfoTable...");

            // Strategy 1–N: try each signature pattern in order.
            foreach (var (sig, relOff, instrLen, desc) in TypeInfoTableSigs)
            {
                var sigAddr = Memory.FindSignature(sig, "GameAssembly.dll");
                if (sigAddr == 0)
                    continue;

                var rva = ResolveRipRelativeRva(sigAddr, relOff, instrLen, gaBase);
                if (rva == 0)
                    continue;

                if (ValidateTypeInfoTable(gaBase, rva))
                {
                    var previous = Offsets.Special.TypeInfoTableRva;
                    Offsets.Special.TypeInfoTableRva = rva;

                    XMLogging.WriteLine($"[Il2CppDumper] TypeInfoTable resolved via: {desc}");
                    XMLogging.WriteLine($"        RVA = 0x{rva:X}");

                    if (previous != rva)
                        XMLogging.WriteLine($"[Il2CppDumper] TypeInfoTableRva updated: 0x{previous:X} → 0x{rva:X}");

                    return true;
                }

                XMLogging.WriteLine($"[Il2CppDumper] Sig matched ({desc}) but validation failed at RVA 0x{rva:X} — trying next pattern.");
            }

            // All signatures failed — validate the hardcoded fallback.
            XMLogging.WriteLine("[Il2CppDumper] All sig scans failed. Validating hardcoded fallback...");

            if (Offsets.Special.TypeInfoTableRva != 0 && ValidateTypeInfoTable(gaBase, Offsets.Special.TypeInfoTableRva))
            {
                XMLogging.WriteLine($"[Il2CppDumper] Hardcoded TypeInfoTableRva 0x{Offsets.Special.TypeInfoTableRva:X} passed validation.");
                return true;
            }

            XMLogging.WriteLine("[Il2CppDumper] WARNING: All TypeInfoTable resolution strategies failed — offsets may be stale!");
            return false;
        }

        /// <summary>
        /// Reads a RIP-relative <c>int32</c> displacement from a matched signature
        /// and computes the target RVA relative to <paramref name="gaBase"/>.
        /// </summary>
        private static ulong ResolveRipRelativeRva(ulong sigAddr, int relOffset, int instrLen, ulong gaBase)
        {
            int rel;
            try { rel = Memory.ReadValue<int>(sigAddr + (ulong)relOffset, false); }
            catch { return 0; }

            ulong globalVa = sigAddr + (ulong)instrLen + (ulong)(long)rel;

            // Basic sanity: the resolved VA must be inside GameAssembly's address space.
            if (globalVa <= gaBase)
                return 0;

            return globalVa - gaBase;
        }

        /// <summary>
        /// Validates a candidate TypeInfoTable RVA by probing the first few entries.
        /// A valid table has non-null class pointers whose <c>Il2CppClass::name</c>
        /// fields point to readable ASCII strings.
        /// </summary>
        private static bool ValidateTypeInfoTable(ulong gaBase, ulong rva)
        {
            ulong tablePtr;
            try { tablePtr = Memory.ReadPtr(gaBase + rva, false); }
            catch { return false; }

            if (!tablePtr.IsValidVirtualAddress())
                return false;

            // Probe a handful of early entries — at least some must look like valid Il2CppClass*.
            const int probeCount = 8;
            const int requiredValid = 3;
            int valid = 0;

            ulong[] ptrs;
            try { ptrs = Memory.ReadArray<ulong>(tablePtr, probeCount, false); }
            catch { return false; }

            for (int i = 0; i < ptrs.Length; i++)
            {
                if (!ptrs[i].IsValidVirtualAddress())
                    continue;

                // Read Il2CppClass::name pointer (offset 0x10) and check it's a readable string.
                ulong namePtr;
                try { namePtr = Memory.ReadValue<ulong>(ptrs[i] + K_Name, false); }
                catch { continue; }

                if (!namePtr.IsValidVirtualAddress())
                    continue;

                var name = ReadStr(namePtr);
                if (!string.IsNullOrEmpty(name) && name.Length < MaxNameLen && IsPlausibleClassName(name))
                    valid++;

                if (valid >= requiredValid)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks whether a string looks like a plausible IL2CPP class name
        /// (printable ASCII or common Unicode escape, no control chars).
        /// </summary>
        private static bool IsPlausibleClassName(string name)
        {
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                // Allow printable ASCII, common C# identifier chars, and IL2CPP unicode escapes
                if (c < 0x20 || (c > 0x7E && c < 0xA0))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Maps IL2CPP class names → <see cref="Offsets.Special"/> TypeIndex field names.
        /// Add entries here when new singleton classes need TypeIndex resolution.
        /// </summary>
        private static readonly (string Il2CppName, string FieldName)[] TypeIndexMap =
        [
            ("EFTHardSettings",     nameof(Offsets.Special.EFTHardSettings_TypeIndex)),
            ("GPUInstancerManager", nameof(Offsets.Special.GPUInstancerManager_TypeIndex)),
            ("WeatherController",   nameof(Offsets.Special.WeatherController_TypeIndex)),
            ("GlobalConfiguration", nameof(Offsets.Special.GlobalConfiguration_TypeIndex)),
        ];

        /// <summary>
        /// Looks up known singleton class names in the scanned type table and
        /// updates <see cref="Offsets.Special"/> TypeIndex fields dynamically.
        /// Falls back to hardcoded values for any class not found.
        /// </summary>
        private static void ResolveTypeIndices(Dictionary<string, int> nameToIndex)
        {
            var specialType = typeof(Offsets.Special);
            const BindingFlags bf = BindingFlags.Public | BindingFlags.Static;

            foreach (var (il2cppName, fieldName) in TypeIndexMap)
            {
                var fi = specialType.GetField(fieldName, bf);
                if (fi is null)
                    continue;

                if (nameToIndex.TryGetValue(il2cppName, out var index))
                {
                    var previous = (uint)fi.GetValue(null);
                    fi.SetValue(null, (uint)index);

                    if (previous != (uint)index)
                        XMLogging.WriteLine($"[Il2CppDumper] {fieldName} updated: {previous} → {index}");
                    else
                        XMLogging.WriteLine($"[Il2CppDumper] {fieldName} matches hardcoded value ({index}).");
                }
                else
                {
                    XMLogging.WriteLine($"[Il2CppDumper] WARN: '{il2cppName}' not found in type table — {fieldName} using fallback ({fi.GetValue(null)}).");
                }
            }
        }

        /// <summary>
        /// Resolves IL2CPP offsets at runtime and applies them to
        /// <see cref="Offsets"/> via reflection. Hardcoded defaults in SDK.cs
        /// serve as fallback for any field that cannot be resolved.
        /// </summary>
        public static void Dump()
        {
            XMLogging.WriteLine("[Il2CppDumper] Dump starting...");

            var gaBase = Memory.GameAssemblyBase;
            if (gaBase == 0)
            {
                XMLogging.WriteLine("[Il2CppDumper] ERROR: GameAssemblyBase is 0 — game not ready.");
                return;
            }

            // Dynamically resolve TypeInfoTableRva via sig scan (falls back to hardcoded).
            ResolveTypeInfoTableRva(gaBase);

            // Resolve the type-info table pointer once — used by both paths.
            ulong tablePtr;
            try { tablePtr = Memory.ReadPtr(gaBase + Offsets.Special.TypeInfoTableRva, false); }
            catch (Exception ex)
            {
                XMLogging.WriteLine($"[Il2CppDumper] ReadPtr(TypeInfoTableRva) failed: {ex.Message}");
                return;
            }

            if (!tablePtr.IsValidVirtualAddress())
            {
                XMLogging.WriteLine("[Il2CppDumper] TypeInfoTable pointer is invalid.");
                return;
            }

            // Scan the full type table — needed for name lookups AND TypeIndex resolution.
            var classes = ReadAllClassesFromTable(tablePtr);
            XMLogging.WriteLine($"[Il2CppDumper] Type table: {classes.Count} classes found.");

            var nameLookup = new Dictionary<string, ulong>(classes.Count, StringComparer.Ordinal);
            var nameToIndex = new Dictionary<string, int>(classes.Count, StringComparer.Ordinal);
            foreach (var (name, _, ptr, idx) in classes)
            {
                nameLookup.TryAdd(name, ptr);
                nameToIndex.TryAdd(name, idx);
            }

            // Dynamically resolve TypeIndex values for known singleton classes.
            ResolveTypeIndices(nameToIndex);

            // Build schema AFTER TypeIndex resolution so it picks up updated values.
            var schema = BuildSchema();

            // Reflection: locate nested types inside Offsets once.
            var offsetsType = typeof(Offsets);
            const BindingFlags bf = BindingFlags.Public | BindingFlags.Static;

            int updated = 0, fallback = 0, classesSkipped = 0;

            foreach (var sc in schema)
            {
                ulong klassPtr;
                string resolvedVia;

                if (sc.TypeIndex.HasValue)
                {
                    klassPtr = ReadPtr(tablePtr + (ulong)sc.TypeIndex.Value * 8UL);
                    resolvedVia = $"TypeIndex={sc.TypeIndex.Value}";

                    if (!klassPtr.IsValidVirtualAddress())
                    {
                        XMLogging.WriteLine($"[Il2CppDumper] SKIP '{sc.CsName}': TypeIndex={sc.TypeIndex.Value} resolved to invalid pointer.");
                        classesSkipped++;
                        continue;
                    }
                }
                else
                {
                    if (!nameLookup.TryGetValue(sc.Il2CppName, out klassPtr))
                    {
                        XMLogging.WriteLine($"[Il2CppDumper] SKIP '{sc.Il2CppName}': not found in type table.");
                        classesSkipped++;
                        continue;
                    }
                    resolvedVia = $"name=\"{sc.Il2CppName}\"";
                }

                // Find the target struct in Offsets via reflection.
                var nestedType = offsetsType.GetNestedType(sc.CsName, BindingFlags.Public | BindingFlags.NonPublic);
                if (nestedType is null)
                {
                    XMLogging.WriteLine($"[Il2CppDumper] WARN: struct Offsets.{sc.CsName} not found via reflection — skipping.");
                    classesSkipped++;
                    continue;
                }

                var fieldMap  = ReadClassFields(klassPtr);
                var methodMap = sc.Fields.Any(sf => sf.Kind == FieldKind.MethodRva)
                    ? ReadClassMethods(klassPtr, gaBase)
                    : null;

                // ── Verbose dump log (comment out to silence) ────────────────
                //LogClassDump(sc, resolvedVia, fieldMap, methodMap);
                // ─────────────────────────────────────────────────────────────

                foreach (var sf in sc.Fields)
                {
                    if (sf.Kind == FieldKind.MethodRva)
                    {
                        var methodName = sf.Il2CppName.EndsWith("_RVA", StringComparison.Ordinal)
                            ? sf.Il2CppName[..^4]
                            : sf.Il2CppName;

                        if (methodMap is not null && methodMap.TryGetValue(methodName, out var rva))
                        {
                            if (TrySetField(nestedType, sf.CsName, rva, bf))
                                updated++;
                            else
                                fallback++;
                        }
                        else
                        {
                            XMLogging.WriteLine($"[Il2CppDumper] WARN: method '{methodName}' not found in '{sc.CsName}' — using fallback.");
                            fallback++;
                        }
                    }
                    else
                    {
                        if (!fieldMap.TryGetValue(sf.Il2CppName, out var offset))
                        {
                            var alt = FlipBackingFieldConvention(sf.Il2CppName);
                            if (alt is null || !fieldMap.TryGetValue(alt, out offset))
                            {
                                XMLogging.WriteLine($"[Il2CppDumper] WARN: field '{sf.Il2CppName}' not found in '{sc.CsName}' — using fallback.");
                                fallback++;
                                continue;
                            }
                        }

                        // FieldInfo::offset is signed. Positive → uint, negative → int.
                        object value = offset >= 0 ? (object)(uint)offset : (object)offset;
                        if (TrySetField(nestedType, sf.CsName, value, bf))
                            updated++;
                        else
                            fallback++;
                    }
                }
            }

            XMLogging.WriteLine($"[Il2CppDumper] Done. {updated} offsets updated from dump, {fallback} using hardcoded fallback, {classesSkipped} classes skipped.");
        }

        /// <summary>
        /// Attempts to set a static field on a type via reflection.
        /// Handles uint/ulong/int type conversion automatically.
        /// For const fields (IsLiteral), skips silently (cannot set at runtime).
        /// For uint[] fields (deref chains), updates only the first element.
        /// </summary>
        private static bool TrySetField(Type type, string fieldName, object value, BindingFlags bf)
        {
            var fi = type.GetField(fieldName, bf);
            if (fi is null)
            {
                XMLogging.WriteLine($"[Il2CppDumper] WARN: field '{fieldName}' not found on '{type.Name}' via reflection.");
                return false;
            }

            // const (literal) fields cannot be changed at runtime — skip silently.
            if (fi.IsLiteral)
                return true;

            try
            {
                // Convert the dumped value to the declared field type.
                var target = fi.FieldType;
                object converted;

                if (target == typeof(uint))
                    converted = Convert.ToUInt32(value);
                else if (target == typeof(ulong))
                    converted = Convert.ToUInt64(value);
                else if (target == typeof(int))
                    converted = Convert.ToInt32(value);
                else if (target == typeof(uint[]))
                {
                    // Deref-chain field: update only the first element with the dumped offset.
                    var arr = (uint[])fi.GetValue(null);
                    if (arr is not null && arr.Length > 0)
                    {
                        arr[0] = Convert.ToUInt32(value);
                        return true; // array is reference type — already mutated in place
                    }
                    return false;
                }
                else
                {
                    XMLogging.WriteLine($"[Il2CppDumper] WARN: unsupported field type '{target}' for '{type.Name}.{fieldName}'.");
                    return false;
                }

                fi.SetValue(null, converted);
                return true;
            }
            catch (Exception ex)
            {
                XMLogging.WriteLine($"[Il2CppDumper] ERROR: Failed to set '{type.Name}.{fieldName}': {ex.Message}");
                return false;
            }
        }

        // ── Verbose dump logging (comment out the call site to silence) ──────

        /// <summary>
        /// Logs every resolved field and method for a single class.
        /// </summary>
        private static void LogClassDump(
            SchemaClass sc,
            string resolvedVia,
            Dictionary<string, int> fieldMap,
            Dictionary<string, ulong> methodMap)
        {
            XMLogging.WriteLine($"[Dump] ── {sc.CsName} ({resolvedVia}) ──");

            if (fieldMap.Count > 0)
            {
                foreach (var (name, offset) in fieldMap)
                {
                    if (offset >= 0)
                        XMLogging.WriteLine($"[Dump]   field  {name} = 0x{(uint)offset:X}");
                    else
                        XMLogging.WriteLine($"[Dump]   field  {name} = {offset}");
                }
            }
            else
            {
                XMLogging.WriteLine($"[Dump]   (no fields)");
            }

            if (methodMap is not null && methodMap.Count > 0)
            {
                foreach (var (name, rva) in methodMap)
                    XMLogging.WriteLine($"[Dump]   method {name} = 0x{rva:X}");
            }
        }

        // ── Memory helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Reads all IL2CppClass* entries from a pre-resolved type-info table pointer.
        /// Uses scatter reads to batch all DMA operations (2 scatter rounds instead of ~4 reads per class).
        /// </summary>
        private static List<(string Name, string Namespace, ulong KlassPtr, int Index)> ReadAllClassesFromTable(ulong tablePtr)
        {
            var result = new List<(string, string, ulong, int)>(4096);

            // Step 1: Bulk read all class pointers (contiguous array — single DMA read).
            ulong[] ptrs;
            try { ptrs = Memory.ReadArray<ulong>(tablePtr, MaxClasses, false); }
            catch (Exception ex)
            {
                XMLogging.WriteLine($"[Il2CppDumper] ReadArray failed: {ex.Message}");
                return result;
            }

            // Collect indices of valid class pointers.
            var validIndices = new List<int>(ptrs.Length / 2);
            for (int i = 0; i < ptrs.Length; i++)
            {
                if (ptrs[i].IsValidVirtualAddress())
                    validIndices.Add(i);
            }

            if (validIndices.Count == 0)
                return result;

            XMLogging.WriteLine($"[Il2CppDumper] Scatter-reading name/namespace pointers for {validIndices.Count} classes...");

            // Step 2: Scatter read name_ptr + namespace_ptr for every valid class (one 16-byte read each).
            var ptrEntries = new ScatterReadEntry<ClassNamePtrs>[validIndices.Count];
            var scatterBatch = new IScatterEntry[validIndices.Count];

            for (int j = 0; j < validIndices.Count; j++)
            {
                ptrEntries[j] = ScatterReadEntry<ClassNamePtrs>.Get(ptrs[validIndices[j]] + K_Name, 0);
                scatterBatch[j] = ptrEntries[j];
            }

            Memory.ReadScatter(scatterBatch, false);

            // Step 3: Scatter read all name and namespace strings in one batch.
            var nameEntries = new ScatterReadEntry<UTF8String>[validIndices.Count];
            var nsEntries   = new ScatterReadEntry<UTF8String>[validIndices.Count];
            var stringBatch = new List<IScatterEntry>(validIndices.Count * 2);

            for (int j = 0; j < validIndices.Count; j++)
            {
                if (ptrEntries[j].IsFailed)
                    continue;

                ref var p = ref ptrEntries[j].Result;

                if (p.NamePtr.IsValidVirtualAddress())
                {
                    nameEntries[j] = ScatterReadEntry<UTF8String>.Get(p.NamePtr, MaxNameLen);
                    stringBatch.Add(nameEntries[j]);
                }

                if (p.NamespacePtr.IsValidVirtualAddress())
                {
                    nsEntries[j] = ScatterReadEntry<UTF8String>.Get(p.NamespacePtr, MaxNameLen);
                    stringBatch.Add(nsEntries[j]);
                }
            }

            XMLogging.WriteLine($"[Il2CppDumper] Scatter-reading {stringBatch.Count} name/namespace strings...");
            Memory.ReadScatter(stringBatch.ToArray(), false);

            // Step 4: Build results.
            for (int j = 0; j < validIndices.Count; j++)
            {
                int i = validIndices[j];

                string name = nameEntries[j] is not null && !nameEntries[j].IsFailed
                    ? (string)(UTF8String)nameEntries[j].Result
                    : null;

                if (string.IsNullOrEmpty(name))
                    continue;

                string ns = nsEntries[j] is not null && !nsEntries[j].IsFailed
                    ? (string)(UTF8String)nsEntries[j].Result
                    : string.Empty;

                result.Add((name, ns ?? string.Empty, ptrs[i], i));
            }

            return result;
        }

        private static Dictionary<string, int> ReadClassFields(ulong klassPtr)
        {
            var result     = new Dictionary<string, int>(StringComparer.Ordinal);
            var fieldCount = Memory.ReadValue<ushort>(klassPtr + K_FieldCount, false);
            if (fieldCount == 0 || fieldCount > 4096) return result;

            var fieldsBase = ReadPtr(klassPtr + K_Fields);
            if (!fieldsBase.IsValidVirtualAddress()) return result;

            // Bulk read the entire field array in one DMA operation.
            RawFieldInfo[] rawFields;
            try { rawFields = Memory.ReadArray<RawFieldInfo>(fieldsBase, fieldCount, false); }
            catch { return result; }

            // Scatter read all field name strings in one batch.
            var nameEntries = new ScatterReadEntry<UTF8String>[rawFields.Length];
            var scatter = new List<IScatterEntry>(rawFields.Length);

            for (int i = 0; i < rawFields.Length; i++)
            {
                if (rawFields[i].NamePtr.IsValidVirtualAddress())
                {
                    nameEntries[i] = ScatterReadEntry<UTF8String>.Get(rawFields[i].NamePtr, MaxNameLen);
                    scatter.Add(nameEntries[i]);
                }
            }

            if (scatter.Count > 0)
                Memory.ReadScatter(scatter.ToArray(), false);

            // Build results.
            for (int i = 0; i < rawFields.Length; i++)
            {
                string name = nameEntries[i] is not null && !nameEntries[i].IsFailed
                    ? (string)(UTF8String)nameEntries[i].Result
                    : null;

                if (string.IsNullOrEmpty(name)) continue;
                result.TryAdd(name, rawFields[i].Offset);
            }

            return result;
        }

        private static Dictionary<string, ulong> ReadClassMethods(ulong klassPtr, ulong gaBase)
        {
            var result      = new Dictionary<string, ulong>(StringComparer.Ordinal);
            var methodCount = Memory.ReadValue<ushort>(klassPtr + K_MethodCount, false);
            if (methodCount == 0 || methodCount > 4096) return result;

            var methodsBase = ReadPtr(klassPtr + K_Methods);
            if (!methodsBase.IsValidVirtualAddress()) return result;

            ulong[] methodPtrs;
            try { methodPtrs = Memory.ReadArray<ulong>(methodsBase, methodCount, false); }
            catch { return result; }

            // Scatter read MethodPointer + NamePtr for all methods in one batch.
            var infoEntries = new ScatterReadEntry<RawMethodInfo>[methodPtrs.Length];
            var scatter1 = new List<IScatterEntry>(methodPtrs.Length);

            for (int i = 0; i < methodPtrs.Length; i++)
            {
                if (!methodPtrs[i].IsValidVirtualAddress()) continue;
                infoEntries[i] = ScatterReadEntry<RawMethodInfo>.Get(methodPtrs[i], 0);
                scatter1.Add(infoEntries[i]);
            }

            if (scatter1.Count > 0)
                Memory.ReadScatter(scatter1.ToArray(), false);

            // Scatter read all method name strings in one batch.
            var nameEntries = new ScatterReadEntry<UTF8String>[methodPtrs.Length];
            var scatter2 = new List<IScatterEntry>(methodPtrs.Length);

            for (int i = 0; i < methodPtrs.Length; i++)
            {
                if (infoEntries[i] is null || infoEntries[i].IsFailed) continue;

                ref var info = ref infoEntries[i].Result;
                if (!info.MethodPointer.IsValidVirtualAddress() || info.MethodPointer < gaBase) continue;
                if (!info.NamePtr.IsValidVirtualAddress()) continue;

                nameEntries[i] = ScatterReadEntry<UTF8String>.Get(info.NamePtr, MaxNameLen);
                scatter2.Add(nameEntries[i]);
            }

            if (scatter2.Count > 0)
                Memory.ReadScatter(scatter2.ToArray(), false);

            // Build results.
            for (int i = 0; i < methodPtrs.Length; i++)
            {
                if (nameEntries[i] is null || nameEntries[i].IsFailed) continue;
                if (infoEntries[i] is null || infoEntries[i].IsFailed) continue;

                string name = (string)(UTF8String)nameEntries[i].Result;
                if (string.IsNullOrEmpty(name)) continue;

                var rva = infoEntries[i].Result.MethodPointer - gaBase;
                result.TryAdd(name, rva);
            }

            return result;
        }

        /// <summary>
        /// Converts between the two IL2CPP backing field naming conventions:
        ///   "&lt;Name&gt;k__BackingField"  ↔  "_Name_k__BackingField"
        /// Returns null if the input is not a backing field name.
        /// </summary>
        private static string FlipBackingFieldConvention(string name)
        {
            const string suffix = "k__BackingField";
            if (!name.EndsWith(suffix, StringComparison.Ordinal))
                return null;

            if (name.Length > suffix.Length + 2 && name[0] == '<')
            {
                // <Name>k__BackingField → _Name_k__BackingField
                var inner = name[1..name.IndexOf('>')];
                return $"_{inner}_{suffix}";
            }

            if (name.Length > suffix.Length + 2 && name[0] == '_')
            {
                // _Name_k__BackingField → <Name>k__BackingField
                var inner = name[1..^suffix.Length];
                if (inner.EndsWith('_'))
                    inner = inner[..^1];
                return $"<{inner}>{suffix}";
            }

            return null;
        }

        // Reads a pointer without throwing (uses ReadValue<ulong> — never ReadPtr which throws)
        private static ulong ReadPtr(ulong addr)
        {
            if (!addr.IsValidVirtualAddress()) return 0;
            try { return Memory.ReadValue<ulong>(addr, false); }
            catch { return 0; }
        }

        private static string ReadStr(ulong addr)
        {
            if (!addr.IsValidVirtualAddress()) return null;
            try { return Memory.ReadString(addr, MaxNameLen, false); }
            catch { return null; }
        }
    }
}
