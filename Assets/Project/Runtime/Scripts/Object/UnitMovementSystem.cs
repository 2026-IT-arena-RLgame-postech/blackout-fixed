using UnityEngine;

public class UnitMovementSystem
{
    private readonly IMapCollisionContext mapContext;
    private readonly TeamData team;

    private static readonly Vector2Int[] neighborDelta = new Vector2Int[]
    {
        new(-1, -1), new(-1, 0), new(-1, 1),
        new(0, -1),              new(0, 1),
        new(1, -1),  new(1, 0),  new(1, 1),
    };

    public UnitMovementSystem(IMapCollisionContext mapContext, TeamData team)
    {
        this.mapContext = mapContext;
        this.team = team;
    }

    /// <summary>
    /// 입력과 델타타임을 기반으로 충돌을 적용한 다음 위치를 계산.
    /// </summary>
    public Vector2 CalculateNextPosition(Vector2 currentPos, Vector2 displacement, CollisionBound collisionBound)
    {
        return ClampPosition(currentPos, displacement, collisionBound);
    }

    /// <summary>
    /// X축과 Y축을 분리해서 순차적으로 클램프한다(축 분리 슬라이딩).
    ///
    /// 예전에는 대각선 변위 전체를 한 번에 여러 벽 타일에 대해 순서대로 클램프했는데, 오목한
    /// 안쪽 모서리(벽 타일 두 개가 맞닿아 만드는 코너)에서 A 타일에 대한 클램프가 위치를 B 타일
    /// 영역으로 밀어넣고, 이어서 B 타일에 대한 클램프가 다시 A 타일 영역으로 밀어넣는 식으로
    /// 상호 되먹임이 생겨 8방향 중 어느 방향을 입력해도(랜덤 포함) 전혀 못 빠져나오는 고정점이
    /// 만들어질 수 있었다(실제로 학습용 heuristic 탐색 유닛이 60틱 넘게 완전히 정지하는 것으로
    /// 확인됨 — blackout-env/debug_heuristic_trace.py). X/Y를 분리해서 처리하면 한쪽 축이
    /// 막혀도 다른 축은 계속 진행할 수 있어(벽을 따라 미끄러짐) 이런 상호 교착이 발생하지 않는다.
    /// </summary>
    private Vector2 ClampPosition(Vector2 currentPos, Vector2 displacement, CollisionBound collisionBound)
    {
        Vector2 afterX = ClampAxis(currentPos, new Vector2(displacement.x, 0f), collisionBound);
        Vector2 afterY = ClampAxis(afterX, new Vector2(0f, displacement.y), collisionBound);
        return afterY;
    }

    private Vector2 ClampAxis(Vector2 currentPos, Vector2 displacement, CollisionBound collisionBound)
    {
        if (displacement == Vector2.zero)
            return currentPos;

        Vector2 desired = currentPos + displacement;
        Vector2Int currentCell = mapContext.WorldToCell(currentPos);

        foreach (Vector2Int delta in neighborDelta)
        {
            float xx = delta.x * displacement.x;
            float yy = delta.y * displacement.y;

            // 이동 방향에 맞지 않는 주변 타일은 검사 불필요.
            if (xx >= 0 && yy >= 0 && xx + yy > 0)
            {
                Vector2Int checkCell = currentCell + delta;

                if (!mapContext.IsWalkable(checkCell, team))
                {
                    CollisionBound tileBound = mapContext.GetTileCollisionBound(checkCell);
                    Vector3 tileCenter = mapContext.CellToCenterWorld(checkCell);

                    desired = CollisionUtils.GetClampedPosition(
                        desired,
                        collisionBound,
                        tileCenter,
                        tileBound
                    );
                }
            }
        }
        return desired;
    }
}