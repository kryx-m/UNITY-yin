using UnityEngine;

[System.Serializable]
public class Landmark
{
    public float x;
    public float y;
    public float z;
    public float visibility;
}

public static class CoordinateConverter
{
    public static float ScaleFactor = 20f;
    public static float ZScale = 0.6f;
    public static bool IsMirror = false; // ⭐ 关闭Unity镜像（Python已flip）
    public static Vector3 OriginOffset = Vector3.zero;

    // 左摄(侧面)坐标矫正符号 — 若混合后前后摆腿方向和实际相反(太空步现象)，切换此项
    public static bool InvertLeftForward = true;

    public static Vector3 ToUnityWorld(Landmark lm)
    {
        float x = lm.x;
        float y = lm.y;
        float z = lm.z;

        if (IsMirror) x = 1f - x;

        y = 1f - y;

        x = (x - 0.5f) * 2f;
        y = (y - 0.5f) * 2f;

        z = z * ZScale;

        return new Vector3(x, y, z) * ScaleFactor + OriginOffset;
    }

    /// <summary>
    /// 左摄(侧面)坐标系矫正:
    ///   左摄画面X = 房间前后 → 应映射到 Unity Z
    ///   左摄画面Z(深度) = 房间左右 → 应映射到 Unity X
    ///   左摄画面Y = 上下 (不变)
    ///
    /// ⚠️ 这是绕 Y 轴的 90° 旋转，不是单纯交换两个分量。
    ///   单纯交换 (x,y,z)→(z,y,x) 是一次镜像反射(行列式=-1)，
    ///   会导致左摄贡献的前后(Z)信号与前摄相反，双摄混合时相互抵消/打架，
    ///   正是"前后走动腿部姿势异常、位移不明显"的主因之一。
    ///   这里改为真正的旋转 (x,y,z)→(z,y,-x)，符号如与实际方向相反，
    ///   用 InvertLeftForward 翻转。
    /// </summary>
    public static Vector3 ToUnityWorldLeft(Landmark lm)
    {
        Vector3 raw = ToUnityWorld(lm);
        float sign = InvertLeftForward ? -1f : 1f;
        return new Vector3(sign * raw.z, raw.y, -sign * raw.x);
    }
}