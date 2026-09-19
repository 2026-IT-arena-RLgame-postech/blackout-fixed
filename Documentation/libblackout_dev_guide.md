# libblackout 개발자 가이드 (폐기 — blackout-env로 이전)

> ⚠️ **이 문서는 더 이상 유효하지 않습니다.**
> 예전 내용은 `libblackout/blackout/` 패키지 구조(`blackout/__init__.py`, 45-float 유닛 벡터, grayscale semantic ID 그래픽)를
> 설명했지만, 그 구조와 관측 프로토콜은 모두 바뀌었습니다. 예전 본문이 필요하면 git 이력(`d2220a7`)을 보세요.

## 📋 지금 어디를 봐야 하나

Python 쪽은 별도 저장소 **`blackout-env`**(패키지 이름 `blackout_env`)입니다. 이 Unity 저장소와 같은 부모 폴더에 클론하는 것을 가정합니다
(`CIBuild` 기본 출력 경로가 `../blackout-env/build/mac/BlackOut.app`).

| 알고 싶은 것 | 문서 |
|---|---|
| 패키지 구조, `BlackOutEnv` 내부 흐름, obs 수정 시 함께 고칠 곳, 테스트 | `blackout-env/docs/internals.md` |
| 공개 API (`BlackOutEnv` 생성자·`reset`·`step`, observation 상세, 경기 함수) | `blackout-env/docs/api.md` |
| 관측 인코딩의 기준 (비트 배치, 채널 표, 44-float 벡터 슬롯) | `blackout-env/blackout_env/env/my_obs_preprocessor.py` 모듈 docstring |
| Unity ↔ Python 통신 구조 (이 저장소) | [ml_agent_design.md](./ml_agent_design.md) |
| 팀 공유 버전 이후 무엇이 바뀌었나 | [changes_since_team_version.md](./changes_since_team_version.md) |

## 🔁 옛 구조 → 현재 대응표

| 옛 (`libblackout/blackout/`) | 현재 (`blackout-env/blackout_env/`) |
|---|---|
| `blackout/env/blackout_env.py` | `env/blackout_env.py` (`BlackOutEnv`) |
| `blackout/env/obs_preprocessor.py` `ObsPreprocessor` | `env/my_obs_preprocessor.py` `MyObsPreprocessor` (옛 클래스는 레거시 기반 클래스로만 남음) |
| `blackout/env/semantic_id.py` | 레거시 — 현재 그래픽 채널과 맞지 않음 |
| `blackout/env/seed_channel.py` | `env/seed_channel.py` (UUID·페이로드 동일) |
| `blackout/env/constants.py` | `env/constants.py` |
| `blackout/competition/`, `blackout/model/` | `competition/`, `model/` |
