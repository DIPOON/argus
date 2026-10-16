using System.Numerics;

namespace Argus.Server.Game;

public sealed partial class Match
{
    // mover가 null이면 플레이어다. 플레이어끼리는 통과하고 나머지 몸 충돌은 막는다.
    private bool CanOccupy(Vector2 position, float radius, Enemy? mover)
    {
        if (!Map.CanStand(position, radius))
        {
            return false;
        }
        for (int i = 0; i < Enemies.Count; i++)
        {
            Enemy other = Enemies[i];
            if (other == mover || other.Hp <= 0)
            {
                continue;
            }
            double minimum = radius + other.Definition.Radius;
            if (Vector2.DistanceSquared(position, other.Position) < minimum * minimum - 0.001)
            {
                return false;
            }
        }
        if (mover != null)
        {
            for (int i = 0; i < Players.Count; i++)
            {
                Player player = Players[i];
                double minimum = radius + Rules.PlayerRadius;
                if (player.State == "alive" && Vector2.DistanceSquared(position, player.Position) < minimum * minimum - 0.001)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private Vector2 MoveBody(Vector2 from, Vector2 delta, float radius, Enemy? mover)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(delta.Length() / 4));
        delta /= steps;
        for (int i = 0; i < steps; i++)
        {
            Vector2 next = new Vector2(from.X + delta.X, from.Y);
            if (CanOccupy(next, radius, mover))
            {
                from = next;
            }
            next = new Vector2(from.X, from.Y + delta.Y);
            if (CanOccupy(next, radius, mover))
            {
                from = next;
            }
        }
        return from;
    }

    // 적 id는 무작위로 정한다. 효과·행동과 같은 순번을 쓰면 보이는 id 사이의 빈 번호로
    // 안개 속에서 생성된 적의 수를 셀 수 있다. 살아 있는 적끼리는 겹치지 않게 한다.
    // 방의 시드에서 뽑으므로 같은 시드면 같은 id가 나온다. 출력으로 다음 값을 알 수 없는 생성기라
    // 보이는 적 id로 다음 생성이나 적의 행동 선택을 미리 알아낼 수 없다.
    private int NewEnemyId()
    {
        while (true)
        {
            int id = _random.NextInt(1, int.MaxValue);
            bool taken = false;
            for (int i = 0; i < Enemies.Count; i++)
            {
                if (Enemies[i].Id == id)
                {
                    taken = true;
                    break;
                }
            }
            if (!taken)
            {
                return id;
            }
        }
    }

    // 처음 배치하는 적의 자리다. 엘리트를 켠 작전도 같은 자리에 종류만 바꿔 둔다.
    // 셋 모두 가장 가까운 벽에서 60 넘게 떨어진 빈 땅이라, 가장 큰 적(철위병, 반지름 24)도 넉넉히 들어간다.
    // (970, 640)은 시설 1 아래 장애물의 남쪽이다.
    public static IReadOnlyList<Vector2> InitialEnemySpots { get; } = new Vector2[]
    {
        new Vector2(610, 730),
        new Vector2(970, 640),
        new Vector2(1260, 770)
    };

    // 처음 배치는 정해진 자료이므로 자리가 막혀 있으면 결함이다. AddEnemy처럼 조용히 빼지 않고 예외를 낸다.
    // 이 시점에는 대원도 숨은 정보도 없으므로 실패를 알려도 안개 속을 엿보는 수단이 되지 않는다.
    // 규칙이 적을 두지 않게 한 경우(검사의 MaxEnemies = 0 등)는 결함이 아니므로 그냥 넘어간다.
    private void PlaceInitialEnemy(Vector2 spot, EnemyDefinition definition)
    {
        if (Enemies.Count >= Rules.MaxEnemies)
        {
            return;
        }
        if (!AddEnemy(spot, definition))
        {
            throw new InvalidOperationException($"처음 배치 자리 {spot}에 {definition.Id}을 둘 수 없습니다. 벽이나 다른 적과 겹칩니다.");
        }
    }

    // 자리가 비어 있으면 적을 추가하고 true를 돌려준다.
    private bool AddEnemy(Vector2 position, EnemyDefinition definition)
    {
        if (Enemies.Count >= Rules.MaxEnemies)
        {
            return false;
        }
        Enemy candidate = new Enemy(NewEnemyId(), position, definition);
        if (!CanOccupy(position, definition.Radius, candidate))
        {
            return false;
        }
        candidate.NextActionTick = Tick + Rules.ToTicks(1);
        Enemies.Add(candidate);
        return true;
    }

    private void SpawnEnemy()
    {
        EnemyDefinition definition = EnemyDefinition.Grunt;
        // 시간이 지나도 첫 작전에는 2티어 적이 섞이지 않도록 추가 생성에도 같은 규칙을 적용한다.
        if (Rules.AllowEliteEnemies)
        {
            double roll = _random.NextDouble();
            if (roll < 0.15)
            {
                definition = EnemyDefinition.Breaker;
            }
            else if (roll < 0.25 && ElapsedTicks > Rules.ToTicks(45))
            {
                definition = EnemyDefinition.Warden;
            }
        }
        // 분대가 보고 있거나 벽·몸에 막힌 자리면 각도를 다시 뽑는다. 끝까지 못 찾으면 이번 회차는 넘긴다.
        for (int attempt = 0; attempt < 12; attempt++)
        {
            double angle = _random.NextDouble() * Math.Tau;
            Vector2 position = new Vector2(1080 + (float)Math.Cos(angle) * 650, 710 + (float)Math.Sin(angle) * 540);
            if (Visible(position))
            {
                continue;
            }
            if (AddEnemy(position, definition))
            {
                return;
            }
        }
    }

    private void BuildFlow()
    {
        Array.Fill(_flow, int.MaxValue);
        Queue<int> queue = new Queue<int>();
        for (int i = 0; i < Players.Count; i++)
        {
            if (Players[i].State != "alive")
            {
                continue;
            }
            Vector2 position = Players[i].Position;
            int index = (int)(position.Y / BattleMap.Cell) * BattleMap.Columns + (int)(position.X / BattleMap.Cell);
            if (index >= 0 && index < _flow.Length && _flow[index] != 0)
            {
                _flow[index] = 0;
                queue.Enqueue(index);
            }
        }
        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            int x = index % BattleMap.Columns;
            int y = index / BattleMap.Columns;
            for (int direction = 0; direction < 4; direction++)
            {
                int nx = x + NeighborX[direction];
                int ny = y + NeighborY[direction];
                if (Map.Solid(nx, ny))
                {
                    continue;
                }
                int next = ny * BattleMap.Columns + nx;
                if (_flow[next] <= _flow[index] + 1)
                {
                    continue;
                }
                _flow[next] = _flow[index] + 1;
                queue.Enqueue(next);
            }
        }
    }

    // 흐름장에서 다음으로 갈 방향을 찾는다. 자기 칸보다 비용이 낮은 이웃이 없으면(대원이 있는 출발점 칸)
    // 0 벡터를 돌려주어 부르는 쪽이 대원에게 직진하게 한다. 이웃 칸(비용 1)으로 향하면 오히려 멀어진다.
    public Vector2 FlowHeading(Vector2 position)
    {
        int x = (int)(position.X / BattleMap.Cell);
        int y = (int)(position.Y / BattleMap.Cell);
        int best = int.MaxValue;
        if (!Map.Solid(x, y))
        {
            best = _flow[y * BattleMap.Columns + x];
        }
        Vector2 heading = Vector2.Zero;
        for (int direction = 0; direction < 4; direction++)
        {
            int nx = x + NeighborX[direction];
            int ny = y + NeighborY[direction];
            if (Map.Solid(nx, ny))
            {
                continue;
            }
            int cost = _flow[ny * BattleMap.Columns + nx];
            if (cost < best)
            {
                best = cost;
                heading = new Vector2((nx + 0.5f) * BattleMap.Cell, (ny + 0.5f) * BattleMap.Cell) - position;
            }
        }
        return heading;
    }

    // 몸이 직선으로 지나갈 수 있는지 본다. 몸 중심선은 호출 전에 사선으로 이미 확인했고,
    // 여기서는 몸 양옆 가장자리를 지나는 두 선을 본다. 세 선의 간격(반지름 이하)이 벽 타일(48)보다 좁아서
    // 그 사이로 벽이 빠져나가지 못한다.
    private bool BodyCanPass(Enemy enemy, Vector2 target)
    {
        Vector2 direction = target - enemy.Position;
        if (direction.LengthSquared() < 0.001f)
        {
            return true;
        }
        // 옆선 간격을 반지름보다 조금 줄인다. 벽에 딱 붙은 적은 옆선이 벽 면 위에서 시작해 거리 0에서 막혔다고 나오기 때문이다.
        Vector2 normal = Vector2.Normalize(new Vector2(-direction.Y, direction.X)) * (enemy.Definition.Radius - 0.5f);
        if (!Map.LineOfSight(enemy.Position + normal, target + normal))
        {
            return false;
        }
        return Map.LineOfSight(enemy.Position - normal, target - normal);
    }

    // 가까운 적끼리 서로 밀어내는 방향을 구한다. 몸이 닿기 조금 전부터, 가까울수록 세게 민다.
    private Vector2 Separation(Enemy enemy)
    {
        Vector2 push = Vector2.Zero;
        for (int i = 0; i < Enemies.Count; i++)
        {
            Enemy other = Enemies[i];
            if (other == enemy || other.Hp <= 0)
            {
                continue;
            }
            Vector2 away = enemy.Position - other.Position;
            float reach = enemy.Definition.Radius + other.Definition.Radius + 12;
            float distance = away.Length();
            if (distance >= reach || distance < 0.001f)
            {
                continue;
            }
            push += away / distance * ((reach - distance) / reach);
        }
        return push;
    }

    private void UpdateEnemies(double dt)
    {
        for (int i = 0; i < Enemies.Count; i++)
        {
            Enemy enemy = Enemies[i];
            if (enemy.Hp <= 0)
            {
                continue;
            }
            if (enemy.Action != null)
            {
                if (enemy.Action.EndsTick > Tick)
                {
                    continue;
                }
                enemy.Action = null;
            }
            Player? target = null;
            double closest = double.PositiveInfinity;
            for (int j = 0; j < Players.Count; j++)
            {
                Player player = Players[j];
                double distance = Vector2.DistanceSquared(player.Position, enemy.Position);
                if (player.State == "alive" && distance < closest)
                {
                    closest = distance;
                    target = player;
                }
            }
            if (target == null)
            {
                continue;
            }
            Vector2 direct = target.Position - enemy.Position;
            bool sight = Map.LineOfSight(enemy.Position, target.Position);
            if (Tick >= enemy.NextActionTick && closest < 75 * 75 && sight)
            {
                int index = _random.NextInt(0, enemy.Definition.Skills.Length);
                EnemySkill skill = enemy.Definition.Skills[index];
                _nextId++;
                enemy.Action = new EnemyAction(_nextId, skill, Math.Atan2(direct.Y, direct.X), Tick, Rules.EnemyWindupMultiplier);
                // 회복이 끝난 틱에서 0.25초(8틱) 쉰 뒤 다음 행동을 고른다.
                enemy.NextActionTick = enemy.Action.EndsTick + Rules.ToTicks(0.25);
                continue;
            }

            // 조향: 움직이기 전에 방향을 한 번 정하고 한 번만 움직인다. 막혔다고 다른 방향을 거듭 시도하지 않는다.
            // 몸이 직선으로 지나갈 수 있으면 직진하고, 사선이 가려졌거나 몸이 모서리에 걸리면 흐름장 경로를 따른다.
            Vector2 heading = direct;
            if (!sight || !BodyCanPass(enemy, target.Position))
            {
                Vector2 flow = FlowHeading(enemy.Position);
                if (flow.LengthSquared() > 0.001f)
                {
                    heading = flow;
                }
            }
            if (heading.LengthSquared() < 0.001f)
            {
                continue;
            }
            // 가까운 적끼리 밀어내는 방향을 더해, 줄지어 막히지 않고 옆으로 비켜 가게 한다.
            Vector2 steer = Vector2.Normalize(heading) + Separation(enemy) * 0.8f;
            if (steer.LengthSquared() < 0.001f)
            {
                continue;
            }
            Vector2 movement = Vector2.Normalize(steer) * (float)(enemy.Definition.Speed * dt);
            enemy.Position = MoveBody(enemy.Position, movement, enemy.Definition.Radius, enemy);
        }
    }
}
