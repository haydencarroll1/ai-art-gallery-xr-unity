using UnityEngine;

public static class MaterialUtility
{
    public static Color Rgb(int r, int g, int b)
    {
        return new Color(r / 255f, g / 255f, b / 255f);
    }

    // Shader fallback chain: URP Lit > Standard > Diffuse > Unlit/Color
    public static Material CreateMaterial(string name, Color color, float metallic = 0f, float smoothness = 0.1f, Texture2D baseMap = null, Vector2? tiling = null)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Diffuse");
        if (shader == null) shader = Shader.Find("Unlit/Color");

        if (shader == null)
        {
            Debug.LogError("[MaterialUtility] No shader found!");
            return null;
        }

        Material mat = new Material(shader);
        mat.name = $"Generated_{name}";

        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", color);
        else if (mat.HasProperty("_Color"))
            mat.SetColor("_Color", color);

        if (mat.HasProperty("_Smoothness"))
            mat.SetFloat("_Smoothness", smoothness);
        if (mat.HasProperty("_Metallic"))
            mat.SetFloat("_Metallic", metallic);

        if (baseMap != null)
        {
            if (mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", baseMap);
            if (mat.HasProperty("_MainTex"))
                mat.SetTexture("_MainTex", baseMap);

            Vector2 textureTiling = tiling ?? Vector2.one;
            if (mat.HasProperty("_BaseMap"))
                mat.SetTextureScale("_BaseMap", textureTiling);
            if (mat.HasProperty("_MainTex"))
                mat.SetTextureScale("_MainTex", textureTiling);
        }

        return mat;
    }
}
