# Unity 쪽 보상 (reward shaping)

Unity가 ML-Agents로 에이전트(유닛)마다 넘기는 보상을 다룬다. 이 보상은 **legacy 트레이너들과 기본 `BlackOutEnv`**
(`unity_shaping=True`)가 쓴다. Run 11은 이 보상의 shaping 부분을 끄고 Python에서 계산하는 리워드 v2를 쓴다
(맨 아래 절). 원래 설계 문서는 blackout-env `docs/archive/reward_proposal.md`(§12, §14, §15, §16)다.

## 1. 한눈에 보기

Unity가 유닛 i(팀 k)에게 주는 보상은 물리 틱(`Fixed Timestep` 0.02 s)마다 이렇게 쌓인다.

```
r_i = z_k                                   # 종료 보상: 이기면 +1, 지면 -1, 무승부 0 (경기 끝나는 틱에만)
    + η     · [γ·Ψ_k(s') − Ψ_k(s)]           # 팀 포텐셜 shaping: 팀 5명에게 같은 값, 두 팀 합은 0
    + η_nav · [γ·Φ_i(s') − Φ_i(s)]           # 개인 네비 포텐셜 shaping: 유닛 i에게만
    + (고정 이벤트 보상: 전부 0으로 설정돼 있어 현재 기여 없음)
```

- 결정은 2틱마다(`BlackOutAgent.decisionPeriod` = 2, 25 Hz)다. ML-Agents가 두 틱치 보상을 합쳐서 한 결정에 넘긴다.
- η=0.25, η_nav=0.08, γ=0.99995(틱당). 420초 경기(21000틱)면 γ^T ≈ 0.35다.
- 두 shaping 항은 `GameState.Playing` 동안에만 준다(`BlackOutEpisodeCoordinator.ApplyPotentialShaping`/`ApplyNavShaping`의 조기 반환).
  경기가 끝나는 틱에는 ±1만 주고 `EndEpisode()`를 부른다(`BlackOutEpisodeCoordinator.OnGameEnded`).
- 새 에피소드가 시작되면 Ψ, Φ의 이전 값을 다시 계산한다. 그래서 이전 판의 포텐셜이 넘어오지 않는다(`BlackOutEpisodeCoordinator.BeginEpisode`).
- 경기 종료 조건은 `MatchManager.CheckWinCondition`(TargetScore 100 도달)과 `MatchManager.OnTimeExpired`(420초, 점수 비교)다.

## 2. 팀 포텐셜 Ψ — `PotentialRewardCalculator`

```
Ψ_A(s) = tanh( (Score_A − Score_B + Σ_b sign_b · amount_b · survive_b) / potentialScale ),   Ψ_B = −Ψ_A
survive_b = 1                                                  적 팀이 그 타일에 아예 못 가면 (보호 창고)
          = exp( −hazardCoefficient / max(d_enemy, 0.5) · τ )  그 외
```

- b는 **득점 배터리**(`ScoreItemEffect`가 있는 아이템, `IsScoringBattery`) 중 유닛이 들고 있거나 주인 있는 창고에 놓인 것이다.
  필드에 떨어진 배터리는 0으로 친다(`EnumerateActiveBatteries`). sign_b는 A팀이 가지고 있으면 +1, B팀이면 −1이다.
- `amount_b`는 `ItemObject.ItemAmount`(점수 단위, Battery는 최대 10)다.
- d_enemy는 배터리 칸에서 가장 가까운 적 유닛까지의 **그리드 경로 거리**다. 적 팀의 walkability로 걷고, 월드 단위로 잰다.
  적이 없거나 도달할 수 없으면 999다(`NearestEnemyDistance`). τ는 다음 흡수까지 남은 초다(`AbsorptionTimer.RemainingTime`, 주기 20초).
- "적이 갈 수 있나"는 에피소드당 한 번, 각 팀 스폰에서 BFS로 구한다(`BuildReachabilitySet`).
- 계산은 A팀 관점에서 한 번만 한다. 코디네이터가 A팀 5명에게 `η[γΨ'−Ψ]`를, B팀 5명에게 그 음수를 준다(`ApplyPotentialShaping`).

## 3. 개인 네비 포텐셜 Φ — `IndividualNavPotentialCalculator`

Saturate(d) = 1 − tanh(d / L)이고 L = `navPotentialScale`(=12)다. w(item) = amount / MaxItemAmount이고, 득점 배터리가 아니면 0이다(`BatteryWeight`).

| 유닛 상태 | Φ_i |
|---|---|
| Collectable(Collector·Carrier), 배터리 들고 있음 | w(held) · Saturate(자팀 창고 중 그 배터리를 받을 수 있는 가장 가까운 타일까지 거리) |
| Collectable, 배터리가 아닌 아이템 들고 있음 | 0 |
| Collectable, 빈손 | w(b) · Saturate(d(유닛→b) + d(b→받을 수 있는 창고)). 배정받은 배터리 b가 없으면 0 |
| Hunter(Collectable 아님, `Beats` 있음) | (base + (1−base)·w(표적의 적재)) · Saturate(d(유닛→표적)). base = `hunterPotentialBaseWeight`, 표적이 없으면 0 |

- **팀 단위 fetch 배정**(`AssignFetchTargets`): 빈손 유닛과 필드 배터리의 모든 (유닛, 배터리) 쌍을 가중 포텐셜 값으로 내림차순 정렬한다.
  그다음 탐욕적으로 짝짓는다. 배터리 하나에는 최대 한 명이다. 동점이면 유닛 index, 그다음 셀 x, y 순으로 정한다. 대상은 땅에 있고, 창고 밖이고, 그 팀이 상호작용할 수 있는 배터리다.
  fetch 거리에 "배터리→창고" 구간을 더해 두었다. 그래서 집는 순간 Φ가 변하지 않는다(같은 아이템, 같은 거리, 같은 w).
- **Hunter 추격**(`AssignHuntTargets`): 표적은 내가 이기고 상대는 나를 못 이기는 적 유닛이다.
  현재 에셋 기준 Hunter는 Collector와 Carrier를 노리고, 적 Hunter는 서로 이기는 관계라 제외된다. 배정도 같은 탐욕 방식이고, 적 하나에 Hunter 하나다.
- 목표가 없거나 도달할 수 없으면 거리를 8·L로 둔다(Saturate ≈ 0, `NoTargetScaleMultiplier`).
- 배달·킬이 일어나면 Φ가 떨어진다. telescoping이라 "접근→완료"의 shaping 합은 0이다. 실제 가치는 Ψ와 ±1에서 나온다.
- 보상은 그 유닛의 에이전트에게만 간다(`ApplyNavShaping`). 제로섬이 아니다.
- 성능: 팀별 walkability를 에피소드 단위로 캐시한다. 탐색 버퍼는 재사용하고, (팀, 아이템) 창고 거리장은 호출당 한 번만 만든다. 할당 없이 매 틱 돈다.

## 4. 그리드 경로 거리 — `GridPathfinder`

- 8방향 이동이다(직교 1, 대각 √2). 대각 이동은 양옆 직교 칸이 둘 다 걸을 수 있을 때만 허용한다(코너컷 금지).
  walkability는 팀마다 다르다(`MapManager.IsWalkable(cell, team)`).
- "조건을 만족하는 가장 가까운 칸" 질의라서 출발점에서 다익스트라를 한 번 돌리고 첫 타깃에서 멈춘다(`NearestMatching`). `maxVisited`=4096을 넘으면 null(도달 불가)이다.
- 지금은 Ψ의 hazard 거리에만 쓴다. Φ는 같은 규칙을 할당 없는 자체 구현(`IndividualNavPotentialCalculator.Flood`)으로 계산한다.
- 칸 거리 × 타일 월드 크기(`TileWorldSize`)로 월드 단위로 바꾼다.

## 5. `reward_config.json` 키 (Assets/StreamingAssets)

| 키 | 현재 값 | 클래스 기본값 (`RewardConfig.cs`) | 의미 / 사용처 |
|---|---|---|---|
| `itemRewards` | Battery 0.0, SpeedBuff 0.0 | 빈 배열, 없는 이름은 0 (`GetItemReward`) | 아이템별 고정 보상. 필드에서 줍기, 적재, 자팀 창고 흡수(팀 전원)에 +, 적이 자팀 창고에서 탈취하면 팀 전원에 − (`BlackOutAgent.Setup`) |
| `killReward` | 0.0 | 0 | 킬한 유닛에게만 + |
| `deathPenalty` | 0.0 | 0 | 죽은 유닛의 팀 **전원**에게 − |
| `teamScoreReward` | 0.0 | 0 | 자팀 점수가 바뀔 때마다(양수일 때) 팀 전원에게 + |
| `teamScorePenalty` | 0.0 | 0 | 상대 점수가 바뀔 때마다 팀 전원에게 − |
| `potentialEta` | 0.25 | 0.25 | η. 0이면 Ψ 계산 자체를 건너뜀 |
| `potentialGamma` | 0.99995 | 0.99995 | γ. Ψ, Φ 두 항이 같이 쓰고 **틱당** 적용 |
| `potentialScale` | 40.0 | 40 | Ψ의 tanh 분모(점수 단위) |
| `hazardCoefficient` | 0.05 | 0.05 | survive의 hazard 계수 c |
| `navPotentialEta` | 0.08 | 0.08 | η_nav. 0이면 Φ 계산 자체를 건너뜀 |
| `navPotentialScale` | 12.0 | 12 | Saturate의 L(월드 단위), 목표 없음 sentinel = 8·L |
| `hunterPotentialBaseWeight` | 0.25 | 0.25 | 빈손 적에 대한 Hunter 끌림(적재가 가득한 적 대비 비율) |

- 파일이 없으면 `Debug.LogError`를 남기고 클래스 기본값을 쓴다(`RewardConfig.Load`). 기본값은 3b636d7부터 JSON과 같다.
  고정 이벤트 보상의 기본값이 0이라서, 파일이 빠져도 옛 보상이 몰래 되살아나지 않는다.
- 고정 보상을 0으로 둔 이유는 모든 전략적 가치를 포텐셜에 넣기 위해서다(reward_proposal.md §12). 필드는 ablation을 위해 남겨 두었다.

## 6. 팀 원본 보상과의 차이 (d2220a7 → 지금)

| | 팀 공유 버전 d2220a7 (이벤트 보상, 0dac1fd) | 지금 |
|---|---|---|
| 종료 | 승 +1 / 패 −1 / 무 0 | 같음 |
| 킬 / 데스 | +0.3 킬러 / −0.2 팀 전원 | 0 / 0 |
| 팀 점수 변화 | +0.1 / −0.1 팀 전원 | 0 / 0 |
| 아이템 (줍기·적재·흡수·탈취) | Battery 0.2, 나머지는 기본값 0.1 | 전부 0 (없는 이름도 0) |
| 팀 포텐셜 Ψ | 없음 | η=0.25, 제로섬, 팀 broadcast (d3b4f56) |
| 개인 Φ | 없음 | η_nav=0.08. fetch/carry(42ec2ed), 그리드 거리(30293cc), 팀 배정·가치 가중(fba77db), Hunter 추격(da0db32) |
| 끄기 스위치 | 없음 | `-noRewardShaping` (94fabbd) |
| 디버그 | 없음 | `RewardDebugUI` + `RewardEventLog` (d5be5f5) |

## 7. shaping 끄기: `-noRewardShaping`

- Unity를 `-noRewardShaping` 인자로 띄우면 `RewardConfig.ApplyCommandLine`이 `potentialEta`와 `navPotentialEta`를 0으로 만든다.
  코디네이터는 η가 0이면 Ψ와 Φ 계산 자체를 건너뛴다(`PsiShapingOn`/`NavShapingOn`, 94fabbd). 종료 ±1과 (0인) 고정 보상은 그대로 온다.
- Python에서는 `BlackOutEnv(unity_shaping=False)`가 이 인자를 자동으로 붙인다(blackout-env `blackout_env/env/blackout_env.py`).
- Run 11이 끄는 이유는 두 가지다.
  1. 보상을 Python의 리워드 v2가 대신 계산하므로 Unity shaping은 쓰이지 않는다.
  2. 매 틱 도는 경로 탐색이 빠져 Unity step이 빨라진다. V17 대 V17 기준 4.46 → 1.64 ms/step, 결정 수로는 194 → 431 decisions/s(약 2.2배, 94fabbd 커밋 메시지).

## 8. `RewardDebugUI` 사용법

- Editor나 Development Build에서만 붙는다(`BlackOutEpisodeCoordinator.Awake`의 `#if UNITY_EDITOR || DEVELOPMENT_BUILD`). 씬 설정은 필요 없다.
- 기본으로 보이고, **F9**로 숨기거나 다시 띄운다(Input System). 화면에 보이는 것은 다음과 같다.
  - 유닛별 이번 에피소드 누적 보상(`Agent.GetCumulativeReward()`)과 팀 A/B 합계
  - 최근 보상 이벤트 60개(`RewardEventLog`, 최신이 위). 라벨은 `WIN`/`LOSE`/`DRAW`, `potential-shaping`, `nav-shaping`, 고정 보상 이벤트(`kill`, `death`, `pickup:<item>`, `deposit:<item>`, `absorbed:<item>`, `stolen:<item>`, `team score+`, `enemy score+`)다.
- 0에 가까운 값(`Mathf.Approximately`)은 기록하지 않는다. shaping이 켜져 있으면 매 틱 유닛마다 `nav-shaping`/`potential-shaping`이 쌓인다.
  그래서 피드가 순식간에 밀린다. 이벤트 단위로 보려면 `-noRewardShaping`으로 띄우거나 누적값을 보면 된다.

## 9. Python 리워드 v2와의 관계

- blackout-env `docs/reward_v2_design.md`가 이 보상을 처음부터 다시 설계한 것이다(구현 `blackout_env/train/reward_v2.py`). Run 11이 이것으로 학습했다.
- 원칙(종료 보상 + 포텐셜 기반 shaping만, 고정 이벤트 보상 없음)은 같다. 차이는 다음과 같다.
  - 포텐셜을 유닛별로 분해하고, 적 쪽 가치 변화를 귀속한다.
  - 필드 배터리 잔량으로 시간 가치를 둔다.
  - 관측만으로 계산한다.
  - shaping γ를 **학습기 γ와 같게** 쓴다. Unity의 `potentialGamma`는 학습기 γ와 따로 놀아서 정책 불변성이 깨진다(v2 설계 §1-6, H1).
- 그래서 Run 11 계열은 Unity를 `-noRewardShaping`으로 띄운다. 이때 Unity가 주는 보상은 종료 ±1(과 0인 고정 보상)뿐이다. 이 문서의 Ψ/Φ는 legacy 트레이너, 기본 `BlackOutEnv`, `unity_shaping=True`로 모은 데이터셋에만 해당한다.
