using UnityEngine;

/// <summary>
/// 双摄 Root 运动估计器 v6 — 按 UDP 包时间计算速度
/// ======================================
/// v6 变更（相对 v5）：
///   - 只按新包处理（LatestFrameId 检测），消除 UDP 包率(30Hz) vs 渲染帧率(60Hz+) 的量化。
///   - 速度按包间 _time 差计算，follower 直接消费速度，不再在 Unity 帧里 delta/dt。
///   - 有效性(_calib_x_valid/z_valid) 门控：无效/恢复首帧只重新锚定，不输出位移。
///   - 左摄失同步/包间间隙过大 → 重新锚定，恢复时不突跳。
///   - 低通滤波只在有新包时执行，与渲染帧率解耦。
/// 算法：
///   前摄 foot 世界X 帧间差值 → 左右位移 (dirX)
///   左摄 foot 世界Z 帧间差值 → 前后位移 (dirZ)
///   差值 / 包间dt → 瞬时速度 → 低通 → rootVelocity（非新包帧保持，防帧间速度跳动）
/// </summary>
public class DualCameraRootEstimator : MonoBehaviour
{
    [Header("References")]
    public UdpPoseReceiver frontReceiver;
    public UdpPoseReceiver leftReceiver;

    [Header("Time Sync")]
    [Tooltip("双摄帧最大允许时间差（秒）")]
    [Range(0.01f, 0.3f)] public float maxTimeDiff = 0.1f;
    [Tooltip("同一路包间最大间隔（秒），超过视为断流，速度归零")]
    [Range(0.1f, 2f)] public float maxGapTime = 0.5f;

    [Header("Filter")]
    [Range(0.02f, 0.9f)] public float lowPassAlpha = 0.25f;
    public float deadZoneMeters = 0.005f;

    [Header("Safety")]
    [Tooltip("单包最大位移(m)，超过归零")]
    public float maxDeltaPerFrame = 3f;

    [Header("Direction Correction")]
    [Tooltip("左右(前摄 world X)位移方向反了时勾选，立即翻转")]
    public bool invertRootX = false;
    [Tooltip("前后(左摄 world Z)位移方向反了时勾选，立即翻转")]
    public bool invertRootZ = false;

    [Header("Calibration")]
    public KeyCode calibrateKey = KeyCode.C;
    public bool autoCalibrateOnStart = true;

    // ── 内部状态 ──
    private float _prevFrontWorldX, _prevLeftWorldZ;
    private double _prevFrontTime, _prevLeftTime;
    private int _lastProcessedFrameId = -1;
    private float _lastPacketWallTime = -1f;
    private bool _frontInvalidated = true;
    private bool _leftInvalidated = true;
    private float _smoothVelX, _smoothVelZ;
    private bool _hasLeft;

    // 有效性去抖：可见度抖动不触发重新锚定，只有连续多包无效才判为真无效。
    private const int InvalidateAfterStreak = 3;
    private int _frontValidStreak, _frontInvalidStreak;
    private int _leftValidStreak, _leftInvalidStreak;

    // ── 输出 ──
    public Vector3 rootVelocity { get; private set; }
    public bool isCalibrated { get; private set; }

    // Debug
    public float rawDX { get; private set; }
    public float rawDZ { get; private set; }
    public Vector3 rootDelta { get { return rootVelocity * Time.deltaTime; } }

    void Start()
    {
        if (frontReceiver == null || leftReceiver == null)
        {
            var receivers = FindObjectsOfType<UdpPoseReceiver>();
            foreach (var r in receivers)
            {
                if (r.port == 8888 && frontReceiver == null) frontReceiver = r;
                else if (r.port == 8889 && leftReceiver == null) leftReceiver = r;
            }
        }

        if (frontReceiver == null)
        {
            Debug.LogError("[RootEst] ❌ 未找到前摄 (port 8888)");
            enabled = false;
            return;
        }

        _hasLeft = leftReceiver != null;
        Debug.Log($"[RootEst] ✅ v6 初始化 — {(_hasLeft ? "双摄" : "单摄")}  仅标定模式  safety={maxDeltaPerFrame}m");

        // 不再用定时器无条件校准 —— 由 LateUpdate 在 CalibActive 时才自动校准，
        // 避免 Python 四点标定尚未完成时从零锚点产生一次跳跃。
    }

    void Update()
    {
        if (Input.GetKeyDown(calibrateKey) && frontReceiver != null && frontReceiver.CalibActive)
            Calibrate();
    }

    void LateUpdate()
    {
        if (frontReceiver == null) return;

        // ── 等待 Python 标定激活 ──
        if (!frontReceiver.CalibActive)
        {
            rootVelocity = Vector3.zero;
            return;
        }

        if (!isCalibrated && autoCalibrateOnStart)
        {
            // 标定数据到达但还没校准 → 自动校准
            Calibrate();
            return;
        }

        if (frontReceiver.LatestTime == 0)
        {
            rootVelocity = Vector3.zero;
            return;
        }

        // ── 1. 按新包计算帧间速度 ──
        ComputeRootFromSyncedFrames();
    }

    void ComputeRootFromSyncedFrames()
    {
        // 只处理新包：包率(30Hz)远低于渲染帧率(60Hz+)
        int fid = frontReceiver.LatestFrameId;
        if (fid == _lastProcessedFrameId)
        {
            // 非新包帧保持上一次速度；断流超过 maxGapTime 才归零，防持续漂移
            if (_lastPacketWallTime >= 0f && Time.time - _lastPacketWallTime > maxGapTime)
                rootVelocity = Vector3.zero;
            return;
        }
        _lastProcessedFrameId = fid;
        _lastPacketWallTime = Time.time;

        double frontTime = frontReceiver.LatestTime;
        float curFrontWorldX = frontReceiver.CalibWorldX;
        bool frontValid = DebounceValid(frontReceiver.CalibXValid,
                                        ref _frontValidStreak, ref _frontInvalidStreak);

        // ── X 轴（前摄 foot 世界X）──
        rawDX = AxisDelta(curFrontWorldX, frontTime, frontValid,
                          ref _prevFrontWorldX, ref _prevFrontTime, ref _frontInvalidated,
                          out double frontDt);

        // ── Z 轴（左摄 foot 世界Z）──
        double leftDt = 0;
        if (!_hasLeft)
        {
            rawDZ = 0f;
            _leftInvalidated = true;
        }
        else if (leftReceiver.TryGetClosestFrame(frontTime, out var leftFrame))
        {
            double timeDiff = System.Math.Abs(frontTime - leftFrame.time);
            if (timeDiff > maxTimeDiff)
            {
                // 双摄失同步 → 本包 Z 重新锚定，恢复时不突跳
                rawDZ = 0f;
                _leftInvalidated = true;
                _prevLeftWorldZ = leftFrame.calibWorldZ;
                _prevLeftTime = leftFrame.time;
            }
            else
            {
                rawDZ = AxisDelta(leftFrame.calibWorldZ, leftFrame.time,
                                  DebounceValid(leftFrame.calibZValid,
                                                ref _leftValidStreak, ref _leftInvalidStreak),
                                  ref _prevLeftWorldZ, ref _prevLeftTime, ref _leftInvalidated,
                                  out leftDt);
            }
        }
        else
        {
            rawDZ = 0f;
            _leftInvalidated = true;
        }

        // ── 2. 硬保护（每包位移上限）──
        if (Mathf.Abs(rawDX) > maxDeltaPerFrame)
        {
            Debug.LogWarning($"[RootEst] ⚠️ rawDX={rawDX:F1} 超上限 → 归零");
            rawDX = 0f;
        }
        if (Mathf.Abs(rawDZ) > maxDeltaPerFrame)
        {
            Debug.LogWarning($"[RootEst] ⚠️ rawDZ={rawDZ:F1} 超上限 → 归零");
            rawDZ = 0f;
        }

        // ── 3. 死区（每包位移）──
        if (Mathf.Abs(rawDX) < deadZoneMeters) rawDX = 0f;
        if (Mathf.Abs(rawDZ) < deadZoneMeters) rawDZ = 0f;

        // ── 4. 包间瞬时速度 ──
        float velX = rawDX != 0f ? rawDX / Mathf.Max((float)frontDt, 1f / 240f) : 0f;
        float velZ = rawDZ != 0f ? rawDZ / Mathf.Max((float)leftDt, 1f / 240f) : 0f;

        // ── 5. 低通（只在新包执行，与渲染帧率解耦）──
        _smoothVelX = Mathf.Lerp(_smoothVelX, velX, lowPassAlpha);
        _smoothVelZ = Mathf.Lerp(_smoothVelZ, velZ, lowPassAlpha);

        // ── 6. 输出 ──
        rootVelocity = new Vector3(
            invertRootX ? -_smoothVelX : _smoothVelX, 0f,
            invertRootZ ? -_smoothVelZ : _smoothVelZ);
    }

    /// <summary>
    /// 有效性去抖：可见度偶发抖动（1~2 包无效）仍视为有效、不触发重新锚定；
    /// 只有连续 InvalidateAfterStreak 包无效才判为真无效（由 AxisDelta 重新锚定）。
    /// </summary>
    bool DebounceValid(bool raw, ref int validStreak, ref int invalidStreak)
    {
        if (raw) { validStreak++; invalidStreak = 0; }
        else { invalidStreak++; validStreak = 0; }
        return invalidStreak < InvalidateAfterStreak;
    }

    /// <summary>
    /// 单轴帧间位移计算：
    ///   invalid → 重新锚定，返回 0
    ///   无效→有效恢复首帧 → 只重新锚定，返回 0
    ///   dt 间隙过大(dt>maxGapTime 或 dt<=0) → 只重新锚定，返回 0
    ///   正常 → 返回帧间位移
    /// </summary>
    float AxisDelta(float cur, double t, bool valid,
                    ref float prev, ref double prevT, ref bool invalidated,
                    out double dt)
    {
        if (!valid)
        {
            prev = cur; prevT = t; invalidated = true;
            dt = 0; return 0f;
        }

        float dx = cur - prev;
        prev = cur;
        dt = t - prevT;
        prevT = t;

        if (invalidated)
        {
            // 恢复首帧：只重新锚定，不输出跨遮挡/断流的位移
            invalidated = false;
            dt = 0;
            return 0f;
        }

        if (dt <= 0 || dt > maxGapTime)
        {
            dt = 0;
            return 0f;
        }

        return dx;
    }

    public void Calibrate()
    {
        if (frontReceiver == null) return;

        _prevFrontWorldX = frontReceiver.CalibWorldX;
        _prevFrontTime = frontReceiver.LatestTime;
        _prevLeftWorldZ = _hasLeft ? leftReceiver.CalibWorldZ : 0f;
        _prevLeftTime = _hasLeft ? leftReceiver.LatestTime : 0;
        _lastProcessedFrameId = frontReceiver.LatestFrameId;
        _lastPacketWallTime = Time.time;
        _frontInvalidated = true;  // 校准后首包只重新锚定，不输出位移
        _leftInvalidated = true;
        _frontValidStreak = 0; _frontInvalidStreak = 0;
        _leftValidStreak = 0; _leftInvalidStreak = 0;
        _smoothVelX = 0f;
        _smoothVelZ = 0f;
        rawDX = 0f;
        rawDZ = 0f;
        rootVelocity = Vector3.zero;
        isCalibrated = true;

        Debug.Log($"[RootEst] ✅ 校准完成  原点 prevX={_prevFrontWorldX:F3}  prevZ={_prevLeftWorldZ:F3}");
    }

    void OnGUI()
    {
        if (frontReceiver == null) return;
        if (!frontReceiver.CalibActive)
        {
            GUI.Label(new Rect(10, Screen.height - 24, 500, 24),
                $"[RootEst] ⏳ 等待 Python 标定...  [C]手动校准");
            return;
        }

        if (!isCalibrated)
        {
            GUI.Label(new Rect(10, Screen.height - 24, 500, 24),
                "[RootEst] 📐 检测到标定数据，正在校准...");
            return;
        }

        int x = 10, y = 170, w = 420, lh = 16;
        GUI.Box(new Rect(x, y, w, lh * 9 + 8), "");
        y += 4;
        void L(string s) { GUI.Label(new Rect(x + 6, y, w - 12, lh), s); y += lh; }

        L($"⚙️ RootEst v6  标定(脚接地)  vel=({rootVelocity.x:F2}, {rootVelocity.z:F2}) m/s");
        L($"  rawDX={rawDX:+0.000;-0.000}  rawDZ={rawDZ:+0.000;-0.000}  (每包位移)");
        L($"  smoothVelX={_smoothVelX:+0.000;-0.000}  smoothVelZ={_smoothVelZ:+0.000;-0.000}");
        L($"  calibWX={frontReceiver.CalibWorldX:F3}  calibWZ={(_hasLeft ? leftReceiver.CalibWorldZ : 0f):F3}");
        L($"  validX={frontReceiver.CalibXValid}  validZ={(_hasLeft ? leftReceiver.CalibZValid : false)}");
        L($"  prevX={_prevFrontWorldX:F3}  prevZ={_prevLeftWorldZ:F3}");
        L($"  footGnd 前={frontReceiver.LatestRootGround.x:F4}  左={(_hasLeft ? leftReceiver.LatestRootGround.x : 0f):F4}");
        L($"  双摄={_hasLeft}  fid={_lastProcessedFrameId}  [C]重校");
    }
}
