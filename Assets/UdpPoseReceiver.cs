using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System;

/// <summary>
/// UDP 姿态数据接收器。
/// 每个实例监听一个端口（8888=前摄, 8889=左摄），
/// 解析 JSON 中的 landmark、结构化字段，并维护短帧缓冲供双摄时间对齐。
/// </summary>
public class UdpPoseReceiver : MonoBehaviour
{
    public int port = 8888;

    private UdpClient udpClient;
    private Thread receiveThread;
    private volatile bool running = false;

    private Dictionary<string, Landmark> latestPoseData =
        new Dictionary<string, Landmark>();

    // ── 结构化顶层字段 ──
    public Vector3 LatestRoot { get; private set; }           // _root: 髋部中点
    public Vector3 LatestRootGround { get; private set; }      // _root_ground: 脚跟中点（地面）
    public double LatestTime { get; private set; }             // _time
    public string CameraSource { get; private set; }           // _camera: "front" / "left"
    public int LatestFrameId { get; private set; }             // _frame_id: 递增帧号
    public float CalibWorldX { get; private set; }             // _calib_world_x: 脚部地面 X (米)
    public float CalibWorldZ { get; private set; }             // _calib_world_z: 脚部地面 Z (米)
    public float CalibHipX { get; private set; }               // _calib_hip_x: 髋部世界 X (米)
    public float CalibHipZ { get; private set; }               // _calib_hip_z: 髋部世界 Z (米)
    public bool CalibActive { get; private set; }              // _calib_active: Python 标定已激活

    // ── 帧缓冲（双摄时间对齐用）──
    public struct FrameEntry
    {
        public double time;
        public int frameId;
        public Vector3 root;
        public Vector3 rootGround;
        public float calibWorldX;
        public float calibWorldZ;
        public float calibHipX;
        public float calibHipZ;
        public bool calibActive;
        public Dictionary<string, Landmark> pose;
    }
    private const int FRAME_BUFFER_SIZE = 60;  // ~1秒缓冲
    private FrameEntry[] _frameBuffer = new FrameEntry[FRAME_BUFFER_SIZE];
    private int _frameBufferWrite;
    private readonly object _frameBufferLock = new object();
    public int BufferedFrameCount
    {
        get { lock (_frameBufferLock) { return _frameBufferWrite; } }
    }

    // ── 通用数据锁 ──
    private readonly object dataLock = new object();

    // ── 特殊 key ──
    private const string KEY_ROOT = "_root";
    private const string KEY_ROOT_GROUND = "_root_ground";
    private const string KEY_TIME = "_time";
    private const string KEY_CAMERA = "_camera";
    private const string KEY_FRAME_ID = "_frame_id";
    private const string KEY_CALIB_X = "_calib_world_x";
    private const string KEY_CALIB_Z = "_calib_world_z";
    private const string KEY_CALIB_HIP_X = "_calib_hip_x";
    private const string KEY_CALIB_HIP_Z = "_calib_hip_z";
    private const string KEY_CALIB_ACTIVE = "_calib_active";

    void Start()
    {
        Bind();
    }

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
            receiveThread = new Thread(ReceiveData);
            receiveThread.IsBackground = true;
            receiveThread.Start();

            Debug.Log($"✅ UDP接收已启动 (port {port})");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"❌ UDP端口 {port} 绑定失败: {ex.Message}");
            if (udpClient != null)
            {
                try { udpClient.Close(); } catch { }
                try { udpClient.Dispose(); } catch { }
                udpClient = null;
            }
        }
    }

    void ReceiveData()
    {
        IPEndPoint anyIP = new IPEndPoint(IPAddress.Any, 0);

        while (running)
        {
            try
            {
                byte[] data = udpClient.Receive(ref anyIP);
                if (data == null || data.Length == 0) continue;

                string json = Encoding.UTF8.GetString(data);
                var raw = MiniJSON.Deserialize(json) as Dictionary<string, object>;
                if (raw == null) continue;

                var dict = new Dictionary<string, Landmark>(raw.Count);

                // 局部变量暂存（解析中不直接更新公开属性）
                Vector3 root = Vector3.zero, rootGround = Vector3.zero;
                double time = 0;
                string cam = "";
                int frameId = 0;
                float cwx = 0, cwz = 0, chx = 0, chz = 0;
                bool calibActive = false;

                foreach (var kv in raw)
                {
                    if (kv.Key == KEY_ROOT && kv.Value is Dictionary<string, object> rd)
                    { root = ParseVector3(rd); continue; }
                    if (kv.Key == KEY_ROOT_GROUND && kv.Value is Dictionary<string, object> rgd)
                    { rootGround = ParseVector3(rgd); continue; }
                    if (kv.Key == KEY_TIME)
                    { time = Convert.ToDouble(kv.Value); continue; }
                    if (kv.Key == KEY_CAMERA)
                    { cam = kv.Value as string; continue; }
                    if (kv.Key == KEY_FRAME_ID)
                    { frameId = Convert.ToInt32(kv.Value); continue; }
                    if (kv.Key == KEY_CALIB_X)
                    { cwx = Convert.ToSingle(kv.Value); continue; }
                    if (kv.Key == KEY_CALIB_Z)
                    { cwz = Convert.ToSingle(kv.Value); continue; }
                    if (kv.Key == KEY_CALIB_HIP_X)
                    { chx = Convert.ToSingle(kv.Value); continue; }
                    if (kv.Key == KEY_CALIB_HIP_Z)
                    { chz = Convert.ToSingle(kv.Value); continue; }
                    if (kv.Key == KEY_CALIB_ACTIVE)
                    { calibActive = true; continue; }

                    var lmDict = kv.Value as Dictionary<string, object>;
                    if (lmDict == null) continue;
                    if (!lmDict.ContainsKey("x") || !lmDict.ContainsKey("y") || !lmDict.ContainsKey("z"))
                        continue;

                    Landmark lm = new Landmark();
                    lm.x = ToSingleSafe(lmDict, "x");
                    lm.y = ToSingleSafe(lmDict, "y");
                    lm.z = ToSingleSafe(lmDict, "z");
                    lm.visibility = lmDict.ContainsKey("visibility") ? ToSingleSafe(lmDict, "visibility") : 1f;
                    dict[kv.Key] = lm;
                }

                // ── 原子更新公开属性 ──
                lock (dataLock)
                {
                    latestPoseData = dict;
                }
                LatestRoot = root;
                LatestRootGround = rootGround;
                LatestTime = time;
                CameraSource = cam;
                LatestFrameId = frameId;
                CalibWorldX = cwx;
                CalibWorldZ = cwz;
                CalibHipX = chx;
                CalibHipZ = chz;
                CalibActive = calibActive;

                // ── 写入帧缓冲（环形）──
                lock (_frameBufferLock)
                {
                    int idx = _frameBufferWrite % FRAME_BUFFER_SIZE;
                    _frameBuffer[idx] = new FrameEntry
                    {
                        time = time,
                        frameId = frameId,
                        root = root,
                        rootGround = rootGround,
                        calibWorldX = cwx,
                        calibWorldZ = cwz,
                        calibHipX = chx,
                        calibHipZ = chz,
                        calibActive = calibActive,
                        pose = dict,
                    };
                    _frameBufferWrite++;
                }
            }
            catch (SocketException)
            {
                if (!running) break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (System.Threading.ThreadAbortException)
            {
                break;
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>
    /// 在帧缓冲中查找与给定时间戳最接近的帧。
    /// 返回 true=找到, false=缓冲为空
    /// </summary>
    public bool TryGetClosestFrame(double targetTime, out FrameEntry entry)
    {
        entry = default;
        lock (_frameBufferLock)
        {
            int count = Math.Min(_frameBufferWrite, FRAME_BUFFER_SIZE);
            if (count == 0) return false;

            int bestIdx = 0;
            double bestDiff = double.MaxValue;
            int start = Math.Max(0, _frameBufferWrite - count);

            for (int i = start; i < _frameBufferWrite; i++)
            {
                int idx = i % FRAME_BUFFER_SIZE;
                double diff = Math.Abs(_frameBuffer[idx].time - targetTime);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestIdx = idx;
                }
            }

            entry = _frameBuffer[bestIdx];
            return true;
        }
    }

    Vector3 ParseVector3(Dictionary<string, object> dict)
    {
        float x = dict.ContainsKey("x") ? ToSingleSafe(dict, "x") : 0f;
        float y = dict.ContainsKey("y") ? ToSingleSafe(dict, "y") : 0f;
        float z = dict.ContainsKey("z") ? ToSingleSafe(dict, "z") : 0f;
        return new Vector3(x, y, z);
    }

    float ToSingleSafe(Dictionary<string, object> dict, string key)
    {
        if (!dict.ContainsKey(key) || dict[key] == null) return 0f;
        try { return Convert.ToSingle(dict[key]); }
        catch { return 0f; }
    }

    public Dictionary<string, Landmark> GetLatestPose()
    {
        lock (dataLock)
        {
            return new Dictionary<string, Landmark>(latestPoseData);
        }
    }

    void OnApplicationQuit() { Shutdown(); }
    void OnDestroy() { Shutdown(); }
    void OnDisable() { Shutdown(); }
    void OnEnable()
    {
        if (udpClient == null) Bind();
    }

    void Shutdown()
    {
        running = false;

        if (udpClient != null)
        {
            try { udpClient.Close(); } catch { }
            try { udpClient.Dispose(); } catch { }
            udpClient = null;
        }

        if (receiveThread != null)
        {
            if (receiveThread.IsAlive && !receiveThread.Join(1500))
            {
                Debug.LogWarning($"[UDP:{port}] 线程未能及时退出，强制中断");
                try { receiveThread.Abort(); } catch { }
            }
            receiveThread = null;
        }
    }
}
