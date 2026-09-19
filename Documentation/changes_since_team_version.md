# 팀 공유 버전(d2220a7) 이후 변경 사항

## 📋 개요

팀이 마지막으로 공유한 버전은 `d2220a7`(2026-05-13, "Deploy v1.0" 머지)입니다.
이 문서는 그 이후 `run11-80k` 브랜치에 들어간 커밋 21개(`d5be5f5`..`5986025`)를 **팀원이 알아야 하는 순서**로 정리합니다.

가장 중요한 것은 **관측(obs) 프로토콜이 완전히 바뀌었다**는 점입니다. `d2220a7` 기준 Python 코드와 체크포인트는
현재 빌드와 호환되지 않습니다. Python 쪽은 `blackout-env` 저장소(`blackout_env/env/my_obs_preprocessor.py`,
`blackout_env/env/blackout_env.py`, `docs/internals.md`)가 이 프로토콜의 짝입니다.

| 영역 | 핵심 커밋 |
|---|---|
| 관측 프로토콜 | `d5be5f5`, `ef1087e`, `20215fe` |
| 결정 주기 25 Hz | `859e4b1`(의도), `d9140fe`(실제 구현) |
| 에피소드·시간 버그 | `d9140fe`, `4685112`, `20215fe`, `438fd1f`, `42ec2ed`(이동) |
| 보상 | `d3b4f56`, `42ec2ed`, `3b636d7`, `30293cc`, `fba77db`, `b4b5c07`, `da0db32`, `94fabbd` |
| 빌드·도구 | `bb2dcbc`, `b9669b2`, `5986025`, `d9140fe`(TimeScaleVerification) |
| 멀티 아레나(폐기) | `e7ed3ab`, `8127508` |

---

## 📡 관측(obs) 프로토콜

### 한눈에 보기

| 항목 | d2220a7 | 현재 |
|---|---|---|
| `BlackOutUnit` × 10 벡터 | float32[**45**] (유닛마다 전체 상태 사본) | float32[**1**] = 자기 `unitIndex`만 |
| `BlackOutMap` × 1 벡터 | 없음 (`VectorObservationSize: 0`) | float32[**44**] 공유 상태 (한 스텝에 1번) |
| `BlackOutMap` 그래픽 | 1×**96×96**, 8비트 grayscale semantic ID (`id/255`) | 1×**24×24**, R16 **비트 패킹** (`packed/1024`) |
| 그래픽에 유닛 | 있음 (ID 4/5) | **없음** — 유닛 위치는 벡터로만 |
| 유닛 결정 주기 | 매 FixedUpdate (50 Hz) | **2틱마다 (25 Hz)**, 사이 틱은 직전 행동 반복 |
| Python 짝 | `ObsPreprocessor` (레거시) | `MyObsPreprocessor` |

벡터 obs를 유닛 10개가 각자 보내던 것을 `MapObsAgent`가 한 번만 보내도록 옮긴 것이 핵심입니다(`d5be5f5`).
유닛 에이전트는 이제 행동·보상 라우팅용 `unitIndex` 하나만 보냅니다.

### BlackOutUnit 벡터 — 이전 (45 floats, 관찰자 기준)

| 슬롯 | 내용 |
|---|---|
| `4i+0`, `4i+1` (i=0..9) | 유닛 i 위치 `(GlobalPos - mapOrigin) / mapBounds` → **[0, 1]** |
| `4i+2` | teamSign: 관찰자와 같은 팀 `+1`, 아니면 `-1` |
| `4i+3` | holdingItemId: `0`=없음, `KnownItems 인덱스 + 1` (배터리 = 1) |
| 40 | **자신의** classId |
| 41, 42 | 아군 점수 / 적군 점수 (`÷ TargetScore`, 관찰자 기준) |
| 43 | 에피소드 남은 시간 `1 - EpisodeTimer.Ratio` |
| 44 | unitIndex (라우팅 전용) |

### BlackOutUnit 벡터 — 현재 (1 float)

| 슬롯 | 내용 |
|---|---|
| 0 | `unitIndex` (0~4 팀 A, 5~9 팀 B) |

Python: `BlackOutEnv._extract_step()`이 `round(obs[0][0])`로 에이전트 이름을 정합니다. 학습 입력에는 들어가지 않습니다.

### BlackOutMap 벡터 — 현재 (44 floats, 팀 중립)

`MapObsAgent.CollectObservations()` (`MapObsAgent.cs`). 블록 i는 항상 `matchManager.Units[i]`이고, i < 5 가 팀 A 입니다.

| 슬롯 | 내용 | 인코딩 |
|---|---|---|
| `4i+0`, `4i+1` | 유닛 i 위치 x, y | `(GlobalPos - mapOrigin) / mapBounds * 2 - 1` → **[-1, 1]** (`ef1087e`) |
| `4i+2` | holdingItemId | `0`=없음, **`-n`=배터리 n개**, `k`(≥2)=비배터리 아이템 (`KnownItems 인덱스 + 1`) |
| `4i+3` | classId | `KnownClasses` 인덱스 (현재 씬: 0=Collector, 1=Hunter, 2=Carrier), 못 찾으면 -1 |
| 40 | score_A | 팀 A 점수 `÷ TargetScore` (절대값, 관찰자 없음) |
| 41 | score_B | 팀 B 점수 `÷ TargetScore` |
| 42 | episode_time_left | `1 - EpisodeTimer.Ratio` |
| 43 | absorption_time_left | `1 - AbsorptionTimer.Ratio` (**신규**) |

이전과 달라진 점:
- teamSign 슬롯이 없어졌습니다. 팀은 블록 인덱스로 정해집니다(0~4 = A).
- "자신의 classId"가 없어지고 10명 모두의 classId가 들어갑니다.
- 점수는 "아군/적군"이 아니라 "A/B" 절대값입니다. 팀별 순서 뒤집기는 Python이 합니다.
- 배터리를 들고 있으면 개수를 음수로 보냅니다.

Python 짝: `MyObsPreprocessor.preprocess_agent_states()` (→ 팀 A/B 시점 `float32[10, 12]` 두 개, team 열은
블록 인덱스로 복원), `MyObsPreprocessor.preprocess_team_states()` (→ `[내 점수, 상대 점수, 남은 시간, 흡수까지 남은 시간]`),
`RAW_VECTOR_SIZE = 44`. 입력 크기는 `assert`로 검사하므로 슬롯 수가 1개라도 다르면 바로 실패합니다.

### BlackOutMap 그래픽 — 이전 (grayscale semantic ID)

`resolutionScale = 4` → 24×24 맵에서 96×96. 픽셀마다 ID 하나(나중에 쓴 것이 이김: 배경 → 아이템 → 유닛).

| ID | 의미 |
|---|---|
| 0 | empty |
| 1 | wall |
| 2 / 3 | ally_storage / enemy_storage |
| 4 / 5 | ally_unit / enemy_unit |
| 6 + i | 아이템 i (`item_id_offset + KnownItems 인덱스`) |

전송: RGB24 Texture2D → `Graphics.Blit` → sRGB ARGB32 RenderTexture → `ReadPixels` → R 채널 `/255`.

### BlackOutMap 그래픽 — 현재 (R16 비트 패킹)

`resolutionScale = 1` → 24×24, 타일당 1픽셀. 픽셀마다 16비트 값 하나에 필드 세 개를 담습니다 (`SemanticMapRenderer.cs`).

| 비트 | 필드 | 값 |
|---|---|---|
| 0–2 | 기본 타일 분류 (에피소드마다 1회 계산) | 0=void 1=wall 2=site_hunter 3=site_carrier 4=spawn_ally 5=spawn_enemy 6=storage_ally 7=storage_enemy |
| 3–6 | 이 타일의 배터리 스택 수 | 0~15로 포화 (현재 `MaxItemAmount` = 10) |
| 7–9 | 비배터리 아이템 인덱스 | 0=없음 1=BuffSpeed 2=DebuffSpeed 3=BuffSize 4=DebuffSize, 5~7 예약 |
| 10–15 | 미사용 | 0 |

- 값 범위 0~1023. ML-Agents에는 `packed / PACK_DIVISOR(1024)`로 정규화한 float로 나갑니다.
- ally/enemy는 **팀 A 시점**으로 고정입니다. 팀 B 시점은 Python이 spawn·storage 채널을 맞바꿔 만듭니다.
- 한 타일에 아이템이 최대 1개라는 가정 위에서 배경과 아이템 비트를 `|` 로 합칩니다.
- 새로 인식하는 타일: `siteHunterTileData`(Site_Hunter), `siteCarrierTileData`(Site_Carrier), 양 팀 스폰 지점
  (`MapSpaceInfo.TeamASpawnPoint/TeamBSpawnPoint`).

전송 경로 (`20215fe`에서 헤드리스 대응으로 바뀜):

```
SemanticMapRenderer.Render()
├── backgroundA/B (ushort[]) 복사 → 지상 아이템 비트 OR
├── Texture2D(R16).SetPixelData → Graphics.CopyTexture → RenderTexture(R16)   ← 화면 표시·디버그용
└── TeamAPixels (CPU ushort[])  ← ML 센서는 이것을 직접 읽음

DynamicRTSensor.Write()
└── writer[0, y, x] = TeamAPixels[(H-1-y)*W + x] / 1024f     (y 뒤집기: 텍스처는 아래→위)
```

GPU 되읽기를 없앤 이유: 헤드리스(`-nographics`/NullGfx)에서 R16 `ReadPixels`가 상수 값을 돌려주고, Metal에서는
`Graphics.Blit`이 0이 아닌 값을 모두 65535로 포화시켜 비트가 깨졌습니다.

Python 짝: `MyObsPreprocessor.preprocess_team_graphics()` → `preprocess_graphic()`
(`round(x * 1024)` 후 mask/shift로 풀어서 13채널: ch0–7 기본 분류 one-hot, ch8 배터리 수/15, ch9–12 아이템 1~4 one-hot),
`flip_team_perspective()` (팀 B).
**`PACK_DIVISOR`, 비트 위치, 기본 분류 번호는 C#과 Python 양쪽 상수를 함께 바꿔야 합니다.**

### 결정 주기 25 Hz (`859e4b1`, `d9140fe`)

- `Fixed Timestep` = 0.02 s (50 Hz)는 그대로입니다.
- `BlackOutAgent.decisionPeriod = 2`: 2틱마다 `RequestDecision()`, 사이 틱은 `RequestAction()`으로 직전 행동을 반복합니다.
- `Unit.prefab`에서 `DecisionRequester` 컴포넌트를 **제거**했습니다. `859e4b1`은 그 컴포넌트의 `DecisionPeriod`를 2로
  바꿨지만, `d9140fe`에서 이 로직을 `BlackOutAgent` 안으로 옮겼습니다(아래 버그 수정 참고).
- `MapObsAgent`는 여전히 매 틱 `RequestDecision()` 합니다. 유닛 결정이 없는 틱에는 Python의
  `BlackOutEnv._advance_until_ready()`가 행동 없이 `unity_env.step()`만 반복해서, `env.step()` 한 번 = 게임 시간 0.04 s가 됩니다.

### 실행 인자 · SideChannel

| 항목 | 내용 | Python 짝 |
|---|---|---|
| `-noRewardShaping` (`94fabbd`) | Unity의 potential/nav 쉐이핑 η를 0으로 만들어 계산 자체를 건너뜀. obs는 그대로 | `BlackOutEnv(unity_shaping=False)`가 자동으로 붙임 (수집 스크립트의 `--no-unity-shaping`) |
| SeedChannel | UUID `7a8b9c0d-1e2f-3a4b-5c6d-7e8f9a0b1c2d`, int32 → `Random.InitState`. 프로토콜 변경 없음. `e7ed3ab`에서 `TryRegister/TryUnregister`로 감쌈(단일 아레나에선 동작 동일) | `SeedChannel.send_seed()`, `env.reset(seed=...)` |

### ⚠️ `semantic_map_config.json`은 더 이상 기준이 아닙니다

`Assets/StreamingAssets/semantic_map_config.json`(과 `blackout_env/semantic_map_config.json` 사본)에는 아직
`resolution_scale: 4`, `ids`, `item_id_offset: 6` 같은 **옛 인코딩 값이 남아 있습니다**. Unity C# 코드는 이 파일을 읽지 않고,
현재 Python은 `n_items`(5)와 `n_classes`(3)만 씁니다. 인코딩의 기준은 `SemanticMapRenderer.cs`와 `my_obs_preprocessor.py`입니다.

---

## 🔄 에피소드·시간 처리 버그 수정

| 커밋 | 무엇이 문제였나 | 무엇을 바꿨나 |
|---|---|---|
| `d9140fe` | `Time.maximumDeltaTime = fixedDeltaTime`(프레임당 1틱)로 묶여 있어 `time_scale`을 올려도 시뮬레이션이 약 1배속에 머묾. 이 제한은 "종료 틱과 새 에피소드 첫 결정이 같은 Academy 스텝에 겹쳐 종료 신호가 덮어써지는" 경쟁을 가리고 있었음 | `maximumDeltaTime = fixedDeltaTime * 50`. 경쟁은 `BlackOutAgent`에서 직접 막음: `OnEpisodeBegin`에서 `suppressDecisionThisTick = true` → 그 다음 FixedUpdate 한 번은 `RequestDecision()`을 건너뜀. `DecisionRequester`는 `Academy.AgentPreStep` 경로로 결정을 다시 요청해 이 가드를 우회하므로 제거하고 결정 주기를 `BlackOutAgent`로 옮김. 커밋 메시지 기준 time_scale=100에서 약 99배 |
| `4685112` | `TimerManager.Tick()`이 인덱스로 순회하는 도중 타이머 콜백이 에피소드를 끝내고 목록을 `Clear()+AddTimer()`로 재구성하면, 새 에피소드의 타이머를 잘못 제거함. **한 번 시간 초과로 끝난 뒤로는 모든 에피소드가 점수로만 끝남** | 스냅샷(`timers.ToArray()`)을 순회하고 완료된 타이머를 참조로 `Remove` |
| `20215fe` | 승리 점수에 도달하는 입금이 같은 호출 안에서 에피소드를 끝내고 유닛을 리셋 → `Storage.OnUnitEnter`에서 `RetrieveItem()`이 null | `if (item == null) return;` |
| `438fd1f` | 같은 재진입으로 `Storage.ExecutePlan`이 미리 계산한 계획의 나머지를 파괴된 아이템에 적용 | `incomingItem`/`TargetTile`/`existingItem` null이면 중단 |
| `20215fe` | 헤드리스 빌드에서 그래픽 obs가 상수/65535로 깨짐 | 위 "전송 경로" 참고 (CPU 배열 직접 읽기, `CopyTexture`) |
| `ef1087e` | `Unit.prefab`의 `VectorObservationSize`가 45로 남아 1 float만 보내는데 0으로 패딩됨. `BlackOutMap`은 0이라 첫 스텝에 NRE | 각각 1과 44로 수정 |
| `42ec2ed` | (게임 로직) 대각선 이동을 벽 여러 개에 한 번에 클램프하다 오목한 모서리에 영구히 끼는 고정점 발생 | `UnitMovementSystem.ClampPosition`을 X축→Y축 순서 분리 클램프로 변경(벽 따라 미끄러짐) |

**종료/리셋 순서 (현재).** `OnGameEnded` → 에이전트마다 `AddReward(±1/0)` + `EndEpisode()`.
ML-Agents의 `EndEpisode()`는 종료 AgentInfo를 즉시 브레인에 넣고, 이어서 `OnEpisodeBegin()`을 **같은 호출 안에서** 부릅니다.
10번째 `OnEpisodeBegin()`이 `BeginEpisode()`(맵 재생성 포함)를 실행하므로, 종료 스텝과 함께 도착하는 `BlackOutMap` 관측은
이미 새 에피소드 상태일 수 있습니다. Python은 종료 스텝의 점수에 리셋 직후 값이 섞이면 직전 점수로 되돌려 승자를 정합니다(`blackout-env` `docs/internals.md`).

---

## 🏆 보상

고정 이벤트 보상(킬/데스/득점/아이템)은 기본값 0으로 바뀌고, 그 자리를 potential 기반 쉐이핑(팀 Ψ, 유닛별 Φ)이 대신합니다
(`d3b4f56`, `42ec2ed`, `3b636d7`, `30293cc`, `fba77db`, `b4b5c07`, `da0db32`). 종료 보상 ±1은 그대로입니다.
Run 11 학습은 Unity가 보낸 보상 대신 Python의 `train/reward_v2.py`로 다시 계산합니다(`blackout-env` `docs/internals.md`).
Unity 쉐이핑 계산이 필요 없으면 `-noRewardShaping`(Python `unity_shaping=False`)으로 끌 수 있습니다.
자세한 내용은 [reward_shaping.md](./reward_shaping.md) 참고.

---

## 🔧 빌드·도구

- **Unity 6000.3.8f1 → 6000.4.11f1** (`bb2dcbc`). 패키지 버전도 함께 올라갔습니다(ML-Agents는 4.0.2 그대로).
- **`Assets/Editor/CIBuild.cs`** (`b9669b2`, `5986025`): `CIBuild.BuildBlackOutMac`이 EditorBuildSettings의 활성 씬(`Prototype.unity`)으로
  macOS 플레이어를 빌드합니다. 출력 경로는 `-buildPath <.app 경로>`, 없으면 `../blackout-env/build/mac/BlackOut.app`
  (두 저장소를 같은 부모 폴더에 클론한 경우). 실패 시 종료 코드 1.
- **배치 빌드 명령** — `blackout-env` 루트에서 `models/run11_step80k/run11_pipeline.sh build` 단계가 실행하는 것과 같습니다
  (이 스크립트는 Unity 프로젝트가 `../blackout`에 있다고 가정):

  ```bash
  /Applications/Unity/Hub/Editor/6000.4.11f1/Unity.app/Contents/MacOS/Unity -batchmode -quit \
    -projectPath ../blackout -executeMethod CIBuild.BuildBlackOutMac \
    -buildPath "$PWD/build/mac/BlackOut.app" -logFile "$PWD/build/mac_build.log"
  ```

- `Assets/Editor/TimeScaleVerification.cs` (`d9140fe`): time_scale 수정을 검증한 일회성 배치모드 하네스. 검증이 끝나
  이 브랜치에서 삭제했습니다(필요하면 `git show d9140fe`).
- **`RewardDebugUI` / `RewardEventLog`** (`d5be5f5`, `6b6ae85`): 에디터·개발 빌드에서만 붙는 보상 오버레이(F9).

---

## 🧪 멀티 아레나 실험

한 프로세스에 아레나 여러 개를 띄워 gRPC 왕복을 줄이려는 실험(`e7ed3ab` 씬·`ArenaDuplicator`, `8127508` `arenaIndex` 필드)은 실제 전체 처리량 이득이 약 1.07배에 그쳐 보류했고(커밋 메시지의 ~4배는 결정 수 기준이라 과대), 이 브랜치에서 씬·`ArenaDuplicator`·`arenaIndex` 필드를 삭제했습니다. 단일 씬의 관측 형식은 그대로입니다(`arenaIndex` 기본값 −1은 아무것도 덧붙이지 않았음). `SeedChannel`의 공유 등록 로직은 단일 씬에서도 동작이 같아 남겼습니다.
