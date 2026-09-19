using UnityEngine;

/// <summary>
/// Renders a bit-packed semantic map of the game state to two single-channel (R16) RenderTextures,
/// one per team perspective. Each pixel is a 16-bit value packing three fields (see below).
/// Team A and Team B see ally/enemy tile categories flipped relative to each other.
/// Units are NOT represented here — unit positions are carried entirely by the vector observation.
///
/// Pixel bit layout (must match Python's MyObsPreprocessor):
///   bit 0-2 : base tile category (static, computed once per episode)
///             0=void 1=wall 2=site_hunter 3=site_carrier
///             4=spawn_ally 5=spawn_enemy 6=storage_ally 7=storage_enemy
///   bit 3-6 : battery stack count on this tile, saturated to [0,15] (covers the current
///             MaxItemAmount of 10 exactly, with headroom to 15 if it's raised later)
///   bit 7-9 : index of the non-battery item on this tile (0=none, 1=BuffSpeed, 2=DebuffSpeed,
///             3=BuffSize, 4=DebuffSize; 5-7 reserved for future items — see KnownItems ordering)
///   Packed value range is 0-1023 (all 10 bits used). Written to the texture as a raw ushort and
///   normalized by PACK_DIVISOR when read by the ML-Agents sensor (see MapObsAgent.cs).
///
/// Base and item fields are combined with bitwise OR — this only works because at most one item
/// ever occupies a given tile (enforced by Storage's merge logic and ground-spawn placement), so the
/// battery/item-index bit ranges never collide with each other.
///
/// R16 (16-bit single-channel, non-color) is used instead of RGB/ARGB so the pixel value round-trips
/// as an exact integer — no sRGB/gamma conversion applies to this format, unlike the previous
/// RGB24/ARGB32 approach which needed a manual sRGB round-trip to avoid corrupting small values.
///
/// Usage:
///   1. Attach to a GameObject in the ML training scene.
///   2. Set references in inspector (gameScenario, knownItems, siteHunterTileData, siteCarrierTileData, ...).
///   3. BlackOutEpisodeCoordinator calls CreateTextures() in Awake, then assigns RTs to agents.
///   4. BlackOutEpisodeCoordinator calls Render() each FixedUpdate.
/// </summary>
public class SemanticMapRenderer : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameScenario gameScenario;
    [Tooltip("Known item types in item-index encoding order. Index 0 MUST be the battery (stack count encoded); indices 1+ are presence-only items (max 7 supported by the 3-bit field).")]
    [SerializeField] private ItemData[] knownItems;
    [Tooltip("Tile data asset identifying 'hunter site' tiles.")]
    [SerializeField] private MapTileData siteHunterTileData;
    [Tooltip("Tile data asset identifying 'carrier site' tiles.")]
    [SerializeField] private MapTileData siteCarrierTileData;

    [Header("Config")]
    [Tooltip("Expected map size in tiles. Used to pre-create RenderTextures before map loads.")]
    [SerializeField] private Vector2Int defaultMapSize = new Vector2Int(24, 24);
    [Tooltip("Pixels per tile. 1 = exact tile grid (no sub-tile precision).")]
    [SerializeField] private int resolutionScale = 1;

    // ===== Bit layout (must match Python MyObsPreprocessor) =====
    private const int BATTERY_SHIFT = 3;
    private const int BATTERY_BITS = 4;
    private const int BATTERY_MAX = (1 << BATTERY_BITS) - 1; // 15 — real MaxItemAmount (10) fits with headroom; excess still saturates.
    private const int ITEM_SHIFT = BATTERY_SHIFT + BATTERY_BITS; // 7

    /// <summary>Divisor used to normalize the packed ushort into a float for the ML-Agents observation writer.</summary>
    /// <remarks>
    /// Contract with Python: blackout_env/env/my_obs_preprocessor.py MyObsPreprocessor.PACK_DIVISOR must
    /// hold the same value — Python decodes with round(x * PACK_DIVISOR). It must also stay strictly
    /// greater than the largest packed value (currently 1023 = 10 bits) so every value maps into [0, 1).
    /// The shift/width constants above and the ID_* values below are mirrored there too
    /// (BASE_MASK, BATTERY_SHIFT/MASK, ITEM_SHIFT/MASK, channel indices VOID..STORAGE_ENEMY);
    /// change both sides together. StreamingAssets/semantic_map_config.json is NOT read by Unity and
    /// its ids/item_id_offset/resolution_scale keys describe the old grayscale encoding.
    /// </remarks>
    public const float PACK_DIVISOR = 1024f;

    // Base tile category IDs — must match MyObsPreprocessor's channel layout.
    private const ushort ID_VOID          = 0;
    private const ushort ID_WALL          = 1;
    private const ushort ID_SITE_HUNTER   = 2;
    private const ushort ID_SITE_CARRIER  = 3;
    private const ushort ID_SPAWN_ALLY    = 4;
    private const ushort ID_SPAWN_ENEMY   = 5;
    private const ushort ID_STORAGE_ALLY  = 6;
    private const ushort ID_STORAGE_ENEMY = 7;

    /// <summary>RenderTexture for Team A agents (Team A = ally).</summary>
    public RenderTexture RenderTextureTeamA { get; private set; }
    /// <summary>RenderTexture for Team B agents (Team B = ally).</summary>
    public RenderTexture RenderTextureTeamB { get; private set; }
    /// <summary>CPU-side Team A packed pixels used by the headless ML sensor.</summary>
    /// <remarks>
    /// This array — not RenderTextureTeamA — is what reaches Python (DynamicRTSensor reads it directly).
    /// Row-major, bottom row first (Texture2D order); the sensor flips rows. The Team B array/RT are
    /// not sent over the wire at all: Python derives Team B from Team A.
    /// </remarks>
    public ushort[] TeamAPixels => pixelsA;
    public int TextureWidth => texWidth;
    public int TextureHeight => texHeight;

    private Texture2D textureA;
    private Texture2D textureB;
    private ushort[] pixelsA;
    private ushort[] pixelsB;
    private ushort[] backgroundA;
    private ushort[] backgroundB;
    private int texWidth;
    private int texHeight;
    private bool isInitialized;

    // ===== Setup (called from BlackOutEpisodeCoordinator.Awake) =====

    /// <summary>
    /// Creates RenderTexture objects using defaultMapSize * resolutionScale dimensions.
    /// Must be called BEFORE agent.Setup() so RenderTextureSensorComponent can reference the RTs.
    /// </summary>
    public void CreateTextures()
    {
        texWidth = defaultMapSize.x * resolutionScale;
        texHeight = defaultMapSize.y * resolutionScale;
        AllocateAll();
    }

    /// <summary>
    /// Subscribes to OnEpisodeStarted to re-render the static tile background when the map reloads.
    /// Must be called AFTER gameScenario.Initialize() (EventBus must exist).
    /// </summary>
    public void SubscribeEvents(GameEventBus eventBus)
    {
        eventBus.Flow.OnEpisodeStarted += OnEpisodeStarted;
    }

    // ===== Per-step render (called from BlackOutEpisodeCoordinator.FixedUpdate) =====

    /// <summary>
    /// Overlays ground items onto the cached static background and blits into both team RenderTextures.
    /// Call once per FixedUpdate, after game logic has run.
    /// </summary>
    public void Render()
    {
        if (!isInitialized) return;

        MapManager mapManager = gameScenario.MapManager;
        Vector2 mapOrigin = mapManager.MapOriginWorld;

        // Start from pre-computed tile background
        System.Array.Copy(backgroundA, pixelsA, pixelsA.Length);
        System.Array.Copy(backgroundB, pixelsB, pixelsB.Length);

        // Overlay ground items (identical for both team perspectives — items aren't team-relative)
        foreach (ItemObject item in gameScenario.LevelDirector.ActiveItems)
        {
            if (item.State != ItemObject.ItemState.OnGround) continue;
            int idx = GetItemIndex(item.ItemData);
            if (idx < 0) continue;

            ushort bits = idx == 0
                ? (ushort)(Mathf.Clamp(item.ItemAmount, 0, BATTERY_MAX) << BATTERY_SHIFT) // battery: stack count
                : (ushort)(idx << ITEM_SHIFT);                                            // other items: type index

            WriteItemBits(pixelsA, item.GlobalPos, mapOrigin, bits);
            WriteItemBits(pixelsB, item.GlobalPos, mapOrigin, bits);
        }

        textureA.SetPixelData(pixelsA, 0);
        textureA.Apply(false);
        // Both surfaces are identically-sized R16 textures containing packed integers.
        // Graphics.Blit goes through a colour-sampling shader; on Metal that conversion can
        // saturate every non-zero ushort to 65535, destroying all semantic bits.  CopyTexture
        // is a raw GPU copy and therefore preserves the exact packed value consumed by
        // DynamicRTSensor.
        Graphics.CopyTexture(textureA, RenderTextureTeamA);

        textureB.SetPixelData(pixelsB, 0);
        textureB.Apply(false);
        Graphics.CopyTexture(textureB, RenderTextureTeamB);
    }

    // ===== Private helpers =====

    private void OnEpisodeStarted(MatchManager mm, GameScenario scenario)
    {
        // Resize if map dimensions changed (e.g. different map configs)
        int newW = gameScenario.MapManager.MapWidth * resolutionScale;
        int newH = gameScenario.MapManager.MapHeight * resolutionScale;
        if (newW != texWidth || newH != texHeight)
        {
            texWidth = newW;
            texHeight = newH;
            AllocateAll();
        }

        RenderBackground();
        isInitialized = true;
        // Pre-render immediately so MapObsAgent's first RequestDecision in the next
        // FixedUpdate sees a valid RenderTexture, not an empty/stale one.
        Render();
    }

    private void AllocateAll()
    {
        RenderTextureTeamA?.Release();
        RenderTextureTeamB?.Release();

        // R16: single-channel, non-color format. No sRGB/gamma conversion is ever applied to it,
        // so the packed integer value survives the RT round-trip exactly.
        RenderTextureTeamA = new RenderTexture(texWidth, texHeight, 0, RenderTextureFormat.R16, RenderTextureReadWrite.Linear);
        RenderTextureTeamB = new RenderTexture(texWidth, texHeight, 0, RenderTextureFormat.R16, RenderTextureReadWrite.Linear);
        RenderTextureTeamA.filterMode = FilterMode.Point;
        RenderTextureTeamB.filterMode = FilterMode.Point;
        RenderTextureTeamA.Create();
        RenderTextureTeamB.Create();
        ClearRT(RenderTextureTeamA);
        ClearRT(RenderTextureTeamB);

        // Point filtering is required: bilinear filtering would blend adjacent tiles' packed
        // integer values into meaningless fractions.
        textureA = new Texture2D(texWidth, texHeight, TextureFormat.R16, false, true) { filterMode = FilterMode.Point };
        textureB = new Texture2D(texWidth, texHeight, TextureFormat.R16, false, true) { filterMode = FilterMode.Point };
        pixelsA = new ushort[texWidth * texHeight];
        pixelsB = new ushort[texWidth * texHeight];
        backgroundA = new ushort[texWidth * texHeight];
        backgroundB = new ushort[texWidth * texHeight];
    }

    private static void ClearRT(RenderTexture rt)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = prev;
    }

    /// <summary>
    /// Pre-renders the static base-category layer (bits 0-2) for both team perspectives.
    /// Called once per episode after the map is generated.
    /// </summary>
    private void RenderBackground()
    {
        MapManager mapManager = gameScenario.MapManager;
        MatchManager matchManager = gameScenario.MatchManager;
        TeamData teamA = matchManager.TeamA;
        Vector2 mapOrigin = mapManager.MapOriginWorld;
        Vector2Int spawnA = mapManager.MapSpaceInfo.TeamASpawnPoint;
        Vector2Int spawnB = mapManager.MapSpaceInfo.TeamBSpawnPoint;

        for (int py = 0; py < texHeight; py++)
        {
            for (int px = 0; px < texWidth; px++)
            {
                // Center of this pixel in world space
                Vector2 worldPos = mapOrigin + new Vector2(px + 0.5f, py + 0.5f) / resolutionScale;
                Vector2Int cell = mapManager.WorldToCell(worldPos);
                MapTile tile = mapManager.GetTile(cell);

                ushort idA, idB;
                if (tile == null || tile.TileData.TileCollisionOption == TileCollisionOption.BlockAll)
                {
                    idA = idB = ID_WALL;
                }
                else if (tile.OwnedRegion is Storage)
                {
                    bool isTeamAStorage = tile.OwnedRegion.OwnedTeam == teamA;
                    idA = isTeamAStorage ? ID_STORAGE_ALLY : ID_STORAGE_ENEMY;
                    idB = isTeamAStorage ? ID_STORAGE_ENEMY : ID_STORAGE_ALLY;
                }
                else if (tile.TileData == siteHunterTileData)
                {
                    idA = idB = ID_SITE_HUNTER;
                }
                else if (tile.TileData == siteCarrierTileData)
                {
                    idA = idB = ID_SITE_CARRIER;
                }
                else if (cell == spawnA)
                {
                    idA = ID_SPAWN_ALLY;
                    idB = ID_SPAWN_ENEMY;
                }
                else if (cell == spawnB)
                {
                    idA = ID_SPAWN_ENEMY;
                    idB = ID_SPAWN_ALLY;
                }
                else
                {
                    idA = idB = ID_VOID;
                }

                int idx = py * texWidth + px;
                backgroundA[idx] = idA;
                backgroundB[idx] = idB;
            }
        }
    }

    private void WriteItemBits(ushort[] pixels, Vector2 worldPos, Vector2 mapOrigin, ushort bits)
    {
        int px = Mathf.FloorToInt((worldPos.x - mapOrigin.x) * resolutionScale);
        int py = Mathf.FloorToInt((worldPos.y - mapOrigin.y) * resolutionScale);
        if (px < 0 || px >= texWidth || py < 0 || py >= texHeight) return;
        int idx = py * texWidth + px;
        pixels[idx] |= bits; // safe: base bits (0-2) never overlap item bits (3-9)
    }

    private int GetItemIndex(ItemData itemData)
    {
        if (knownItems == null) return -1;
        for (int i = 0; i < knownItems.Length; i++)
            if (knownItems[i] == itemData) return i;
        return -1;
    }

    private void OnDestroy()
    {
        if (gameScenario?.EventBus != null)
            gameScenario.EventBus.Flow.OnEpisodeStarted -= OnEpisodeStarted;
        RenderTextureTeamA?.Release();
        RenderTextureTeamB?.Release();
    }
}
