# ML-Agents 브릿지 설계 (Black Out)

## 📋 개요

이 문서는 Unity ML-Agents와 Python(`blackout-env` 저장소의 `blackout_env` 패키지) 사이의 통신 구조, obs/action 처리 방식, 설정을 설명합니다.
팀 공유 버전(`d2220a7`)에서 무엇이 바뀌었는지는 [changes_since_team_version.md](./changes_since_team_version.md)를 먼저 보세요.

Python 쪽 기준 문서: `blackout-env/blackout_env/env/my_obs_preprocessor.py` 모듈 docstring, `blackout-env/docs/internals.md`, `blackout-env/docs/api.md`.

---

## 🏗️ 아키텍처

10개 유닛 에이전트 + 1개 맵·상태 전용 에이전트 구조입니다.

```
BlackOutEpisodeCoordinator (MonoBehaviour)
├── SemanticMapRenderer          ← 매 FixedUpdate 비트 패킹 맵 갱신 (CPU ushort[] + R16 RT)
├── GameScenario (Facade)
├── BlackOutAgent × 10           ← BehaviorName = "BlackOutUnit", obs = unitIndex 1개
│   ├── Team A: unitIndex 0~4, TeamId=0
│   └── Team B: unitIndex 5~9, TeamId=1
└── MapObsAgent × 1              ← BehaviorName = "BlackOutMap"
    ├── VectorSensor (44)        ← 10유닛 공유 상태 + 게임 스칼라
    └── DynamicRTSensorComponent ← 팀 A 시점 비트 패킹 맵 (1×24×24)
```

**핵심 설계 — 공유 정보는 MapObsAgent가 한 번만 보낸다**

맵과 10유닛 상태는 모든 유닛에게 (팀 시점만 다를 뿐) 같은 정보입니다. 유닛 10개가 각자 보내는 대신
`MapObsAgent`가 스텝마다 **팀 중립 사본 하나**를 보내고, Python이 팀 A/B 시점을 만듭니다.
유닛 에이전트는 행동·보상·종료 라우팅에 필요한 `unitIndex`만 보냅니다.

---

## ⚙️ Behavior Parameters

### BlackOutUnit (10개 에이전트, `Unit.prefab`)

| 항목 | 값 |
|---|---|
| Behavior Name | `BlackOutUnit` |
| Vector Observation Size | `1` (unitIndex) |
| Stacked Vectors | 1 |
| Continuous Actions | 2 |
| Discrete Actions | 없음 |
| Visual Obs | 없음 (MapObsAgent가 담당) |
| TeamId (Team A / B) | 0 / 1 |
| DecisionRequester | **없음** — `BlackOutAgent.decisionPeriod`(기본 2)가 대신함 |

### BlackOutMap (1개 에이전트, `Prototype.unity`)

| 항목 | 값 |
|---|---|
| Behavior Name | `BlackOutMap` |
| Vector Observation Size | `44` |
| Continuous / Discrete Actions | 0 / 없음 |
| Visual Obs | `DynamicRTSensor` 1개 ("TeamAMap", `ObservationSpec.Visual(1, 24, 24)`) |

`VectorObservationSize`는 `CollectObservations()`가 넣는 float 수와 반드시 같아야 합니다. 0이면 ML-Agents가 VectorSensor를
만들지 않아 첫 스텝에 NRE가 나고, 크면 0으로 패딩됩니다(`ef1087e`에서 둘 다 겪음).

---

## 📡 관찰 벡터

### BlackOutUnit (1 float)

| 인덱스 | 내용 |
|---|---|
| 0 | `unitIndex` (0~9). Python `BlackOutEnv._extract_step()`이 에이전트 이름으로 변환. 학습 입력 아님 |

### BlackOutMap (44 floats, 팀 중립)

`MapObsAgent.CollectObservations()`. 블록 i = `matchManager.Units[i]`, i < 5 는 팀 A.

| 인덱스 | 내용 | 인코딩 |
|---|---|---|
| `4i+0`, `4i+1` | 유닛 i 위치 x, y | `(GlobalPos - MapOriginWorld) / MapBounds * 2 - 1` → [-1, 1] |
| `4i+2` | holdingItemId | `0`=없음, `-n`=배터리 n개, `k`(≥2)=비배터리 아이템 (`KnownItems` 인덱스 + 1) |
| `4i+3` | classId | `KnownClasses` 인덱스 (0=Collector, 1=Hunter, 2=Carrier), 없으면 -1 |
| 40 | score_A | `TeamA 점수 / TargetScore` |
| 41 | score_B | `TeamB 점수 / TargetScore` |
| 42 | episode_time_left | `1 - EpisodeTimer.Ratio` |
| 43 | absorption_time_left | `1 - AbsorptionTimer.Ratio` |

- teamSign 슬롯은 없습니다. 팀은 블록 인덱스로 결정됩니다.
- `MapBounds`는 코디네이터 인스펙터 값(현재 씬 24×24), `MapOriginWorld`는 `MapManager`에서 옵니다.
- `KnownItems`/`KnownClasses` 순서는 코디네이터 인스펙터 배열 순서입니다(현재: Battery, BuffSpeed, DebuffSpeed, BuffSize, DebuffSize).

### 전처리 후 (Python, `MyObsPreprocessor`)

| 함수 | 출력 | 크기 |
|---|---|---|
| `preprocess_agent_states(raw)` | 팀 A/B 시점 유닛 표. 행 = `[pos_x, pos_y, team(±1), item one-hot(n_items+1), class one-hot(n_classes)]`. 배터리 칸은 1.0 대신 `개수/15` | `float32[10, 12]` × 2 |
| `preprocess_team_states(raw)` | `[내 점수, 상대 점수, episode_time_left, absorption_time_left]` | `float32[4]` × 2 |

---

## 🗺️ Visual Obs — 비트 패킹 맵

### 픽셀 인코딩 (`SemanticMapRenderer`)

해상도 = 맵 타일 수 × `resolutionScale`(현재 **1**) → 24×24. 픽셀 = 16비트 값 하나.

| 비트 | 필드 | 값 |
|---|---|---|
| 0–2 | 기본 타일 분류 | 0=void 1=wall 2=site_hunter 3=site_carrier 4=spawn_ally 5=spawn_enemy 6=storage_ally 7=storage_enemy |
| 3–6 | 배터리 스택 수 | 0~15 포화 |
| 7–9 | 비배터리 아이템 인덱스 | 0=없음 1=BuffSpeed 2=DebuffSpeed 3=BuffSize 4=DebuffSize (5~7 예약) |

- ally/enemy는 팀 A 시점 고정. 기본 분류 우선순위: 벽(또는 타일 없음) → 창고 → 사냥터 → 운반 사이트 → 스폰 지점 → void.
- 기본 분류는 에피소드 시작 때 한 번(`RenderBackground`), 아이템 비트는 매 틱 OR로 덧씌웁니다.
- **유닛은 그리지 않습니다.**

### Unity → Python 파이프라인

```
SemanticMapRenderer.Render()   (BlackOutEpisodeCoordinator.FixedUpdate)
├── background 복사 → 지상 아이템 비트 OR   (ushort[] pixelsA/B)
├── Texture2D(R16).SetPixelData → Graphics.CopyTexture → RenderTexture(R16)   (표시용)
└── TeamAPixels = pixelsA

DynamicRTSensor.Write()   (MapObsAgent)
└── writer[0, y, x] = TeamAPixels[(H-1-y)*W + x] / PACK_DIVISOR(1024)

Python BlackOutEnv._collect_map_obs()
├── obs_list에서 ndim==3 → visual, ndim==1 → 44벡터 (순서 가정 없음)
├── (C,H,W) → (H,W,C) transpose
└── MyObsPreprocessor.preprocess_team_graphics()
      preprocess_graphic(): round(x*1024) → mask/shift → 13채널
      flip_team_perspective(): 팀 B = spawn/storage ally↔enemy 교환
```

센서가 RenderTexture를 되읽지 않고 CPU 배열을 직접 읽는 이유: 헤드리스(NullGfx)에서 R16 `ReadPixels`가 상수를 돌려주고,
Metal에서 `Graphics.Blit`이 값을 65535로 포화시켰기 때문입니다(`20215fe`).

### 전처리 후 graphic 크기

`float32[24 × 24 × 13]`: ch0–7 기본 분류 one-hot, ch8 배터리 수/15, ch9–12 아이템 1~4 one-hot.

---

## 🌐 gRPC 통신

### 흐름

```
[Unity]                                  [Python]
  │  UnityOutput (obs + reward + done)       │
  │ ───────────────────────────────────────> │  env.step() 반환
  │  UnityInput (actions)                    │
  │ <─────────────────────────────────────── │
  │  OnActionReceived() 실행                 │
  └──────────────────── (반복) ──────────────┘
```

단계당 전체 에이전트 obs가 **하나의 protobuf 메시지**로 묶여 전송됩니다.

### 결정 주기

- `Fixed Timestep` 0.02 s (50 Hz). `MapObsAgent`는 매 틱 `RequestDecision()`.
- `BlackOutAgent`는 `decisionPeriod`(2)틱마다 `RequestDecision()`, 사이 틱은 `RequestAction()`(직전 행동 반복) → **25 Hz**.
- 유닛 결정이 없는 틱은 Python `_advance_until_ready()`가 흡수합니다. `env.step()` 1회 = 게임 시간 0.04 s.

### 패킷 용량 (step당, 비압축 float32)

| 항목 | 크기 |
|---|---|
| BlackOutUnit × 10 × 1 float | 40 B |
| BlackOutMap 벡터 44 float | 176 B |
| BlackOutMap 그래픽 1×24×24 | 2,304 B |
| **합계** | **≈ 2.5 KB** |

---

## 🎮 액션 공간

| 인덱스 | 내용 | 범위 |
|---|---|---|
| `ContinuousActions[0]` | X축 이동 | -1 ~ 1 |
| `ContinuousActions[1]` | Y축 이동 | -1 ~ 1 |

`BlackOutAgent.OnActionReceived()` → `GameScenario.MoveUnit(unitIndex, (x, y), Time.fixedDeltaTime)`. `GameState.Playing`이 아니면 무시.
`MapObsAgent`는 빈 행동을 받습니다. (Run 11 모델은 8방향 이산 행동을 골라 단위 벡터로 바꿔 보냅니다 — Python 쪽 일.)

---

## 🏆 보상 설계

- 종료 보상: 승리 +1, 패배 -1, 무승부 0 (`OnGameEnded`). 시간 초과도 점수로 승패를 가리므로 모든 종료는 `EndEpisode()`(truncation 없음).
- 고정 이벤트 보상(킬/데스/득점/아이템)은 기본값 0. 중간 보상은 potential 기반 쉐이핑(팀 Ψ, 유닛별 Φ)이 담당.
- 실행 인자 `-noRewardShaping`이면 쉐이핑 η=0, 계산도 건너뜀.

자세한 내용은 [reward_shaping.md](./reward_shaping.md) 참고.

---

## 🔄 에피소드 생명주기

```
Awake:  Time.maximumDeltaTime = fixedDeltaTime × 50      (캐치업 폭주만 막음, timeScale은 제한하지 않음)
        GameScenario.Initialize()
        SemanticMapRenderer.CreateTextures()
        MapObsAgent.Setup(renderer, coordinator, scenario)
        SeedChannel.TryRegister()
        RewardConfig.Load() (+ -noRewardShaping), 쉐이핑 계산기 생성
        agent.Setup() × 10, OnGameEnded 구독

Start:  BeginEpisode()  = GameScenario.EpisodeBegin() + 쉐이핑 상태 초기화

[Python reset(seed=N)]
SeedChannel.OnMessageReceived(N) → Random.InitState(N)   ← 다음 EpisodeBegin 전에 적용

[매 FixedUpdate]
Coordinator: EpisodeUpdate() → SemanticMapRenderer.Render() → 쉐이핑 보상(η≠0일 때)
MapObsAgent: RequestDecision()
BlackOutAgent: suppressDecisionThisTick이면 건너뜀, 아니면 decisionPeriod에 따라 RequestDecision / RequestAction

[에피소드 종료 — 목표 점수 또는 시간 초과]
OnGameEnded → 에이전트마다 AddReward(±1/0) + EndEpisode()
  EndEpisode() 안에서 (ML-Agents, 동기):
    종료 AgentInfo를 브레인에 즉시 넣음
    → OnEpisodeBegin(): suppressDecisionThisTick = true, NotifyAgentEpisodeBegin()
  10번째 에이전트 → episodeBeginCount == 10 → BeginEpisode() (맵 재생성)
  다음 FixedUpdate: 유닛은 결정 요청을 한 번 건너뜀 (종료 AgentInfo가 덮어써지지 않게)
```

주의: 새 에피소드가 종료 처리와 같은 호출 안에서 시작되므로, 종료 스텝과 함께 도착하는 `BlackOutMap` 관측(점수·시간)은
이미 리셋된 값일 수 있습니다. Python은 이를 감지해 직전 값으로 승자를 정합니다.

---

## 📬 SideChannel

### SeedChannel

| 항목 | 값 |
|---|---|
| Channel UUID | `7a8b9c0d-1e2f-3a4b-5c6d-7e8f9a0b1c2d` |
| 방향 | Python → Unity (단방향) |
| 페이로드 | `int32` seed |
| Unity 처리 | `UnityEngine.Random.InitState(seed)` |
| Python | `blackout_env/env/seed_channel.py` `SeedChannel.send_seed()`, `env.reset(seed=N)` |

- SideChannel 메시지는 Unity의 reset 처리 전에 소비됨 → 다음 에피소드 맵 생성 시점에는 시드 적용 완료.
- `seed` 미전달 시 메시지 없음 → Unity Random 상태 유지.
- `TryRegister/TryUnregister`: 같은 UUID를 두 번 등록하면 예외라서 첫 코디네이터만 등록합니다(단일 씬에서는 동작 동일).

### EngineConfigurationChannel

ML-Agents 기본 채널. Python `BlackOutEnv(time_scale=...)`가 `Time.timeScale`을 설정합니다.

---

## 🐍 Python API

Python 패키지는 이제 별도 저장소 **`blackout-env`**(`blackout_env` 패키지)입니다. 옛 `libblackout/blackout` 구조는 쓰지 않습니다.

```
Unity gRPC (step마다 1 protobuf 패킷)
    ├── BlackOutUnit × 10     raw: float32[1]  (unitIndex)
    └── BlackOutMap × 1       visual: float32[1, 24, 24] + vector: float32[44]
    ▼
BlackOutEnv._collect_obs()
    ├── _collect_map_obs()
    │     preprocess_team_graphics() → graphic 팀 A/B   float32[24, 24, 13]
    │     preprocess_agent_states()  → agent_states A/B float32[10, 12]
    │     preprocess_team_states()   → team_state A/B   float32[4]
    │     _latest_scalars (score_0/1, time_left, absorption_time_left) → infos
    └── BlackOutUnit × 10 (Decision/TerminalSteps)
          _extract_step(): unitIndex → 에이전트 이름
          _build_obs(): 팀별 캐시에서 {"graphic", "team_state", "agent_states"} 조립
```

자세한 API는 `blackout-env/docs/api.md`, 내부 흐름은 `blackout-env/docs/internals.md`를 보세요.

---

## ⚠️ Obs 수정 시 유의사항

### 공유 상태 벡터(44)에 값 추가

1. **Unity**: `MapObsAgent.CollectObservations()`에 추가하고 `RawStateSize` 상수 갱신 (`matchManager == null` 분기의 0 채우기도 같은 크기)
2. **Unity**: `Prototype.unity`의 BlackOutMap `Behavior Parameters → Vector Observation Size` 갱신
3. **Python**: `my_obs_preprocessor.py`의 `RAW_VECTOR_SIZE`/`SCALAR_COUNT`/`UNIT_BLOCK_SIZE`, `preprocess_*`, `agent_state_size`/`team_state_size`
4. **Python**: `blackout_env.py`의 `_latest_scalars`, observation_space

유닛 에이전트(`BlackOutUnit`)의 1-float obs에 값을 붙이지 마세요. Python은 `obs[0][0]`만 읽고, 모든 유닛이 같은 크기를 가져야 합니다.

### 그래픽 비트 레이아웃 변경

- `SemanticMapRenderer`의 `BATTERY_SHIFT/BATTERY_BITS/ITEM_SHIFT`, `ID_*`, `PACK_DIVISOR`와
  Python `MyObsPreprocessor`의 `BASE_MASK/BATTERY_SHIFT/BATTERY_MASK/ITEM_SHIFT/ITEM_MASK/PACK_DIVISOR`, 채널 상수는 **짝**입니다.
- 10비트를 넘기면 `PACK_DIVISOR`(1024)도 함께 키워야 합니다(값이 [0,1) 안에 있어야 함).
- ally/enemy 구분이 있는 새 분류는 Python `flip_team_perspective()`와 `team_frame.py`도 확인.
- 아이템 추가는 [adding_item_type.md](./adding_item_type.md).

### 체크포인트 호환성

`n_graphic_channels`, `agent_state_size`, `team_state_size`가 바뀌면 기존 체크포인트는 로드되지 않습니다.

### DynamicRTSensor 수정 시

- `ObservationSpec.Visual(channels, height, width)` 순서 (ML-Agents 4.0.2).
- `SetSource()`는 `CreateSensors()` 전후 어느 때 호출해도 됩니다(`_pendingSource` 패턴).
- 헤드리스 빌드에서 GPU 되읽기(`ReadPixels`)를 다시 도입하지 마세요.

---

## 🗂️ `semantic_map_config.json`

**경로:** `Assets/StreamingAssets/semantic_map_config.json` (Python 패키지에도 사본 `blackout_env/semantic_map_config.json`)

현재 이 파일에서 실제로 쓰이는 값은 **`n_items`(5)와 `n_classes`(3)뿐이며, Python만 읽습니다.** Unity C# 코드는 이 파일을 읽지 않습니다.
`resolution_scale`, `ids`, `item_id_offset`은 옛 grayscale 인코딩의 흔적이고 현재 인코딩과 맞지 않습니다(레거시 `ObsPreprocessor`만 사용).

| 키 | 사용처 | 변경 시 영향 |
|---|---|---|
| `n_items` | Python `MyObsPreprocessor`: item one-hot 크기, 그래픽 아이템 채널 수(`n_items - 1`, 최대 7) | agent_state/graphic 크기 변경 → 재훈련 |
| `n_classes` | Python: class one-hot 크기 | agent_state 크기 변경 → 재훈련 |
| `resolution_scale`, `ids`, `item_id_offset` | 현재 경로에서 사용 안 함 | — |

---

## 🔧 주요 컴포넌트

### BlackOutEpisodeCoordinator

**역할:** ML 훈련 씬 전용 진입점 (GameBootstrapper와 동시 사용 불가)

- `Awake()`: `GameScenario.Initialize()`를 가장 먼저 호출(TimerManager 등 초기화), 이후 렌더러·MapObsAgent·SeedChannel·보상 설정
- `NotifyAgentEpisodeBegin()`: 10개 에이전트가 모두 `OnEpisodeBegin`을 부르면 `BeginEpisode()`
- `OnGameEnded()`: 승패 보상 후 모든 에이전트 `EndEpisode()`
- `GetItemIndex()` / `GetClassIndex()`: obs 인코딩용 인덱스

### BlackOutAgent

**역할:** 유닛 1개당 에이전트 1개. obs는 `unitIndex` 1개, 행동은 이동 벡터 2개.

- `Setup()`: 레퍼런스 초기화, 이벤트 구독(람다를 필드로 저장 → `OnDestroy()`에서 해제)
- `OnEpisodeBegin()`: 결정 1틱 억제 플래그 설정, coordinator에 준비 완료 통보
- `FixedUpdate()`: `decisionPeriod` 주기로 `RequestDecision` / `RequestAction`

### MapObsAgent

**역할:** 팀 A 시점 맵 + 44-float 공유 상태 송신

- `Setup(SemanticMapRenderer, coordinator, scenario)`: 센서 소스와 상태 레퍼런스 연결
- `FixedUpdate()`: 매 틱 `RequestDecision()`
- `Awake()`: 남아 있는 `RenderTextureSensorComponent` 제거 후 `DynamicRTSensorComponent` 추가

### SemanticMapRenderer

- `CreateTextures()`: `Awake`에서 호출. R16 텍스처와 ushort 배열 생성
- `SubscribeEvents()`: `OnEpisodeStarted` 구독 → 맵 재생성 시 background 재계산
- `Render()`: 매 FixedUpdate. background 복사 → 아이템 비트 OR → RT 복사

### DynamicRTSensor (ISensor)

- `Write()`: `SemanticMapRenderer.TeamAPixels`를 y 뒤집어 `/1024f`로 기록. 배열 크기가 맞지 않으면 0 반환

---

## ✅ 씬 설정 체크리스트

- [ ] `BlackOutEpisodeCoordinator` GameObject 배치, `GameScenario` 연결
- [ ] `BlackOutAgent` × 10 배열 연결 (배열 인덱스 = unitIndex)
- [ ] `MapObsAgent` × 1 연결, Behavior Parameters Vector Observation Size = 44
- [ ] `SemanticMapRenderer`: `KnownItems`(배터리가 0번), `siteHunterTileData`, `siteCarrierTileData`, `resolutionScale = 1`
- [ ] 코디네이터 `KnownItems`는 렌더러와 같은 순서
- [ ] `MapBounds` = 맵 크기 (현재 24×24)
- [ ] `KnownClasses` 설정 (Collector, Hunter, Carrier 순서)
- [ ] 각 유닛 `Behavior Parameters` → Vector Observation Size = 1, TeamId 0 또는 1
- [ ] Unit에 `DecisionRequester`를 다시 붙이지 않기 (종료 신호 경쟁 가드를 우회함)
- [ ] `GameBootstrapper` 제거 (ML 훈련 씬에서)
