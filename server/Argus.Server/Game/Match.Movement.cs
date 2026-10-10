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

    private void AddEnemy(Vector2 position, EnemyDefinition definition)
    {
        if (Enemies.Count >= Rules.MaxEnemies)
        {
            return;
        }
        Enemy candidate = new Enemy(_nextId + 1, position, definition);
        if (!CanOccupy(position, definition.Radius, candidate))
        {
            return;
        }
        _nextId++;
        candidate.NextActionAt = Now + 1;
        Enemies.Add(candidate);
    }

    private void SpawnEnemy()
    {
        double angle = NextRandom() * Math.Tau;
        Vector2 position = new Vector2(1080 + (float)Math.Cos(angle) * 650, 710 + (float)Math.Sin(angle) * 540);
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
        AddEnemy(position, definition);
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
            Vector2 heading = target.Position - enemy.Position;
            bool sight = Map.LineOfSight(enemy.Position, target.Position);
            if (Now >= enemy.NextActionAt && closest < 75 * 75 && sight)
            {
                int index = (int)(NextRandom() * enemy.Definition.Skills.Length);
                EnemySkill skill = enemy.Definition.Skills[index];
                _nextId++;
                enemy.Action = new EnemyAction(_nextId, skill, Math.Atan2(heading.Y, heading.X), Now, Rules.EnemyWindupMultiplier);
                enemy.NextActionAt = enemy.Action.EndsAt + 0.25;
                continue;
            }
            if (!sight)
            {
                int x = (int)(enemy.Position.X / BattleMap.Cell);
                int y = (int)(enemy.Position.Y / BattleMap.Cell);
                int best = int.MaxValue;
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
            }
            if (heading.LengthSquared() < 0.001)
            {
                continue;
            }
            Vector2 movement = Vector2.Normalize(heading) * (float)(enemy.Definition.Speed * dt);
            Vector2 moved = MoveBody(enemy.Position, movement, enemy.Definition.Radius, enemy);
            // 앞줄의 적에게 막히면 옆으로 돌아서되 몸을 겹치거나 밀어 넣지 않는다.
            if (Vector2.DistanceSquared(moved, enemy.Position) < movement.LengthSquared() * 0.1f)
            {
                float side = 1;
                if (enemy.Id % 2 == 0)
                {
                    side = -1;
                }
                movement = new Vector2(-movement.Y * side, movement.X * side);
                moved = MoveBody(enemy.Position, movement, enemy.Definition.Radius, enemy);
            }
            enemy.Position = moved;
        }
    }
}
