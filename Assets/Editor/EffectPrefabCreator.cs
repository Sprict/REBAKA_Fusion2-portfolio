using UnityEngine;
using UnityEditor;
using System.IO;

public class EffectPrefabCreator : MonoBehaviour
{
    [MenuItem("Tools/Create Effect Prefabs")]
    public static void CreateEffectPrefabs()
    {
        // フォルダの作成
        if (!Directory.Exists("Assets/Resources"))
            AssetDatabase.CreateFolder("Assets", "Resources");
            
        if (!Directory.Exists("Assets/Resources/Effects"))
            AssetDatabase.CreateFolder("Assets/Resources", "Effects");

        // GrabEffectの作成
        GameObject grabEffect = new GameObject("GrabEffect");
        ParticleSystem grabParticles = grabEffect.AddComponent<ParticleSystem>();
        
        // パーティクルの設定
        var mainModule = grabParticles.main;
        mainModule.startColor = Color.green;
        mainModule.startLifetime = 0.5f;
        mainModule.startSpeed = 2f;
        mainModule.startSize = 0.2f;
        mainModule.simulationSpace = ParticleSystemSimulationSpace.World;
        
        // プレハブとして保存
        string grabPrefabPath = "Assets/Resources/Effects/GrabEffect.prefab";
        PrefabUtility.SaveAsPrefabAsset(grabEffect, grabPrefabPath);
        DestroyImmediate(grabEffect);
        
        // ReleaseEffectの作成
        GameObject releaseEffect = new GameObject("ReleaseEffect");
        ParticleSystem releaseParticles = releaseEffect.AddComponent<ParticleSystem>();
        
        // パーティクルの設定
        mainModule = releaseParticles.main;
        mainModule.startColor = Color.red;
        mainModule.startLifetime = 0.5f;
        mainModule.startSpeed = 2f;
        mainModule.startSize = 0.2f;
        mainModule.simulationSpace = ParticleSystemSimulationSpace.World;
        
        // プレハブとして保存
        string releasePrefabPath = "Assets/Resources/Effects/ReleaseEffect.prefab";
        PrefabUtility.SaveAsPrefabAsset(releaseEffect, releasePrefabPath);
        DestroyImmediate(releaseEffect);
        
        AssetDatabase.Refresh();
        
        Debug.Log("エフェクトプレハブを作成しました: " + grabPrefabPath + " および " + releasePrefabPath);
    }
}