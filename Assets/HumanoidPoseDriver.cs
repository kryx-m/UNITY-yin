using UnityEngine;
using System.Collections.Generic;

public class HumanoidPoseDriver : MonoBehaviour
{
    public UdpPoseReceiver receiver;        // 前摄 port 8888
    public UdpPoseReceiver leftReceiver;     // 左摄 port 8889
    public Animator targetAnimator;

    [Header("Pose Source")]
    [Tooltip("true=姿态全部用前摄(v4 单摄闭环，推荐)；false=左摄参与四肢混合(需外参标定，当前会拉低腿部质量)")]
    public bool frontOnlyPose = true;

    [Header("Dual-Cam Blend")]
    [Tooltip("左摄对腿骨的混合权重 (0=仅前摄, 0.5=平均, 1=仅左摄)")]
    [Range(0f, 1f)] public float legBlendWeight = 0.5f;
    [Tooltip("禁用身体旋转以单独测试 Root Motion（调试用）")]
    public bool disableBodyRotation = false;

    [Header("Sync / Gating")]
    [Tooltip("前/左摄姿态最大允许时间差（秒），超差退化为仅前摄")]
    public float maxTimeDiff = 0.1f;
    [Tooltip("关键点可见度门控：低于此值的点不参与双摄混合")]
    [Range(0f, 1f)] public float minLandmarkVisibility = 0.3f;

    [Header("Body")]
    public bool enableBodyRotation = true;
    // 配合 Python FRONT_POSE_ALWAYS_SWAP：前摄翻转图 + swap 使肩线恒偏 180°，
    // 恒翻转躯干前向恢复正确朝向（自动前后 + 人物转身）。
    [Tooltip("翻转躯干前向：配合 Python swap 恒需 true")]
    public bool invertBodyForward = true;
    public float bodyRotationSmooth = 8f;

    [Header("Head")]
    public bool enableHeadRotation = true;
    public float headRotationSmooth = 10f;

    [Header("Arms / Legs / Fingers")]
    public float rotationSmooth = 10f;

    [Header("Hands")]
    public bool enableFingerRotation = true;
    public float fingerRotationSmooth = 14f;

    [Header("Debug")]
    public bool showBoneWarnings = true;

    private Transform _hips, _spine, _chest, _upperChest, _head, _leftFoot, _rightFoot;
    private Quaternion _spineRestLocalRot, _chestRestLocalRot, _upperChestRestLocalRot;

    private BoneRig _leftUpperArmRig = new BoneRig(), _leftLowerArmRig = new BoneRig();
    private BoneRig _rightUpperArmRig = new BoneRig(), _rightLowerArmRig = new BoneRig();
    private BoneRig _leftUpperLegRig = new BoneRig(), _leftLowerLegRig = new BoneRig();
    private BoneRig _rightUpperLegRig = new BoneRig(), _rightLowerLegRig = new BoneRig();
    private BoneRig _leftFootRig = new BoneRig(), _rightFootRig = new BoneRig();

    private BasisRig _leftHandRig = new BasisRig(), _rightHandRig = new BasisRig();
    private FingerRigChain[] _leftFingers = new FingerRigChain[5];
    private FingerRigChain[] _rightFingers = new FingerRigChain[5];

    private class BoneRig
    {
        public Transform bone;
        public Quaternion restLocalRotation;
        public Vector3 restAxisLocal = Vector3.forward;
    }

    private class BasisRig
    {
        public Transform bone;
        public Quaternion restLocalRotation;
        public Vector3 restForwardLocal = Vector3.forward;
        public Vector3 restUpLocal = Vector3.up;
    }

    private class FingerRigChain
    {
        public BoneRig proximal = new BoneRig();
        public BoneRig intermediate = new BoneRig();
        public BoneRig distal = new BoneRig();
    }

    void Start()
    {
        if (receiver == null)
        {
            var receivers = FindObjectsOfType<UdpPoseReceiver>();
            foreach (var r in receivers) { if (r.port == 8888) { receiver = r; break; } }
        }
        if (leftReceiver == null)
        {
            var receivers = FindObjectsOfType<UdpPoseReceiver>();
            foreach (var r in receivers) { if (r.port == 8889) { leftReceiver = r; break; } }
        }
        if (targetAnimator == null) targetAnimator = GetComponent<Animator>();
        if (receiver == null) { Debug.LogError("❌ 没找到 UdpPoseReceiver!"); return; }
        if (targetAnimator == null) { Debug.LogError("❌ 没找到 Animator!"); return; }
        if (!targetAnimator.isHuman) { Debug.LogError("❌ 不是 Humanoid!"); return; }

        InitFingerChains();
        CacheBones();
        targetAnimator.applyRootMotion = false;

        Debug.Log($"✅ HumanoidPoseDriver — {(leftReceiver != null ? "双摄" : "仅前摄")} legBlend={legBlendWeight:F1}");
    }

    void InitFingerChains()
    {
        for (int i = 0; i < 5; i++) { _leftFingers[i] = new FingerRigChain(); _rightFingers[i] = new FingerRigChain(); }
    }

    void CacheBones()
    {
        _hips = targetAnimator.GetBoneTransform(HumanBodyBones.Hips);
        _spine = targetAnimator.GetBoneTransform(HumanBodyBones.Spine);
        _chest = targetAnimator.GetBoneTransform(HumanBodyBones.Chest);
        _upperChest = targetAnimator.GetBoneTransform(HumanBodyBones.UpperChest);
        _head = targetAnimator.GetBoneTransform(HumanBodyBones.Head);

        if (_spine != null) _spineRestLocalRot = _spine.localRotation;
        if (_chest != null) _chestRestLocalRot = _chest.localRotation;
        if (_upperChest != null) _upperChestRestLocalRot = _upperChest.localRotation;

        CacheBoneRig(_leftUpperArmRig,  targetAnimator.GetBoneTransform(HumanBodyBones.LeftUpperArm),  targetAnimator.GetBoneTransform(HumanBodyBones.LeftLowerArm),  Vector3.forward);
        CacheBoneRig(_leftLowerArmRig,  targetAnimator.GetBoneTransform(HumanBodyBones.LeftLowerArm),  targetAnimator.GetBoneTransform(HumanBodyBones.LeftHand),      Vector3.forward);
        CacheBoneRig(_rightUpperArmRig, targetAnimator.GetBoneTransform(HumanBodyBones.RightUpperArm), targetAnimator.GetBoneTransform(HumanBodyBones.RightLowerArm), Vector3.forward);
        CacheBoneRig(_rightLowerArmRig, targetAnimator.GetBoneTransform(HumanBodyBones.RightLowerArm), targetAnimator.GetBoneTransform(HumanBodyBones.RightHand),     Vector3.forward);
        CacheBoneRig(_leftUpperLegRig,  targetAnimator.GetBoneTransform(HumanBodyBones.LeftUpperLeg),  targetAnimator.GetBoneTransform(HumanBodyBones.LeftLowerLeg),  Vector3.forward);
        CacheBoneRig(_leftLowerLegRig,  targetAnimator.GetBoneTransform(HumanBodyBones.LeftLowerLeg),  targetAnimator.GetBoneTransform(HumanBodyBones.LeftFoot),      Vector3.forward);
        CacheBoneRig(_rightUpperLegRig, targetAnimator.GetBoneTransform(HumanBodyBones.RightUpperLeg), targetAnimator.GetBoneTransform(HumanBodyBones.RightLowerLeg), Vector3.forward);
        CacheBoneRig(_rightLowerLegRig, targetAnimator.GetBoneTransform(HumanBodyBones.RightLowerLeg), targetAnimator.GetBoneTransform(HumanBodyBones.RightFoot),     Vector3.forward);

        _leftFoot  = targetAnimator.GetBoneTransform(HumanBodyBones.LeftFoot);
        _rightFoot = targetAnimator.GetBoneTransform(HumanBodyBones.RightFoot);
        CacheBoneRig(_leftFootRig,  _leftFoot,  targetAnimator.GetBoneTransform(HumanBodyBones.LeftToes),  Vector3.forward);
        CacheBoneRig(_rightFootRig, _rightFoot, targetAnimator.GetBoneTransform(HumanBodyBones.RightToes), Vector3.forward);

        // 左手手指
        CacheBoneRig(_leftFingers[0].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftThumbProximal), targetAnimator.GetBoneTransform(HumanBodyBones.LeftThumbIntermediate), Vector3.forward);
        CacheBoneRig(_leftFingers[0].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.LeftThumbIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.LeftThumbDistal), _leftFingers[0].proximal.restAxisLocal);
        CacheBoneRig(_leftFingers[0].distal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftThumbDistal), null, _leftFingers[0].intermediate.restAxisLocal);
        CacheBoneRig(_leftFingers[1].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftIndexProximal), targetAnimator.GetBoneTransform(HumanBodyBones.LeftIndexIntermediate), Vector3.forward);
        CacheBoneRig(_leftFingers[1].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.LeftIndexIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.LeftIndexDistal), _leftFingers[1].proximal.restAxisLocal);
        CacheBoneRig(_leftFingers[1].distal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftIndexDistal), null, _leftFingers[1].intermediate.restAxisLocal);
        CacheBoneRig(_leftFingers[2].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal), targetAnimator.GetBoneTransform(HumanBodyBones.LeftMiddleIntermediate), Vector3.forward);
        CacheBoneRig(_leftFingers[2].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.LeftMiddleIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.LeftMiddleDistal), _leftFingers[2].proximal.restAxisLocal);
        CacheBoneRig(_leftFingers[2].distal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftMiddleDistal), null, _leftFingers[2].intermediate.restAxisLocal);
        CacheBoneRig(_leftFingers[3].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftRingProximal), targetAnimator.GetBoneTransform(HumanBodyBones.LeftRingIntermediate), Vector3.forward);
        CacheBoneRig(_leftFingers[3].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.LeftRingIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.LeftRingDistal), _leftFingers[3].proximal.restAxisLocal);
        CacheBoneRig(_leftFingers[3].distal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftRingDistal), null, _leftFingers[3].intermediate.restAxisLocal);
        CacheBoneRig(_leftFingers[4].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftLittleProximal), targetAnimator.GetBoneTransform(HumanBodyBones.LeftLittleIntermediate), Vector3.forward);
        CacheBoneRig(_leftFingers[4].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.LeftLittleIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.LeftLittleDistal), _leftFingers[4].proximal.restAxisLocal);
        CacheBoneRig(_leftFingers[4].distal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftLittleDistal), null, _leftFingers[4].intermediate.restAxisLocal);

        // 右手手指
        CacheBoneRig(_rightFingers[0].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.RightThumbProximal), targetAnimator.GetBoneTransform(HumanBodyBones.RightThumbIntermediate), Vector3.forward);
        CacheBoneRig(_rightFingers[0].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.RightThumbIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.RightThumbDistal), _rightFingers[0].proximal.restAxisLocal);
        CacheBoneRig(_rightFingers[0].distal, targetAnimator.GetBoneTransform(HumanBodyBones.RightThumbDistal), null, _rightFingers[0].intermediate.restAxisLocal);
        CacheBoneRig(_rightFingers[1].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.RightIndexProximal), targetAnimator.GetBoneTransform(HumanBodyBones.RightIndexIntermediate), Vector3.forward);
        CacheBoneRig(_rightFingers[1].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.RightIndexIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.RightIndexDistal), _rightFingers[1].proximal.restAxisLocal);
        CacheBoneRig(_rightFingers[1].distal, targetAnimator.GetBoneTransform(HumanBodyBones.RightIndexDistal), null, _rightFingers[1].intermediate.restAxisLocal);
        CacheBoneRig(_rightFingers[2].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.RightMiddleProximal), targetAnimator.GetBoneTransform(HumanBodyBones.RightMiddleIntermediate), Vector3.forward);
        CacheBoneRig(_rightFingers[2].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.RightMiddleIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.RightMiddleDistal), _rightFingers[2].proximal.restAxisLocal);
        CacheBoneRig(_rightFingers[2].distal, targetAnimator.GetBoneTransform(HumanBodyBones.RightMiddleDistal), null, _rightFingers[2].intermediate.restAxisLocal);
        CacheBoneRig(_rightFingers[3].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.RightRingProximal), targetAnimator.GetBoneTransform(HumanBodyBones.RightRingIntermediate), Vector3.forward);
        CacheBoneRig(_rightFingers[3].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.RightRingIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.RightRingDistal), _rightFingers[3].proximal.restAxisLocal);
        CacheBoneRig(_rightFingers[3].distal, targetAnimator.GetBoneTransform(HumanBodyBones.RightRingDistal), null, _rightFingers[3].intermediate.restAxisLocal);
        CacheBoneRig(_rightFingers[4].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.RightLittleProximal), targetAnimator.GetBoneTransform(HumanBodyBones.RightLittleIntermediate), Vector3.forward);
        CacheBoneRig(_rightFingers[4].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.RightLittleIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.RightLittleDistal), _rightFingers[4].proximal.restAxisLocal);
        CacheBoneRig(_rightFingers[4].distal, targetAnimator.GetBoneTransform(HumanBodyBones.RightLittleDistal), null, _rightFingers[4].intermediate.restAxisLocal);

        CacheHandRig(_leftHandRig,  targetAnimator.GetBoneTransform(HumanBodyBones.LeftHand),  _leftFingers[1].proximal.bone,  _leftFingers[2].proximal.bone,  _leftFingers[4].proximal.bone);
        CacheHandRig(_rightHandRig, targetAnimator.GetBoneTransform(HumanBodyBones.RightHand), _rightFingers[1].proximal.bone, _rightFingers[2].proximal.bone, _rightFingers[4].proximal.bone);

        if (showBoneWarnings) WarnAll();
    }

    void CacheBoneRig(BoneRig rig, Transform bone, Transform child, Vector3 fallbackAxisLocal)
    {
        rig.bone = bone;
        if (bone == null) return;
        rig.restLocalRotation = bone.localRotation;
        Vector3 axis = fallbackAxisLocal;
        if (child != null)
        {
            Vector3 childLocalDir = child.localPosition;
            if (childLocalDir.sqrMagnitude < 1e-6f) childLocalDir = bone.InverseTransformDirection(child.position - bone.position);
            if (childLocalDir.sqrMagnitude > 1e-6f) axis = childLocalDir.normalized;
        }
        if (axis.sqrMagnitude < 1e-6f) axis = Vector3.forward;
        rig.restAxisLocal = axis.normalized;
    }

    void CacheHandRig(BasisRig rig, Transform hand, Transform indexRoot, Transform middleRoot, Transform pinkyRoot)
    {
        rig.bone = hand;
        if (hand == null) return;
        rig.restLocalRotation = hand.localRotation;

        Vector3 indexDir = GetLocalDirection(hand, indexRoot);
        Vector3 middleDir = GetLocalDirection(hand, middleRoot);
        Vector3 pinkyDir = GetLocalDirection(hand, pinkyRoot);

        Vector3 forward = Vector3.zero; int count = 0;
        if (indexDir.sqrMagnitude > 1e-6f) { forward += indexDir.normalized; count++; }
        if (middleDir.sqrMagnitude > 1e-6f) { forward += middleDir.normalized; count++; }
        if (pinkyDir.sqrMagnitude > 1e-6f) { forward += pinkyDir.normalized; count++; }
        if (count == 0) { rig.restForwardLocal = Vector3.forward; rig.restUpLocal = Vector3.up; return; }
        forward.Normalize();

        Vector3 up = Vector3.Cross(indexDir, pinkyDir);
        if (up.sqrMagnitude < 1e-6f) up = Vector3.up;
        up = Vector3.ProjectOnPlane(up, forward);
        if (up.sqrMagnitude < 1e-6f) up = Vector3.up;
        rig.restForwardLocal = forward.normalized;
        rig.restUpLocal = up.normalized;
    }

    Vector3 GetLocalDirection(Transform parent, Transform child)
    {
        if (parent == null || child == null) return Vector3.zero;
        Vector3 local = child.localPosition;
        if (local.sqrMagnitude < 1e-6f) local = parent.InverseTransformDirection(child.position - parent.position);
        return local.sqrMagnitude < 1e-6f ? Vector3.zero : local.normalized;
    }

    void WarnAll()
    {
        WarnIfNull("Hips", _hips); WarnIfNull("Spine", _spine); WarnIfNull("Chest", _chest);
        WarnIfNull("UpperChest", _upperChest); WarnIfNull("Head", _head);
        WarnIfNull("LeftUpperArm", _leftUpperArmRig.bone); WarnIfNull("LeftLowerArm", _leftLowerArmRig.bone);
        WarnIfNull("RightUpperArm", _rightUpperArmRig.bone); WarnIfNull("RightLowerArm", _rightLowerArmRig.bone);
        WarnIfNull("LeftUpperLeg", _leftUpperLegRig.bone); WarnIfNull("LeftLowerLeg", _leftLowerLegRig.bone);
        WarnIfNull("RightUpperLeg", _rightUpperLegRig.bone); WarnIfNull("RightLowerLeg", _rightLowerLegRig.bone);
    }
    void WarnIfNull(string name, Transform t) { if (t == null) Debug.LogWarning($"⚠️ 骨骼未映射: {name}"); }

    // ══════════════════════════════════════════════════════════════
    // 主循环
    // ══════════════════════════════════════════════════════════════

    void LateUpdate()
    {
        if (receiver == null) return;

        var frontPose = receiver.GetLatestPose();
        if (frontPose == null || frontPose.Count == 0) return;

        Dictionary<string, Landmark> leftPose = null;
        if (leftReceiver != null && !frontOnlyPose)
        {
            // 时间匹配：取左摄中最接近前摄时间的帧；超差/空则本帧退化为仅前摄，
            // 避免左摄卡住后旧姿态仍以固定权重混入造成重影/拉扯。
            double frontTime = receiver.LatestTime;
            if (leftReceiver.TryGetClosestFrame(frontTime, out var leftFrame)
                && System.Math.Abs(frontTime - leftFrame.time) <= maxTimeDiff
                && leftFrame.pose != null && leftFrame.pose.Count > 0)
            {
                leftPose = leftFrame.pose;
            }
        }

        if (enableBodyRotation && !disableBodyRotation)
            ApplyBodyRotation(frontPose);

        if (enableHeadRotation)
            ApplyHeadRotation(frontPose);

        ApplyLimbRigDual(_leftUpperArmRig,  _leftLowerArmRig,  frontPose, leftPose, "11", "13", "15", 0.3f);
        ApplyLimbRigDual(_rightUpperArmRig, _rightLowerArmRig, frontPose, leftPose, "12", "14", "16", 0.3f);
        ApplyLimbRigDual(_leftUpperLegRig,  _leftLowerLegRig,  frontPose, leftPose, "23", "25", "27", legBlendWeight);
        ApplyLimbRigDual(_rightUpperLegRig, _rightLowerLegRig, frontPose, leftPose, "24", "26", "28", legBlendWeight);

        if (enableFingerRotation)
        {
            ApplyHandAndFingers(frontPose, true);
            ApplyHandAndFingers(frontPose, false);
        }

        ApplyFootRigDual(frontPose, leftPose, true, legBlendWeight);
        ApplyFootRigDual(frontPose, leftPose, false, legBlendWeight);
    }

    // ══════════════════════════════════════════════════════════════
    // 双摄混合
    // ══════════════════════════════════════════════════════════════

    void ApplyLimbRigDual(BoneRig upperRig, BoneRig lowerRig,
        Dictionary<string, Landmark> front, Dictionary<string, Landmark> left,
        string a, string b, string c, float leftWeight)
    {
        // 前摄是已验证的 v4 基准姿态源：与 v4 一样，只要求关键点存在。
        // visibility 门槛只用于左摄贡献；否则前摄任一点短暂低于阈值时，
        // 整条肢体会冻结在上一姿势，看起来像映射修改完全没有生效。
        bool fOk = front != null && LandmarksPresent(front, a, b, c);
        bool lOk = left  != null && LandmarksUsable(left,  a, b, c);
        if (!fOk && !lOk) return;

        Vector3 dirUpper = Vector3.zero, dirLower = Vector3.zero;
        float totalW = 0f;

        if (fOk)
        {
            Vector3 p1 = CoordinateConverter.ToUnityWorld(front[a]);
            Vector3 p2 = CoordinateConverter.ToUnityWorld(front[b]);
            Vector3 p3 = CoordinateConverter.ToUnityWorld(front[c]);
            float w = 1f - leftWeight;
            dirUpper += (p2 - p1).normalized * w;
            dirLower += (p3 - p2).normalized * w;
            totalW += w;
        }
        if (lOk)
        {
            // ⚠️ 左摄坐标系矫正: X↔Z 交换
            //    左摄画面X=房间前后 → 应映射到Unity Z
            Vector3 p1 = CoordinateConverter.ToUnityWorldLeft(left[a]);
            Vector3 p2 = CoordinateConverter.ToUnityWorldLeft(left[b]);
            Vector3 p3 = CoordinateConverter.ToUnityWorldLeft(left[c]);
            dirUpper += (p2 - p1).normalized * leftWeight;
            dirLower += (p3 - p2).normalized * leftWeight;
            totalW += leftWeight;
        }

        if (dirUpper.sqrMagnitude > 1e-6f) dirUpper.Normalize();
        if (dirLower.sqrMagnitude > 1e-6f) dirLower.Normalize();
        ApplyBoneRig(upperRig, dirUpper, rotationSmooth);
        ApplyBoneRig(lowerRig, dirLower, rotationSmooth);
    }

    void ApplyFootRigDual(Dictionary<string, Landmark> front, Dictionary<string, Landmark> left,
        bool isLeftFoot, float leftWeight)
    {
        BoneRig rig = isLeftFoot ? _leftFootRig : _rightFootRig;
        if (rig.bone == null) return;
        string ankle = isLeftFoot ? "27" : "28";
        string toe   = isLeftFoot ? "31" : "32";

        bool fOk = front != null && LandmarksPresent(front, ankle, toe);
        bool lOk = left  != null && LandmarksUsable(left,  ankle, toe);
        if (!fOk && !lOk) return;

        Vector3 dir = Vector3.zero;
        if (fOk) { Vector3 a = CoordinateConverter.ToUnityWorld(front[ankle]); Vector3 t = CoordinateConverter.ToUnityWorld(front[toe]); dir += (t - a).normalized * (1f - leftWeight); }
        if (lOk) { Vector3 a = CoordinateConverter.ToUnityWorldLeft(left[ankle]);  Vector3 t = CoordinateConverter.ToUnityWorldLeft(left[toe]);  dir += (t - a).normalized * leftWeight; }
        if (dir.sqrMagnitude > 1e-6f) dir.Normalize();
        ApplyBoneRig(rig, dir, rotationSmooth);
    }

    // ══════════════════════════════════════════════════════════════
    // 身体 / 头 / 手指
    // ══════════════════════════════════════════════════════════════

    void ApplyBodyRotation(Dictionary<string, Landmark> pose)
    {
        if (_hips == null) return;
        if (!pose.ContainsKey("11") || !pose.ContainsKey("12") || !pose.ContainsKey("23") || !pose.ContainsKey("24")) return;

        Vector3 ls = CoordinateConverter.ToUnityWorld(pose["11"]);
        Vector3 rs = CoordinateConverter.ToUnityWorld(pose["12"]);
        Vector3 lh = CoordinateConverter.ToUnityWorld(pose["23"]);
        Vector3 rh = CoordinateConverter.ToUnityWorld(pose["24"]);

        Vector3 shoulderCenter = (ls + rs) * 0.5f;
        Vector3 hipCenter = (lh + rh) * 0.5f;

        Vector3 up = shoulderCenter - hipCenter;
        if (up.sqrMagnitude < 1e-6f) return;
        up.Normalize();

        Vector3 shoulderRight = rs - ls;
        if (shoulderRight.sqrMagnitude < 1e-6f) return;
        shoulderRight.Normalize();

        Vector3 forward = Vector3.Cross(shoulderRight, up).normalized;
        if (invertBodyForward) forward = -forward;

        Quaternion targetHipsWorldRot = Quaternion.LookRotation(forward, up);
        _hips.rotation = Quaternion.Slerp(_hips.rotation, targetHipsWorldRot, Time.deltaTime * bodyRotationSmooth);

        Vector3 hipRight = rh - lh;
        if (hipRight.sqrMagnitude < 1e-6f) return;
        hipRight.Normalize();

        Vector3 hipRightFlat = Vector3.ProjectOnPlane(hipRight, up);
        Vector3 shoulderRightFlat = Vector3.ProjectOnPlane(shoulderRight, up);
        if (hipRightFlat.sqrMagnitude < 1e-6f || shoulderRightFlat.sqrMagnitude < 1e-6f) return;
        hipRightFlat.Normalize(); shoulderRightFlat.Normalize();

        float twistAngle = Mathf.Clamp(Vector3.SignedAngle(hipRightFlat, shoulderRightFlat, up), -70f, 70f);
        DriveSpineBone(_spine, _spineRestLocalRot, twistAngle * 0.40f);
        DriveSpineBone(_chest, _chestRestLocalRot, twistAngle * 0.35f);
        DriveSpineBone(_upperChest, _upperChestRestLocalRot, twistAngle * 0.25f);
    }

    void DriveSpineBone(Transform bone, Quaternion restLocalRot, float twistDeg)
    {
        if (bone == null) return;
        Quaternion targetLocal = restLocalRot * Quaternion.AngleAxis(twistDeg, Vector3.up);
        bone.localRotation = Quaternion.Slerp(bone.localRotation, targetLocal, Time.deltaTime * bodyRotationSmooth);
    }

    void ApplyHeadRotation(Dictionary<string, Landmark> pose)
    {
        if (_head == null || !pose.ContainsKey("0")) return;
        Vector3 nose = CoordinateConverter.ToUnityWorld(pose["0"]);

        if (pose.ContainsKey("7") && pose.ContainsKey("8"))
        {
            Vector3 ear1 = CoordinateConverter.ToUnityWorld(pose["7"]);
            Vector3 ear2 = CoordinateConverter.ToUnityWorld(pose["8"]);
            Vector3 earCenter = (ear1 + ear2) * 0.5f;

            // 头部前向：鼻子 → 远离耳朵中心
            Vector3 fwdWorld = nose - earCenter;
            if (fwdWorld.sqrMagnitude < 1e-6f) return;
            fwdWorld.Normalize();

            // 耳线方向：pose[7]↔pose[8] 经 FRONT_POSE_SIDE_SWAP 后左右
            // 含义反转（7=右耳, 8=左耳）。以下用世界 up 自动判断正确符号：
            Vector3 earDir = ear2 - ear1;                    // 可能是左或右
            Vector3 approxRight = Vector3.Cross(Vector3.up, fwdWorld);
            // 若 earDir 和 approxRight 反向 → 翻转 earDir
            if (Vector3.Dot(earDir, approxRight) < 0f)
                earDir = -earDir;

            Vector3 upWorld = Vector3.Cross(fwdWorld, earDir.normalized);
            if (upWorld.sqrMagnitude < 1e-6f) upWorld = Vector3.up; else upWorld.Normalize();

            Quaternion targetWorld = SafeLookRotation(fwdWorld, upWorld);
            Quaternion targetLocal = _head.parent != null
                ? Quaternion.Inverse(_head.parent.rotation) * targetWorld
                : targetWorld;

            _head.localRotation = Quaternion.Slerp(
                _head.localRotation, targetLocal,
                Time.deltaTime * headRotationSmooth);
        }
        else if (pose.ContainsKey("11") && pose.ContainsKey("12"))
        {
            Vector3 sc = (CoordinateConverter.ToUnityWorld(pose["11"]) +
                          CoordinateConverter.ToUnityWorld(pose["12"])) * 0.5f;
            Vector3 fwdWorld = nose - sc;
            if (fwdWorld.sqrMagnitude < 1e-6f) return;
            fwdWorld.Normalize();

            Quaternion targetWorld = SafeLookRotation(fwdWorld, Vector3.up);
            Quaternion targetLocal = _head.parent != null
                ? Quaternion.Inverse(_head.parent.rotation) * targetWorld
                : targetWorld;

            _head.localRotation = Quaternion.Slerp(
                _head.localRotation, targetLocal,
                Time.deltaTime * headRotationSmooth);
        }
    }

    void ApplyHandAndFingers(Dictionary<string, Landmark> pose, bool isLeft)
    {
        string prefix = isLeft ? "LH_" : "RH_";
        BasisRig handRig = isLeft ? _leftHandRig : _rightHandRig;
        if (handRig.bone == null) return;

        if (TryGetHandPoint(pose, prefix, 0, out Vector3 wrist) &&
            TryGetHandPoint(pose, prefix, 5, out Vector3 iMcp) &&
            TryGetHandPoint(pose, prefix, 9, out Vector3 mMcp) &&
            TryGetHandPoint(pose, prefix, 17, out Vector3 pMcp))
        {
            Vector3 fwd = mMcp - wrist;
            if (fwd.sqrMagnitude > 1e-6f)
            {
                fwd.Normalize();
                Vector3 up = Vector3.Cross(iMcp - wrist, pMcp - wrist);
                if (up.sqrMagnitude < 1e-6f) up = Vector3.up; else up.Normalize();
                ApplyBasisRig(handRig, fwd, up, fingerRotationSmooth);
            }
        }

        ApplyFingerChains(pose, prefix, isLeft ? _leftFingers : _rightFingers);
    }

    void ApplyFingerChains(Dictionary<string, Landmark> pose, string prefix, FingerRigChain[] chains)
    {
        for (int f = 0; f < 5; f++)
        {
            var c = chains[f]; if (c == null) continue;
            if (!TryGetHandPoint(pose, prefix, FingerIdx(f, 1), out Vector3 p1)) continue;
            if (!TryGetHandPoint(pose, prefix, FingerIdx(f, 2), out Vector3 p2)) continue;
            if (!TryGetHandPoint(pose, prefix, FingerIdx(f, 3), out Vector3 p3)) continue;
            TryGetHandPoint(pose, prefix, FingerIdx(f, 4), out Vector3 p4);
            ApplyBoneRig(c.proximal, p2 - p1, fingerRotationSmooth);
            ApplyBoneRig(c.intermediate, p3 - p2, fingerRotationSmooth);
            ApplyBoneRig(c.distal, p4.sqrMagnitude > 0f ? p4 - p3 : p3 - p2, fingerRotationSmooth);
        }
    }

    int FingerIdx(int f, int j) { return f * 4 + j; }

    /// <summary>
    /// 该路姿态的关键点是否可用于双摄混合：都存在且可见度达标。
    /// 遮挡/低置信度时剔除该摄贡献，避免旧姿态混入。
    /// </summary>
    bool LandmarksUsable(Dictionary<string, Landmark> pose, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!pose.TryGetValue(k, out var lm)) return false;
            if (lm.visibility < minLandmarkVisibility) return false;
        }
        return true;
    }

    bool LandmarksPresent(Dictionary<string, Landmark> pose, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!pose.ContainsKey(k)) return false;
        }
        return true;
    }

    bool TryGetHandPoint(Dictionary<string, Landmark> pose, string prefix, int idx, out Vector3 w)
    {
        string key = prefix + idx.ToString();
        if (pose != null && pose.ContainsKey(key)) { w = CoordinateConverter.ToUnityWorld(pose[key]); return true; }
        w = Vector3.zero; return false;
    }

    // ══════════════════════════════════════════════════════════════
    // 骨骼驱动工具
    // ══════════════════════════════════════════════════════════════

    void ApplyBoneRig(BoneRig rig, Vector3 targetDirWorld, float smooth)
    {
        if (rig?.bone == null || targetDirWorld.sqrMagnitude < 1e-6f) return;

        Vector3 desiredParentDir = rig.bone.parent != null
            ? rig.bone.parent.InverseTransformDirection(targetDirWorld.normalized)
            : targetDirWorld.normalized;
        if (desiredParentDir.sqrMagnitude < 1e-6f) return;

        Vector3 restParentDir = rig.restLocalRotation * rig.restAxisLocal;
        if (restParentDir.sqrMagnitude < 1e-6f) return;

        Quaternion delta = Quaternion.FromToRotation(restParentDir.normalized, desiredParentDir.normalized);
        Quaternion targetLocalRot = delta * rig.restLocalRotation;

        rig.bone.localRotation = Quaternion.Slerp(rig.bone.localRotation, targetLocalRot, Time.deltaTime * smooth);
    }

    void ApplyBasisRig(BasisRig rig, Vector3 targetForwardWorld, Vector3 targetUpWorld, float smooth)
    {
        if (rig?.bone == null) return;
        if (targetForwardWorld.sqrMagnitude < 1e-6f || targetUpWorld.sqrMagnitude < 1e-6f) return;

        Vector3 desiredParentForward = rig.bone.parent != null ? rig.bone.parent.InverseTransformDirection(targetForwardWorld.normalized) : targetForwardWorld.normalized;
        Vector3 desiredParentUp = rig.bone.parent != null ? rig.bone.parent.InverseTransformDirection(targetUpWorld.normalized) : targetUpWorld.normalized;

        Vector3 restParentForward = rig.restLocalRotation * rig.restForwardLocal;
        Vector3 restParentUp = rig.restLocalRotation * rig.restUpLocal;

        Quaternion restBasis = SafeLookRotation(restParentForward, restParentUp);
        Quaternion targetBasis = SafeLookRotation(desiredParentForward, desiredParentUp);
        Quaternion delta = targetBasis * Quaternion.Inverse(restBasis);
        Quaternion targetLocalRot = delta * rig.restLocalRotation;

        rig.bone.localRotation = Quaternion.Slerp(rig.bone.localRotation, targetLocalRot, Time.deltaTime * smooth);
    }

    Quaternion SafeLookRotation(Vector3 forward, Vector3 upHint)
    {
        if (forward.sqrMagnitude < 1e-6f) return Quaternion.identity;
        forward.Normalize();
        upHint = Vector3.ProjectOnPlane(upHint, forward);
        if (upHint.sqrMagnitude < 1e-6f) upHint = Vector3.ProjectOnPlane(Vector3.up, forward);
        if (upHint.sqrMagnitude < 1e-6f) return Quaternion.identity;
        return Quaternion.LookRotation(forward, upHint.normalized);
    }
}
