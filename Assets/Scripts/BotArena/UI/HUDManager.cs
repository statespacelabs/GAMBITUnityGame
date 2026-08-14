using UnityEngine;

/// <summary>
/// Debug HUD overlay using Unity's immediate-mode GUI (OnGUI).
/// Displays both players' health, score, hits, kills, deaths, cooldown status,
/// game mode, and controller types.
///
/// Toggleable with Tab key. Training can run with HUD disabled.
/// Uses IMGUI for simplicity — no Canvas/TMP dependency for core functionality.
/// </summary>
public class HUDManager : MonoBehaviour
{
    private MatchManager matchManager;
    private string gameModeName = "";
    private bool isVisible = true;

    // Cached style
    private GUIStyle headerStyle;
    private GUIStyle playerAStyle;
    private GUIStyle playerBStyle;
    private GUIStyle statusStyle;
    private GUIStyle eventStyle;
    private GUIStyle deferredStyle;
    private GUIStyle beliefStyle;
    private GUIStyle heatLabelStyle;
    private GUIStyle heatValueStyle;
    private GUIStyle contactTimerStyle;
    private GUIStyle contactTimerValueStyle;
    private bool stylesInitialized = false;

    private bool showEnemyHeatBar = true;
    private const float EnemyHeatHotDistance = 8f;
    private const float EnemyHeatColdDistance = 60f;
    private const float EnemyHeatMaxDisplayDistance = 180f;
    private const float EnemyHeatMinValidY = -25f;

    // Event log
    private string lastEvent = "";
    private float lastEventTime = 0f;
    private string deferredEvent = "";
    private float deferredEventTime = -999f;
    private float deferredSafetySeconds = 0f;

    public void Initialize(MatchManager mm, string gameMode)
    {
        matchManager = mm;
        gameModeName = gameMode;
        showEnemyHeatBar = GambitDemoRuntimeSettings.ShowEnemyHeatBar;
        if (!GambitDemoRuntimeSettings.ShowHud)
            isVisible = false;

        // Subscribe to events for the event log
        if (mm != null)
        {
            mm.OnHit += (shooter, victim) =>
            {
                lastEvent = $"HIT: {shooter.DisplayName} → {victim.DisplayName}";
                lastEventTime = Time.time;
            };
            mm.OnMiss += (shooter) =>
            {
                lastEvent = $"MISS: {shooter.DisplayName}";
                lastEventTime = Time.time;
            };
            mm.OnKill += (killer, victim) =>
            {
                lastEvent = $"KILL: {killer.DisplayName} eliminated {victim.DisplayName}!";
                lastEventTime = Time.time;
            };
            mm.OnCooldownStarted += (duration) =>
            {
                lastEvent = $"COOLDOWN: {duration:F1}s";
                lastEventTime = Time.time;
            };
            mm.OnMatchOver += () =>
            {
                lastEvent = "MATCH OVER!";
                lastEventTime = Time.time;
            };
            mm.OnDeferredContinuationStarted += (killer, victim, safetySeconds) =>
            {
                deferredEvent = $"RESPAWN SAFETY: {killer.DisplayName} → {victim.DisplayName}";
                deferredEventTime = Time.time;
                deferredSafetySeconds = safetySeconds;
                lastEvent = deferredEvent;
                lastEventTime = Time.time;
            };
            mm.OnContinuousContactTimeout += (penalty, safetySeconds) =>
            {
                deferredEvent = $"CONTACT TIMEOUT: -{penalty:F1}  NEW ENEMY";
                deferredEventTime = Time.time;
                deferredSafetySeconds = safetySeconds;
                lastEvent = deferredEvent;
                lastEventTime = Time.time;
            };
        }
    }

    private void Update()
    {
        // Toggle HUD visibility with Tab
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            isVisible = !isVisible;
        }

        // Reset match with R key
        if (Input.GetKeyDown(KeyCode.R) && matchManager != null)
        {
            matchManager.ResetMatch();
        }
    }

    private void InitStyles()
    {
        headerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 16,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };

        playerAStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            normal = { textColor = new Color(0.4f, 0.7f, 1f) } // Blue
        };

        playerBStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            normal = { textColor = new Color(1f, 0.5f, 0.4f) } // Red
        };

        statusStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            normal = { textColor = Color.yellow }
        };

        eventStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white }
        };

        deferredStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.red }
        };

        beliefStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperRight,
            normal = { textColor = Color.yellow }
        };

        heatLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = Color.white }
        };

        heatValueStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = new Color(0.88f, 0.92f, 1f) }
        };

        contactTimerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = Color.white }
        };

        contactTimerValueStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = Color.white }
        };

        stylesInitialized = true;
    }

    private void OnGUI()
    {
        if (!isVisible || matchManager == null) return;
        if (!stylesInitialized) InitStyles();

        // Semi-transparent background
        GUI.color = new Color(0f, 0f, 0f, 0.7f);
        GUI.DrawTexture(new Rect(10, 10, 280, 280), Texture2D.whiteTexture);
        GUI.color = Color.white;

        float y = 15f;
        float x = 15f;

        // Header
        GUI.Label(new Rect(x, y, 270, 20), $"ASCENT GAME — {gameModeName}", headerStyle);
        y += 25f;

        // Player A
        var pA = matchManager.PlayerA?.Identity;
        if (pA != null)
        {
            string ctrlA = matchManager.PlayerA.Controller?.GetType().Name ?? "None";
            GUI.Label(new Rect(x, y, 270, 18), $"─── {pA.DisplayName} [{ctrlA}] ───", playerAStyle);
            y += 18f;
            int hpA = matchManager.PlayerA.Health.CurrentHealth;
            int maxA = matchManager.PlayerA.Health.MaxHealth;
            GUI.Label(new Rect(x, y, 270, 16), $"  HP: {hpA}/{maxA}  Score: {pA.Score:F1}", playerAStyle);
            y += 16f;
            GUI.Label(new Rect(x, y, 270, 16), $"  Hits: {pA.Hits}  Kills: {pA.Kills}  Deaths: {pA.Deaths}", playerAStyle);
            y += 22f;

            // Health bar
            DrawHealthBar(x, y, 260, 8, (float)hpA / maxA, new Color(0.4f, 0.7f, 1f));
            y += 16f;
        }

        // Player B
        var pB = matchManager.PlayerB?.Identity;
        if (pB != null)
        {
            string ctrlB = matchManager.PlayerB.Controller?.GetType().Name ?? "None";
            GUI.Label(new Rect(x, y, 270, 18), $"─── {pB.DisplayName} [{ctrlB}] ───", playerBStyle);
            y += 18f;
            int hpB = matchManager.PlayerB.Health.CurrentHealth;
            int maxB = matchManager.PlayerB.Health.MaxHealth;
            GUI.Label(new Rect(x, y, 270, 16), $"  HP: {hpB}/{maxB}  Score: {pB.Score:F1}", playerBStyle);
            y += 16f;
            GUI.Label(new Rect(x, y, 270, 16), $"  Hits: {pB.Hits}  Kills: {pB.Kills}  Deaths: {pB.Deaths}", playerBStyle);
            y += 22f;

            // Health bar
            DrawHealthBar(x, y, 260, 8, (float)hpB / maxB, new Color(1f, 0.5f, 0.4f));
            y += 16f;
        }

        // Cooldown status
        if (matchManager.IsInCooldown)
        {
            GUI.Label(new Rect(x, y, 270, 16), $"⏳ COOLDOWN: {matchManager.CooldownRemaining:F1}s", statusStyle);
            y += 18f;
        }

        if (matchManager.IsMatchOver)
        {
            GUI.Label(new Rect(x, y, 270, 16), "🏆 MATCH OVER — Press R to restart", statusStyle);
            y += 18f;
        }

        // Controls help
        y += 5f;
        GUIStyle smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 10, normal = { textColor = Color.gray } };
        GUI.Label(new Rect(x, y, 270, 14), "[1] Observer  [2] A POV  [3] B POV", smallStyle);
        y += 14f;
        GUI.Label(new Rect(x, y, 270, 14), "[Tab] Toggle HUD  [R] Reset Match", smallStyle);

        DrawActionHUD(matchManager.PlayerA, 50f, Screen.height - 120f);
        DrawActionHUD(matchManager.PlayerB, Screen.width - 200f, Screen.height - 120f);
        DrawEnemyHeatBar();
        DrawContinuousContactTimer();

        // Event feed (center of screen, fades after 3 seconds)
        if (!string.IsNullOrEmpty(lastEvent) && Time.time - lastEventTime < 3f)
        {
            float alpha = Mathf.Clamp01(1f - (Time.time - lastEventTime - 2f));
            eventStyle.normal.textColor = new Color(1f, 1f, 1f, alpha);

            // Background
            float feedW = 400f;
            float feedH = 30f;
            float feedX = (Screen.width - feedW) / 2f;
            float feedY = Screen.height * 0.15f;

            GUI.color = new Color(0f, 0f, 0f, 0.5f * alpha);
            GUI.DrawTexture(new Rect(feedX, feedY, feedW, feedH), Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUI.Label(new Rect(feedX, feedY, feedW, feedH), lastEvent, eventStyle);
        }

        DrawBeliefHUD();
        DrawDeferredContinuationHUD();

        // Crosshair — always drawn (even when HUD panel is hidden)
        DrawCrosshair();
    }

    private void DrawBeliefHUD()
    {
        if (System.Environment.GetEnvironmentVariable("PHASE4_5_SHOW_BELIEF_HUD") != "1")
            return;
        Phase45LiveTelemetryBridge bridge = UnityEngine.Object.FindObjectOfType<Phase45LiveTelemetryBridge>();
        if (bridge == null || !bridge.HasBeliefPrior)
            return;

        float w = 170f;
        float h = 46f;
        float x = Screen.width - w - 12f;
        float y = 12f;
        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(x + 6f, y + 5f, w - 12f, 18f), $"score {bridge.BeliefMu:F1}", beliefStyle);
        GUI.Label(new Rect(x + 6f, y + 24f, w - 12f, 18f), $"unc {bridge.BeliefSigma:F1}", beliefStyle);
    }

    private void DrawDeferredContinuationHUD()
    {
        bool recentDeferred = !string.IsNullOrEmpty(deferredEvent) && Time.time - deferredEventTime < 2.75f;
        float safetyRemaining = matchManager != null ? matchManager.DeferredDamageImmunityRemaining : 0f;
        if (!recentDeferred && safetyRemaining <= 0f)
            return;

        float w = Mathf.Min(560f, Screen.width - 32f);
        float h = safetyRemaining > 0f ? 76f : 54f;
        float x = (Screen.width - w) / 2f;
        float y = Screen.height * 0.28f;
        GUI.color = new Color(0f, 0f, 0f, 0.64f);
        GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
        GUI.color = Color.white;
        string label = string.IsNullOrEmpty(deferredEvent) ? "RESPAWN SAFETY" : deferredEvent;
        GUI.Label(new Rect(x + 8f, y + 9f, w - 16f, 34f), label, deferredStyle);
        if (safetyRemaining > 0f)
        {
            GUIStyle safeStyle = new GUIStyle(statusStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.yellow }
            };
            GUI.Label(new Rect(x + 8f, y + 46f, w - 16f, 22f), $"safety {safetyRemaining:F1}s", safeStyle);
        }
    }

    private void DrawEnemyHeatBar()
    {
        if (!showEnemyHeatBar || matchManager == null) return;

        PlayerBody source = GetHeatSourcePlayer();
        if (source == null) return;
        PlayerBody nearest = GetNearestEnemy(source);
        bool targetValid = nearest != null;
        float distance = targetValid ? Vector3.Distance(source.transform.position, nearest.transform.position) : float.PositiveInfinity;
        float heat = targetValid ? Mathf.InverseLerp(EnemyHeatColdDistance, EnemyHeatHotDistance, distance) : 0f;
        Color cold = new Color(0.05f, 0.86f, 0.25f, 0.95f);
        Color warm = new Color(1f, 0.82f, 0.12f, 0.95f);
        Color hot = new Color(1f, 0.10f, 0.06f, 0.95f);
        Color fillColor = heat < 0.55f
            ? Color.Lerp(cold, warm, heat / 0.55f)
            : Color.Lerp(warm, hot, (heat - 0.55f) / 0.45f);

        float width = Mathf.Min(250f, Screen.width - 40f);
        float height = 58f;
        float x = Screen.width - width - 20f;
        float y = Screen.height - 196f;
        if (Screen.width < 560f)
        {
            x = Screen.width - width - 12f;
            y = Screen.height - 184f;
        }

        GUI.color = new Color(0f, 0f, 0f, 0.62f);
        GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUI.Label(new Rect(x + 10f, y + 6f, 130f, 18f), "ENEMY HEAT", heatLabelStyle);
        GUI.Label(new Rect(x + width - 94f, y + 6f, 84f, 18f), targetValid ? $"{distance:F1}m" : "SEARCH", heatValueStyle);

        float barX = x + 10f;
        float barY = y + 30f;
        float barW = width - 20f;
        float barH = 14f;
        GUI.color = new Color(0.08f, 0.08f, 0.08f, 0.90f);
        GUI.DrawTexture(new Rect(barX, barY, barW, barH), Texture2D.whiteTexture);

        int segments = 18;
        for (int i = 0; i < segments; i++)
        {
            float t0 = (float)i / segments;
            float t1 = (float)(i + 1) / segments;
            if (t0 > heat) break;
            float segFill = Mathf.Clamp01((heat - t0) / (t1 - t0));
            float segX = barX + barW * t0;
            float segW = (barW / segments - 1f) * segFill;
            Color segColor = t0 < 0.55f
                ? Color.Lerp(cold, warm, t0 / 0.55f)
                : Color.Lerp(warm, hot, (t0 - 0.55f) / 0.45f);
            GUI.color = segColor;
            GUI.DrawTexture(new Rect(segX, barY, segW, barH), Texture2D.whiteTexture);
        }

        GUI.color = new Color(1f, 1f, 1f, 0.24f);
        GUI.DrawTexture(new Rect(barX, barY, barW, 1f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(barX, barY + barH - 1f, barW, 1f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(barX, barY, 1f, barH), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(barX + barW - 1f, barY, 1f, barH), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    private void DrawContinuousContactTimer()
    {
        if (matchManager == null || !matchManager.ContinuousContactTimerActive)
            return;
        float timeout = matchManager.ContinuousContactTimeoutSeconds;
        if (timeout <= 0f)
            return;
        float remaining = matchManager.ContinuousContactTimeRemaining;
        float frac = Mathf.Clamp01(remaining / timeout);

        float width = Mathf.Min(250f, Screen.width - 40f);
        float height = 42f;
        float x = Screen.width - width - 20f;
        float heatY = Screen.height - 196f;
        if (Screen.width < 560f)
        {
            x = Screen.width - width - 12f;
            heatY = Screen.height - 184f;
        }
        float y = heatY - 50f;

        Color ok = new Color(0.12f, 0.82f, 0.24f, 0.95f);
        Color warn = new Color(1f, 0.78f, 0.10f, 0.95f);
        Color danger = new Color(1f, 0.14f, 0.08f, 0.95f);
        Color fill = frac > 0.5f
            ? Color.Lerp(warn, ok, (frac - 0.5f) / 0.5f)
            : Color.Lerp(danger, warn, frac / 0.5f);

        GUI.color = new Color(0f, 0f, 0f, 0.62f);
        GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(x + 10f, y + 4f, 120f, 16f), "CONTACT", contactTimerStyle);
        GUI.Label(new Rect(x + width - 78f, y + 2f, 68f, 18f), $"{remaining:F1}s", contactTimerValueStyle);

        float barX = x + 10f;
        float barY = y + 24f;
        float barW = width - 20f;
        float barH = 8f;
        GUI.color = new Color(0.08f, 0.08f, 0.08f, 0.90f);
        GUI.DrawTexture(new Rect(barX, barY, barW, barH), Texture2D.whiteTexture);
        GUI.color = fill;
        GUI.DrawTexture(new Rect(barX, barY, barW * frac, barH), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    private PlayerBody GetHeatSourcePlayer()
    {
        if (matchManager == null) return null;
        if (matchManager.PlayerA != null && matchManager.PlayerA.IsLocalPlayer) return matchManager.PlayerA;
        if (matchManager.PlayerB != null && matchManager.PlayerB.IsLocalPlayer) return matchManager.PlayerB;
        return matchManager.PlayerA != null ? matchManager.PlayerA : matchManager.PlayerB;
    }

    private PlayerBody GetNearestEnemy(PlayerBody source)
    {
        if (source == null || matchManager == null) return null;
        PlayerBody nearest = null;
        float bestSqr = float.PositiveInfinity;
        ConsiderEnemy(matchManager.PlayerA, source, ref nearest, ref bestSqr);
        ConsiderEnemy(matchManager.PlayerB, source, ref nearest, ref bestSqr);
        return nearest;
    }

    private bool IsFiniteVector(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z)
            && !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);
    }

    private void ConsiderEnemy(PlayerBody candidate, PlayerBody source, ref PlayerBody nearest, ref float bestSqr)
    {
        if (candidate == null || candidate == source) return;
        if (candidate.Health != null && candidate.Health.CurrentHealth <= 0) return;
        Vector3 sourcePos = source.transform.position;
        Vector3 candidatePos = candidate.transform.position;
        if (!IsFiniteVector(sourcePos) || !IsFiniteVector(candidatePos)) return;
        if (candidatePos.y < EnemyHeatMinValidY) return;
        float sqr = (candidatePos - sourcePos).sqrMagnitude;
        float maxSqr = EnemyHeatMaxDisplayDistance * EnemyHeatMaxDisplayDistance;
        if (sqr > maxSqr) return;
        if (sqr < bestSqr)
        {
            bestSqr = sqr;
            nearest = candidate;
        }
    }

    private void DrawActionHUD(PlayerBody player, float startX, float startY)
    {
        if (player == null || player.Controller == null) return;
        PlayerCommand cmd = player.Controller.GetCommand();

        // Background
        GUI.color = new Color(0f, 0f, 0f, 0.5f);
        GUI.DrawTexture(new Rect(startX - 10, startY - 10, 160, 110), Texture2D.whiteTexture);

        // Render WASD
        GUI.color = cmd.MoveZ > 0.5f ? Color.green : Color.white;
        GUI.Label(new Rect(startX + 40, startY, 30, 20), "W", headerStyle);

        GUI.color = cmd.MoveX < -0.5f ? Color.green : Color.white;
        GUI.Label(new Rect(startX, startY + 30, 30, 20), "A", headerStyle);

        GUI.color = cmd.MoveZ < -0.5f ? Color.green : Color.white;
        GUI.Label(new Rect(startX + 40, startY + 30, 30, 20), "S", headerStyle);

        GUI.color = cmd.MoveX > 0.5f ? Color.green : Color.white;
        GUI.Label(new Rect(startX + 80, startY + 30, 30, 20), "D", headerStyle);

        // Render Turn
        GUI.color = cmd.Turn < -0.5f ? Color.green : Color.white;
        GUI.Label(new Rect(startX, startY + 60, 30, 20), "<-", headerStyle);

        GUI.color = cmd.Turn > 0.5f ? Color.green : Color.white;
        GUI.Label(new Rect(startX + 80, startY + 60, 30, 20), "->", headerStyle);

        // Render Shoot
        GUI.color = cmd.Shoot ? Color.red : Color.white;
        GUI.Label(new Rect(startX + 30, startY + 60, 60, 20), "SHOOT", headerStyle);

        GUI.color = Color.white;
    }

    private void DrawCrosshair()
    {
        float cx = Screen.width / 2f;
        float cy = Screen.height / 2f;
        float size = 10f;    // arm length in pixels
        float thick = 1f;    // line thickness
        float gap = 3f;      // gap around center

        // Very faint white
        GUI.color = new Color(1f, 1f, 1f, 0.35f);

        // Horizontal arms
        GUI.DrawTexture(new Rect(cx - size - gap, cy - thick / 2f, size, thick), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(cx + gap, cy - thick / 2f, size, thick), Texture2D.whiteTexture);

        // Vertical arms
        GUI.DrawTexture(new Rect(cx - thick / 2f, cy - size - gap, thick, size), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(cx - thick / 2f, cy + gap, thick, size), Texture2D.whiteTexture);

        // Tiny center dot
        GUI.color = new Color(1f, 1f, 1f, 0.5f);
        GUI.DrawTexture(new Rect(cx - 0.5f, cy - 0.5f, 1f, 1f), Texture2D.whiteTexture);

        GUI.color = Color.white;
    }

    private void DrawHealthBar(float x, float y, float width, float height, float fillPercent, Color fillColor)
    {
        // Background
        GUI.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);
        GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);

        // Fill
        GUI.color = fillColor;
        GUI.DrawTexture(new Rect(x, y, width * fillPercent, height), Texture2D.whiteTexture);

        GUI.color = Color.white;
    }
}
