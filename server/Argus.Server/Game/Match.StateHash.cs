namespace Argus.Server.Game;

public sealed partial class Match
{
    // 진행 상태 전체를 SHA-256 하나로 요약한다. 같은 시드로 만들고 같은 틱마다 같은 입력을 넣은 두 방은
    // 틱마다 같은 값을 가져야 한다. 결정론 검사와, 기록을 다시 돌렸을 때 같은 길을 가는지 확인하는 데 쓴다.
    // 방 코드와 규칙도 적는다. 규칙만 다른 두 방이 처음 몇 초 동안 같은 값을 내면, 재생할 때 규칙을 잘못 넣은 실수를 늦게 잡는다.
    // 규칙은 JSON으로 적어 새 규칙 값을 추가해도 따로 챙기지 않아도 된다. 기술·적 정의는 코드에 정해진 것이므로
    // 대원 기술은 id, 적 기술은 정의 안의 순서만 적는다. 시드는 상태가 아니라 처음에 받은 값이므로 적지 않고,
    // 난수를 얼마나 뽑았는지만 적는다. 연결 번호(Player.Connection)는 접속 처리가 쓰는 값이고 진행에 영향을 주지 않으므로
    // 넣지 않는다. 시야 밖 적까지 담은 값이므로 클라이언트에 보내지 않는다.
    public string StateHash()
    {
        using StateHasher hasher = new StateHasher();
        hasher.AddText(Code);
        hasher.AddBytes(_rulesJson);
        // 지금 시각(Now)은 틱에서 바로 구하므로 틱만 적는다.
        hasher.AddLong(Tick);
        hasher.AddLong(StartedTick);
        hasher.AddOptionalLong(EndedTick);
        hasher.AddText(Phase);
        hasher.AddOptionalText(Result);
        hasher.AddLong(_extractionHalfTicks);
        hasher.AddInt(Departures);
        hasher.AddLong(_nextSpawnTick);
        hasher.AddInt(_nextId);
        hasher.AddInt(_nextNoticeId);
        hasher.AddLong(_nextFlowTick);
        hasher.AddIntArray(_flow);
        hasher.AddLong(_random.Position);
        Layout.AddState(hasher);
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
            hasher.AddText(SupplyRecord.KindName(supply.Kind));
            hasher.AddBool(supply.Collected);
            long? uses = null;
            if (supply.UsesLeft != null)
            {
                uses = supply.UsesLeft.Value;
            }
            hasher.AddOptionalLong(uses);
            hasher.AddTextLongMap(supply.ReadyTicks);
        }
        hasher.AddInt(Effects.Count);
        for (int i = 0; i < Effects.Count; i++)
        {
            Effect effect = Effects[i];
            hasher.AddInt(effect.Id);
            hasher.AddPoint(effect.Position);
            hasher.AddText(effect.Kind);
            hasher.AddLong(effect.UntilTick);
        }
        hasher.AddInt(Notices.Count);
        for (int i = 0; i < Notices.Count; i++)
        {
            hasher.AddInt(Notices[i].Id);
            hasher.AddLong(Notices[i].AtTick);
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
        hasher.AddInt(player.Aim.Units);
        hasher.AddText(player.State);
        hasher.AddBool(player.HasDeployed);
        hasher.AddLong(player.DeployTick);
        hasher.AddPoint(player.Landing);
        hasher.AddDouble(player.Hp);
        hasher.AddDouble(player.MaxHp);
        hasher.AddInt(player.Mana);
        hasher.AddInt(player.MaxMana);
        hasher.AddLong(player.BusyUntilTick);
        hasher.AddInt(player.RequestedSlot);
        hasher.AddInt(player.Actions.Count);
        for (int i = 0; i < player.Actions.Count; i++)
        {
            PlayerAction action = player.Actions[i];
            hasher.AddInt(action.Id);
            hasher.AddText(action.Skill.Id);
            hasher.AddInt(action.Slot);
            hasher.AddLong(action.StartedTick);
            hasher.AddLong(action.ContactTick);
            hasher.AddLong(action.ActiveUntilTick);
            hasher.AddLong(action.EndsTick);
            hasher.AddBool(action.Cancelled);
            hasher.AddOptionalInt(action.Target);
            hasher.AddBool(action.TargetLocked);
            hasher.AddIntSet(action.JudgedEnemies);
            hasher.AddIntSet(action.FollowupHits);
            hasher.AddIntSet(action.HitFacilities);
            hasher.AddIntSet(action.HitWalls);
        }
        hasher.AddLong(player.LastInputTick);
        hasher.AddLong(player.LastSequence);
        hasher.AddPoint(player.Move);
        hasher.AddBool(player.Connected);
        hasher.AddOptionalLong(player.DisconnectedTick);
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
        hasher.AddLong(enemy.NextActionTick);
        EnemyAction? action = enemy.Action;
        hasher.AddBool(action != null);
        if (action == null)
        {
            return;
        }
        hasher.AddInt(action.Id);
        // 적 기술에는 id가 없으므로 정의 안의 순서로 적는다. 값을 하나씩 적으면 나중에 추가한 값을 빠뜨리기 쉽다.
        // 정의에 없는 기술(검사가 직접 만든 기술)이면 -1을 적고 기술 값을 JSON으로 적는다.
        int skill = Array.IndexOf(enemy.Definition.Skills, action.Skill);
        hasher.AddInt(skill);
        if (skill < 0)
        {
            hasher.AddBytes(StateHasher.ToJson(action.Skill));
        }
        hasher.AddInt(action.Aim.Units);
        hasher.AddLong(action.StartedTick);
        hasher.AddLong(action.ContactTick);
        hasher.AddLong(action.ActiveUntilTick);
        hasher.AddLong(action.EndsTick);
        hasher.AddTextSet(action.HitPlayers);
    }
}
