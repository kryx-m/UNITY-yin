using UnityEngine;

/// <summary>
/// 双摄 Root 运动估计器 v5 — 纯标定模式
/// ======================================
/// v5 核心决策：
///   只有在 Python 标定已激活 (CalibActive=true) 时才计算位移。
///   未标定 fallback 已彻底移除 — 它输出的绝对偏移(不是每帧位移)
///   会导致 Avatar 每帧叠加同一个偏移量而飞走。
///
/// 算法：
///   前摄 CalibWorldX 帧间差值 → 左右位移 (dirX)
///   左摄 CalibWorldZ 帧间差值 → 前后位移 (dirZ)
///   差值 × 低通滤波 → rootDelta
/// </summary>
public class DualCameraRootEstimator : MonoBehaviour
{
    [Header("References")]
    public UdpPoseReceiver frontReceiver;
    public UdpPoseReceiver leftReceiver;

    [Header("Time Sync")]
    [Tooltip("双摄帧最大允许时间差（秒）")]
    [Range(0.01f, 0.3f)] public float maxTimeDiff = 0.1f;

    [Header("Filter")]
    [Range(0.02f, 0.5f)] public float lowPassAlpha = 0.15f;
    public float deadZoneMeters = 0.015f;

    [Header("Safety")]
    [Tooltip("单帧最大位移(m)，超过归零")]
    public float maxDeltaPerFrame = 3f;

    [Header("Calibration")]
    public KeyCode calibrateKey = KeyCode.C;
    public bool autoCalibrateOnStart = true;

    // ── 内部状态 ──
    private float _prevFrontWorldX, _prevLeftWorldZ;
    private float _smoothDX, _smoothDZ;
    private bool _hasLeft;

    // ── 输出 ──
    public Vector3 rootDelta { get; private set; }
    public bool isCalibrated { get; private set; }

    // Debug
    public float rawDX { get; private set; }
    public float rawDZ { get; private set; }

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
        Debug.Log($"[RootEst] ✅ v5 初始化 — {(_hasLeft ? "双摄" : "单摄")}  仅标定模式  safety={maxDeltaPerFrame}m");

        if (autoCalibrateOnStart)
            Invoke(nameof(Calibrate), 2f);
    }

    void Update()
    {
        if (Input.GetKeyDown(calibrateKey)) Calibrate();
    }

    void LateUpdate()
    {
        if (frontReceiver == null) return;

        // ── 等待 Python 标定激活 ──
        if (!frontReceiver.CalibActive)
        {
            rootDelta = Vector3.zero;
            return;
        }

        if (!isCalibrated)
        {
            // 标定数据到达但还没校准 → 自动校准
            Calibrate();
            return;
        }

        if (frontReceiver.LatestTime == 0)
        {
            rootDelta = Vector3.zero;
            return;
        }

        // ── 1. 计算帧间位移 ──
        ComputeRootFromSyncedFrames();

        // ── 2. 硬保护 ──
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

        // ── 3. 死区 ──
        if (Mathf.Abs(rawDX) < deadZoneMeters) rawDX = 0f;
        if (Mathf.Abs(rawDZ) < deadZoneMeters) rawDZ = 0f;

        // ── 4. 低通滤波 ──
        _smoothDX = Mathf.Lerp(_smoothDX, rawDX, lowPassAlpha);
        _smoothDZ = Mathf.Lerp(_smoothDZ, rawDZ, lowPassAlpha);

        // ── 5. 输出 ──
        rootDelta = new Vector3(_smoothDX, 0f, _smoothDZ);
    }

    void ComputeRootFromSyncedFrames()
    {
        double frontTime = frontReceiver.LatestTime;
        float curFrontWorldX = frontReceiver.CalibWorldX;

        if (!_hasLeft)
        {
            rawDX = curFrontWorldX - _prevFrontWorldX;
            rawDZ = 0f;
            _prevFrontWorldX = curFrontWorldX;
            return;
        }

        if (leftReceiver.TryGetClosestFrame(frontTime, out var leftFrame))
        {
            double timeDiff = System.Math.Abs(frontTime - leftFrame.time);
            if (timeDiff > maxTimeDiff)
            {
                rawDX = curFrontWorldX - _prevFrontWorldX;
                rawDZ = 0f;
            }
            else
            {
                float curLeftWorldZ = leftFrame.calibWorldZ;
                rawDX = curFrontWorldX - _prevFrontWorldX;
                rawDZ = curLeftWorldZ - _prevLeftWorldZ;
                _prevLeftWorldZ = curLeftWorldZ;
            }
        }
        else
        {
            float curLeftWorldZ = leftReceiver.CalibWorldZ;
            rawDX = curFrontWorldX - _prevFrontWorldX;
            rawDZ = curLeftWorldZ - _prevLeftWorldZ;
            _prevLeftWorldZ = curLeftWorldZ;
        }

        _prevFrontWorldX = curFrontWorldX;
    }

    public void Calibrate()
    {
        if (frontReceiver == null) return;

        _prevFrontWorldX = frontReceiver.CalibWorldX;
        _prevLeftWorldZ = _hasLeft ? leftReceiver.CalibWorldZ : 0f;
        _smoothDX = 0f;
        _smoothDZ = 0f;
        rawDX = 0f;
        rawDZ = 0f;
        rootDelta = Vector3.zero;
        isCalibrated = true;

        Debug.Log($"[RootEst] ✅ 校准完成  原点 prevX={_prevFrontWorldX:F3}  prevZ={_prevLeftWorldZ:F3}");
    }

    void OnGUI()
    {
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
        GUI.Box(new Rect(x, y, w, lh * 8 + 8), "");
        y += 4;
        void L(string s) { GUI.Label(new Rect(x + 6, y, w - 12, lh), s); y += lh; }

        L($"⚙️ RootEst v5  标定(脚接地)  delta=({rootDelta.x:F3}, {rootDelta.z:F3})");
        L($"  rawDX={rawDX:+0.000;-0.000}  rawDZ={rawDZ:+0.000;-0.000}");
        L($"  smoothDX={_smoothDX:+0.000;-0.000}  smoothDZ={_smoothDZ:+0.000;-0.000}");
        L($"  calibWX={frontReceiver.CalibWorldX:F3}  calibWZ={(_hasLeft ? leftReceiver.CalibWorldZ : 0f):F3}");
        L($"  prevX={_prevFrontWorldX:F3}  prevZ={_prevLeftWorldZ:F3}");
        L($"  footGnd 前={frontReceiver.LatestRootGround.x:F4}  左={(_hasLeft ? leftReceiver.LatestRootGround.x : 0f):F4}");
        L($"  双摄={_hasLeft}  buf={frontReceiver.BufferedFrameCount}  [C]重校");
    }
}
