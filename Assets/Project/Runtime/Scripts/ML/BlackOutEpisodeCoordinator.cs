using Unity.MLAgents.SideChannels;
using UnityEngine;

/// <summary>
/// Manages episode lifecycle for the 10-agent Black Out ML training setup.
/// Replaces <see cref="GameBootstrapper"/> in ML training scenes — do not use both simultaneously.
/// </summary>
public class BlackOutEpisodeCoordinator : MonoBehaviour
{
    [Header("Game")]
    [SerializeField] private GameScenario gameScenario;

    [Tooltip("All 10 BlackOutAgent components. Array index must match each agent's unitIndex.")]
    [SerializeField] private BlackOutAgent[] agents;

    [Header("Observation Settings")]
    [Tooltip("Playable map size in world units. Used to normalize relative positions in observations.")]
    [SerializeField] private Vector2 mapBounds = new Vector2(20f, 20f);

    [Tooltip("Unit classes in observation encoding order (Worker, Guard, Carrier). Index = classId float.")]
    [SerializeField] private UnitData[] knownClasses;

    [Tooltip("Known item types in holdingItemId encoding order. Index+1 = float ID (0=none).")]
    [SerializeField] private ItemData[] knownItems;

    [Tooltip("Semantic map renderer (shared, one per scene). Renders ally/enemy ID map per team.")]
    [SerializeField] private SemanticMapRenderer semanticMapRenderer;

    [Tooltip("Agent that broadcasts both team maps over gRPC (reduces graphic transmissions from 10 to 2).")]
    [SerializeField] private MapObsAgent mapObsAgent;

    /// <summary>Gets the map size used to normalize absolute positions in observations.</summary>
    public Vector2 MapBounds => mapBounds;

    /// <summary>Gets the world-space bottom-left origin of the map for absPos normalization.</summary>
    public Vector2 MapOriginWorld => gameScenario.MapManager.MapOriginWorld;

    /// <summary>Gets the ordered unit class list for classId encoding.</summary>
    public UnitData[] KnownClasses => knownClasses;

    /// <summary>Gets the ordered item list for holdingItemId encoding.</summary>
    public ItemData[] KnownItems => knownItems;

    /// <summary>Gets the semantic map renderer used by all agents in this scene.</summary>
    public SemanticMapRenderer SemanticMapRenderer => semanticMapRenderer;

    private int episodeBeginCount;
    private SeedChannel _seedChannel;
    private RewardConfig rewardConfig;
    private PotentialRewardCalculator potentialCalc;
    private float prevPsiA;
    private IndividualNavPotentialCalculator navPotentialCalc;
    private float[] prevPhi;

    private void Awake()
    {
        // Allow Unity to run in background (required for standalone training builds).
        Application.runInBackground = true;

        // Bound FixedUpdate catch-up per rendered frame so a stalled Python trainer can't
        // spiral (each tick blocks on a gRPC round-trip). This used to be clamped to exactly
        // Time.fixedDeltaTime (1 tick/frame) to also stop a terminal/decision race — a new
        // episode's first decision silently overwriting the previous episode's terminal
        // signal when both landed in the same Academy step — but that made Time.timeScale
        // unable to speed up training (timeScale works by letting several ticks run per
        // rendered frame). The race is now prevented directly in BlackOutAgent, which
        // suppresses its own RequestDecision() for one tick right after OnEpisodeBegin, so
        // this only needs to guard against runaway catch-up, not single-step it.
        Time.maximumDeltaTime = Time.fixedDeltaTime * 50f;

        gameScenario.Initialize();

        // Must create RenderTextures before agent.Setup() so RenderTextureSensorComponent
        // can reference them during Agent.OnEnable() → InitializeSensors().
        semanticMapRenderer.CreateTextures();
        mapObsAgent?.Setup(semanticMapRenderer, this, gameScenario);

        _seedChannel = new SeedChannel();
        SideChannelManager.RegisterSideChannel(_seedChannel);

        rewardConfig = RewardConfig.Load();
        potentialCalc = new PotentialRewardCalculator(gameScenario, rewardConfig);
        navPotentialCalc = new IndividualNavPotentialCalculator(gameScenario, rewardConfig);
        prevPhi = new float[agents.Length];

        foreach (var agent in agents)
            agent.Setup(this, gameScenario, rewardConfig);

        semanticMapRenderer.SubscribeEvents(gameScenario.EventBus);
        gameScenario.EventBus.Flow.OnGameEnded += OnGameEnded;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Debug-only reward overlay (F9 to toggle). Self-attached — no scene wiring needed.
        gameObject.AddComponent<RewardDebugUI>().Initialize(agents);
#endif
    }

    private void Start()
    {
        BeginEpisode();
    }

    private void FixedUpdate()
    {
        gameScenario.EpisodeUpdate(Time.fixedDeltaTime);
        semanticMapRenderer.Render();
        ApplyPotentialShaping();
        ApplyNavShaping();
    }

    /// <summary>
    /// Called by each <see cref="BlackOutAgent.OnEpisodeBegin"/>.
    /// Starts a new game episode once all agents have confirmed they are ready.
    /// </summary>
    public void NotifyAgentEpisodeBegin()
    {
        episodeBeginCount++;
        if (episodeBeginCount >= agents.Length)
        {
            episodeBeginCount = 0;
            BeginEpisode();
        }
    }

    /// <summary>
    /// Starts a new game episode and resets potential-shaping state so the previous episode's
    /// Ψ never leaks into the new one (reward_proposal.md Scenario 22).
    /// </summary>
    private void BeginEpisode()
    {
        gameScenario.EpisodeBegin();
        potentialCalc.OnEpisodeBegin();
        prevPsiA = potentialCalc.ComputePotential(gameScenario.MatchManager.TeamA);

        MatchManager mm = gameScenario.MatchManager;
        foreach (var agent in agents)
            prevPhi[agent.UnitIndex] = navPotentialCalc.ComputePotential(mm.Units[agent.UnitIndex]);
    }

    /// <summary>
    /// Adds potential-based shaping reward for this tick: η[γΨ(s') - Ψ(s)], split by sign
    /// between the two teams (Ψ_B = -Ψ_A always, so this is exactly zero-sum) and broadcast
    /// undivided to all 5 agents on each team, matching the existing kill/death/item reward
    /// convention (see reward_proposal.md §14.2).
    /// </summary>
    private void ApplyPotentialShaping()
    {
        if (gameScenario.CurrentState != GameState.Playing) return;

        MatchManager mm = gameScenario.MatchManager;
        float newPsiA = potentialCalc.ComputePotential(mm.TeamA);
        float shapedA = rewardConfig.potentialEta * (rewardConfig.potentialGamma * newPsiA - prevPsiA);
        prevPsiA = newPsiA;

        foreach (var agent in agents)
        {
            Unit unit = mm.Units[agent.UnitIndex];
            float r = unit.Team == mm.TeamA ? shapedA : -shapedA;
            agent.AddReward(r);
            RewardEventLog.Record(agent.UnitIndex, "potential-shaping", r);
        }
    }

    /// <summary>
    /// Adds individual navigation-potential shaping for this tick: η_nav[γΦ_i(s') - Φ_i(s)],
    /// per unit, credited only to that unit's own agent (not team-broadcast — unlike
    /// <see cref="ApplyPotentialShaping"/>, this is not required to be zero-sum between teams;
    /// see reward_proposal.md §15).
    /// </summary>
    private void ApplyNavShaping()
    {
        if (gameScenario.CurrentState != GameState.Playing) return;

        MatchManager mm = gameScenario.MatchManager;
        foreach (var agent in agents)
        {
            Unit unit = mm.Units[agent.UnitIndex];
            float newPhi = navPotentialCalc.ComputePotential(unit);
            float r = rewardConfig.navPotentialEta * (rewardConfig.potentialGamma * newPhi - prevPhi[agent.UnitIndex]);
            prevPhi[agent.UnitIndex] = newPhi;

            agent.AddReward(r);
            RewardEventLog.Record(agent.UnitIndex, "nav-shaping", r);
        }
    }

    /// <summary>
    /// Returns the index of <paramref name="unitData"/> within <see cref="KnownClasses"/>, or -1 if not found.
    /// </summary>
    public int GetClassIndex(UnitData unitData)
    {
        if (knownClasses == null) return -1;
        for (int i = 0; i < knownClasses.Length; i++)
            if (knownClasses[i] == unitData) return i;
        return -1;
    }

    /// <summary>
    /// Returns the index of <paramref name="itemData"/> within <see cref="KnownItems"/>, or -1 if not found.
    /// Unity sends <c>index + 1</c> as the float holdingItemId (0 = no item).
    /// </summary>
    public int GetItemIndex(ItemData itemData)
    {
        if (knownItems == null) return -1;
        for (int i = 0; i < knownItems.Length; i++)
            if (knownItems[i] == itemData) return i;
        return -1;
    }

    private void OnGameEnded(TeamData winner)
    {
        MatchManager mm = gameScenario.MatchManager;

        foreach (var agent in agents)
        {
            Unit unit = mm.Units[agent.UnitIndex];
            float reward = winner == null ? 0f : winner == unit.Team ? 1f : -1f;
            agent.AddReward(reward);
            RewardEventLog.Record(agent.UnitIndex, winner == null ? "DRAW" : reward > 0f ? "WIN" : "LOSE", reward);
            agent.EndEpisode();
        }
    }

    private void OnDestroy()
    {
        if (gameScenario?.EventBus != null)
            gameScenario.EventBus.Flow.OnGameEnded -= OnGameEnded;

        if (_seedChannel != null)
            SideChannelManager.UnregisterSideChannel(_seedChannel);
    }
}
