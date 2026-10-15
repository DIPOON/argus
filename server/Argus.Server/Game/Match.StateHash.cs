namespace Argus.Server.Game;

public sealed partial class Match
{
    // 진행 상태 전체를 SHA-256 하나로 요약한다. 같은 시드로 만들고 같은 틱마다 같은 입력을 넣은 두 방은
    // 틱마다 같은 값을 가져야 한다. 결정론 검사와, 기록을 다시 돌렸을 때 같은 길을 가는지 확인하는 데 쓴다.
    // 규칙과 기술·적 정의는 진행 중에 바뀌지 않으므로 id만 적는다. 시드는 상태가 아니라 처음에 받은 값이므로
    // 적지 않고, 난수를 얼마나 뽑았는지만 적는다. 연결 번호(Player.Connection)는 접속 처리가 쓰는 값이고
    // 진행에 영향을 주지 않으므로 넣지 않는다. 시야 밖 적까지 담은 값이므로 클라이언트에 보내지 않는다.
    public string StateHash()
    {
        using StateHasher hasher = new StateHasher();
        hasher.AddLong(Tick);
        hasher.AddDouble(Now);
        hasher.AddDouble(StartedAt);
        hasher.AddOptionalDouble(EndedAt);
        hasher.AddText(Phase);
        hasher.AddOptionalText(Result);
        hasher.AddDouble(ExtractionProgress);
        hasher.AddInt(Pulse);
        hasher.AddInt(Departures);
        hasher.AddDouble(_nextSpawn);
        hasher.AddInt(_nextId);
        hasher.AddDouble(_nextFlow);
        hasher.AddIntArray(_flow);
        hasher.AddLong(_random.Position);
        Map.AddState(hasher);

        hasher.AddInt(Players.Count);
        for (int i = 0; i < Players.Count; i++)
        {
            AddPlayerState(hasher, Players[i]);
        }
        hasher.AddInt(Enemies.Count);
        for (int i = 0; i < Enemies.Count; i++)
        {
            AddEnemyState(hasher, Enemies[i]);
        }
        hasher.AddInt(Facilities.Count);
        for (int i = 0; i < Facilities.Count; i++)
        {
            Facility facility = Facilities[i];
            hasher.AddInt(facility.Id);
            hasher.AddPoint(facility.Position);
            hasher.AddDouble(facility.Hp);
            hasher.AddDouble(facility.MaxHp);
        }
        hasher.AddInt(Supplies.Count);
        for (int i = 0; i < Supplies.Count; i++)
        {
            Supply supply = Supplies[i];
            hasher.AddInt(supply.Id);
            hasher.AddPoint(supply.Position);
            hasher.AddBool(supply.Objective);
            hasher.AddBool(supply.Collected);
            hasher.AddTextDoubleMap(supply.ReadyAt);
        }
        hasher.AddInt(Effects.Count);
        for (int i = 0; i < Effects.Count; i++)
        {
            Effect effect = Effects[i];
            hasher.AddInt(effect.Id);
            hasher.AddDouble(effect.X);
            hasher.AddDouble(effect.Y);
            hasher.AddText(effect.Kind);
            hasher.AddDouble(effect.Until);
        }
        hasher.AddInt(Notices.Count);
        for (int i = 0; i < Notices.Count; i++)
        {
            hasher.AddDouble(Notices[i].At);
            hasher.AddText(Notices[i].Text);
        }
        return hasher.Finish();
    }

    private static void AddPlayerState(StateHasher hasher, Player player)
    {
        hasher.AddText(player.Id);
        hasher.AddText(player.Name);
        hasher.AddTextArray(player.Slots);
        hasher.AddPoint(player.Position);
        hasher.AddDouble(player.Aim);
        hasher.AddText(player.State);
        hasher.AddBool(player.HasDeployed);
        hasher.AddDouble(player.DeployAt);
        hasher.AddPoint(player.Landing);
        hasher.AddDouble(player.Hp);
        hasher.AddDouble(player.MaxHp);
        hasher.AddInt(player.Mana);
        hasher.AddInt(player.MaxMana);
        hasher.AddDouble(player.BusyUntil);
        hasher.AddInt(player.RequestedSlot);
        hasher.AddInt(player.Actions.Count);
        for (int i = 0; i < player.Actions.Count; i++)
        {
            PlayerAction action = player.Actions[i];
            hasher.AddInt(action.Id);
            hasher.AddText(action.Skill.Id);
            hasher.AddInt(action.Slot);
            hasher.AddDouble(action.StartedAt);
            hasher.AddDouble(action.ContactAt);
            hasher.AddDouble(action.ActiveUntil);
            hasher.AddDouble(action.EndsAt);
            hasher.AddBool(action.Cancelled);
            hasher.AddOptionalInt(action.Target);
            hasher.AddBool(action.TargetLocked);
            hasher.AddIntSet(action.JudgedEnemies);
            hasher.AddIntSet(action.FollowupHits);
            hasher.AddIntSet(action.HitFacilities);
            hasher.AddIntSet(action.HitWalls);
        }
        hasher.AddDouble(player.LastInputAt);
        hasher.AddLong(player.LastSequence);
        hasher.AddPoint(player.Move);
        hasher.AddBool(player.Connected);
        hasher.AddOptionalDouble(player.DisconnectedAt);
        hasher.AddBool(player.AwaitingFirstConnection);
        hasher.AddInt(player.Kills);
        hasher.AddInt(player.Deaths);
    }

    private static void AddEnemyState(StateHasher hasher, Enemy enemy)
    {
        hasher.AddInt(enemy.Id);
        hasher.AddPoint(enemy.Position);
        hasher.AddText(enemy.Definition.Id);
        hasher.AddDouble(enemy.Hp);
        hasher.AddDouble(enemy.NextActionAt);
        EnemyAction? action = enemy.Action;
        hasher.AddBool(action != null);
        if (action == null)
        {
            return;
        }
        hasher.AddInt(action.Id);
        hasher.AddText(action.Skill.Kind);
        hasher.AddInt(action.Skill.Tier);
        hasher.AddInt(action.Skill.PartialDamage);
        hasher.AddDouble(action.Skill.Windup);
        hasher.AddDouble(action.Skill.Range);
        hasher.AddDouble(action.Skill.HalfWidth);
        hasher.AddDouble(action.Aim);
        hasher.AddDouble(action.StartedAt);
        hasher.AddDouble(action.ContactAt);
        hasher.AddDouble(action.ActiveUntil);
        hasher.AddDouble(action.EndsAt);
        hasher.AddTextSet(action.HitPlayers);
    }
}
