using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

/// <summary>
/// 一键把 FootPlantingController 挂到场景中的人形角色并保存场景；或对已有挂载应用修正版释放参数。
/// 菜单：
///   Mocap → Setup Foot Planting                （新建/修复挂载）
///   Mocap → Apply Foot IK Corrected Params     （对场景已有挂载刷新为修正版默认参数，保留其余挂载/参数）
/// </summary>
public static class FootPlantingSetup
{
    [MenuItem("Mocap/Setup Foot Planting")]
    public static void Setup()
    {
        Animator animator = null;
        foreach (var a in Object.FindObjectsOfType<Animator>())
        {
            if (a.isHuman && a.GetComponent<HumanoidPoseDriver>() != null) { animator = a; break; }
        }
        if (animator == null)
        {
            foreach (var a in Object.FindObjectsOfType<Animator>())
                if (a.isHuman) { animator = a; break; }
        }

        if (animator == null)
        {
            EditorUtility.DisplayDialog("Foot Planting", "未在场景中找到 Humanoid Animator。", "确定");
            return;
        }

        var ctrl = animator.gameObject.GetComponent<FootPlantingController>();
        if (ctrl == null)
            ctrl = animator.gameObject.AddComponent<FootPlantingController>();

        ctrl.animator = animator;
        ctrl.poseDriver = animator.GetComponent<HumanoidPoseDriver>();
        ctrl.enableFootIK = true;
        ctrl.boardY = 0f;
        ctrl.ankleOffset = 0.08f;
        ctrl.showOverlay = true;
        ctrl.ApplyCorrectedParams();

        if (ctrl.poseDriver != null) ctrl.poseDriver.footIKActive = true;

        EditorSceneManager.MarkSceneDirty(animator.gameObject.scene);
        EditorSceneManager.SaveScene(animator.gameObject.scene);

        Debug.Log($"[FootPlanting] 已挂载到 {animator.gameObject.name} 并保存（修正版参数）。请停止/重开 Play 测试。");
        EditorUtility.DisplayDialog("Foot Planting",
            $"已挂载到 {animator.gameObject.name} 并保存，应用修正版释放参数。\n请停止并重新进入 Play 模式测试（Stand / Walk / Turn / Low-step / 快速抬脚）。\n左上角叠加诊断：plant / anchor / rel / rise / dev / adist / dist。",
            "确定");
    }

    [MenuItem("Mocap/Apply Foot IK Corrected Params")]
    public static void ApplyParamsToExisting()
    {
        var ctrls = Object.FindObjectsOfType<FootPlantingController>();
        if (ctrls == null || ctrls.Length == 0)
        {
            EditorUtility.DisplayDialog("Foot Planting", "场景中没有 FootPlantingController。", "确定");
            return;
        }

        Scene scene = default;
        int updated = 0;
        foreach (var c in ctrls)
        {
            c.ApplyCorrectedParams();
            if (c.animator != null && c.animator.gameObject != null)
            {
                scene = c.animator.gameObject.scene;
            }
            updated++;
        }
        if (scene.IsValid())
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        Debug.Log($"[FootPlanting] 已对 {updated} 个 FootPlantingController 应用修正版释放参数并保存。");
        EditorUtility.DisplayDialog("Foot Planting",
            $"已更新 {updated} 个 FootPlantingController 的释放参数并保存场景。\n请停止 Play 后重开验证（旧默认值不会自动更新，本操作即用于覆盖它们）。",
            "确定");
    }
}
