# 아이템 타입 추가 가이드

## 📋 개요

새 아이템을 ML 관측에 반영하려면 **Unity 인스펙터 배열 2곳(+선택 1곳) + 설정 파일 사본 2개 + Python 크기 확인**이 필요합니다.
관측 인코딩 전체는 [ml_agent_design.md](./ml_agent_design.md)를 참고하세요.

**먼저 확인할 한계:**

| 대상 | 비트 / 슬롯 | 한계 |
|---|---|---|
| 그래픽: 비배터리 아이템 인덱스 | 비트 7–9 (3비트) | **최대 7종** (현재 4종 사용: BuffSpeed, DebuffSpeed, BuffSize, DebuffSize → 5~7 남음) |
| 그래픽: 배터리 스택 수 | 비트 3–6 (4비트) | 0~15로 포화. 배터리 `MaxItemAmount`(현재 10)가 15를 넘으면 정보 손실 |
| 벡터: holdingItemId | 44벡터의 `4i+2` | `0`=없음, `-n`=배터리 n개, `k`=`KnownItems 인덱스 + 1` (≥2) |

8번째 비배터리 아이템이 필요하면 비트 레이아웃 자체를 바꿔야 합니다: `SemanticMapRenderer`의 `ITEM_SHIFT`/비트 수,
10비트를 넘으면 `PACK_DIVISOR`(1024), 그리고 Python `MyObsPreprocessor`의 `ITEM_MASK`/`PACK_DIVISOR`를 함께 수정합니다.

---

## 1️⃣ Unity — ItemData ScriptableObject 생성

Project 창에서 우클릭 → **Create > Project > Item Data** 로 새 에셋 생성.
기존 아이템은 `Assets/Project/Runtime/Resources/ScriptableObjects/ItemData/` 에 있습니다.

**필드 설정:**
- **Effects**: 아이템 효과 (기존 ItemEffect 에셋 참조 또는 신규 생성)
- **MaxItemAmount**: 최대 스택량 (비배터리 아이템은 현재 모두 1 — 그래픽은 비배터리 아이템의 개수를 표현하지 않음)
- **Sprite / AmountTiers**: 스프라이트

---

## 2️⃣ Unity — Inspector에 아이템 등록 (`Prototype.unity`)

아래 두 배열은 **반드시 같은 순서**여야 하며, **0번은 반드시 배터리**입니다.
0번만 개수(스택 수)로 인코딩되고, 1번부터는 종류 인덱스로 인코딩되기 때문입니다.

### 2-1. `BlackOutEpisodeCoordinator` — Known Items

배열 **끝에** 새 ItemData 추가.

> 인덱스 `i`(≥1)의 아이템을 든 유닛 → 44벡터의 holdingItemId = `i + 1`

### 2-2. `SemanticMapRenderer` — Known Items

같은 ItemData를 **같은 위치(끝)** 에 추가.

> 인덱스 `i`(≥1)의 아이템이 놓인 타일 → 픽셀 비트 7–9 = `i`

### 2-3. `LevelDirector` — Special Item Prototypes (선택)

필드에 스폰되어야 하는 아이템이면 `specialItemPrototypes` 목록에도 추가.

### 2-4. 보상 (선택)

`Assets/StreamingAssets/reward_config.json`의 `itemRewards`는 **ItemData 에셋 이름**으로 조회합니다. 고정 아이템 보상을 쓸 때만 추가하세요
(현재 기본 설계는 고정 보상 0 — [reward_shaping.md](./reward_shaping.md)).

---

## 3️⃣ 설정 파일 — `semantic_map_config.json` (사본 2개)

`n_items`를 +1 합니다. **사본이 두 개**이고 둘 다 맞춰야 합니다:

1. Unity: `Assets/StreamingAssets/semantic_map_config.json`
2. Python: `blackout-env/blackout_env/semantic_map_config.json` ← `BlackOutEnv`가 기본으로 읽는 것은 **이쪽**입니다

Unity C# 코드는 이 파일을 읽지 않습니다. Python은 `n_items`와 `n_classes`만 씁니다
(`resolution_scale`, `ids`, `item_id_offset`은 옛 인코딩의 흔적이라 무시됨).

---

## 4️⃣ Python — 자동으로 바뀌는 크기 확인

`MyObsPreprocessor(n_items=...)`가 다음을 계산합니다. `n_items - 1`이 7을 넘으면 생성자에서 `ValueError`가 납니다.

| 항목 | 계산 | 현재 (n_items=5, n_classes=3) |
|---|---|---|
| `n_graphic_channels` | `8 + 1 + (n_items - 1)` | 13 |
| `agent_state_size` | `2 + 1 + (n_items + 1) + n_classes` | 12 |

- ally/enemy 구분이 없는 아이템이면 팀 B 변환(`flip_team_perspective`)은 수정할 필요 없음
- 모델·파생 특징(`model/derived_obs.py`)과 휴리스틱이 아이템 채널 번호를 가정하는지 확인 (`blackout-env/docs/internals.md` "Obs 수정 가이드")

---

## ✅ 체크리스트

```
[ ] 비배터리 아이템 수가 7 이하인지 확인 (넘으면 비트 레이아웃부터 변경)
[ ] ItemData ScriptableObject 생성 및 설정
[ ] BlackOutEpisodeCoordinator.KnownItems 배열 끝에 추가 (0번 = Battery 유지)
[ ] SemanticMapRenderer.KnownItems 배열 끝에 같은 순서로 추가
[ ] LevelDirector.specialItemPrototypes 추가 (스폰 필요 시)
[ ] semantic_map_config.json n_items +1 — Unity StreamingAssets와 blackout_env 패키지 사본 둘 다
[ ] Python 모델 입력 크기 (n_graphic_channels, agent_state_size) 확인
```

---

## 🚨 주의사항

### 순서 변경 금지
KnownItems 배열 중간에 삽입하면 기존 체크포인트의 아이템 인덱스가 틀어집니다. 항상 **배열 끝에 추가**하세요.

### 체크포인트 호환성
`n_items`가 달라지면 그래픽 채널 수와 agent_state 크기가 바뀌므로 이전 체크포인트는 로드할 수 없습니다.
