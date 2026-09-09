using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

/// <summary>
/// 一键把 FootPlantingController 挂到场景中的人形角色并保存场景。
/// 菜单：Mocap → Setup Foot Planting
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

        // 同步给姿态驱动，确保由脚锁接管腿部
        if (ctrl.poseDriver != null) ctrl.poseDriver.footIKActive = true;

        EditorSceneManager.MarkSceneDirty(animator.gameObject.scene);
        EditorSceneManager.SaveScene(animator.gameObject.scene);

        Debug.Log($"[FootPlanting] 已挂载到 {animator.gameObject.name} 并保存。请停止/重开 Play 测试。");
        EditorUtility.DisplayDialog("Foot Planting",
            $"已挂载到 {animator.gameObject.name} 并保存场景。\n请停止并重新进入 Play 模式后测试（Stand / Walk / Turn）。\n可调：boardY / ankleOffset / plantHeightMax / plantSpeedMax / liftSpeedThreshold；\n画面左上角会叠加诊断（数据帧龄、planted权重、脚-锚距、垂速、不可达）。",
            "确定");
    }
}
