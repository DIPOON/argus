namespace Argus.Server.Game;

public sealed partial class Match
{
    private sealed class Response
    {
        public Player Player { get; }
        public PlayerAction Action { get; }
        public bool Initiates { get; }

        public Response(Player player, PlayerAction action, bool initiates)
        {
            Player = player;
            Action = action;
            Initiates = initiates;
        }
    }

    private void ResolveCombat()
    {
        ChooseGrabTargets();
        for (int i = 0; i < Enemies.Count; i++)
        {
            Enemy enemy = Enemies[i];
            if (enemy.Hp <= 0)
            {
                continue;
            }
            if (enemy.Action != null && enemy.Action.IsActive(Tick))
            {
                ResolveEnemyAction(enemy, enemy.Action);
            }
            else
            {
                ResolveIdleEnemy(enemy);
            }
            ResolveFollowups(enemy);
        }
        ResolveStructures();
    }

    private void ChooseGrabTargets()
    {
        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.State != "alive")
            {
                continue;
            }
            for (int j = 0; j < player.Actions.Count; j++)
            {
                PlayerAction action = player.Actions[j];
                if (!action.IsActive(Tick) || !action.Skill.SingleTarget || action.TargetLocked)
                {
                    continue;
                }
                // 판정을 받았으면 그 대상으로 정한다. 대상이 처치되어도 같은 시전에서 다시 고르지 않는다.
                if (action.JudgedEnemies.Count > 0)
                {
                    action.TargetLocked = true;
                    continue;
                }
                Enemy? nearest = null;
                Fixed closest = Fixed.Zero;
                for (int k = 0; k < Enemies.Count; k++)
                {
                    Enemy enemy = Enemies[k];
                    Fixed distance = FixedVector.DistanceSquared(player.Position, enemy.Position);
                    if (enemy.Hp <= 0 || !PlayerContact(player, action, enemy))
                    {
                        continue;
                    }
                    // 거리가 같으면 id가 작은 적을 고른다. 적 목록의 순서에 기대지 않는다.
                    if (nearest == null || distance < closest || (distance == closest && enemy.Id < nearest.Id))
                    {
                        closest = distance;
                        nearest = enemy;
                    }
                }
                // 준비 동작 중에는 매 틱 가장 가까운 적으로 고친다. 범위 안에 아무도 없으면 대상을 비워
                // 범위를 벗어난 적이 대상으로 남지 않게 한다.
                if (nearest == null)
                {
                    action.Target = null;
                    continue;
                }
                action.Target = nearest.Id;
                // 접촉하는 틱이나 그 뒤에 고른 대상으로 정한다. 접촉 틱에도 다시 고르므로 접촉 순간 가장 가까운 적이 된다.
                // 접촉 순간 범위에 아무도 없었으면 유효 시간 안에 처음 들어온 적이 대상이 된다.
                if (Tick >= action.ContactTick)
                {
                    action.TargetLocked = true;
                }
            }
        }
    }

    private bool PlayerContact(Player player, PlayerAction action, Enemy enemy)
    {
        return SkillReaches(player, action.Skill, enemy.Position, enemy.Definition.Radius);
    }

    // 대원의 기술이 target(반지름 radius인 몸)에 닿는지 본다. 둘레 기술은 대원 둘레의 원, 그 밖에는 조준 방향의 사각형이다.
    private bool SkillReaches(Player player, SkillDefinition skill, FixedVector target, Fixed radius)
    {
        if (skill.Around)
        {
            return InCircle(player.Position, skill.RangeFixed, target, radius);
        }
        return InReach(player.Position, player.AimDirection, skill.RangeFixed, skill.HalfWidthFixed, target, radius);
    }

    // target의 몸이 origin 둘레 반지름 range의 원에 닿는지 본다. 거리는 제곱끼리 비교한다.
    private bool InCircle(FixedVector origin, Fixed range, FixedVector target, Fixed radius)
    {
        Fixed reach = range + radius;
        if (FixedVector.DistanceSquared(origin, target) > reach * reach)
        {
            return false;
        }
        // 조준 방향의 범위처럼, 몸의 중심이 벽 반대편이면 닿지 않는다.
        return Map.LineOfSight(origin, target);
    }

    // forward는 조준 방향의 길이 1 벡터다(Player.AimDirection, EnemyAction.Direction). 판정마다 삼각함수를 다시 구하지 않는다.
    // 대상까지의 벡터를 조준 방향의 앞뒤 거리(내적)와 옆 거리(외적의 크기)로 나눠, 사거리와 반폭(대상의 반지름만큼 넓힌)과 비교한다.
    private bool InReach(FixedVector origin, FixedVector forward, Fixed range, Fixed halfWidth, FixedVector target, Fixed radius)
    {
        FixedVector offset = target - origin;
        Fixed along = offset.Dot(forward);
        Fixed side = Fixed.Abs(offset.Cross(forward));
        if (along < Fixed.Zero || along > range + radius || side > halfWidth + radius)
        {
            return false;
        }
        // 몸의 중심이 벽 반대편이면 좁은 근접 공격도 통과하지 못한다.
        return Map.LineOfSight(origin, target);
    }

    // origin에서 aim 방향을 기준으로 target이 전방 120°(방어 각도) 안에 있는지 본다.
    // 대원의 방어, 적 방어의 반격 차단, 측면 공격 판정이 모두 이 계산을 쓴다.
    // 사잇각을 구하지 않고 내적으로 비교한다. 사잇각이 반각 이하 ⟺ offset·forward ≥ |offset| × cos(반각)이다.
    // 반각이 반 바퀴 이상이면 내적을 보지 않고 모든 방향을 막는다. 방향 벡터는 1/4096 단위로 반올림해 길이가 1보다 조금 길 수 있어,
    // 코사인 -1과 내적으로 비교하면 정확히 뒤쪽(약 1° 안)을 놓친다. 반각이 60°일 때 이 오차는 0.01° 안쪽이라 판정에 드러나지 않는다.
    private bool Faces(FixedVector origin, FixedVector forward, FixedVector target)
    {
        if (!_guardsAnyDirection)
        {
            return false;
        }
        if (_guardsEveryDirection)
        {
            return true;
        }
        FixedVector offset = target - origin;
        Fixed along = offset.Dot(forward);
        return along >= offset.Length() * _guardCosine;
    }

    private bool GuardFaces(Player player, PlayerAction action, Enemy enemy)
    {
        // 둘레 기술의 방어는 모든 방향을 막는다.
        if (action.Skill.Around)
        {
            return Map.LineOfSight(player.Position, enemy.Position);
        }
        return Faces(player.Position, player.AimDirection, enemy.Position) && Map.LineOfSight(player.Position, enemy.Position);
    }

    // 대원이 적 행동이 향한 범위 안에 있는지 본다. 타격과 잡기는 그 공격이 닿는 범위다.
    // 방어는 밀어치기가 닿는 범위와 방패가 향한 전방 120°를 합친다.
    // 밀어치기는 적 몸 옆까지 조금 더 닿으므로, 전방 120°만 보면 그 자리의 대원이 맞기만 하고 상성으로 대응하지 못한다.
    private bool InActionArea(Enemy enemy, EnemyAction attack, Player player)
    {
        bool reached = InReach(enemy.Position, attack.Direction, attack.Skill.RangeFixed, attack.Skill.HalfWidthFixed, player.Position, Rules.PlayerRadius);
        if (reached)
        {
            return true;
        }
        if (attack.Skill.Kind == "block")
        {
            return Faces(enemy.Position, attack.Direction, player.Position);
        }
        return false;
    }

    private bool Incoming(Enemy enemy, EnemyAction attack, Player player)
    {
        return Tick >= attack.ContactTick && Tick < attack.ActiveUntilTick && !attack.HitPlayers.Contains(player.Id) &&
            InReach(enemy.Position, attack.Direction, attack.Skill.RangeFixed, attack.Skill.HalfWidthFixed, player.Position, Rules.PlayerRadius);
    }

    private void ResolveEnemyAction(Enemy enemy, EnemyAction attack)
    {
        List<Response> responses = new List<Response>();
        // 적 행동의 범위 밖에서 친 대원들이다. 그 행동의 상성을 받지 않고 일반 피해만 준다.
        List<Response> flanks = new List<Response>();
        List<Player> threatened = new List<Player>();
        bool contact = false;
        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.State != "alive")
            {
                continue;
            }
            bool incoming = Incoming(enemy, attack, player);
            if (incoming)
            {
                threatened.Add(player);
                contact = true;
            }
            for (int j = 0; j < player.Actions.Count; j++)
            {
                PlayerAction action = player.Actions[j];
                if (!action.IsActive(Tick) || action.JudgedEnemies.Contains(enemy.Id))
                {
                    continue;
                }
                bool initiates = false;
                if (action.Skill.GuardsFront)
                {
                    // 방어 창에 들어온 공격이 없으면 아무 판정도 일어나지 않는다.
                    if (!incoming || !GuardFaces(player, action, enemy))
                    {
                        continue;
                    }
                }
                else
                {
                    if (!PlayerContact(player, action, enemy) || (action.Skill.SingleTarget && action.Target != enemy.Id))
                    {
                        continue;
                    }
                    // 적 행동의 범위 밖에서 친 대원에게는 그 행동의 상성을 적용하지 않는다(측면 공격).
                    // 무행동 적을 칠 때처럼 접촉한 순간 일반 피해만 준다.
                    if (!InActionArea(enemy, attack, player))
                    {
                        if (Tick >= action.ContactTick)
                        {
                            flanks.Add(new Response(player, action, true));
                        }
                        continue;
                    }
                    initiates = Tick >= action.ContactTick;
                }
                responses.Add(new Response(player, action, initiates));
                if (initiates)
                {
                    contact = true;
                }
            }
        }
        // 측면 공격을 먼저 처리한다. 그 피해로 적이 쓰러지면 이 행동은 무효가 되어 앞쪽 대원도 피해를 받지 않는다.
        for (int i = 0; i < flanks.Count; i++)
        {
            Response flank = flanks[i];
            flank.Action.JudgedEnemies.Add(enemy.Id);
            DamageEnemy(enemy, 1, flank.Player);
        }
        if (enemy.Hp <= 0 || !contact)
        {
            return;
        }

        // 같은 공격에 유효한 대응을 먼저 모두 모은다. 플레이어 순서 때문에 C가 먼저 다치면 안 된다.
        List<Response> counters = new List<Response>();
        for (int i = 0; i < responses.Count; i++)
        {
            Response response = responses[i];
            if (Affinity.Beats(response.Action.Skill.Kind, attack.Skill.Kind))
            {
                counters.Add(response);
            }
        }
        if (counters.Count >= attack.Skill.Tier)
        {
            Response credit = counters[0];
            for (int i = 0; i < counters.Count; i++)
            {
                Response response = counters[i];
                response.Action.JudgedEnemies.Add(enemy.Id);
                if (response.Action.Id < credit.Action.Id)
                {
                    credit = response;
                }
            }
            DamageEnemy(enemy, enemy.Hp, credit.Player, "break");
            return;
        }

        double enemyDamage = 0;
        // 비김 피해로 쓰러뜨리면 비긴 대원 중 가장 먼저 기술을 시작한 대원이 처치를 가져간다.
        Response? drawCredit = null;
        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.State != "alive")
            {
                continue;
            }
            Response? best = null;
            int priority = -1;
            bool engaged = threatened.Contains(player);
            for (int j = 0; j < responses.Count; j++)
            {
                Response candidate = responses[j];
                if (candidate.Player != player)
                {
                    continue;
                }
                int rank = 0;
                if (candidate.Action.Skill.Kind == attack.Skill.Kind)
                {
                    rank = 1;
                }
                if (Affinity.Beats(candidate.Action.Skill.Kind, attack.Skill.Kind))
                {
                    rank = 2;
                }
                if (candidate.Initiates || rank == 2)
                {
                    engaged = true;
                }
                if (rank > priority)
                {
                    priority = rank;
                    best = candidate;
                }
            }
            if (!engaged)
            {
                continue;
            }
            attack.HitPlayers.Add(player.Id);
            if (best != null)
            {
                best.Action.JudgedEnemies.Add(enemy.Id);
            }
            if (priority == 2)
            {
                // 2티어에 하나만 맞춘 경우다(부분 대응). 이 구현의 기본 기술은 중단되고 대원은 피해 1을 받는다.
                // 적은 기술별 부분 대응 피해를 받고 그 피해로도 쓰러지며, 처치는 이 대원이 가져간다.
                // 맞는 대응이 둘이면 위에서 완전 파훼로 끝나므로 한 판정에 이 경우는 많아야 한 명이다.
                // 같은 적 행동에 맞는 대응이 따로 닿으면 판정마다 이 피해를 받는다.
                // 비김 피해는 대원을 모두 본 뒤에 주므로 이 피해가 먼저다. 이 피해로 쓰러지면 비김 피해는 더하지 않는다.
                CancelActions(player);
                Hurt(player, 1);
                DamageEnemy(enemy, attack.Skill.PartialDamage, player, "partial");
            }
            else if (priority == 1)
            {
                enemyDamage += 1;
                if (best != null && (drawCredit == null || best.Action.Id < drawCredit.Action.Id))
                {
                    drawCredit = best;
                }
                Hurt(player, attack.Skill.Tier);
            }
            else
            {
                CancelActions(player);
                Hurt(player, attack.Skill.Tier);
            }
        }
        // 비긴 대원이 여럿이어도 비김 효과는 판정마다 하나다. 효과는 적 자리에 그려지므로 대원마다 만들면 겹친다.
        // 부분 대응 피해로 이미 쓰러졌어도 비김은 일어났으므로 효과는 만들고, 피해는 더하지 않는다(DamageEnemy).
        if (drawCredit != null)
        {
            Effect(enemy.Position, "draw", 0.65);
            DamageEnemy(enemy, enemyDamage, drawCredit.Player, null);
        }
    }

    private void CancelActions(Player player)
    {
        for (int i = 0; i < player.Actions.Count; i++)
        {
            player.Actions[i].Cancelled = true;
        }
    }

    private void ResolveIdleEnemy(Enemy enemy)
    {
        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.State != "alive")
            {
                continue;
            }
            for (int j = 0; j < player.Actions.Count; j++)
            {
                PlayerAction action = player.Actions[j];
                if (enemy.Hp <= 0 || !action.IsActive(Tick) || Tick < action.ContactTick ||
                    action.Skill.GuardsFront || action.JudgedEnemies.Contains(enemy.Id))
                {
                    continue;
                }
                if (action.Skill.SingleTarget && action.Target != enemy.Id)
                {
                    continue;
                }
                if (PlayerContact(player, action, enemy))
                {
                    action.JudgedEnemies.Add(enemy.Id);
                    DamageEnemy(enemy, 1, player);
                }
            }
        }
    }

    private void ResolveFollowups(Enemy enemy)
    {
        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.State != "alive")
            {
                continue;
            }
            for (int j = 0; j < player.Actions.Count; j++)
            {
                PlayerAction action = player.Actions[j];
                if (enemy.Hp <= 0 || !action.IsFollowup(Tick, Rules.ParryFollowupSeconds) ||
                    action.FollowupHits.Contains(enemy.Id) || !PlayerContact(player, action, enemy))
                {
                    continue;
                }
                // 빈 방어 뒤에도 나오며, 상대가 어떤 상성이든 일반 피해만 준다.
                action.FollowupHits.Add(enemy.Id);
                if (EnemyBlocksFollowup(enemy, player))
                {
                    // 상성 없는 피해도 전방 방어에는 막힌다. 이것은 RPS 승패가 아니다.
                    Effect(enemy.Position, "guard", 0.4);
                    continue;
                }
                DamageEnemy(enemy, 1, player);
            }
        }
    }

    private bool EnemyBlocksFollowup(Enemy enemy, Player player)
    {
        EnemyAction? guard = enemy.Action;
        if (guard == null || guard.Skill.Kind != "block" || !guard.IsActive(Tick))
        {
            return false;
        }
        return Faces(enemy.Position, guard.Direction, player.Position);
    }

    // 벽을 친 효과를 벽 면에서 대원 쪽으로 당기는 거리다.
    private static readonly Fixed WallEffectInset = Fixed.FromInt(6);
    // 시설을 칠 때 시설 몸의 반지름이다.
    private static readonly Fixed FacilityRadius = Fixed.FromInt(27);

    private void ResolveStructures()
    {
        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.State != "alive")
            {
                continue;
            }
            for (int j = 0; j < player.Actions.Count; j++)
            {
                PlayerAction action = player.Actions[j];
                bool contact = action.Skill.BreaksStructures && action.IsActive(Tick) && Tick >= action.ContactTick;
                // 반격이 구조물을 부수는지도 기술 속성으로 정한다. 공개되는 속성만 보고도 동작을 알 수 있어야 한다.
                bool followup = action.Skill.FollowupBreaksStructures && action.IsFollowup(Tick, Rules.ParryFollowupSeconds);
                if (!contact && !followup)
                {
                    continue;
                }
                for (int k = 0; k < Facilities.Count; k++)
                {
                    Facility facility = Facilities[k];
                    if (facility.Hp > 0 && !action.HitFacilities.Contains(facility.Id) &&
                        SkillReaches(player, action.Skill, facility.Position, FacilityRadius))
                    {
                        action.HitFacilities.Add(facility.Id);
                        DamageFacility(facility);
                    }
                }
                // 벽은 조준 방향의 광선으로 친다. 둘레 기술은 조준하지 않으므로 벽을 치지 않는다.
                if (action.Skill.Around)
                {
                    continue;
                }
                FixedVector facing = player.AimDirection;
                WallHit wall = Map.RayWall(player.Position, player.Position + facing * action.Skill.RangeFixed);
                // 맵 밖에서 닿으면 Tile이 -1이다. Hit도 함께 봐서, 닿지 않은 결과(Tile이 0인 default 포함)를 0번 타일로 치지 않는다.
                if (wall.Hit && wall.Tile >= 0 && !action.HitWalls.Contains(wall.Tile))
                {
                    action.HitWalls.Add(wall.Tile);
                    // Map.Damage는 벽이 부서졌을 때만 true를 돌려주므로, 부술 수 있는 벽인지는 치기 전에 따로 본다.
                    // 시설을 칠 때처럼 칠 때마다 효과를 낸다.
                    bool breakable = Map.Tiles[wall.Tile] == 1;
                    Map.Damage(wall.Tile, 1);
                    if (breakable)
                    {
                        // 효과는 벽 면에서 대원 쪽으로 조금 당긴 곳에 둔다. 벽 면 위에 두면 시야 판정의 사선이
                        // 그 벽에 막혀, 벽이 부서지는 마지막 타격을 빼고는 스냅샷에서 걸러진다.
                        Fixed reach = Fixed.Max(Fixed.Zero, action.Skill.RangeFixed * wall.Fraction - WallEffectInset);
                        Effect(player.Position + facing * reach, "hit", 0.4);
                    }
                }
            }
        }
    }
}
