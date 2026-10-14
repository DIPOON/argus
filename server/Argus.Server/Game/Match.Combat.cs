using System.Numerics;

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
            if (enemy.Action != null && enemy.Action.IsActive(Now))
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
                if (!action.IsActive(Now) || !action.Skill.SingleTarget)
                {
                    continue;
                }
                // 접촉 순간 가장 가까운 적을 잡도록 접촉 전까지는 대상을 고친다.
                // 접촉했거나 이미 판정을 받았으면 대상을 고정해 같은 시전에서 다시 고르지 않는다.
                if (action.Target.HasValue && (Now >= action.ContactAt || action.JudgedEnemies.Count > 0))
                {
                    continue;
                }
                Enemy? nearest = null;
                double closest = double.PositiveInfinity;
                for (int k = 0; k < Enemies.Count; k++)
                {
                    Enemy enemy = Enemies[k];
                    double distance = Vector2.DistanceSquared(player.Position, enemy.Position);
                    if (enemy.Hp <= 0 || !PlayerContact(player, action, enemy))
                    {
                        continue;
                    }
                    if (distance < closest || (distance == closest && nearest != null && enemy.Id < nearest.Id))
                    {
                        closest = distance;
                        nearest = enemy;
                    }
                }
                if (nearest != null)
                {
                    action.Target = nearest.Id;
                }
            }
        }
    }

    private bool PlayerContact(Player player, PlayerAction action, Enemy enemy)
    {
        return InReach(player.Position, player.Aim, action.Skill.Range, action.Skill.HalfWidth,
            enemy.Position, enemy.Definition.Radius);
    }

    private bool InReach(Vector2 origin, double aim, double range, double halfWidth, Vector2 target, float radius)
    {
        Vector2 forward = new Vector2((float)Math.Cos(aim), (float)Math.Sin(aim));
        Vector2 offset = target - origin;
        double along = Vector2.Dot(offset, forward);
        double side = Math.Abs(offset.X * forward.Y - offset.Y * forward.X);
        if (along < 0 || along > range + radius || side > halfWidth + radius)
        {
            return false;
        }
        // 몸의 중심이 벽 반대편이면 좁은 근접 공격도 통과하지 못한다.
        return Map.LineOfSight(origin, target);
    }

    // origin에서 aim 방향을 기준으로 target이 전방 120°(방어 각도) 안에 있는지 본다.
    // 대원의 방어, 적 방어의 반격 차단, 측면 공격 판정이 모두 이 계산을 쓴다.
    private bool Faces(Vector2 origin, double aim, Vector2 target)
    {
        Vector2 offset = target - origin;
        double angle = Math.Atan2(offset.Y, offset.X);
        double difference = Math.Abs(Math.IEEERemainder(angle - aim, Math.Tau));
        return difference <= Rules.GuardHalfAngle;
    }

    private bool GuardFaces(Player player, Enemy enemy)
    {
        return Faces(player.Position, player.Aim, enemy.Position) && Map.LineOfSight(player.Position, enemy.Position);
    }

    // 대원이 적 행동이 향한 범위 안에 있는지 본다. 타격과 잡기는 그 공격이 닿는 범위다.
    // 방어는 밀어치기가 닿는 범위와 방패가 향한 전방 120°를 합친다.
    // 밀어치기는 적 몸 옆까지 조금 더 닿으므로, 전방 120°만 보면 그 자리의 대원이 맞기만 하고 상성으로 대응하지 못한다.
    private bool InActionArea(Enemy enemy, EnemyAction attack, Player player)
    {
        bool reached = InReach(enemy.Position, attack.Aim, attack.Skill.Range, attack.Skill.HalfWidth, player.Position, Rules.PlayerRadius);
        if (reached)
        {
            return true;
        }
        if (attack.Skill.Kind == "block")
        {
            return Faces(enemy.Position, attack.Aim, player.Position);
        }
        return false;
    }

    private bool Incoming(Enemy enemy, EnemyAction attack, Player player)
    {
        return Now >= attack.ContactAt && Now < attack.ActiveUntil && !attack.HitPlayers.Contains(player.Id) &&
            InReach(enemy.Position, attack.Aim, attack.Skill.Range, attack.Skill.HalfWidth, player.Position, Rules.PlayerRadius);
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
                if (!action.IsActive(Now) || action.JudgedEnemies.Contains(enemy.Id))
                {
                    continue;
                }
                bool initiates = false;
                if (action.Skill.GuardsFront)
                {
                    // 방어 창에 들어온 공격이 없으면 아무 판정도 일어나지 않는다.
                    if (!incoming || !GuardFaces(player, enemy))
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
                        if (Now >= action.ContactAt)
                        {
                            flanks.Add(new Response(player, action, true));
                        }
                        continue;
                    }
                    initiates = Now >= action.ContactAt;
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
        // 부분 대응은 체력을 1 아래로 내리지 않으므로 처치와 관계없다.
        Response? drawCredit = null;
        bool partial = false;
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
                // 2티어에 하나만 맞춘 경우다. 이 구현의 기본 기술은 중단되고 적은 살아남는다.
                partial = true;
                CancelActions(player);
                Hurt(player, 1);
                Effect(enemy.Position, "partial", 0.65);
            }
            else if (priority == 1)
            {
                enemyDamage += 1;
                if (best != null && (drawCredit == null || best.Action.Id < drawCredit.Action.Id))
                {
                    drawCredit = best;
                }
                Hurt(player, attack.Skill.Tier);
                Effect(enemy.Position, "draw", 0.65);
            }
            else
            {
                CancelActions(player);
                Hurt(player, attack.Skill.Tier);
            }
        }
        if (partial)
        {
            // 부분 대응 효과는 기술별 설정이다. 이것만으로는 엘리트를 처치하지 않는다.
            enemy.Hp = Math.Max(1, enemy.Hp - attack.Skill.PartialDamage);
        }
        if (enemyDamage > 0 && drawCredit != null)
        {
            DamageEnemy(enemy, enemyDamage, drawCredit.Player, "draw");
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
                if (enemy.Hp <= 0 || !action.IsActive(Now) || Now < action.ContactAt ||
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
                if (enemy.Hp <= 0 || !action.IsFollowup(Now, Rules.ParryFollowupSeconds) ||
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
        if (guard == null || guard.Skill.Kind != "block" || !guard.IsActive(Now))
        {
            return false;
        }
        return Faces(enemy.Position, guard.Aim, player.Position);
    }

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
                bool contact = action.Skill.BreaksStructures && action.IsActive(Now) && Now >= action.ContactAt;
                if (!contact && !action.IsFollowup(Now, Rules.ParryFollowupSeconds))
                {
                    continue;
                }
                for (int k = 0; k < Facilities.Count; k++)
                {
                    Facility facility = Facilities[k];
                    if (facility.Hp > 0 && !action.HitFacilities.Contains(facility.Id) &&
                        InReach(player.Position, player.Aim, action.Skill.Range, action.Skill.HalfWidth, facility.Position, 27))
                    {
                        action.HitFacilities.Add(facility.Id);
                        DamageFacility(facility);
                    }
                }
                Vector2 facing = new Vector2((float)Math.Cos(player.Aim), (float)Math.Sin(player.Aim));
                WallHit wall = Map.RayWall(player.Position, player.Position + facing * (float)action.Skill.Range);
                if (wall.Tile >= 0 && !action.HitWalls.Contains(wall.Tile))
                {
                    action.HitWalls.Add(wall.Tile);
                    if (Map.Damage(wall.Tile, 1))
                    {
                        Effect(player.Position + facing * (float)(action.Skill.Range * wall.Fraction), "hit", 0.4);
                    }
                }
            }
        }
    }
}
