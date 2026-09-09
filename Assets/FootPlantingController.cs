using UnityEngine;

/// <summary>
/// Stage A (修正版 v3) — 脚锁 + 直解两骨腿 IK。
///
/// v3 变更（吸收评审意见，仅改本文件）：
///  1) plantBaseY = 接触候选期“有效计划踝位 Y”的中值；anchor.y = 板面+踝偏移。抬升量用 plantBaseY，贴地目标用 anchor.y。
///  2) 抬脚 = 相对基准抬升(>riseThreshold, 默认0.14m) 立即快速放；速度路径需“小幅实际抬升 + 短时连续证据”，防孤立速度尖峰。
///  3) 不可达 = 检查“髋→锁定锚点”距离(非已投影IK目标)，超真实腿长 + 容差(2cm)，持续(0.15s)才放。
///  4) 释放是完整状态(releasing)：进入后持续降权到0，中途不因重新满足支撑而回升；清锚时清空候选统计，下次必须重新接触确认。
///  5) 姿态无效/断流/时间戳重置：不累计锁放计时（只在有效前进的新帧计时）。
///  另提供“应用修正版释放参数”菜单，更新 Inspector 里缓存的旧默认值。
/// </summary>
[DefaultExecutionOrder(150)]
public class FootPlantingController : MonoBehaviour
{
    [Header("Toggle")]
    public bool enableFootIK = true;

    [Header("References")]
    public Animator animator;
    public HumanoidPoseDriver poseDriver;

    [Header("Ground / Board")]
    public float boardY = 0f;
    public float ankleOffset = 0.08f;

    [Header("Stance (contact candidate)")]
    public float plantHeightMax = 0.20f;   // 踝世界Y-板面 ≤ 此值 → 近地候选
    public float plantSpeedMax = 0.25f;    // 水平速度 ≤ 此值 → 慢
    public float minPlantTime = 0.10f;     // 接触候选最短持续(有效新帧时间)

    [Header("Release: lift (relative to plant base)")]
    public float riseThreshold = 0.14f;    // 相对 plantBaseY 抬升超过 → 明确抬脚(立即放)
    public float liftVSpeed = 0.25f;       // 向上速度(需结合小幅抬升+连续证据)
    public float vEvidenceRise = 0.03f;    // 速度路径需要的最小抬升
    public float vEvidenceTime = 0.06f;    // 速度路径连续证据时长(有效新帧)

    [Header("Release: low-step deviation")]
    public float stepDev = 0.15f;          // 独立目标相对锚点水平偏离超过 → 低位迈步释放
    public float stepDevTime = 0.06f;      // 偏离持续(有效新帧)

    [Header("Release: stuck (anchor unreachable)")]
    public float unreachTolerance = 0.02f; // 超出腿可达的容差(不超过此不判不可达)
    public float unreachTime = 0.15f;      // 不可达持续(有效新帧)

    [Header("Rates")]
    public float plantEntryRate = 10f;
    public float plantExitRateFast = 80f;  // 明确抬脚/不可达等:快速放

    [Header("Smoothing")]
    public float swingLowpassTimeConst = 0.05f;
    public float reachMargin = 0.002f;
    public float footRotateSmooth = 30f;

    [Header("Speed deadzone")]
    public float hSpeedDeadzone = 0.05f;
    public float vSpeedDeadzone = 0.05f;

    [Header("Diagnostics")]
    public bool showOverlay = true;
    public bool logDiagnostics = false;

    // 骨骼
    private Transform _hips, _lUp, _lLo, _lFoot, _lToe, _rUp, _rLo, _rFoot, _rToe;
    private Quaternion _lUpRest, _lLoRest, _rUpRest, _rLoRest;
    private Vector3 _lUpAxis, _lLoAxis, _rUpAxis, _rLoAxis;
    private float _lThigh, _lShin, _rThigh, _rShin;
    private Vector3 _hipsForwardLocal = Vector3.forward;
    private Quaternion _lFootRestLocal, _rFootRestLocal;
    private Vector3 _lFootFwdLocal, _lFootUpLocal, _rFootFwdLocal, _rFootUpLocal;

    private struct FootState
    {
        public bool hasAnchor;         // 是否已锁定(进入支撑)
        public Vector3 anchor;         // 锁定锚点(y=板面+踝偏移)
        public float plantBaseY;       // 接触候选期计划踝位Y的中值(抬升基准)
        public int anchorId;
        public float planted;          // 0..1 支撑权重
        public bool releasing;         // 完整释放状态:一旦true持续降权到0,中途不回升
        public string releaseReason;   // lifted/liftedV/step/stuck/none/--
        public bool smoothInit;
        public Vector3 smoothPlan;
        public bool init;
        public Vector3 prevPlan;
        public double prevTime;
        public int lastFrameId;
        public float hSpeed, vSpeed;
        // 接触候选统计
        public float candTime;
        public double candSumX, candSumZ;
        public int candCount;
        public float[] candYs;         // 计划踝位Y 环形缓冲(取中值)
        public int candYHead;          // 下一个写入位置
        // 释放证据计时(有效新帧)
        public float vEvidTime;
        public float stepDevTimeAcc;
        public float unreachTimeAcc;
        // 诊断
        public float liftRise, anchorDev, anchorDist;
        public bool unreach;
        public bool contact;
        public float distToAnchor;
        public Vector3 polePerp;
    }
    private FootState _l, _r;
    private const int CandBuffer = 16;

    private float _frontAgeMs = float.PositiveInfinity;
    private readonly string _session = System.Guid.NewGuid().ToString("N");
    private int _seq;

    // ── 结构化诊断快照（供 FootIkUdpSender / Python 回传）──
    public struct FootDiag
    {
        public bool hasAnchor;
        public int anchorId;
        public float planted;
        public bool releasing;
        public string status;        // zh: 支撑/释放/摆动
        public string releaseReason; // zh: 抬脚/持续上升/迈步偏离/锚点不可达/—/支撑
        public float liftRise;       // m
        public float anchorDev;      // m
        public float hipToAnchor;    // m
        public float ankleToAnchor;  // m
        public float vSpeed;         // m/s
        public bool unreach;
        public float unreachTimeAcc; // s
        public bool contact;
    }
    public struct Snapshot
    {
        public string session;
        public int seq;
        public bool ikEnabled;
        public bool controllerEnabled;
        public float frontAgeMs;
        public bool published;      // 是否已发布过(有数据)
        public FootDiag left;
        public FootDiag right;
    }

    public Snapshot GetDiagnostics()
    {
        var s = new Snapshot();
        s.session = _session;
        s.seq = ++_seq;
        s.ikEnabled = enableFootIK;
        s.controllerEnabled = enabled;
        s.frontAgeMs = _frontAgeMs;
        s.published = animator != null && animator.isHuman && poseDriver != null;
        s.left = BuildFootDiag(_l);
        s.right = BuildFootDiag(_r);
        return s;
    }

    FootDiag BuildFootDiag(FootState st)
    {
        var d = new FootDiag();
        d.hasAnchor = st.hasAnchor;
        d.anchorId = st.anchorId;
        d.planted = st.planted;
        d.releasing = st.releasing;
        d.liftRise = st.liftRise;
        d.anchorDev = st.anchorDev;
        d.hipToAnchor = st.anchorDist;
        d.ankleToAnchor = st.distToAnchor;
        d.vSpeed = st.vSpeed;
        d.unreach = st.unreach;
        d.unreachTimeAcc = st.unreachTimeAcc;
        d.contact = st.contact;
        if (st.releasing) { d.status = "释放"; d.releaseReason = ZhReason(st.releaseReason); }
        else if (st.hasAnchor) { d.status = st.planted >= 0.5f ? "支撑" : "释放中"; d.releaseReason = "支撑"; }
        else { d.status = "摆动"; d.releaseReason = "—"; }
        return d;
    }

    static string ZhReason(string r)
    {
        switch (r)
        {
            case "lifted": return "抬脚";
            case "liftedV": return "持续上升";
            case "step": return "迈步偏离";
            case "stuck": return "锚点不可达";
            case "sliding": return "滑移";
            case "none": return "支撑";
            default: return "—";
        }
    }

    void Awake()
    {
        if (animator == null) animator = GetComponent<Animator>();
        if (animator == null || !animator.isHuman) { enabled = false; return; }

        _hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        _lUp = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        _lLo = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
        _lFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        _lToe = animator.GetBoneTransform(HumanBodyBones.LeftToes);
        _rUp = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        _rLo = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
        _rFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        _rToe = animator.GetBoneTransform(HumanBodyBones.RightToes);

        if (_hips == null || _lUp == null || _lLo == null || _lFoot == null ||
            _rUp == null || _rLo == null || _rFoot == null) { enabled = false; return; }

        CacheBone(_lUp, _lLo, out _lUpRest, out _lUpAxis);
        CacheBone(_lLo, _lFoot, out _lLoRest, out _lLoAxis);
        CacheBone(_rUp, _rLo, out _rUpRest, out _rUpAxis);
        CacheBone(_rLo, _rFoot, out _rLoRest, out _rLoAxis);

        _lThigh = Mathf.Max(0.05f, _lLo.localPosition.magnitude);
        _lShin = Mathf.Max(0.05f, _lFoot.localPosition.magnitude);
        _rThigh = Mathf.Max(0.05f, _rLo.localPosition.magnitude);
        _rShin = Mathf.Max(0.05f, _rFoot.localPosition.magnitude);

        if (_hips != null) _hipsForwardLocal = Quaternion.Inverse(_hips.rotation) * _hips.forward;
        CacheFootBasis(_lFoot, _lToe, out _lFootRestLocal, out _lFootFwdLocal, out _lFootUpLocal);
        CacheFootBasis(_rFoot, _rToe, out _rFootRestLocal, out _rFootFwdLocal, out _rFootUpLocal);

        ResetState(ref _l); ResetState(ref _r);
        EnablePoseDriver();
    }

    void EnablePoseDriver()
    {
        if (poseDriver == null && animator != null) poseDriver = animator.GetComponent<HumanoidPoseDriver>();
        if (poseDriver != null) poseDriver.footIKActive = enableFootIK;
    }

    void ResetState(ref FootState s)
    {
        s = new FootState();
        s.prevTime = -1;
        s.lastFrameId = -1;
        s.planted = 0f;
        s.releaseReason = "--";
        s.candYs = new float[CandBuffer];
        s.candYHead = 0;
    }

    void CacheBone(Transform bone, Transform child, out Quaternion rest, out Vector3 axis)
    {
        rest = bone.localRotation;
        axis = child == null ? Vector3.forward : child.localPosition.normalized;
        if (axis.sqrMagnitude < 1e-6f) axis = Vector3.forward;
    }

    void CacheFootBasis(Transform foot, Transform toe, out Quaternion rest, out Vector3 fwdLocal, out Vector3 upLocal)
    {
        rest = foot.localRotation;
        fwdLocal = toe != null ? toe.localPosition.normalized : Vector3.forward;
        if (fwdLocal.sqrMagnitude < 1e-6f) fwdLocal = Vector3.forward;
        upLocal = foot.InverseTransformDirection(Vector3.up);
        upLocal = Vector3.ProjectOnPlane(upLocal, fwdLocal).normalized;
        if (upLocal.sqrMagnitude < 1e-6f) upLocal = Vector3.up;
    }

    void LateUpdate()
    {
        EnablePoseDriver();
        if (!enableFootIK || animator == null || !animator.isHuman) return;
        if (poseDriver == null) return;

        _frontAgeMs = poseDriver.receiver != null ? poseDriver.receiver.LatestPacketAgeMs : float.PositiveInfinity;

        SolveFoot(ref _l, poseDriver.PlannedLeftAnkle, true);
        SolveFoot(ref _r, poseDriver.PlannedRightAnkle, false);

        if (logDiagnostics && Time.frameCount % 30 == 0)
            Debug.Log($"[FootIK] age={_frontAgeMs:F0}ms | L:[{Diag(_l)}] | R:[{Diag(_r)}]");
    }

    string Diag(FootState s) =>
        $"plant={s.planted:F2} anchor={s.anchorId} contact={s.contact} rel={s.releaseReason} " +
        $"rise={s.liftRise:F2} dev={s.anchorDev:F2} adist={s.anchorDist:F2} " +
        $"dist={(s.hasAnchor ? s.distToAnchor.ToString("F3") : "-")} v={s.vSpeed:+0.00;-0.00} unreach={s.unreach}";

    void SolveFoot(ref FootState st, Vector3 rawPlan, bool isLeft)
    {
        Transform up = isLeft ? _lUp : _rUp;
        Transform lo = isLeft ? _lLo : _rLo;
        Transform foot = isLeft ? _lFoot : _rFoot;
        Transform toe = isLeft ? _lToe : _rToe;
        Quaternion upRest = isLeft ? _lUpRest : _rUpRest;
        Quaternion loRest = isLeft ? _lLoRest : _rLoRest;
        Vector3 upAxis = isLeft ? _lUpAxis : _rUpAxis;
        Vector3 loAxis = isLeft ? _lLoAxis : _rLoAxis;
        float thigh = isLeft ? _lThigh : _rThigh;
        float shin = isLeft ? _lShin : _rShin;
        Quaternion footRest = isLeft ? _lFootRestLocal : _rFootRestLocal;
        Vector3 footFwdLocal = isLeft ? _lFootFwdLocal : _rFootFwdLocal;
        Vector3 footUpLocal = isLeft ? _lFootUpLocal : _rFootUpLocal;

        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        bool newFrame = false;
        bool validFrame = false;
        float effDt = dt;

        var recv = poseDriver.receiver;
        if (recv != null && recv.LatestFrameId != st.lastFrameId)
        {
            double now = recv.LatestTime;
            if (st.lastFrameId >= 0 && now > st.prevTime && now - st.prevTime > 1e-4)
            {
                validFrame = true;
                float fr = (float)(1.0 / (now - st.prevTime));
                Vector3 dH = new Vector3(rawPlan.x - st.prevPlan.x, 0f, rawPlan.z - st.prevPlan.z);
                st.hSpeed = dH.magnitude * fr;
                st.vSpeed = (rawPlan.y - st.prevPlan.y) * fr;
                effDt = (float)(now - st.prevTime);
            }
            st.lastFrameId = recv.LatestFrameId;
            st.prevPlan = rawPlan;
            st.prevTime = now;
            st.init = true;
            newFrame = true;
        }
        else if (recv == null)
        {
            st.hSpeed = (new Vector3(rawPlan.x - st.prevPlan.x, 0f, rawPlan.z - st.prevPlan.z)).magnitude / dt;
            st.vSpeed = (rawPlan.y - st.prevPlan.y) / dt;
            st.prevPlan = rawPlan;
            validFrame = true;
            newFrame = true;
        }

        if (st.hSpeed < hSpeedDeadzone) st.hSpeed = 0f;
        if (Mathf.Abs(st.vSpeed) < vSpeedDeadzone) st.vSpeed = 0f;

        // 低通计划踝位（仅显示/摆动跟踪）
        if (!st.smoothInit) { st.smoothPlan = rawPlan; st.smoothInit = true; }
        else
        {
            float a = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, swingLowpassTimeConst));
            st.smoothPlan = Vector3.Lerp(st.smoothPlan, rawPlan, a);
        }

        float footH = rawPlan.y - boardY;
        bool low = footH <= plantHeightMax;
        bool slow = st.hSpeed <= plantSpeedMax;
        bool candidate = low && slow;

        // ── 候选统计（仅有效新帧）──
        if (newFrame && validFrame)
        {
            if (candidate)
            {
                st.candTime += effDt;
                st.candSumX += rawPlan.x;
                st.candSumZ += rawPlan.z;
                st.candCount++;
                st.candYs[st.candYHead] = rawPlan.y;
                st.candYHead = (st.candYHead + 1) % CandBuffer;
            }
            else
            {
                st.candTime = 0f;
                st.candSumX = st.candSumZ = 0;
                st.candCount = 0;
                st.candYHead = 0;
            }
        }

        // ── 锚点生命周期 ──
        if (!st.hasAnchor)
        {
            if (!st.releasing && st.candTime >= minPlantTime && candidate && st.candCount > 0)
            {
                // plantBaseY = 候选期计划踝位Y 的中值
                float baseY = MedianY(st);
                st.plantBaseY = baseY;
                st.anchor = new Vector3((float)(st.candSumX / st.candCount), boardY + ankleOffset,
                                        (float)(st.candSumZ / st.candCount));
                st.hasAnchor = true;
                st.anchorId++;
                st.planted = 0f;
                st.releasing = false;
                st.releaseReason = "none";
                st.vEvidTime = 0f; st.stepDevTimeAcc = 0f; st.unreachTimeAcc = 0f;
            }
            st.distToAnchor = 0f;
        }
        else
        {
            float liftRise = rawPlan.y - st.plantBaseY;
            float anchorDev = HorizontalLen(st.smoothPlan - st.anchor);
            float anchorDist = Vector3.Distance(up.position, st.anchor);
            st.liftRise = liftRise;
            st.anchorDev = anchorDev;
            st.anchorDist = anchorDist;
            // 超出“真实腿长 + 容差”才判不可达（避免把站立时近伸直的腿误判为不可达）
            float reach = thigh + shin + unreachTolerance;

            if (!st.releasing)
            {
                // A) 明确抬升(相对基准) → 立即放
                if (liftRise > riseThreshold)
                {
                    st.releasing = true;
                    st.releaseReason = "lifted";
                }
                // D) 持续不可达(针对锚点,含容差) → 放
                else if (anchorDist > reach)
                {
                    st.unreachTimeAcc += effDt;
                    if (st.unreachTimeAcc >= unreachTime) { st.releasing = true; st.releaseReason = "stuck"; }
                }
                else
                {
                    st.unreachTimeAcc = 0f;
                    // B) 向上速度 + 小幅实际抬升 + 连续证据
                    if (st.vSpeed > liftVSpeed && liftRise > vEvidenceRise)
                    {
                        st.vEvidTime += effDt;
                        if (st.vEvidTime >= vEvidenceTime) { st.releasing = true; st.releaseReason = "liftedV"; }
                    }
                    else st.vEvidTime = Mathf.Max(0f, st.vEvidTime - effDt);

                    // C) 低位迈步释放(独立目标偏离锚点)
                    if (anchorDev > stepDev)
                    {
                        st.stepDevTimeAcc += effDt;
                        if (st.stepDevTimeAcc >= stepDevTime) { st.releasing = true; st.releaseReason = "step"; }
                    }
                    else st.stepDevTimeAcc = Mathf.Max(0f, st.stepDevTimeAcc - effDt);

                    if (!st.releasing)
                    {
                        st.releaseReason = "none";
                        st.planted = Mathf.MoveTowards(st.planted, 1f, plantEntryRate * dt);
                    }
                }
            }

            // 释放状态：持续降权到0，不中途回升
            if (st.releasing)
            {
                st.planted = Mathf.MoveTowards(st.planted, 0f, plantExitRateFast * dt);
                if (st.planted <= 0f)
                {
                    st.hasAnchor = false;
                    st.planted = 0f;
                    st.releasing = false;
                    // 清候选统计：下次必须重新接触确认
                    st.candTime = 0f; st.candSumX = st.candSumZ = 0; st.candCount = 0; st.candYHead = 0;
                    st.vEvidTime = 0f; st.stepDevTimeAcc = 0f; st.unreachTimeAcc = 0f;
                    st.distToAnchor = 0f;
                }
            }
        }

        // ── 目标 ──
        Vector3 target = st.hasAnchor
            ? Vector3.Lerp(st.smoothPlan, st.anchor, st.planted)
            : st.smoothPlan;

        Solve2BoneIKDirect(up, lo, target, upRest, upAxis, loRest, loAxis, thigh, shin, ref st.polePerp, out bool clamped);
        st.unreach = clamped;
        st.distToAnchor = st.hasAnchor ? Vector3.Distance(foot.position, st.anchor) : 0f;

        RestoreFootFlat(foot, toe, footRest, footFwdLocal, footUpLocal);
    }

    float MedianY(FootState st)
    {
        int n = Mathf.Min(st.candCount, CandBuffer);
        if (n <= 0) return st.prevPlan.y;
        float[] tmp = new float[n];
        int start = (st.candYHead - n + CandBuffer) % CandBuffer;
        for (int i = 0; i < n; i++) tmp[i] = st.candYs[(start + i) % CandBuffer];
        System.Array.Sort(tmp);
        if ((n & 1) == 1) return tmp[n / 2];
        return (tmp[n / 2 - 1] + tmp[n / 2]) * 0.5f;
    }

    static float HorizontalLen(Vector3 v)
    {
        v.y = 0f;
        return v.magnitude;
    }

    void Solve2BoneIKDirect(Transform up, Transform lo, Vector3 targetAnkle,
        Quaternion upRest, Vector3 upAxis, Quaternion loRest, Vector3 loAxis,
        float thigh, float shin, ref Vector3 cachedPolePerp, out bool clamped)
    {
        clamped = false;
        Vector3 hipPos = up.position;
        Vector3 toTarget = targetAnkle - hipPos;
        float rawD = toTarget.magnitude;
        float maxD = thigh + shin - reachMargin;
        float minD = Mathf.Abs(thigh - shin) + reachMargin;

        if (rawD > maxD + 1e-4f || rawD < minD - 1e-4f) clamped = true;
        float d = Mathf.Clamp(rawD, minD, maxD);
        Vector3 dirN = d > 1e-5f ? toTarget.normalized : Vector3.down;
        Vector3 clampedTarget = hipPos + dirN * d;

        float a = (thigh * thigh - shin * shin + d * d) / (2f * d);
        float h = Mathf.Sqrt(Mathf.Max(0f, thigh * thigh - a * a));

        Vector3 pole = _hips != null ? (_hips.rotation * _hipsForwardLocal) : Vector3.forward;
        Vector3 desired = pole - Vector3.Project(pole, dirN);
        if (desired.sqrMagnitude < 1e-6f)
        {
            if (cachedPolePerp.sqrMagnitude < 1e-6f) desired = Vector3.Cross(dirN, Vector3.up);
            else desired = cachedPolePerp;
        }
        if (desired.sqrMagnitude < 1e-6f) desired = Vector3.forward;
        desired = Vector3.ProjectOnPlane(desired, dirN);
        if (desired.sqrMagnitude < 1e-6f) desired = Vector3.Cross(dirN, Vector3.up);
        desired.Normalize();
        if (cachedPolePerp.sqrMagnitude > 1e-6f && Vector3.Dot(cachedPolePerp, desired) < 0f)
            desired = -desired;
        cachedPolePerp = desired;

        Vector3 kneePos = hipPos + dirN * a + desired * h;
        OrientBoneDirect(up, upRest, upAxis, (kneePos - hipPos).normalized);
        OrientBoneDirect(lo, loRest, loAxis, (clampedTarget - kneePos).normalized);
    }

    void OrientBoneDirect(Transform bone, Quaternion rest, Vector3 restAxisLocal, Vector3 desiredWorldDir)
    {
        if (bone == null || desiredWorldDir.sqrMagnitude < 1e-6f) return;
        Vector3 desiredParent = bone.parent != null
            ? bone.parent.InverseTransformDirection(desiredWorldDir.normalized)
            : desiredWorldDir.normalized;
        if (desiredParent.sqrMagnitude < 1e-6f) return;
        Vector3 restParent = rest * restAxisLocal;
        if (restParent.sqrMagnitude < 1e-6f) return;
        Quaternion delta = Quaternion.FromToRotation(restParent.normalized, desiredParent.normalized);
        bone.localRotation = delta * rest;
    }

    void RestoreFootFlat(Transform foot, Transform toe, Quaternion rest,
        Vector3 fwdLocal, Vector3 upLocal)
    {
        if (foot == null) return;
        Vector3 bodyFwd = _hips != null ? (_hips.rotation * _hipsForwardLocal) : Vector3.forward;
        Vector3 fwd = bodyFwd; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
        fwd.Normalize();
        Vector3 desiredUp = Vector3.up;
        if (Mathf.Abs(Vector3.Dot(fwd, desiredUp)) > 0.999f) desiredUp = Vector3.forward;
        Vector3 upFlat = Vector3.ProjectOnPlane(desiredUp, fwd).normalized;
        if (upFlat.sqrMagnitude < 1e-6f) upFlat = Vector3.ProjectOnPlane(Vector3.forward, fwd).normalized;

        Vector3 desiredParentFwd = foot.parent != null ? foot.parent.InverseTransformDirection(fwd) : fwd;
        Vector3 desiredParentUp = foot.parent != null ? foot.parent.InverseTransformDirection(upFlat) : upFlat;
        Vector3 restParentFwd = rest * fwdLocal;
        Vector3 restParentUp = rest * upLocal;
        Quaternion restBasis = SafeLook(restParentFwd, restParentUp);
        Quaternion targetBasis = SafeLook(desiredParentFwd, desiredParentUp);
        Quaternion delta = targetBasis * Quaternion.Inverse(restBasis);
        Quaternion targetLocal = delta * rest;
        foot.localRotation = Quaternion.Slerp(foot.localRotation, targetLocal, Time.deltaTime * footRotateSmooth);
    }

    Quaternion SafeLook(Vector3 fwd, Vector3 up)
    {
        if (fwd.sqrMagnitude < 1e-6f) return Quaternion.identity;
        fwd.Normalize();
        up = Vector3.ProjectOnPlane(up, fwd);
        if (up.sqrMagnitude < 1e-6f) up = Vector3.ProjectOnPlane(Vector3.up, fwd);
        if (up.sqrMagnitude < 1e-6f) return Quaternion.identity;
        return Quaternion.LookRotation(fwd, up.normalized);
    }

    void OnGUI()
    {
        if (!showOverlay || !enableFootIK) return;
        float x = 10, y = 220, w = 500, lh = 15;
        GUI.Box(new Rect(x, y, w, lh * 8 + 8), "");
        y += 4;
        void L(string s) { GUI.Label(new Rect(x + 6, y, w - 12, lh), s); y += lh; }
        L($"[FootIK] 数据帧龄 {(_frontAgeMs == float.PositiveInfinity ? "--" : _frontAgeMs.ToString("F0"))} ms  (非端到端延迟)");
        L($"  L: {Diag(_l)}");
        L($"  R: {Diag(_r)}");
        L("  plant=支撑权重 anchor=锚点编号 rel=释放原因 rise=相对基准抬升 dev=偏离锚点 adist=髋->锚点");
        L($"  [空格测试] 关闭EnableFootIK 对比 FK。");
    }

    /// <summary>应用修正版释放参数（用于更新场景里缓存的旧默认值）。</summary>
    public void ApplyCorrectedParams()
    {
        plantHeightMax = 0.20f;
        plantSpeedMax = 0.25f;
        minPlantTime = 0.10f;
        riseThreshold = 0.14f;
        liftVSpeed = 0.25f;
        vEvidenceRise = 0.03f;
        vEvidenceTime = 0.06f;
        stepDev = 0.15f;
        stepDevTime = 0.06f;
        unreachTolerance = 0.02f;
        unreachTime = 0.15f;
        vSpeedDeadzone = 0.05f;
        hSpeedDeadzone = 0.05f;
    }
}
