using UnityEngine;

/// <summary>
/// Stage A (修正版 v2) — 脚锁 + 直解两骨腿 IK。
///
/// v2 修正（吸收评审意见，仅改本文件）：
///  A1) 锚点生命周期：接触候选期(近地+慢+不抬)用有效新帧的多帧平均建锚，anchorId 递增；
///      支持相锚点不动；只在释放权重降到 0 时才清锚（不会中途删掉正在参与混合的锚）。
///  A2) 放脚去抖：轻微异常用短去抖(以有效新帧时间差累计)；明确抬升/滑移立即快速放。
///  A3) 膝方向缓存：重投影到当前腿轴垂直平面，退化时保留连续方向（避免膝/腿翻转）。
///  A4) 摆动期按时间常数低通计划踝位（alpha=1-exp(-dt/tc)），不同帧率下一致。
///  A5) 不可达：先求投影后的 clampedTarget，膝与小腿都用同一 clampedTarget（不同名不一致）。
///  A6) 诊断：数据帧龄、contact、planted、anchorId、释放原因、脚到锚点距离(无锚显示不适用)、垂速、不可达。
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

    [Header("Stance (per effective data frame)")]
    public float plantHeightMax = 0.28f;      // 踝世界Y-板面 ≤ 此值 → 近地候选
    public float plantSpeedMax = 0.45f;       // 水平速度 ≤ 此值 → 慢
    public float exitSpeedMax = 0.85f;        // 超过且不低 → 视为滑移(脱离)
    public float clearLiftHeight = 0.42f;     // 踝世界Y-板面 > 此值 → 明确离地(高度抗噪声,立即放)
    public float minPlantTime = 0.10f;        // 接触候选最短持续(有效新帧时间)
    public float swingDebounce = 0.06f;       // 轻微滑移的短去抖(有效新帧时间)
    public float plantEntryRate = 10f;
    public float plantExitRateNormal = 20f;   // 滑移脱离
    public float plantExitRateFast = 80f;     // 明确抬升,立即放

    [Header("Smoothing")]
    public float swingLowpassTimeConst = 0.05f; // 摆动期计划踝位低通时间常数(秒)
    public float reachMargin = 0.002f;
    public float footRotateSmooth = 30f;

    [Header("Speed deadzone")]
    public float hSpeedDeadzone = 0.05f;      // 水平速度低于此视为静止
    public float vSpeedDeadzone = 0.05f;      // 垂直速度死区(小噪声不算抬脚)

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
        public bool hasAnchor;
        public Vector3 anchor;
        public int anchorId;
        public float planted;         // 0=摆动 1=完全支撑(钉住锚点)
        public bool smoothInit;
        public Vector3 smoothPlan;    // 低通后的计划踝位(摆动跟踪用)
        public bool init;
        public Vector3 prevPlan;      // 上一数据帧计划踝位(算速度)
        public double prevTime;
        public int lastFrameId;
        public float hSpeed, vSpeed;
        public float candTime;        // 接触候选持续(有效新帧时间)
        public double candSumX, candSumZ;
        public int candCount;
        public float swingTime;       // 轻微滑移去抖(有效新帧时间)
        public Vector3 polePerp;      // 缓存的膝极值方向
        public bool unreach;
        public bool contact;          // 诊断
        public string releaseReason;  // 诊断: lifted/sliding/none/--
        public float distToAnchor;    // 诊断: 解算后脚踝->锚点距离
    }
    private FootState _l, _r;

    private float _frontAgeMs = float.PositiveInfinity;

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
        double now = 0;
        bool newFrame = false;
        float effDt = dt;

        var recv = poseDriver.receiver;
        if (recv != null && recv.LatestFrameId != st.lastFrameId)
        {
            now = recv.LatestTime;
            if (st.init && st.lastFrameId >= 0 && now > st.prevTime && now - st.prevTime > 1e-4)
            {
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
            newFrame = true;
        }

        // 速度死区
        if (st.hSpeed < hSpeedDeadzone) st.hSpeed = 0f;
        if (Mathf.Abs(st.vSpeed) < vSpeedDeadzone) st.vSpeed = 0f;

        // 低通计划踝位（摆动跟踪用，时间常数）
        if (!st.smoothInit) { st.smoothPlan = rawPlan; st.smoothInit = true; }
        else
        {
            float a = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, swingLowpassTimeConst));
            st.smoothPlan = Vector3.Lerp(st.smoothPlan, rawPlan, a);
        }

        float footH = rawPlan.y - boardY;
        bool low = footH <= plantHeightMax;
        bool slow = st.hSpeed <= plantSpeedMax;
        bool liftedHigh = footH > clearLiftHeight;   // 明确离地（高度信号，抗单帧速度噪声）
        bool candidate = low && slow && !liftedHigh;
        st.contact = candidate;

        if (newFrame)
        {
            if (candidate)
            {
                st.candTime += effDt;
                st.candSumX += rawPlan.x;
                st.candSumZ += rawPlan.z;
                st.candCount++;
            }
            else
            {
                st.candTime = 0f;
                st.candSumX = st.candSumZ = 0;
                st.candCount = 0;
            }
        }

        // ── 锚点生命周期 ──
        if (!st.hasAnchor)
        {
            // 接触候选满足最短时间 → 建新锚（anchorId 递增），用候选期平均落点
            if (st.candTime >= minPlantTime && candidate && st.candCount > 0)
            {
                st.anchor = new Vector3((float)(st.candSumX / st.candCount), boardY + ankleOffset,
                                        (float)(st.candSumZ / st.candCount));
                st.hasAnchor = true;
                st.anchorId++;
                st.planted = 0f;
                st.releaseReason = "none";
            }
            st.distToAnchor = 0f;
        }
        else
        {
            // 明确离地(高度) → 立即快速放；滑移(水平快+不低) → 短去抖后放；否则维持支撑
            if (liftedHigh)
            {
                st.planted = Mathf.MoveTowards(st.planted, 0f, plantExitRateFast * dt);
                st.releaseReason = "lifted";
                st.swingTime = 0f;
            }
            else
            {
                bool fastAway = st.hSpeed > exitSpeedMax && !low;
                if (fastAway)
                {
                    st.swingTime += effDt;
                    st.releaseReason = "sliding";
                    if (st.swingTime >= swingDebounce)
                        st.planted = Mathf.MoveTowards(st.planted, 0f, plantExitRateNormal * dt);
                }
                else
                {
                    st.swingTime = 0f;
                    st.releaseReason = "none";
                    st.planted = Mathf.MoveTowards(st.planted, 1f, plantEntryRate * dt);
                }
            }

            // 释放权重降到 0 → 清锚（下一次落脚必须重新建锚）
            if (st.planted <= 0f)
            {
                st.hasAnchor = false;
                st.planted = 0f;
                if (st.releaseReason == "none") st.releaseReason = "none";
            }
        }

        // ── 目标 = 锚点与"低通计划位"的插值（planted=1→锚；planted=0→摆动跟踪）──
        Vector3 target = st.hasAnchor
            ? Vector3.Lerp(st.smoothPlan, st.anchor, st.planted)
            : st.smoothPlan;

        // 直解 IK（不可达先投影，膝/小腿用同一 clampedTarget）
        Solve2BoneIKDirect(up, lo, target, upRest, upAxis, loRest, loAxis, thigh, shin,
                           ref st.polePerp, out bool clamped);
        st.unreach = clamped;

        // 诊断：解算后脚踝到锚点距离（无锚 = 0，显示不适用）
        st.distToAnchor = st.hasAnchor ? Vector3.Distance(foot.position, st.anchor) : 0f;

        // 恢复脚朝向
        RestoreFootFlat(foot, toe, footRest, footFwdLocal, footUpLocal);
    }

    /// <summary>直解两骨 IK：不可达时先把目标投影到可达范围，膝与小腿用同一投影目标。
    /// polePerp 缓存并重投影到当前腿轴垂直平面，退化时保留连续方向。</summary>
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

        // 同一投影目标：膝与小腿都用它（修正"钳制距离却解到原始目标"的不一致）
        Vector3 clampedTarget = hipPos + dirN * d;

        float a = (thigh * thigh - shin * shin + d * d) / (2f * d);
        float h = Mathf.Sqrt(Mathf.Max(0f, thigh * thigh - a * a));

        // 膝极值：当前期望方向重投影到垂直平面 → 连续性
        Vector3 pole = _hips != null ? (_hips.rotation * _hipsForwardLocal) : Vector3.forward;
        Vector3 desired = pole - Vector3.Project(pole, dirN);
        if (desired.sqrMagnitude < 1e-6f)
        {
            // 退化：保留缓存连续方向
            if (cachedPolePerp.sqrMagnitude < 1e-6f) desired = Vector3.Cross(dirN, Vector3.up);
            else desired = cachedPolePerp;
        }
        if (desired.sqrMagnitude < 1e-6f) desired = Vector3.forward;
        desired = Vector3.ProjectOnPlane(desired, dirN);
        if (desired.sqrMagnitude < 1e-6f) desired = Vector3.Cross(dirN, Vector3.up);
        desired.Normalize();

        // 连续性：与上一帧保持同向，避免翻转
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
        float x = 10, y = 220, w = 470, lh = 15;
        GUI.Box(new Rect(x, y, w, lh * 7 + 8), "");
        y += 4;
        void L(string s) { GUI.Label(new Rect(x + 6, y, w - 12, lh), s); y += lh; }
        L($"[FootIK] 数据帧龄 {(_frontAgeMs == float.PositiveInfinity ? "--" : _frontAgeMs.ToString("F0"))} ms  (非端到端延迟)");
        L($"  L: {Diag(_l)}");
        L($"  R: {Diag(_r)}");
        L("  plant=支撑权重  anchor=锚点编号(每次换锚+1)  rel=释放原因  dist=脚->锚距(无锚=-)");
        L($"  [空格测] 关闭EnableFootIK 对比 FK。");
    }
}
