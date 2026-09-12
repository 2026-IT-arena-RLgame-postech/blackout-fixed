using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

/// <summary>
/// Broadcasts the TeamA semantic map (visual) and the shared 10-unit game state (vector)
/// once per step, instead of each of the 10 BlackOutUnit agents redundantly sending their
/// own near-identical copy. Uses DynamicRTSensorComponent for the visual half so the
/// RenderTexture can be assigned after the agent's sensors are initialized (avoiding the
/// timing race between BlackOutEpisodeCoordinator.Awake and Agent.OnEnable→InitializeSensors).
///
/// Setup (Unity Editor):
///   1. Attach this component to a GameObject in the ML training scene.
///   2. Do NOT attach a RenderTextureSensorComponent — this agent creates its own sensor.
///   3. Behavior Parameters: Name = "BlackOutMap", Continuous Actions = 0,
///      Discrete Branches = 0, Vector Observations = RawStateSize (44).
///   4. Assign this agent in BlackOutEpisodeCoordinator inspector.
/// </summary>
public class MapObsAgent : Agent
{
    private DynamicRTSensorComponent _sensorComp;

    private BlackOutEpisodeCoordinator coordinator;
    private GameScenario gameScenario;
    private MatchManager matchManager;

    // Raw state layout: 10 units × 4 floats (pos_x, pos_y, holdingItemId, classId) + 4 scalars
    // (score_A, score_B, episode_time_left, absorption_time_left). No team_sign and no
    // unitIndex here — team is derived from block index in Python (fixed 0-4=A/5-9=B
    // convention), and unitIndex is carried separately by each BlackOutUnit agent's own
    // 1-float observation (needed for per-agent action/reward routing).
    private const int NUnits = 10;
    private const int UnitBlockSize = 4;
    private const int ScalarCount = 4;
    private const int RawStateSize = NUnits * UnitBlockSize + ScalarCount;

    private void Awake()
    {
        // Remove any stale RenderTextureSensorComponent to prevent duplicate sensors.
        var legacy = GetComponent<RenderTextureSensorComponent>();
        if (legacy != null)
        {
            Debug.LogWarning("[MapObsAgent] Removing RenderTextureSensorComponent — sensor is now created by DynamicRTSensorComponent.", this);
            DestroyImmediate(legacy);
        }

        // Ensure DynamicRTSensorComponent exists before OnEnable → InitializeSensors runs.
        _sensorComp = GetComponent<DynamicRTSensorComponent>();
        if (_sensorComp == null)
            _sensorComp = gameObject.AddComponent<DynamicRTSensorComponent>();
    }

    /// <summary>
    /// Assigns the TeamA RenderTexture and the game-state references needed to broadcast the
    /// shared unit-state table. Safe to call before or after InitializeSensors —
    /// DynamicRTSensorComponent handles both orderings internally.
    /// </summary>
    public void Setup(SemanticMapRenderer semanticRenderer, BlackOutEpisodeCoordinator episodeCoordinator, GameScenario scenario)
    {
        _sensorComp.SetSource(semanticRenderer);
        coordinator = episodeCoordinator;
        gameScenario = scenario;
        matchManager = scenario.MatchManager;
    }

    private void FixedUpdate() => RequestDecision();

    /// <summary>
    /// Broadcasts the shared, team-agnostic game state once per step. Position/holdingItemId/
    /// classId are objective (not observer-relative), and team scores are absolute (this agent
    /// isn't owned by either team) — so a single copy covers both teams' Python-side views.
    /// </summary>
    public override void CollectObservations(VectorSensor sensor)
    {
        if (matchManager == null)
        {
            for (int i = 0; i < RawStateSize; i++) sensor.AddObservation(0f);
            return;
        }

        Vector2 mapOrigin = coordinator.MapOriginWorld;
        Vector2 bounds = coordinator.MapBounds;

        foreach (Unit u in matchManager.Units)
        {
            // Normalized to [-1, 1] (not [0, 1]) so position is zero-centered like the other
            // per-unit fields (team sign is already +-1); Python passes this through as-is
            // (see MyObsPreprocessor.preprocess_agent_states), so this is the only place the
            // scale is defined.
            sensor.AddObservation((u.GlobalPos - mapOrigin) / bounds * 2f - Vector2.one);
            int itemIdx = u.HoldingItem != null ? coordinator.GetItemIndex(u.HoldingItem.ItemData) : -1;
            float holdingItemId;
            if (itemIdx < 0)
                holdingItemId = 0f;                                    // not holding anything
            else if (itemIdx == 0)
                holdingItemId = -(float)u.HoldingItem.ItemAmount;      // battery: sign-encoded stack count
            else
                holdingItemId = (float)(itemIdx + 1);                  // other items
            sensor.AddObservation(holdingItemId);
            sensor.AddObservation((float)coordinator.GetClassIndex(u.UnitData));
        }

        float targetScore = gameScenario.BalanceConfig.TargetScore;
        sensor.AddObservation(matchManager.GetTeamContext(matchManager.TeamA).Score / targetScore);
        sensor.AddObservation(matchManager.GetTeamContext(matchManager.TeamB).Score / targetScore);
        sensor.AddObservation(gameScenario.EpisodeTimer != null ? 1f - gameScenario.EpisodeTimer.Ratio : 1f);
        sensor.AddObservation(gameScenario.AbsorptionTimer != null ? 1f - gameScenario.AbsorptionTimer.Ratio : 1f);
    }

    public override void OnActionReceived(ActionBuffers actions) { }
}

/// <summary>
/// SensorComponent that creates a DynamicRTSensor at InitializeSensors time.
/// The RenderTexture can be assigned before or after CreateSensors() is called;
/// _pendingTexture covers the case where Setup hasn't run yet.
/// </summary>
public class DynamicRTSensorComponent : SensorComponent
{
    private SemanticMapRenderer _pendingSource;
    private DynamicRTSensor _sensor;

    public void SetSource(SemanticMapRenderer source)
    {
        _pendingSource = source;
        _sensor?.SetSource(source);
    }

    public override ISensor[] CreateSensors()
    {
        _sensor = new DynamicRTSensor("TeamAMap");
        if (_pendingSource != null)
            _sensor.SetSource(_pendingSource);
        return new ISensor[] { _sensor };
    }
}

/// <summary>
/// Visual-shaped sensor that writes SemanticMapRenderer's CPU-side packed ushort map.
/// It deliberately avoids a RenderTexture readback: headless/NullGfx R16 ReadPixels can
/// return invalid constant data, and a GPU round-trip is unnecessary because the renderer
/// already owns the exact CPU array.
/// </summary>
internal class DynamicRTSensor : ISensor
{
    private SemanticMapRenderer _source;
    private readonly string _name;

    // Dimensions used for the ObservationSpec — updated when RT is first assigned.
    private int _width;
    private int _height;
    private ObservationSpec _spec;

    internal DynamicRTSensor(string name)
    {
        _name = name;
        // Placeholder spec; real dimensions are set when SetRenderTexture is called.
        _width = 1;
        _height = 1;
        _spec = ObservationSpec.Visual(1, _height, _width);
    }

    internal void SetSource(SemanticMapRenderer source)
    {
        _source = source;
        if (source == null) return;

        if (_width != source.TextureWidth || _height != source.TextureHeight)
        {
            _width = source.TextureWidth;
            _height = source.TextureHeight;
            _spec = ObservationSpec.Visual(1, _height, _width);
        }
    }

    public string GetName() => _name;
    public ObservationSpec GetObservationSpec() => _spec;
    public byte[] GetCompressedObservation() => null;
    public CompressionSpec GetCompressionSpec() => CompressionSpec.Default();
    public void Update() { }
    public void Reset() { }

    public int Write(ObservationWriter writer)
    {
        ushort[] pixels = _source?.TeamAPixels;
        if (pixels == null || pixels.Length != _width * _height) return 0;

        // Texture2D stores rows bottom-up; ObservationWriter expects top-down.
        // Normalize the packed value into [0,1] for the ML-Agents float observation channel;
        // MyObsPreprocessor on the Python side multiplies back by the same divisor and bit-unpacks.
        for (int y = 0; y < _height; y++)
        {
            int srcY = _height - 1 - y;
            for (int x = 0; x < _width; x++)
                writer[0, y, x] = pixels[srcY * _width + x] / SemanticMapRenderer.PACK_DIVISOR;
        }
        return _width * _height;
    }
}
