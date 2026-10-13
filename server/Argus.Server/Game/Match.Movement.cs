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

    // 자리가 비어 있으면 적을 추가하고 true를 돌려준다.
    private bool AddEnemy(Vector2 position, EnemyDefinition definition)
    {
        if (Enemies.Count >= Rules.MaxEnemies)
        {
            return false;
        }
        Enemy candidate = new Enemy(_nextId + 1, position, definition);
        if (!CanOccupy(position, definition.Radius, candidate))
        {
            return false;
        }
        _nextId++;
        candidate.NextActionAt = Now + 1;
        Enemies.Add(candidate);
        return true;
    }

    private void SpawnEnemy()
    {
        EnemyDefinition definition = EnemyDefinition.Grunt;
        // 시간이 지나도 첫 작전에는 2티어 적이 섞이지 않도록 추가 생성에도 같은 규칙을 적용한다.
        if (Rules.AllowEliteEnemies)
        {
            double roll = NextRandom();
            if (roll < 0.15)
            {
                definition = EnemyDefinition.Breaker;
            }
            else if (roll < 0.25 && Elapsed > 45)
            {
                definition = EnemyDefinition.Warden;
            }
        }
        // 분대가 보고 있거나 벽·몸에 막힌 자리면 각도를 다시 뽑는다. 끝까지 못 찾으면 이번 회차는 넘긴다.
        for (int attempt = 0; attempt < 12; attempt++)
        {
            double angle = NextRandom() * Math.Tau;
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

    // 흐름장에서 다음으로 갈 방향을 찾는다. 갈 곳이 없으면 0 벡터를 돌려준다.
    private Vector2 FlowHeading(Enemy enemy)
    {
        int x = (int)(enemy.Position.X / BattleMap.Cell);
        int y = (int)(enemy.Position.Y / BattleMap.Cell);
        int best = int.MaxValue;
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
                heading = new Vector2((nx + 0.5f) * BattleMap.Cell, (ny + 0.5f) * BattleMap.Cell) - enemy.Position;
            }
        }
        return heading;
    }

    // 한 방향으로 움직여 보고 실제로 전진했는지 알려준다. moved에는 시도 결과를 담는다.
    private bool TryMove(Enemy enemy, Vector2 heading, double dt, out Vector2 moved)
    {
        moved = enemy.Position;
        if (heading.LengthSquared() < 0.001)
        {
            return false;
        }
        Vector2 movement = Vector2.Normalize(heading) * (float)(enemy.Definition.Speed * dt);
        moved = MoveBody(enemy.Position, movement, enemy.Definition.Radius, enemy);
        return Vector2.DistanceSquared(moved, enemy.Position) >= movement.LengthSquared() * 0.1f;
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
                if (enemy.Action.EndsAt > Now)
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
            if (Now >= enemy.NextActionAt && closest < 75 * 75 && sight)
            {
                int index = (int)(NextRandom() * enemy.Definition.Skills.Length);
                EnemySkill skill = enemy.Definition.Skills[index];
                _nextId++;
                enemy.Action = new EnemyAction(_nextId, skill, Math.Atan2(direct.Y, direct.X), Now, Rules.EnemyWindupMultiplier);
                enemy.NextActionAt = enemy.Action.EndsAt + 0.25;
                continue;
            }

            // 사선이 보이면 직진하고, 가려져 있으면 흐름장 경로를 따른다.
            Vector2 heading = direct;
            if (!sight)
            {
                heading = FlowHeading(enemy);
            }
            Vector2 moved;
            bool advanced = TryMove(enemy, heading, dt, out moved);
            if (advanced)
            {
                enemy.Position = moved;
                continue;
            }
            // 전진하지 못했어도 조금 움직인 결과는 남겨 두고 다른 방향을 시도한다.
            Vector2 creep = moved;
            // 사선이 열려 있어도 몸이 모서리에 걸릴 수 있다. 이때는 흐름장 경로로 돌아간다.
            if (sight)
            {
                advanced = TryMove(enemy, FlowHeading(enemy), dt, out moved);
                if (advanced)
                {
                    enemy.Position = moved;
                    continue;
                }
            }
            // 앞줄의 적에게 막히면 양옆을 모두 시도한다. 짝수·홀수로 먼저 도는 쪽을 나눈다.
            Vector2 forward = heading;
            if (forward.LengthSquared() < 0.001)
            {
                forward = direct;
            }
            float side = 1;
            if (enemy.Id % 2 == 0)
            {
                side = -1;
            }
            for (int turn = 0; turn < 2 && !advanced; turn++)
            {
                Vector2 sideways = new Vector2(-forward.Y * side, forward.X * side);
                advanced = TryMove(enemy, sideways, dt, out moved);
                if (advanced)
                {
                    enemy.Position = moved;
                }
                side = -side;
            }
            if (!advanced)
            {
                enemy.Position = creep;
            }
        }
    }
}
