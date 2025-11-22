using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Math.Quaternion;
using FFXIVClientStructs.Havok.Common.Base.Math.Vector;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using static FFXIVClientStructs.Havok.Animation.Rig.hkaPose;
using Dalamud.Hooking;
using Dalamud.Game;

namespace AetherBridge.Services;

/// <summary>
/// Service for direct skeleton bone manipulation - bypasses Brio for real-time animation
/// </summary>
public unsafe class SkeletonService : IDisposable
{
    private delegate nint UpdateBonePhysicsDelegate(nint a1);

    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly Hook<UpdateBonePhysicsDelegate>? updateBonePhysicsHook;

    // Cache bone name to index mappings per character per partial
    private readonly Dictionary<ulong, Dictionary<int, Dictionary<string, int>>> boneIndexCache = new();

    // Store bone overrides that should be applied every frame
    private readonly Dictionary<ulong, Dictionary<string, BoneTransform>> boneOverrides = new();
    
    // Store current interpolated state (what's actually applied)
    private readonly Dictionary<ulong, Dictionary<string, BoneTransform>> currentState = new();
    
    private readonly object boneOverridesLock = new();

    // Cache skeleton pointers to avoid GameObject lookups
    private readonly Dictionary<ulong, nint> skeletonCache = new(); public SkeletonService(IObjectTable objectTable, IFramework framework, IPluginLog log, Configuration configuration, ISigScanner sigScanner, IGameInteropProvider hooking)
    {
        this.objectTable = objectTable;
        this.framework = framework;
        this.log = log;
        this.configuration = configuration;

        try
        {
            // Hook UpdateBonePhysics - this is where Brio applies transforms
            var updateBonePhysicsAddress = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 56 48 83 EC ?? 48 8B 59 ?? 45 33 E4";
            var address = sigScanner.ScanText(updateBonePhysicsAddress);
            updateBonePhysicsHook = hooking.HookFromAddress<UpdateBonePhysicsDelegate>(address, UpdateBonePhysicsDetour);
            updateBonePhysicsHook.Enable();
            log.Information("[SKELETON] UpdateBonePhysics hook enabled");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[SKELETON] Failed to hook UpdateBonePhysics - direct bone manipulation may not work correctly");
        }
    }

    private nint UpdateBonePhysicsDetour(nint a1)
    {
        // Call original first
        var result = updateBonePhysicsHook!.Original(a1);

        try
        {
            // Apply our bone transforms after physics update
            ApplyAllBoneOverrides();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[SKELETON] Error in UpdateBonePhysics detour");
        }

        return result;
    }

    private void ApplyAllBoneOverrides()
    {
        lock (boneOverridesLock)
        {
            if (boneOverrides.Count == 0)
                return;

            foreach (var kvp in boneOverrides)
            {
                var objectId = kvp.Key;
                var targetBones = kvp.Value;

                // Get or create current state for this character
                if (!currentState.TryGetValue(objectId, out var current))
                {
                    // First frame - initialize current state to target
                    current = new Dictionary<string, BoneTransform>();
                    foreach (var bone in targetBones)
                    {
                        current[bone.Key] = bone.Value;
                    }
                    currentState[objectId] = current;
                }
                else
                {
                    // Interpolate from current to target
                    foreach (var targetBone in targetBones)
                    {
                        var boneName = targetBone.Key;
                        var target = targetBone.Value;

                        if (!current.TryGetValue(boneName, out var currentTransform))
                        {
                            // New bone - initialize to target
                            current[boneName] = target;
                        }
                        else
                        {
                            // Lerp/Slerp to target
                            var interpolated = new BoneTransform();

                            if (target.Position.HasValue && currentTransform.Position.HasValue)
                            {
                                interpolated.Position = Vector3.Lerp(
                                    currentTransform.Position.Value,
                                    target.Position.Value,
                                    configuration.BoneInterpolationSpeed
                                );
                            }
                            else if (target.Position.HasValue)
                            {
                                interpolated.Position = target.Position;
                            }

                            if (target.Rotation.HasValue && currentTransform.Rotation.HasValue)
                            {
                                interpolated.Rotation = Quaternion.Slerp(
                                    currentTransform.Rotation.Value,
                                    target.Rotation.Value,
                                    configuration.BoneInterpolationSpeed
                                );
                            }
                            else if (target.Rotation.HasValue)
                            {
                                interpolated.Rotation = target.Rotation;
                            }

                            if (target.Scale.HasValue && currentTransform.Scale.HasValue)
                            {
                                interpolated.Scale = Vector3.Lerp(
                                    currentTransform.Scale.Value,
                                    target.Scale.Value,
                                    configuration.BoneInterpolationSpeed
                                );
                            }
                            else if (target.Scale.HasValue)
                            {
                                interpolated.Scale = target.Scale;
                            }

                            current[boneName] = interpolated;
                        }
                    }
                }

                // Apply the interpolated state
                ApplyBoneTransformsImmediate(objectId, current);
            }
        }
    }

    /// <summary>
    /// Set bone transforms directly on the character's skeleton
    /// Stores the transforms and applies them every frame
    /// </summary>
    public bool SetBoneTransforms(ulong objectId, Dictionary<string, BoneTransform> bones)
    {
        lock (boneOverridesLock)
        {
            // Store or update the bone overrides for this character
            boneOverrides[objectId] = new Dictionary<string, BoneTransform>(bones);
        }

        // Hook will apply on next UpdateBonePhysics
        return true;
    }

    /// <summary>
    /// Clear bone overrides for a specific character
    /// </summary>
    public void ClearBoneOverrides(ulong objectId)
    {
        lock (boneOverridesLock)
        {
            boneOverrides.Remove(objectId);
            currentState.Remove(objectId);
            boneIndexCache.Remove(objectId);
            skeletonCache.Remove(objectId);
        }
    }

    /// <summary>
    /// Immediately apply bone transforms to the skeleton (called every frame for active overrides)
    /// </summary>
    private bool ApplyBoneTransformsImmediate(ulong objectId, Dictionary<string, BoneTransform> bones)
    {
        bool success = false;

        framework.RunOnFrameworkThread(() =>
        {
            var managedGameObject = GetGameObject(objectId);
            if (managedGameObject == null)
            {
                return;
            }

            var gameObject = (GameObject*)managedGameObject.Address;
            var drawObject = gameObject->DrawObject;
            if (drawObject == null)
            {
                return;
            }

            var characterBase = (CharacterBase*)drawObject;
            if (characterBase->Skeleton == null)
            {
                return;
            }

            var skeleton = characterBase->Skeleton;

            int bonesSet = 0;

            // Iterate through all partial skeletons
            for (int partialIdx = 0; partialIdx < skeleton->PartialSkeletonCount; partialIdx++)
            {
                var partial = &skeleton->PartialSkeletons[partialIdx];

                // Try to get the pose (typically poseIdx 0 is the active one)
                var pose = partial->GetHavokPose(0);
                if (pose == null || pose->Skeleton == null)
                {
                    continue;
                }

                var boneCount = pose->Skeleton->Bones.Length;

                // Build or get bone index cache for this partial
                if (!boneIndexCache.TryGetValue(objectId, out var partialCaches))
                {
                    partialCaches = new Dictionary<int, Dictionary<string, int>>();
                    boneIndexCache[objectId] = partialCaches;
                }

                if (!partialCaches.TryGetValue(partialIdx, out var indexCache))
                {
                    indexCache = new Dictionary<string, int>();
                    for (int i = 0; i < boneCount; i++)
                    {
                        var boneName = pose->Skeleton->Bones[i].Name.String;
                        if (boneName != null)
                        {
                            indexCache[boneName] = i;
                        }
                    }
                    partialCaches[partialIdx] = indexCache;
                }

                // For each bone transform we want to set
                foreach (var kvp in bones)
                {
                    string boneName = kvp.Key;
                    BoneTransform transform = kvp.Value;

                    // Use cached bone index
                    if (!indexCache.TryGetValue(boneName, out var boneIdx))
                    {
                        continue;
                    }

                    // Access the bone in model space with propagation
                    var modelSpace = pose->AccessBoneModelSpace(boneIdx, PropagateOrNot.Propagate);

                    // Set the transform
                    if (transform.Position.HasValue)
                    {
                        var pos = transform.Position.Value;
                        modelSpace->Translation.X = pos.X;
                        modelSpace->Translation.Y = pos.Y;
                        modelSpace->Translation.Z = pos.Z;
                        modelSpace->Translation.W = 1.0f;
                    }

                    if (transform.Rotation.HasValue)
                    {
                        var rot = transform.Rotation.Value;
                        modelSpace->Rotation.X = rot.X;
                        modelSpace->Rotation.Y = rot.Y;
                        modelSpace->Rotation.Z = rot.Z;
                        modelSpace->Rotation.W = rot.W;
                    }

                    if (transform.Scale.HasValue)
                    {
                        var scale = transform.Scale.Value;
                        modelSpace->Scale.X = scale.X;
                        modelSpace->Scale.Y = scale.Y;
                        modelSpace->Scale.Z = scale.Z;
                        modelSpace->Scale.W = 1.0f;
                    }

                    bonesSet++;
                }
            }

            success = bonesSet > 0;

        }).Wait();

        return success;
    }

    private IGameObject? GetGameObject(ulong objectId)
    {
        foreach (var obj in objectTable)
        {
            if (obj != null && obj.GameObjectId == objectId)
                return obj;
        }
        return null;
    }

    /// <summary>
    /// Clear bone cache when character changes or is removed
    /// </summary>
    public void ClearBoneCache(ulong objectId)
    {
        boneIndexCache.Remove(objectId);
    }

    public void Dispose()
    {
        updateBonePhysicsHook?.Dispose();

        lock (boneOverridesLock)
        {
            boneOverrides.Clear();
            currentState.Clear();
        }

        boneIndexCache.Clear();
        skeletonCache.Clear();
        log.Information("SkeletonService disposed");
    }
}

/// <summary>
/// Bone transform data for direct skeleton manipulation
/// </summary>
public class BoneTransform
{
    public Vector3? Position { get; set; }
    public Quaternion? Rotation { get; set; }
    public Vector3? Scale { get; set; }
}
