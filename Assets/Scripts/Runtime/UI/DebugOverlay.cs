using UnityEngine;

/// <summary>
/// Lightweight IMGUI debug overlay showing low-level runtime info.
/// Useful for profiling and debugging during development.
/// Toggle with F3 key.
/// </summary>
public class DebugOverlay : MonoBehaviour
{
    private bool isVisible = false;
    private float deltaTime = 0f;

    private MatchManager matchManager;

    public void SetMatchManager(MatchManager mm)
    {
        matchManager = mm;
    }

    private void Update()
    {
        deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;

        if (Input.GetKeyDown(KeyCode.F3))
        {
            isVisible = !isVisible;
        }
    }

    private void OnGUI()
    {
        if (!isVisible) return;

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            normal = { textColor = Color.green }
        };

        float x = Screen.width - 260f;
        float y = 10f;

        // Semi-transparent background
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(x - 5, y - 5, 255, 140), Texture2D.whiteTexture);
        GUI.color = Color.white;

        // FPS
        float fps = 1.0f / deltaTime;
        GUI.Label(new Rect(x, y, 250, 16), $"FPS: {fps:F1} ({deltaTime * 1000f:F1}ms)", style);
        y += 16f;

        // Time
        GUI.Label(new Rect(x, y, 250, 16), $"Time: {Time.time:F1}s  TimeScale: {Time.timeScale}", style);
        y += 16f;

        // Physics
        GUI.Label(new Rect(x, y, 250, 16), $"FixedDT: {Time.fixedDeltaTime:F4}  Frame: {Time.frameCount}", style);
        y += 16f;

        // Match state
        if (matchManager != null)
        {
            GUI.Label(new Rect(x, y, 250, 16), $"Cooldown: {matchManager.IsInCooldown} ({matchManager.CooldownRemaining:F1}s)", style);
            y += 16f;
            GUI.Label(new Rect(x, y, 250, 16), $"MatchOver: {matchManager.IsMatchOver}", style);
            y += 16f;

            if (matchManager.PlayerA != null)
            {
                Vector3 posA = matchManager.PlayerA.transform.position;
                GUI.Label(new Rect(x, y, 250, 16), $"A pos: ({posA.x:F1}, {posA.y:F1}, {posA.z:F1})", style);
                y += 16f;
            }
            if (matchManager.PlayerB != null)
            {
                Vector3 posB = matchManager.PlayerB.transform.position;
                GUI.Label(new Rect(x, y, 250, 16), $"B pos: ({posB.x:F1}, {posB.y:F1}, {posB.z:F1})", style);
                y += 16f;
            }
        }

        // Controls hint
        style.normal.textColor = Color.gray;
        style.fontSize = 10;
        GUI.Label(new Rect(x, y, 250, 14), "[F3] Toggle Debug Overlay", style);
    }
}
