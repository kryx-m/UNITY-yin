using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System;

/// <summary>
/// UDP 姿态数据接收器。
/// 后台线程收包解析 → 打包成 Snapshot → 一次 volatile 赋值 → 主线程读取。
/// </summary>
public class UdpPoseReceiver : MonoBehaviour
{
    public int port = 8888;

    private UdpClient udpClient;
    private Thread receiveThread;
    private volatile bool running = false;

    // ═══════════════ 安全跨线程模型：锁写→主线程 Update 读 ═══════════════
    public class Snapshot
    {
        public double time; public int frameId; public string camera;
        public Vector3 root, rootGround;
        public float calibWorldX, calibWorldZ, calibHipX, calibHipZ;
        public bool calibActive;
        public bool calibXValid, calibZValid;
        public Dictionary<string, Landmark> landmarks;
    }

    // 后台线程写入（有锁），主线程 Update() 读取并缓存
    private Snapshot _incoming;      // 后台线程写
    private readonly object _incomingLock = new object();

    // 主线程缓存（只在 Update 里更新，其他组件读这里）
    private Snapshot _cached;
    private readonly object _cacheLock = new object();

    // ── 属性从缓存读取 ──
    public Vector3 LatestRoot       { get { lock(_cacheLock) return _cached?.root ?? Vector3.zero; } }
    public Vector3 LatestRootGround { get { lock(_cacheLock) return _cached?.rootGround ?? Vector3.zero; } }
    public double  LatestTime       { get { lock(_cacheLock) return _cached?.time ?? 0.0; } }
    public string  CameraSource     { get { lock(_cacheLock) return _cached?.camera ?? ""; } }
    public int     LatestFrameId    { get { lock(_cacheLock) return _cached?.frameId ?? 0; } }
    public float   CalibWorldX      { get { lock(_cacheLock) return _cached?.calibWorldX ?? 0f; } }
    public float   CalibWorldZ      { get { lock(_cacheLock) return _cached?.calibWorldZ ?? 0f; } }
    public float   CalibHipX        { get { lock(_cacheLock) return _cached?.calibHipX ?? 0f; } }
    public float   CalibHipZ        { get { lock(_cacheLock) return _cached?.calibHipZ ?? 0f; } }
    public bool    CalibActive      { get { lock(_cacheLock) return _cached?.calibActive ?? false; } }
    public bool    CalibXValid      { get { lock(_cacheLock) return _cached?.calibXValid ?? false; } }
    public bool    CalibZValid      { get { lock(_cacheLock) return _cached?.calibZValid ?? false; } }

    public Dictionary<string, Landmark> GetLatestPose()
    {
        lock (_cacheLock)
        {
            var s = _cached;
            if (s == null || s.landmarks == null) return null;
            return new Dictionary<string, Landmark>(s.landmarks);
        }
    }
    public struct FrameEntry
    {
        public double time; public int frameId;
        public Vector3 root, rootGround;
        public float calibWorldX, calibWorldZ, calibHipX, calibHipZ;
        public bool calibActive;
        public bool calibXValid, calibZValid;
        public Dictionary<string, Landmark> pose;
    }
    private const int BUF_SIZE = 60;
    private FrameEntry[] _buf = new FrameEntry[BUF_SIZE];
    private volatile int _bufWrite;
    private readonly object _bufLock = new object();
    public int BufferedFrameCount { get { lock (_bufLock) { return _bufWrite; } } }

    // ── 特殊 key ──
    private const string K_ROOT = "_root";
    private const string K_ROOT_G = "_root_ground";
    private const string K_TIME = "_time";
    private const string K_CAM = "_camera";
    private const string K_FID = "_frame_id";
    private const string K_CX = "_calib_world_x";
    private const string K_CZ = "_calib_world_z";
    private const string K_CHX = "_calib_hip_x";
    private const string K_CHZ = "_calib_hip_z";
    private const string K_CACT = "_calib_active";
    private const string K_CXV = "_calib_x_valid";
    private const string K_CZV = "_calib_z_valid";

    // ════════════════════════════════════════════════════════════
    // 主线程：每帧从 incoming 拷贝到 cached
    // ════════════════════════════════════════════════════════════

    void Update()
    {
        Snapshot newSnap = null;
        lock (_incomingLock) { newSnap = _incoming; _incoming = null; }
        if (newSnap != null)
        {
            lock (_cacheLock) { _cached = newSnap; }
        }
    }

    void Start() => Bind();
    void OnEnable() { if (udpClient == null) Bind(); }
    void OnDisable() => Shutdown();
    void OnDestroy() => Shutdown();
    void OnApplicationQuit() => Shutdown();

    void Bind()
    {
        Shutdown();
        Debug.Log($"[UDP] 正在绑定端口 {port}...");
        try
        {
            udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            udpClient.Client.ReceiveTimeout = 1000;
            running = true;
            receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
            receiveThread.Start();
            Debug.Log($"✅ UDP接收已启动 (port {port})");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ UDP端口 {port} 绑定失败: {ex.Message}");
            try { udpClient?.Close(); udpClient?.Dispose(); } catch { }
            udpClient = null;
        }
    }

    void Shutdown()
    {
        running = false;
        if (udpClient != null) { try { udpClient.Close(); } catch { } try { udpClient.Dispose(); } catch { } udpClient = null; }
        if (receiveThread != null)
        {
            if (receiveThread.IsAlive && !receiveThread.Join(1500))
                try { receiveThread.Abort(); } catch { }
            receiveThread = null;
        }
    }

    // ════════════════════════════════════════════════════════════
    // 后台线程
    // ════════════════════════════════════════════════════════════

    void ReceiveLoop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
        int pkt = 0;

        while (running)
        {
            try
            {
                byte[] data = udpClient.Receive(ref ep);
                if (data == null || data.Length == 0) continue;
                pkt++;

                string json = Encoding.UTF8.GetString(data);
                var raw = MiniJSON.Deserialize(json) as Dictionary<string, object>;
                if (raw == null) continue;

                var dict = new Dictionary<string, Landmark>(raw.Count);
                Vector3 root = default, rootG = default;
                double time = 0;
                string cam = "";
                int fid = 0;
                float cwx = 0, cwz = 0, chx = 0, chz = 0;
                bool cact = false;
                bool cxv = false, czv = false;

                foreach (var kv in raw)
                {
                    if (kv.Key == K_ROOT && kv.Value is Dictionary<string, object> rd)
                    { root = V3(rd); continue; }
                    if (kv.Key == K_ROOT_G && kv.Value is Dictionary<string, object> rgd)
                    { rootG = V3(rgd); continue; }
                    if (kv.Key == K_TIME) { time = Convert.ToDouble(kv.Value); continue; }
                    if (kv.Key == K_CAM) { cam = kv.Value as string; continue; }
                    if (kv.Key == K_FID) { fid = Convert.ToInt32(kv.Value); continue; }
                    if (kv.Key == K_CX) { cwx = Convert.ToSingle(kv.Value); continue; }
                    if (kv.Key == K_CZ) { cwz = Convert.ToSingle(kv.Value); continue; }
                    if (kv.Key == K_CHX) { chx = Convert.ToSingle(kv.Value); continue; }
                    if (kv.Key == K_CHZ) { chz = Convert.ToSingle(kv.Value); continue; }
                    if (kv.Key == K_CACT) { cact = Convert.ToInt32(kv.Value) != 0; continue; }
                    if (kv.Key == K_CXV) { cxv = Convert.ToInt32(kv.Value) != 0; continue; }
                    if (kv.Key == K_CZV) { czv = Convert.ToInt32(kv.Value) != 0; continue; }

                    var lmDict = kv.Value as Dictionary<string, object>;
                    if (lmDict == null) continue;
                    if (!lmDict.ContainsKey("x") || !lmDict.ContainsKey("y") || !lmDict.ContainsKey("z")) continue;
                    dict[kv.Key] = new Landmark
                    {
                        x = F(lmDict, "x"), y = F(lmDict, "y"), z = F(lmDict, "z"),
                        visibility = lmDict.ContainsKey("visibility") ? F(lmDict, "visibility") : 1f
                    };
                }

                // ⭐ 打包→一次 atomic 赋值（volatile 引用，主线程立即可见）
                var snap = new Snapshot
                {
                    time = time, frameId = fid, camera = cam,
                    root = root, rootGround = rootG,
                    calibWorldX = cwx, calibWorldZ = cwz,
                    calibHipX = chx, calibHipZ = chz,
                    calibActive = cact,
                    calibXValid = cxv, calibZValid = czv,
                    landmarks = dict,
                };
                lock (_incomingLock) { _incoming = snap; }

                // 帧缓冲
                lock (_bufLock)
                {
                    int i = _bufWrite % BUF_SIZE;
                    _buf[i] = new FrameEntry
                    {
                        time = time, frameId = fid,
                        root = root, rootGround = rootG,
                        calibWorldX = cwx, calibWorldZ = cwz,
                        calibHipX = chx, calibHipZ = chz,
                        calibActive = cact,
                        calibXValid = cxv, calibZValid = czv,
                        pose = dict,
                    };
                    _bufWrite++;
                }

                if (pkt % 60 == 1)
                    Debug.Log($"[UDP:{port}] ✅ 第{pkt}包 time={time:F1} keys={dict.Count} calibAct={cact}");
            }
            catch (SocketException) { if (!running) break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    Vector3 V3(Dictionary<string, object> d)
    {
        float x = d.ContainsKey("x") ? F(d, "x") : 0f;
        float y = d.ContainsKey("y") ? F(d, "y") : 0f;
        float z = d.ContainsKey("z") ? F(d, "z") : 0f;
        return new Vector3(x, y, z);
    }

    float F(Dictionary<string, object> d, string k)
    {
        if (!d.ContainsKey(k) || d[k] == null) return 0f;
        try { return Convert.ToSingle(d[k]); } catch { return 0f; }
    }

    // ════════════════════════════════════════════════════════════
    // TryGetClosestFrame
    // ════════════════════════════════════════════════════════════

    public bool TryGetClosestFrame(double targetTime, out FrameEntry entry)
    {
        entry = default;
        lock (_bufLock)
        {
            int cnt = Math.Min(_bufWrite, BUF_SIZE);
            if (cnt == 0) return false;
            int best = 0; double bestD = double.MaxValue;
            int start = Math.Max(0, _bufWrite - cnt);
            for (int j = start; j < _bufWrite; j++)
            {
                int idx = j % BUF_SIZE;
                double d = Math.Abs(_buf[idx].time - targetTime);
                if (d < bestD) { bestD = d; best = idx; }
            }
            entry = _buf[best];
            return true;
        }
    }
}
