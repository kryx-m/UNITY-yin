using System;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// 把 FootPlantingController 的脚锁诊断快照经 UDP (127.0.0.1:8890) 回传给 Python 桌面监看。
/// - 自动关联场景中的 FootPlantingController，无需手工拖引用。
/// - 每秒 10 次，在本帧脚锁求解完成后发送（Update 中节流）。
/// - 非阻塞 UDP 发送；发送失败不阻塞姿态驱动；退出时关闭 socket。
/// - 控制器缺失/禁用/IK 关闭时仍回传对应状态，避免 Python 把旧数据当实时。
/// </summary>
public class FootIkUdpSender : MonoBehaviour
{
    [Header("Target")]
    public string host = "127.0.0.1";
    public int port = 8890;
    [Range(1, 60)] public float sendHz = 10f;

    [Header("Auto-link")]
    public FootPlantingController controller;   // 可在 Inspector 指定；为空则自动 FindObjectOfType

    private UdpClient _udp;
    private float _sendAccum;
    private bool _sentAtLeastOnce;

    void Start()
    {
        if (controller == null) controller = FindObjectOfType<FootPlantingController>();
        try
        {
            _udp = new UdpClient();
            _udp.Connect(host, port);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[FootIkUdp] UDP 发送器初始化失败: {ex.Message}");
            _udp = null;
        }
    }

    void Update()
    {
        if (_udp == null) return;
        float interval = 1f / Mathf.Max(1f, sendHz);
        _sendAccum += Time.deltaTime;
        if (_sendAccum < interval) return;
        _sendAccum = 0f;

        byte[] payload = BuildPayload();
        if (payload == null) return;
        try { _udp.Send(payload, payload.Length); _sentAtLeastOnce = true; }
        catch (Exception) { /* 发送失败不阻塞 */ }
    }

    void OnDisable() { CloseSocket(); }
    void OnDestroy() { CloseSocket(); }
    void OnApplicationQuit() { CloseSocket(); }

    void CloseSocket()
    {
        if (_udp != null) { try { _udp.Close(); } catch { } try { _udp.Dispose(); } catch { } _udp = null; }
    }

    byte[] BuildPayload()
    {
        var msg = new Dictionary<string, object>
        {
            ["type"] = "foot_ik_diagnostics",
            ["version"] = 1,
            ["controller"] = "missing",
            ["ik_enabled"] = false,
            ["published"] = false,
            ["session"] = controller != null ? controller.GetDiagnostics().session : "-",
            ["seq"] = 0,
            ["front_age_ms"] = Null(),
        };

        if (controller == null)
        {
            return Encode(msg);
        }

        var snap = controller.GetDiagnostics();
        msg["session"] = snap.session;
        msg["seq"] = snap.seq;
        msg["ik_enabled"] = snap.ikEnabled;
        msg["controller"] = snap.controllerEnabled ? (snap.ikEnabled ? "active" : "disabled") : "disabled";
        msg["published"] = snap.published;
        msg["front_age_ms"] = Ms(snap.frontAgeMs);

        msg["left"] = FootToDict(snap.left, snap.ikEnabled);
        msg["right"] = FootToDict(snap.right, snap.ikEnabled);
        return Encode(msg);
    }

    Dictionary<string, object> FootToDict(FootPlantingController.FootDiag d, bool ikEnabled)
    {
        var dict = new Dictionary<string, object>
        {
            ["status"] = ikEnabled ? d.status : "—",
            ["reason"] = ikEnabled ? d.releaseReason : "IK 已关闭",
            ["has_anchor"] = d.hasAnchor && ikEnabled,
            ["anchor_id"] = d.hasAnchor && ikEnabled ? d.anchorId : (object)null,
            ["planted"] = ikEnabled ? Rounded(d.planted, 3) : (object)null,
            ["lift_rise_cm"] = ikEnabled && d.hasAnchor ? Cm(d.liftRise) : Null(),
            ["anchor_dev_cm"] = ikEnabled && d.hasAnchor ? Cm(d.anchorDev) : Null(),
            ["hip_to_anchor_cm"] = ikEnabled && d.hasAnchor ? Cm(d.hipToAnchor) : Null(),
            ["ankle_to_anchor_cm"] = ikEnabled && d.hasAnchor ? Cm(d.ankleToAnchor) : Null(),
            ["v_speed_cms"] = ikEnabled ? Cms(d.vSpeed) : Null(),
            ["unreach"] = d.unreach && ikEnabled,
            ["unreach_ms"] = ikEnabled ? Ms(d.unreachTimeAcc) : (object)null,
            ["contact"] = d.contact && ikEnabled,
        };
        return dict;
    }

    static object Cm(float m) => float.IsFinite(m) ? Math.Round(m * 100f, 1) : Null();
    static object Cms(float ms) => float.IsFinite(ms) ? Math.Round(ms * 100f, 1) : Null();
    static object Ms(float s) => float.IsFinite(s) ? Math.Round(s * 1000f, 0) : Null();
    static object Rounded(float v, int digits) => float.IsFinite(v) ? Math.Round(v, digits) : Null();
    static object Null() => null;

    byte[] Encode(Dictionary<string, object> msg)
    {
        try
        {
            string json = JsonConvert.SerializeObject(msg);
            return System.Text.Encoding.UTF8.GetBytes(json);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
