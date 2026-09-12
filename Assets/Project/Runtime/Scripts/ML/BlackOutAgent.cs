using System;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;
using SensorCompressionType = Unity.MLAgents.Sensors.SensorCompressionType;

/// <summary>
/// ML-Agents Agent for a single unit in Black Out.
/// 10 instances run in parallel (5 per team), sharing behavior name "BlackOutUnit".
/// Behavior Parameters: Vector Observation Size=RawObsSize(1), Continuous Actions=2, TeamId=0(A)/1(B).
/// The shared 10-unit state table and game-state scalars are broadcast separately, once per
/// step, by MapObsAgent — see MapObsAgent.cs.
/// </summary>
public class BlackOutAgent : Agent
{
    // Raw obs is just this unit's own index (1 float) — the shared 10-unit state table and
    // game-state scalars are now broadcast once per step by MapObsAgent instead of being
    // redundantly resubmitted by all 10 BlackOutUnit agents. unitIndex alone is enough for
    // Python to route this agent's per-step decision/reward/termination.
    private const int RawObsSize = 1;

    [SerializeField] private int unitIndex; // 0-4: Team A, 5-9: Team B

    [Tooltip("Request a fresh decision every N FixedUpdate ticks; the previous action is " +
        "repeated on skipped ticks (replaces the DecisionRequester component, which this " +
        "class's own RequestDecision() call already made a no-op).")]
    [SerializeField] private int decisionPeriod = 2;

    private int ticksUntilDecision;

    // Set by OnEpisodeBegin so the FixedUpdate right after an episode ends skips
    // RequestDecision(). EndEpisode() already sent this tick's terminal AgentInfo
    // synchronously (Agent.NotifyAgentDone); requesting another decision on the same tick
    // would overwrite that terminal info in the brain's per-agent buffer before it ever
    // reaches Python, silently dropping the "done" signal for this episode.
    private bool suppressDecisionThisTick;

    private BlackOutEpisodeCoordinator coordinator;
    private GameScenario gameScenario;
    private MatchManager matchManager;
    private Unit unit;
    private TeamContext myTeamCtx;
    private TeamContext opponentCtx;
    private RewardConfig rewardConfig;
    private Action<int> onMyTeamScored;
    private Action<int> onOpponentScored;
    private Action<Unit, ItemObject> onItemPickedUp;
    private Action<Unit, Unit> onUnitKilled;
    private Action<Unit, ItemData> onItemDeposited;
    private Action<ItemObject> onItemAbsorbed;

    public int UnitIndex => unitIndex;

    /// <summary>
    /// Initializes agent references and subscribes to score events.
    /// Called once by <see cref="BlackOutEpisodeCoordinator"/> after <see cref="GameScenario.Initialize"/>.
    /// </summary>
    public void Setup(BlackOutEpisodeCoordinator episodeCoordinator, GameScenario scenario, RewardConfig config)
    {
        coordinator = episodeCoordinator;
        gameScenario = scenario;
        rewardConfig = config;
        matchManager = scenario.MatchManager;
        unit = matchManager.Units[unitIndex];
        myTeamCtx = matchManager.GetTeamContext(unit.Team);
        opponentCtx = matchManager.GetTeamContext(matchManager.OpponentTeam(unit.Team));

        // NOTE: score > 0 guard prevents false reward when TeamContext.Reset() fires OnScoreChanged(0).
        onMyTeamScored = score =>
        {
            if (score <= 0) return;
            AddReward(rewardConfig.teamScoreReward);
            RewardEventLog.Record(unitIndex, "team score+", rewardConfig.teamScoreReward);
        };
        onOpponentScored = score =>
        {
            if (score <= 0) return;
            AddReward(-rewardConfig.teamScorePenalty);
            RewardEventLog.Record(unitIndex, "enemy score+", -rewardConfig.teamScorePenalty);
        };

        onItemPickedUp = (picker, item) =>
        {
            bool fromOurStorage = item.OwnedTile?.OwnedRegion is Storage s && s.OwnedTeam == unit.Team;
            float rv = rewardConfig.GetItemReward(item.ItemData.name);
            if (picker == unit && !fromOurStorage)
            {
                // 내가 필드에서 집음
                AddReward(rv);
                RewardEventLog.Record(unitIndex, $"pickup:{item.ItemData.name}", rv);
            }
            else if (picker.Team != unit.Team && fromOurStorage)
            {
                // 적이 아군 창고에서 탈취 → 아군 전체 패널티
                AddReward(-rv);
                RewardEventLog.Record(unitIndex, $"stolen:{item.ItemData.name}", -rv);
            }
        };

        onUnitKilled = (killer, victim) =>
        {
            if (killer == unit)
            {
                AddReward(rewardConfig.killReward);
                RewardEventLog.Record(unitIndex, "kill", rewardConfig.killReward);
            }
            if (victim.Team == unit.Team)
            {
                AddReward(-rewardConfig.deathPenalty);
                RewardEventLog.Record(unitIndex, "death", -rewardConfig.deathPenalty);
            }
        };

        onItemDeposited = (depositor, itemData) =>
        {
            if (depositor != unit) return;
            float rv = rewardConfig.GetItemReward(itemData.name);
            AddReward(rv);
            RewardEventLog.Record(unitIndex, $"deposit:{itemData.name}", rv);
        };

        onItemAbsorbed = item =>
        {
            // 아군 창고 아이템 absorb → 아군 전체 보상
            if (item.OwnedTile?.OwnedRegion is Storage s && s.OwnedTeam == unit.Team)
            {
                float rv = rewardConfig.GetItemReward(item.ItemData.name);
                AddReward(rv);
                RewardEventLog.Record(unitIndex, $"absorbed:{item.ItemData.name}", rv);
            }
        };

        myTeamCtx.OnScoreChanged += onMyTeamScored;
        opponentCtx.OnScoreChanged += onOpponentScored;
        scenario.EventBus.Unit.OnItemPickedUp += onItemPickedUp;
        scenario.EventBus.Unit.OnUnitKilled += onUnitKilled;
        scenario.EventBus.Unit.OnItemDeposited += onItemDeposited;
        scenario.EventBus.Unit.OnItemAbsorbed += onItemAbsorbed;

        // Assign the correct team-perspective RenderTexture to the sensor.
        // Must be done here (Awake phase) before Agent.OnEnable() calls InitializeSensors().
        var rtSensor = GetComponent<RenderTextureSensorComponent>();
        if (rtSensor != null)
        {
            SemanticMapRenderer renderer = episodeCoordinator.SemanticMapRenderer;
            rtSensor.RenderTexture = unit.Team == matchManager.TeamA
                ? renderer.RenderTextureTeamA
                : renderer.RenderTextureTeamB;
            rtSensor.Grayscale = true;
            rtSensor.CompressionType = SensorCompressionType.None;
        }
    }

    /// <summary>
    /// Notifies coordinator that this agent is ready.
    /// Coordinator starts a new game episode once all 10 agents have notified.
    /// </summary>
    public override void OnEpisodeBegin()
    {
        suppressDecisionThisTick = true;
        ticksUntilDecision = 0;
        coordinator?.NotifyAgentEpisodeBegin();
    }

    /// <summary>
    /// Collects this agent's single-float observation: its own unitIndex. Position, item,
    /// class, and game-state data are no longer sent per-agent — MapObsAgent broadcasts that
    /// shared table once per step instead of all 10 BlackOutUnit agents redundantly resending
    /// their own near-identical copy of it. unitIndex alone is enough for Python to route this
    /// agent's decision/reward/termination to the right slot in that shared table.
    /// </summary>
    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation((float)unitIndex);
    }

    private void FixedUpdate()
    {
        if (suppressDecisionThisTick)
        {
            suppressDecisionThisTick = false;
            return;
        }

        if (ticksUntilDecision <= 0)
        {
            ticksUntilDecision = decisionPeriod - 1;
            RequestDecision();
        }
        else
        {
            ticksUntilDecision--;
            RequestAction();
        }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (gameScenario.CurrentState != GameState.Playing) return;
        gameScenario.MoveUnit(
            unitIndex,
            new Vector2(actions.ContinuousActions[0], actions.ContinuousActions[1]),
            Time.fixedDeltaTime
        );
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var ca = actionsOut.ContinuousActions;
        ca[0] = UnityEngine.InputSystem.Keyboard.current != null
            ? (UnityEngine.InputSystem.Keyboard.current.dKey.isPressed ? 1f :
               UnityEngine.InputSystem.Keyboard.current.aKey.isPressed ? -1f : 0f)
            : 0f;
        ca[1] = UnityEngine.InputSystem.Keyboard.current != null
            ? (UnityEngine.InputSystem.Keyboard.current.wKey.isPressed ? 1f :
               UnityEngine.InputSystem.Keyboard.current.sKey.isPressed ? -1f : 0f)
            : 0f;
    }

    private void OnDestroy()
    {
        if (myTeamCtx != null) myTeamCtx.OnScoreChanged -= onMyTeamScored;
        if (opponentCtx != null) opponentCtx.OnScoreChanged -= onOpponentScored;
        if (gameScenario?.EventBus != null)
        {
            gameScenario.EventBus.Unit.OnItemPickedUp -= onItemPickedUp;
            gameScenario.EventBus.Unit.OnUnitKilled -= onUnitKilled;
            gameScenario.EventBus.Unit.OnItemDeposited -= onItemDeposited;
            gameScenario.EventBus.Unit.OnItemAbsorbed -= onItemAbsorbed;
        }
    }
}
