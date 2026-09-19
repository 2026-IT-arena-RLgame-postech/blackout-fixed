# RLGame2026 프로젝트 문서

## 📋 개요

Black Out의 **시스템 구조**를 설명하는 문서.

기획 문서는 노션 참고.

---

## 📚 문서 구성

### 🆕 [팀 공유 버전(d2220a7) 이후 변경 사항](./changes_since_team_version.md)
**먼저 읽을 문서.** run11-80k 브랜치에서 바뀐 것
- 관측 프로토콜 전면 변경 (유닛 obs = unitIndex, MapObsAgent 44-float + R16 비트 패킹 맵)
- 결정 주기 25 Hz, 에피소드·시간 처리 버그 수정
- 보상 쉐이핑, 빌드 스크립트

### 🔌 [ML-Agents 브릿지 설계](./ml_agent_design.md)
Unity ↔ Python 통신 구조의 현재 기준
- Behavior Parameters, 관찰 벡터·그래픽 비트 레이아웃
- 에피소드 생명주기, SideChannel
- Obs 수정 시 함께 고칠 곳 (Python `blackout-env`)

### 🏆 [보상 쉐이핑](./reward_shaping.md)
potential 기반 보상 설계 (Ψ 팀 potential, Φ 유닛별 nav potential)

### ➕ [아이템 타입 추가 가이드](./adding_item_type.md)
새 아이템을 관측에 반영하는 절차와 비트 한계

### 🐍 [libblackout 개발자 가이드 (폐기)](./libblackout_dev_guide.md)
Python 패키지가 `blackout-env` 저장소로 옮겨졌다는 안내와 대응표

### 🤖 [ML-Agent 통합 가이드](./ml_interface_guide.md)
ML-Agent 훈련을 위한 인터페이스 사용법
- GameScenario Facade 패턴 사용법
- 에피소드 관리 (시작/업데이트/종료)
- 유닛 제어 API
- 관찰/보상 설계 가이드

### 🏗️ [아키텍처 상세](./architecture_details.md)
내부 시스템 구조 이해
- 전체 아키텍처 다이어그램
- 주요 매니저 역할
- 이벤트 시스템
- 오브젝트 풀링
- 스탯 계산 시스템

### 📦 [데이터 설정 가이드](./data_setting_guide.md)
ScriptableObject 생성 및 설정
- UnitData 설정
- ItemData 설정
- TeamData 설정
- MapTileData 설정
- 유효성 검사(OnValidate)

---

## 🚀 빠른 시작

### 1. 게임 실행
1. Unity에서 `Prototype` 씬 열기
2. Play 버튼 클릭
3. 0~9로 유닛 선택. 한 번 더 누르면 해제.
4. WASD 선택한 유닛 이동(선택 유닛 없으면 카메라 이동)

### 2. 밸런스 조정
1. `Assets/Project/Runtime/Resources/ScriptableObjects/Config/GameBalanceConfig.asset` 열기
2. 목표 점수, 타이머 등 수정
3. 저장 후 Play 모드에서 테스트

### 3. ML-Agent 연동
```csharp
public class MyAgent : Agent
{
    private GameScenario gameScenario;

    public override void OnEpisodeBegin()
    {
        gameScenario.EpisodeBegin();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 유닛 위치, 점수 등 관찰
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        int unitIndex = 0; // 제어할 유닛
        Vector2 moveInput = new Vector2(actions.ContinuousActions[0], actions.ContinuousActions[1]);
        gameScenario.MoveUnit(unitIndex, moveInput, Time.fixedDeltaTime);
    }

    void FixedUpdate()
    {
        gameScenario.EpisodeUpdate(Time.fixedDeltaTime);
    }
}
```

자세한 내용은 [ML-Agent 통합 가이드](./ml_interface_guide.md)를 참조하세요.

---