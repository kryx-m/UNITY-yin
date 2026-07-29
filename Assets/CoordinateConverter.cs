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
    public static float ZScale = 0.6f;   // ⭐ 关键：z单独缩放
    public static bool IsMirror = false; // ⭐ 关闭Unity镜像（Python已flip）
    public static Vector3 OriginOffset = Vector3.zero;

    public static Vector3 ToUnityWorld(Landmark lm)
    {
        float x = lm.x;
        float y = lm.y;
        float z = lm.z;

        // 镜像（默认关）
        if (IsMirror) x = 1f - x;

        // 图像坐标 → Unity坐标
        y = 1f - y;

        x = (x - 0.5f) * 2f;
        y = (y - 0.5f) * 2f;

        // ⭐ MediaPipe z 处理
        z = z * ZScale;

        return new Vector3(x, y, z) * ScaleFactor + OriginOffset;
    }
}
