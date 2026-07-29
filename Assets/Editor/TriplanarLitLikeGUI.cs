using UnityEditor;
using UnityEngine;

public class TriplanarLitLikeGUI : ShaderGUI
{
    MaterialProperty baseMap;
    MaterialProperty baseColor;
    MaterialProperty metallicMap;
    MaterialProperty metallic;
    MaterialProperty smoothness;
    MaterialProperty normalMap;

    public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] props)
    {
        // Shader Graph側の「Reference」名と一致させる
        baseMap      = FindProperty("_BaseMap", props, false);
        baseColor    = FindProperty("_BaseColor", props, false);
        metallicMap  = FindProperty("_MetallicGlossMap", props, false);
        metallic     = FindProperty("_Metallic", props, false);
        smoothness   = FindProperty("_Smoothness", props, false);
        normalMap    = FindProperty("_BumpMap", props, false);

        EditorGUILayout.LabelField("Surface Inputs", EditorStyles.boldLabel);

        if (baseMap != null)
        {
            if (baseColor != null)
                materialEditor.TexturePropertySingleLine(
                    new GUIContent("Base Map"),
                    baseMap,
                    baseColor
                );
            else
                materialEditor.TexturePropertySingleLine(
                    new GUIContent("Base Map"),
                    baseMap
                );
        }

        if (metallicMap != null)
            materialEditor.TexturePropertySingleLine(
                new GUIContent("Metallic Map"),
                metallicMap
            );

        if (metallic != null)
            materialEditor.ShaderProperty(metallic, "Metallic");

        if (smoothness != null)
            materialEditor.ShaderProperty(smoothness, "Smoothness");

        if (normalMap != null)
            materialEditor.TexturePropertySingleLine(
                new GUIContent("Normal Map"),
                normalMap
            );
    }
}