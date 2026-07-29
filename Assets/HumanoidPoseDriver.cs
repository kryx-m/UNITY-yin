using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Humanoid 姿态驱动器 v3 — 置信度融合
/// ======================================
/// 核心改进：
///   1. 可见度动态权重：摄像头看不到的关节自动降权
///   2. 轴感知融合：前摄(XY) + 左摄(Z) 按身体部位分配合适的比例
///   3. 手臂 30% 左摄 / 腿部 50% 左摄 / 手指纯前摄
///
/// 前摄: 正面，XY 精度高，Z 噪声大
/// 左摄: 侧面，X→Unity Z 精度高，补充前后运动盲区
/// </summary>
public class HumanoidPoseDriver : MonoBehaviour
{
    public UdpPoseReceiver receiver;        // 前摄 port 8888
    public UdpPoseReceiver leftReceiver;     // 左摄 port 8889
    public Animator targetAnimator;

    [Header("Dual-Cam Fusion")]
    [Tooltip("腿部左摄基础权重 (0=纯前摄, 0.5=均衡, 1=纯左摄)")]
    [Range(0f, 1f)] public float legBaseWeight = 0.5f;
    [Tooltip("手臂左摄基础权重")]
    [Range(0f, 1f)] public float armBaseWeight = 0.3f;

    [Header("Confidence")]
    [Tooltip("可见度低于此值的摄像头不参与融合")]
    [Range(0f, 1f)] public float minVisibility = 0.3f;

    [Header("Body")]
    public bool enableBodyRotation = true;
    public bool invertBodyForward = false;
    public float bodyRotationSmooth = 8f;
    [Tooltip("禁用身体旋转以单独测试 Root Motion（调试用）")]
    public bool disableBodyRotation = false;

    [Header("Head")]
    public bool enableHeadRotation = true;
    public float headRotationSmooth = 10f;

    [Header("Arms / Legs / Fingers")]
    public float rotationSmooth = 10f;

    [Header("Hands")]
    public bool enableFingerRotation = true;
    public float fingerRotationSmooth = 14f;

    [Header("Bone Limits")]
    public bool enableBoneLimits = true;

    [Header("Debug")]
    public bool showBoneWarnings = true;

    private Transform _hips, _spine, _chest, _upperChest, _head;
    private Transform _leftFoot, _rightFoot;

    private Quaternion _spineRestLocalRot, _chestRestLocalRot, _upperChestRestLocalRot;

    // 身体朝向平滑（减少 MediaPipe 噪声导致的旋转抖动）
    private Vector3 _smoothBodyForward = Vector3.forward;
    private bool _bodyForwardInitialized;

    private BoneRig _leftUpperArmRig = new BoneRig();
    private BoneRig _leftLowerArmRig = new BoneRig();
    private BoneRig _rightUpperArmRig = new BoneRig();
    private BoneRig _rightLowerArmRig = new BoneRig();
    private BoneRig _leftUpperLegRig = new BoneRig();
    private BoneRig _leftLowerLegRig = new BoneRig();
    private BoneRig _rightUpperLegRig = new BoneRig();
    private BoneRig _rightLowerLegRig = new BoneRig();
    private BoneRig _leftFootRig = new BoneRig();
    private BoneRig _rightFootRig = new BoneRig();

    private BasisRig _leftHandRig = new BasisRig();
    private BasisRig _rightHandRig = new BasisRig();
    private FingerRigChain[] _leftFingers = new FingerRigChain[5];
    private FingerRigChain[] _rightFingers = new FingerRigChain[5];

    private class BoneRig
    {
        public Transform bone;
        public Quaternion restLocalRotation;
        public Vector3 restAxisLocal = Vector3.forward;
        public float minAngle = -180f;
        public float maxAngle = 180f;
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
            foreach (var r in receivers)
            {
                if (r.port == 8888) { receiver = r; break; }
            }
        }

        if (leftReceiver == null)
        {
            var receivers = FindObjectsOfType<UdpPoseReceiver>();
            foreach (var r in receivers)
            {
                if (r.port == 8889) { leftReceiver = r; break; }
            }
        }

        if (targetAnimator == null)
            targetAnimator = GetComponent<Animator>();

        if (receiver == null) { Debug.LogError("❌ 没找到 UdpPoseReceiver!"); return; }
        if (targetAnimator == null) { Debug.LogError("❌ 没找到 Animator!"); return; }
        if (!targetAnimator.isHuman) { Debug.LogError("❌ 不是 Humanoid!"); return; }

        InitFingerChains();
        CacheBones();
        targetAnimator.applyRootMotion = false;

        string info = leftReceiver != null
            ? $"双摄置信度融合 (腿={legBaseWeight:F1} 臂={armBaseWeight:F1})"
            : "仅前摄";
        Debug.Log($"✅ HumanoidPoseDriver v3 — {info}");
    }

    void InitFingerChains()
    {
        for (int i = 0; i < 5; i++)
        {
            _leftFingers[i] = new FingerRigChain();
            _rightFingers[i] = new FingerRigChain();
        }
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

        CacheBoneRig(_leftUpperArmRig,  HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm,  Vector3.forward);
        CacheBoneRig(_leftLowerArmRig,  HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand,      Vector3.forward);
        CacheBoneRig(_rightUpperArmRig, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, Vector3.forward);
        CacheBoneRig(_rightLowerArmRig, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,     Vector3.forward);

        CacheBoneRig(_leftUpperLegRig,  HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg,  Vector3.forward);
        CacheBoneRig(_leftLowerLegRig,  HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot,      Vector3.forward);
        CacheBoneRig(_rightUpperLegRig, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, Vector3.forward);
        CacheBoneRig(_rightLowerLegRig, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,     Vector3.forward);

        // 骨骼限制
        SetBoneLimit(_leftUpperArmRig,  0f, 180f); SetBoneLimit(_leftLowerArmRig,  0f, 180f);
        SetBoneLimit(_rightUpperArmRig, 0f, 180f); SetBoneLimit(_rightLowerArmRig, 0f, 180f);
        SetBoneLimit(_leftUpperLegRig,  0f, 160f); SetBoneLimit(_leftLowerLegRig,  0f, 160f);
        SetBoneLimit(_rightUpperLegRig, 0f, 160f); SetBoneLimit(_rightLowerLegRig, 0f, 160f);

        _leftFoot  = targetAnimator.GetBoneTransform(HumanBodyBones.LeftFoot);
        _rightFoot = targetAnimator.GetBoneTransform(HumanBodyBones.RightFoot);
        CacheBoneRig(_leftFootRig,  _leftFoot,  targetAnimator.GetBoneTransform(HumanBodyBones.LeftToes),  Vector3.forward);
        CacheBoneRig(_rightFootRig, _rightFoot, targetAnimator.GetBoneTransform(HumanBodyBones.RightToes), Vector3.forward);

        // 手指
        CacheFinger(_leftFingers,  true);
        CacheFinger(_rightFingers, false);

        // 手掌
        CacheHandRig(_leftHandRig,  HumanBodyBones.LeftHand,  _leftFingers);
        CacheHandRig(_rightHandRig, HumanBodyBones.RightHand, _rightFingers);

        if (showBoneWarnings) WarnAll();
    }

    void CacheBoneRig(BoneRig rig, HumanBodyBones bone, HumanBodyBones child, Vector3 fallback)
    {
        Transform t = targetAnimator.GetBoneTransform(bone);
        rig.bone = t;
        if (t == null) return;
        rig.restLocalRotation = t.localRotation;

        Transform c = targetAnimator.GetBoneTransform(child);
        Vector3 axis = fallback;
        if (c != null)
        {
            Vector3 d = c.localPosition;
            if (d.sqrMagnitude < 1e-6f) d = t.InverseTransformDirection(c.position - t.position);
            if (d.sqrMagnitude > 1e-6f) axis = d.normalized;
        }
        if (axis.sqrMagnitude < 1e-6f) axis = Vector3.forward;
        rig.restAxisLocal = axis.normalized;
    }

    void CacheBoneRig(BoneRig rig, Transform bone, Transform child, Vector3 fallback)
    {
        rig.bone = bone;
        if (bone == null) return;
        rig.restLocalRotation = bone.localRotation;

        Vector3 axis = fallback;
        if (child != null)
        {
            Vector3 d = child.localPosition;
            if (d.sqrMagnitude < 1e-6f) d = bone.InverseTransformDirection(child.position - bone.position);
            if (d.sqrMagnitude > 1e-6f) axis = d.normalized;
        }
        if (axis.sqrMagnitude < 1e-6f) axis = Vector3.forward;
        rig.restAxisLocal = axis.normalized;
    }

    void CacheFinger(FingerRigChain[] chains, bool isLeft)
    {
        for (int f = 0; f < 5; f++)
        {
            HumanBodyBones[] bones = GetFingerBones(f, isLeft);
            if (bones[0] == HumanBodyBones.LastBone) continue;
            CacheBoneRig(chains[f].proximal,
                targetAnimator.GetBoneTransform(bones[0]),
                targetAnimator.GetBoneTransform(bones[1]), Vector3.forward);
            CacheBoneRig(chains[f].intermediate,
                targetAnimator.GetBoneTransform(bones[1]),
                targetAnimator.GetBoneTransform(bones[2]), chains[f].proximal.restAxisLocal);
            CacheBoneRig(chains[f].distal,
                targetAnimator.GetBoneTransform(bones[2]),
                null, chains[f].intermediate.restAxisLocal);
        }
    }

    HumanBodyBones[] GetFingerBones(int finger, bool isLeft)
    {
        // 0=thumb, 1=index, 2=middle, 3=ring, 4=little
        if (isLeft)
        {
            switch (finger)
            {
                case 0: return new[] { HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal };
                case 1: return new[] { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal };
                case 2: return new[] { HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal };
                case 3: return new[] { HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal };
                case 4: return new[] { HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal };
            }
        }
        else
        {
            switch (finger)
            {
                case 0: return new[] { HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal };
                case 1: return new[] { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal };
                case 2: return new[] { HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal };
                case 3: return new[] { HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal };
                case 4: return new[] { HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal };
            }
        }
        return new[] { HumanBodyBones.LastBone };
    }

    void CacheHandRig(BasisRig rig, HumanBodyBones handBone, FingerRigChain[] fingers)
    {
        Transform hand = targetAnimator.GetBoneTransform(handBone);
        rig.bone = hand;
        if (hand == null) return;
        rig.restLocalRotation = hand.localRotation;

        Vector3 indexDir = GetLocalDir(hand, fingers[1].proximal.bone);
        Vector3 middleDir = GetLocalDir(hand, fingers[2].proximal.bone);
        Vector3 pinkyDir = GetLocalDir(hand, fingers[4].proximal.bone);

        Vector3 fwd = Vector3.zero; int n = 0;
        if (indexDir.sqrMagnitude > 1e-6f) { fwd += indexDir.normalized; n++; }
        if (middleDir.sqrMagnitude > 1e-6f) { fwd += middleDir.normalized; n++; }
        if (pinkyDir.sqrMagnitude > 1e-6f) { fwd += pinkyDir.normalized; n++; }

        if (n == 0) { rig.restForwardLocal = Vector3.forward; rig.restUpLocal = Vector3.up; return; }
        fwd.Normalize();

        Vector3 up = Vector3.Cross(indexDir, pinkyDir);
        if (up.sqrMagnitude < 1e-6f) up = Vector3.up;
        up = Vector3.ProjectOnPlane(up, fwd);
        if (up.sqrMagnitude < 1e-6f) up = Vector3.up;

        rig.restForwardLocal = fwd.normalized;
        rig.restUpLocal = up.normalized;
    }

    Vector3 GetLocalDir(Transform parent, Transform child)
    {
        if (parent == null || child == null) return Vector3.zero;
        Vector3 d = child.localPosition;
        if (d.sqrMagnitude < 1e-6f) d = parent.InverseTransformDirection(child.position - parent.position);
        return d.sqrMagnitude < 1e-6f ? Vector3.zero : d.normalized;
    }

    void SetBoneLimit(BoneRig rig, float min, float max) { rig.minAngle = min; rig.maxAngle = max; }

    void WarnAll()
    {
        WarnIfNull("Hips", _hips); WarnIfNull("Spine", _spine); WarnIfNull("Chest", _chest);
        WarnIfNull("UpperChest", _upperChest); WarnIfNull("Head", _head);
        WarnIfNull("LeftUpperArm", _leftUpperArmRig.bone); WarnIfNull("LeftLowerArm", _leftLowerArmRig.bone);
        WarnIfNull("RightUpperArm", _rightUpperArmRig.bone); WarnIfNull("RightLowerArm", _rightLowerArmRig.bone);
        WarnIfNull("LeftUpperLeg", _leftUpperLegRig.bone); WarnIfNull("LeftLowerLeg", _leftLowerLegRig.bone);
        WarnIfNull("RightUpperLeg", _rightUpperLegRig.bone); WarnIfNull("RightLowerLeg", _rightLowerLegRig.bone);
    }
    void WarnIfNull(string n, Transform t) { if (t == null) Debug.LogWarning($"⚠️ 骨骼未映射: {n}"); }

    // ══════════════════════════════════════════════════════════════
    // 主循环
    // ══════════════════════════════════════════════════════════════

    void LateUpdate()
    {
        if (receiver == null) return;

        var frontPose = receiver.GetLatestPose();
        if (frontPose == null || frontPose.Count == 0) return;

        Dictionary<string, Landmark> leftPose = null;
        bool hasLeft = leftReceiver != null;
        if (hasLeft)
        {
            leftPose = leftReceiver.GetLatestPose();
            hasLeft = leftPose != null && leftPose.Count > 0;
        }

        // ── 身体 + 头：前摄即可（正面 XY 足够）──
        if (enableBodyRotation && !disableBodyRotation) ApplyBodyRotation(frontPose);
        if (enableHeadRotation) ApplyHeadRotation(frontPose);

        // ── 手臂：置信度融合（前摄为主，左摄辅助 Z）──
        ApplyLimbConfidenceFusion(_leftUpperArmRig,  _leftLowerArmRig,  frontPose, leftPose, "11", "13", "15", armBaseWeight);
        ApplyLimbConfidenceFusion(_rightUpperArmRig, _rightLowerArmRig, frontPose, leftPose, "12", "14", "16", armBaseWeight);

        // ── 腿：置信度融合（左摄权重更高，前后运动靠侧面）──
        ApplyLimbConfidenceFusion(_leftUpperLegRig,  _leftLowerLegRig,  frontPose, leftPose, "23", "25", "27", legBaseWeight);
        ApplyLimbConfidenceFusion(_rightUpperLegRig, _rightLowerLegRig, frontPose, leftPose, "24", "26", "28", legBaseWeight);

        // ── 脚：置信度融合 ──
        ApplyFootConfidenceFusion(frontPose, leftPose, true,  legBaseWeight);
        ApplyFootConfidenceFusion(frontPose, leftPose, false, legBaseWeight);

        // ── 手掌 + 手指：纯前摄 ──
        if (enableFingerRotation)
        {
            ApplyHandAndFingers(frontPose, true);
            ApplyHandAndFingers(frontPose, false);
        }
    }

    // ══════════════════════════════════════════════════════════════
    // 置信度融合 — 核心改进
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 置信度驱动的双摄方向融合。
    ///
    /// 权重公式：
    ///   前摄权 = (1 - baseWeight) × frontVisibility
    ///   左摄权 = baseWeight × leftVisibility
    ///   归一化 → 可见度低的摄像头自动降权
    ///
    /// baseWeight: 0.3=手臂, 0.5=腿部
    /// </summary>
    void ApplyLimbConfidenceFusion(BoneRig upperRig, BoneRig lowerRig,
        Dictionary<string, Landmark> front, Dictionary<string, Landmark> left,
        string a, string b, string c, float baseLeftWeight)
    {
        bool fOk = front != null && front.ContainsKey(a) && front.ContainsKey(b) && front.ContainsKey(c);
        bool lOk = left  != null && left.ContainsKey(a)  && left.ContainsKey(b)  && left.ContainsKey(c);

        if (!fOk && !lOk) return;

        // 获取可见度（取三个关节中最低的，保守估计）
        float fVis = fOk ? Mathf.Min(front[a].visibility, front[b].visibility, front[c].visibility) : 0f;
        float lVis = lOk ? Mathf.Min(left[a].visibility,  left[b].visibility,  left[c].visibility)  : 0f;

        // 低于阈值的不参与
        if (fVis < minVisibility) fVis = 0f;
        if (lVis < minVisibility) lVis = 0f;

        // 动态权重
        float fw = (1f - baseLeftWeight) * fVis;
        float lw = baseLeftWeight * lVis;
        float total = fw + lw;
        if (total < 0.001f) return;

        // 归一化
        fw /= total;
        lw /= total;

        Vector3 dirUpper = Vector3.zero, dirLower = Vector3.zero;

        if (fOk && fw > 0.001f)
        {
            Vector3 p1 = CoordinateConverter.ToUnityWorld(front[a]);
            Vector3 p2 = CoordinateConverter.ToUnityWorld(front[b]);
            Vector3 p3 = CoordinateConverter.ToUnityWorld(front[c]);
            dirUpper += (p2 - p1).normalized * fw;
            dirLower += (p3 - p2).normalized * fw;
        }

        if (lOk && lw > 0.001f)
        {
            Vector3 p1 = CoordinateConverter.ToUnityWorld(left[a]);
            Vector3 p2 = CoordinateConverter.ToUnityWorld(left[b]);
            Vector3 p3 = CoordinateConverter.ToUnityWorld(left[c]);
            dirUpper += (p2 - p1).normalized * lw;
            dirLower += (p3 - p2).normalized * lw;
        }

        if (dirUpper.sqrMagnitude > 1e-6f) dirUpper.Normalize();
        if (dirLower.sqrMagnitude > 1e-6f) dirLower.Normalize();

        ApplyBoneRig(upperRig, dirUpper, rotationSmooth);
        ApplyBoneRig(lowerRig, dirLower, rotationSmooth);
    }

    void ApplyFootConfidenceFusion(Dictionary<string, Landmark> front, Dictionary<string, Landmark> left,
        bool isLeftFoot, float baseLeftWeight)
    {
        BoneRig rig = isLeftFoot ? _leftFootRig : _rightFootRig;
        if (rig.bone == null) return;

        string ankle = isLeftFoot ? "27" : "28";
        string toe   = isLeftFoot ? "31" : "32";

        bool fOk = front != null && front.ContainsKey(ankle) && front.ContainsKey(toe);
        bool lOk = left  != null && left.ContainsKey(ankle)  && left.ContainsKey(toe);
        if (!fOk && !lOk) return;

        float fVis = fOk ? Mathf.Min(front[ankle].visibility, front[toe].visibility) : 0f;
        float lVis = lOk ? Mathf.Min(left[ankle].visibility,  left[toe].visibility)  : 0f;
        if (fVis < minVisibility) fVis = 0f;
        if (lVis < minVisibility) lVis = 0f;

        float fw = (1f - baseLeftWeight) * fVis;
        float lw = baseLeftWeight * lVis;
        float total = fw + lw;
        if (total < 0.001f) return;
        fw /= total; lw /= total;

        Vector3 dir = Vector3.zero;
        if (fOk && fw > 0.001f)
        {
            Vector3 a = CoordinateConverter.ToUnityWorld(front[ankle]);
            Vector3 t = CoordinateConverter.ToUnityWorld(front[toe]);
            dir += (t - a).normalized * fw;
        }
        if (lOk && lw > 0.001f)
        {
            Vector3 a = CoordinateConverter.ToUnityWorld(left[ankle]);
            Vector3 t = CoordinateConverter.ToUnityWorld(left[toe]);
            dir += (t - a).normalized * lw;
        }
        if (dir.sqrMagnitude > 1e-6f) dir.Normalize();
        ApplyBoneRig(rig, dir, rotationSmooth);
    }

    // ══════════════════════════════════════════════════════════════
    // 身体 / 头 / 手指 — 纯前摄（不变）
    // ══════════════════════════════════════════════════════════════

    void ApplyBodyRotation(Dictionary<string, Landmark> pose)
    {
        if (_hips == null) return;
        if (!pose.ContainsKey("11") || !pose.ContainsKey("12") || !pose.ContainsKey("23") || !pose.ContainsKey("24")) return;

        Vector3 ls = CoordinateConverter.ToUnityWorld(pose["11"]);
        Vector3 rs = CoordinateConverter.ToUnityWorld(pose["12"]);
        Vector3 lh = CoordinateConverter.ToUnityWorld(pose["23"]);
        Vector3 rh = CoordinateConverter.ToUnityWorld(pose["24"]);

        Vector3 shoulderRight = rs - ls;
        if (shoulderRight.sqrMagnitude < 1e-6f) return;
        shoulderRight.Normalize();

        Vector3 rawForward = Vector3.Cross(shoulderRight, Vector3.up).normalized;
        if (invertBodyForward) rawForward = -rawForward;

        // 平滑朝向（减少 MediaPipe 噪声导致的旋转抖动）
        if (!_bodyForwardInitialized)
        {
            // ⭐ 第一帧直接初始化为当前方向，避免从 Vector3.forward(0,0,1) 慢慢 Slerp 过去
            _smoothBodyForward = rawForward;
            _bodyForwardInitialized = true;
        }
        else
        {
            _smoothBodyForward = Vector3.Slerp(_smoothBodyForward, rawForward,
                Time.deltaTime * bodyRotationSmooth * 0.3f);  // 较慢的平滑，抵消噪声
        }

        _hips.rotation = Quaternion.Slerp(_hips.rotation,
            Quaternion.LookRotation(_smoothBodyForward, Vector3.up),
            Time.deltaTime * bodyRotationSmooth);

        Vector3 hipRight = rh - lh;
        if (hipRight.sqrMagnitude < 1e-6f) return;
        hipRight.Normalize();

        Vector3 hipRightFlat = Vector3.ProjectOnPlane(hipRight, Vector3.up);
        Vector3 shoulderRightFlat = Vector3.ProjectOnPlane(shoulderRight, Vector3.up);
        if (hipRightFlat.sqrMagnitude < 1e-6f || shoulderRightFlat.sqrMagnitude < 1e-6f) return;
        hipRightFlat.Normalize(); shoulderRightFlat.Normalize();

        float twist = Mathf.Clamp(Vector3.SignedAngle(hipRightFlat, shoulderRightFlat, Vector3.up), -70f, 70f);
        DriveSpineBone(_spine, _spineRestLocalRot, twist * 0.40f);
        DriveSpineBone(_chest, _chestRestLocalRot, twist * 0.35f);
        DriveSpineBone(_upperChest, _upperChestRestLocalRot, twist * 0.25f);
    }

    void DriveSpineBone(Transform b, Quaternion rest, float deg)
    {
        if (b == null) return;
        Quaternion tgt = rest * Quaternion.AngleAxis(deg, Vector3.up);
        b.localRotation = Quaternion.Slerp(b.localRotation, tgt, Time.deltaTime * bodyRotationSmooth);
    }

    void ApplyHeadRotation(Dictionary<string, Landmark> pose)
    {
        if (_head == null || !pose.ContainsKey("0")) return;

        Vector3 nose = CoordinateConverter.ToUnityWorld(pose["0"]);
        Vector3 fwd, up;

        if (pose.ContainsKey("7") && pose.ContainsKey("8"))
        {
            Vector3 le = CoordinateConverter.ToUnityWorld(pose["7"]);
            Vector3 re = CoordinateConverter.ToUnityWorld(pose["8"]);
            fwd = nose - (le + re) * 0.5f;
            if (fwd.sqrMagnitude < 1e-6f) return;
            fwd.Normalize();
            up = Vector3.Cross(fwd, (re - le).normalized);
            if (up.sqrMagnitude < 1e-6f) up = Vector3.up;
            up.Normalize();
        }
        else
        {
            if (!pose.ContainsKey("11") || !pose.ContainsKey("12")) return;
            Vector3 sc = (CoordinateConverter.ToUnityWorld(pose["11"]) + CoordinateConverter.ToUnityWorld(pose["12"])) * 0.5f;
            fwd = nose - sc;
            if (fwd.sqrMagnitude < 1e-6f) return;
            fwd.Normalize();
            up = Vector3.up;
        }

        Quaternion targetWorld = SafeLookRotation(fwd, up);
        Quaternion targetLocal = _head.parent != null
            ? Quaternion.Inverse(_head.parent.rotation) * targetWorld : targetWorld;
        _head.localRotation = Quaternion.Slerp(_head.localRotation, targetLocal, Time.deltaTime * headRotationSmooth);
    }

    void ApplyHandAndFingers(Dictionary<string, Landmark> pose, bool isLeft)
    {
        string prefix = isLeft ? "LH_" : "RH_";
        BasisRig handRig = isLeft ? _leftHandRig : _rightHandRig;
        if (handRig.bone == null) return;

        if (TryGetHandPt(pose, prefix, 0, out Vector3 wrist) &&
            TryGetHandPt(pose, prefix, 5, out Vector3 iMcp) &&
            TryGetHandPt(pose, prefix, 9, out Vector3 mMcp) &&
            TryGetHandPt(pose, prefix, 17, out Vector3 pMcp))
        {
            Vector3 fwd = mMcp - wrist;
            if (fwd.sqrMagnitude > 1e-6f)
            {
                fwd.Normalize();
                Vector3 up = Vector3.Cross(iMcp - wrist, pMcp - wrist);
                if (up.sqrMagnitude < 1e-6f) up = Vector3.up;
                up.Normalize();
                ApplyBasisRig(handRig, fwd, up, fingerRotationSmooth);
            }
        }

        ApplyFingerChains(pose, prefix, isLeft ? _leftFingers : _rightFingers);
    }

    void ApplyFingerChains(Dictionary<string, Landmark> pose, string prefix, FingerRigChain[] chains)
    {
        for (int f = 0; f < 5; f++)
        {
            var c = chains[f];
            if (c == null) continue;
            if (!TryGetHandPt(pose, prefix, FingerIdx(f, 1), out Vector3 p1)) continue;
            if (!TryGetHandPt(pose, prefix, FingerIdx(f, 2), out Vector3 p2)) continue;
            if (!TryGetHandPt(pose, prefix, FingerIdx(f, 3), out Vector3 p3)) continue;
            TryGetHandPt(pose, prefix, FingerIdx(f, 4), out Vector3 p4);
            ApplyBoneRig(c.proximal, p2 - p1, fingerRotationSmooth);
            ApplyBoneRig(c.intermediate, p3 - p2, fingerRotationSmooth);
            ApplyBoneRig(c.distal, p4.sqrMagnitude > 0f ? p4 - p3 : p3 - p2, fingerRotationSmooth);
        }
    }

    int FingerIdx(int f, int j) { return f * 4 + j; }

    bool TryGetHandPt(Dictionary<string, Landmark> pose, string prefix, int idx, out Vector3 w)
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
        Quaternion targetLocal = delta * rig.restLocalRotation;

        rig.bone.localRotation = Quaternion.Slerp(rig.bone.localRotation, targetLocal, Time.deltaTime * smooth);

        if (enableBoneLimits && (rig.minAngle > -180f || rig.maxAngle < 180f))
            rig.bone.localRotation = ClampLocalRotation(rig.bone.localRotation, rig.restLocalRotation, rig.minAngle, rig.maxAngle);
    }

    Quaternion ClampLocalRotation(Quaternion cur, Quaternion rest, float minDeg, float maxDeg)
    {
        Quaternion rel = Quaternion.Inverse(rest) * cur;
        Vector3 euler = rel.eulerAngles;
        float a = euler.x; if (a > 180f) a -= 360f;
        a = Mathf.Clamp(a, minDeg, maxDeg);
        return rest * Quaternion.Euler(a, euler.y, euler.z);
    }

    void ApplyBasisRig(BasisRig rig, Vector3 fwdWorld, Vector3 upWorld, float smooth)
    {
        if (rig?.bone == null) return;
        if (fwdWorld.sqrMagnitude < 1e-6f || upWorld.sqrMagnitude < 1e-6f) return;

        Vector3 pf = rig.bone.parent != null ? rig.bone.parent.InverseTransformDirection(fwdWorld.normalized) : fwdWorld.normalized;
        Vector3 pu = rig.bone.parent != null ? rig.bone.parent.InverseTransformDirection(upWorld.normalized) : upWorld.normalized;

        Vector3 rf = rig.restLocalRotation * rig.restForwardLocal;
        Vector3 ru = rig.restLocalRotation * rig.restUpLocal;

        Quaternion rBasis = SafeLookRotation(rf, ru);
        Quaternion tBasis = SafeLookRotation(pf, pu);
        Quaternion delta = tBasis * Quaternion.Inverse(rBasis);
        Quaternion targetLocal = delta * rig.restLocalRotation;

        rig.bone.localRotation = Quaternion.Slerp(rig.bone.localRotation, targetLocal, Time.deltaTime * smooth);
    }

    Quaternion SafeLookRotation(Vector3 fwd, Vector3 up)
    {
        if (fwd.sqrMagnitude < 1e-6f) return Quaternion.identity;
        fwd.Normalize();
        up = Vector3.ProjectOnPlane(up, fwd);
        if (up.sqrMagnitude < 1e-6f) up = Vector3.ProjectOnPlane(Vector3.up, fwd);
        if (up.sqrMagnitude < 1e-6f) return Quaternion.identity;
        return Quaternion.LookRotation(fwd, up.normalized);
    }
}
