using System.Numerics;
using Argus.Server.Game;
using Argus.Server.Networking;
using Argus.Server.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using static TestTools;

internal static class RecordingTests
{
    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("A recorded session replays to the same state at every checkpoint and at the end", RecordedSessionReplays));
        tests.Add(new TestCase("Changing one recorded input makes the replay stop at the next checkpoint", TamperedRecordingStops));
        tests.Add(new TestCase("Replaying with another seed stops at the first checkpoint", WrongSeedStops));
        tests.Add(new TestCase("Replaying up to a tick leaves the room as it was at that tick", ReplayUntilTick));
        tests.Add(new TestCase("A room recorded to disk through RoomHost replays from its file, with its bug report", DiskRecording));
        tests.Add(new TestCase("Old recordings are pruned while reported ones are kept apart", Retention));
        tests.Add(new TestCase("A recording cut off by a crash still replays up to where it was written", TruncatedRecording));
    }

    // 기록을 메모리에 모은다. 서버에서는 파일에 쓰는 쪽이 같은 자리에 들어간다.
    private sealed class MemoryRecorder : IMatchRecorder
    {
        public List<MatchRecord> Records { get; } = new List<MatchRecord>();

        public void Write(MatchRecord record)
        {
            Records.Add(record);
        }
    }

    private sealed class Session
    {
        public RecordingHeader Header { get; } = new RecordingHeader();
        public List<MatchRecord> Records { get; } = new List<MatchRecord>();
        public string FinalHash = "";
        // 450틱의 요청까지 반영한 직후의 상태 요약값이다.
        public string HashAt450 = "";
        // 600틱 뒤 처음으로 받아들여진 첫 대원의 입력 줄 위치다. 변조 검사가 이 줄을 바꾼다.
        public int TamperIndex = -1;
    }

    private static Session? _session;

    // 기록은 한 번만 만들어 여러 검사가 함께 쓴다.
    private static Session Recorded()
    {
        if (_session == null)
        {
            _session = Record();
        }
        return _session;
    }

    // 대원 넷이 60초 동안 캠프 둘레를 돌며 싸운다. 중간에 한 명이 끊겼다가 다시 접속하고,
    // 장비를 바꾸고, 범위를 넘는 입력(거절됨)도 보낸다. 서버의 접속 처리와 같은 메서드만 쓴다.
    private static Session Record()
    {
        Session session = new Session();
        Rules rules = new Rules { AllowEliteEnemies = true, SpawnInterval = 3 };
        byte[] seed = SeededRandom.SeedFromNumber(73);
        Match match = new Match("REC001", rules, seed);
        MemoryRecorder recorder = new MemoryRecorder();
        match.Recorder = recorder;
        session.Header.Room = "REC001";
        session.Header.Seed = Convert.ToHexString(seed);
        session.Header.Rules = rules;
        session.Header.Version = "test";

        string[] ids = new string[] { "a", "b", "c", "d" };
        Player[] squad = new Player[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            squad[i] = match.Join(ids[i], "pilot" + i)!;
            match.AwaitConnection(squad[i]);
            match.Connect(squad[i]);
        }
        Check(match.SetLoadout(squad[1], new string[] { "strike", "parry", "grab", "grab" }), "장비를 바꾸지 못했습니다");

        long sequence = 0;
        for (int tick = 0; tick < 1800; tick++)
        {
            if (tick == 900)
            {
                match.Disconnect(squad[2]);
            }
            if (tick == 1200)
            {
                match.Connect(squad[2]);
            }
            for (int i = 0; i < squad.Length; i++)
            {
                Player player = squad[i];
                // 끊긴 대원은 입력을 보내지 않는다.
                if (!player.Connected)
                {
                    continue;
                }
                if (player.State == "waiting")
                {
                    match.Deploy(player, BattleMap.Camp.X + i * 30, BattleMap.Camp.Y);
                    continue;
                }
                double heading = tick * 0.05;
                double aim = tick * 0.1 + i;
                int slot = 0;
                if (tick % 8 == 0)
                {
                    slot = 1 + (tick / 8 + i) % 4;
                }
                sequence++;
                bool accepted = match.Input(player, sequence, Math.Cos(heading), Math.Sin(heading), aim, slot);
                if (accepted && i == 0 && tick >= 600 && session.TamperIndex < 0)
                {
                    session.TamperIndex = recorder.Records.Count - 1;
                }
            }
            if (tick == 300)
            {
                // 이동 값이 범위를 넘어 거절되는 입력이다. 거절되어도 기록하고, 다시 돌릴 때도 거절된다.
                Check(!match.Input(squad[0], sequence + 1000, 5, 0, 0, 0), "범위를 넘는 입력을 받아들였습니다");
            }
            if (tick == 450)
            {
                session.HashAt450 = match.StateHash();
            }
            match.Step();
        }
        MatchRecord end = new MatchRecord(match.Tick, "end");
        end.Text = "test";
        recorder.Write(end);
        session.FinalHash = match.StateHash();
        session.Records.AddRange(recorder.Records);
        Check(session.TamperIndex >= 0, "바꿀 입력 줄을 찾지 못했습니다");
        return session;
    }

    private static void RecordedSessionReplays()
    {
        Session session = Recorded();
        ReplayResult result = MatchReplay.Run(session.Header, session.Records);
        if (result.DivergedAt.HasValue)
        {
            throw new Exception($"{result.DivergedAt}틱에서 기록과 갈라졌습니다");
        }
        // 1800틱 동안 30틱마다 남긴 요약값 60개가 모두 같아야 한다.
        Equal(result.Checkpoints, 60);
        Check(result.Ended, "기록의 끝 줄까지 가지 못했습니다");
        Equal(result.EndReason, "test");
        Equal(result.Failures.Count, 0);
        Equal(result.Problems.Count, 0);
        Equal(result.Match.Tick, 1800L);
        Equal(result.Match.StateHash(), session.FinalHash);
    }

    private static void TamperedRecordingStops()
    {
        Session session = Recorded();
        MatchRecord original = session.Records[session.TamperIndex];
        Equal(original.Type, "input");
        // 이동 방향을 뒤집는다. 조준은 다음 입력이 곧바로 덮어써 상태가 원래대로 돌아올 수 있지만, 위치 차이는 계속 남는다.
        MatchRecord changed = new MatchRecord(original.Tick, original.Type);
        changed.Player = original.Player;
        changed.Sequence = original.Sequence;
        changed.MoveX = -original.MoveX;
        changed.MoveY = -original.MoveY;
        changed.Aim = original.Aim;
        changed.Slot = original.Slot;
        List<MatchRecord> tampered = new List<MatchRecord>(session.Records);
        tampered[session.TamperIndex] = changed;

        ReplayResult result = MatchReplay.Run(session.Header, tampered);
        // 바뀐 입력이 들어간 틱 다음의 첫 요약값에서 멈춘다. 같은 틱의 요약값은 그 틱의 요청보다 먼저 남긴 것이다.
        long expected = (original.Tick / Rules.TicksPerSecond + 1) * Rules.TicksPerSecond;
        Check(result.DivergedAt.HasValue, "바뀐 입력을 알아채지 못했습니다");
        Equal(result.DivergedAt!.Value, expected);
        Equal(result.Checkpoints, (int)(original.Tick / Rules.TicksPerSecond));
        Check(result.ActualHash != result.ExpectedHash, "갈라진 요약값이 같다고 나왔습니다");
    }

    private static void WrongSeedStops()
    {
        Session session = Recorded();
        RecordingHeader other = new RecordingHeader();
        other.Room = session.Header.Room;
        other.Rules = session.Header.Rules;
        other.Seed = Convert.ToHexString(SeededRandom.SeedFromNumber(74));
        ReplayResult result = MatchReplay.Run(other, session.Records);
        Equal(result.DivergedAt, (long?)Rules.TicksPerSecond);
        Equal(result.Checkpoints, 0);
    }

    private static void ReplayUntilTick()
    {
        Session session = Recorded();
        // 450틱을 진행하고 그 틱의 요청까지 반영한 순간에서 멈춘다.
        ReplayResult result = MatchReplay.Run(session.Header, session.Records, 450);
        Equal(result.Match.Tick, 450L);
        Equal(result.Match.StateHash(), session.HashAt450);
        Check(result.DivergedAt == null, "멈추기 전에 기록과 갈라졌습니다");
        Equal(result.Checkpoints, 15);
    }

    // 검사마다 따로 쓰는 임시 기록 폴더다. 끝나면 지운다.
    private static string TemporaryFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "argus-recordings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void DiskRecording()
    {
        string folder = TemporaryFolder();
        try
        {
            RecordingStore store = new RecordingStore(folder, 200, NullLogger<RecordingStore>.Instance);
            using RoomHost host = new RoomHost(NullLogger<RoomHost>.Instance, store);
            Guest guest = host.Create(new EntryRequest("pilot", new string[] { "strike", "parry", "grab", "grab" }))!;
            (Room room, Player player, Peer peer) = host.Attach(guest.Room, guest.Token)!.Value;
            lock (room.Gate)
            {
                Check(room.Match.Deploy(player, BattleMap.Camp.X, BattleMap.Camp.Y));
            }
            // 투입을 기다렸다가 20초 동안 걸으며 기술을 쓴다. 서버처럼 틱 사이에 잠금을 잡고 입력을 넣는다.
            long sequence = 0;
            long reportTick = -1;
            for (int tick = 0; tick < 750; tick++)
            {
                lock (room.Gate)
                {
                    if (player.State == "alive")
                    {
                        sequence++;
                        double heading = tick * 0.04;
                        int slot = 0;
                        if (tick % 10 == 0)
                        {
                            slot = 1 + tick / 10 % 4;
                        }
                        room.Match.Input(player, sequence, Math.Cos(heading), Math.Sin(heading), heading, slot);
                    }
                    if (tick == 400)
                    {
                        long? reported = host.Report(room, player, "  적이 벽을 지나간 것 같아요\n", out string failure);
                        Check(reported.HasValue, failure);
                        reportTick = reported!.Value;
                        // 같은 대원은 5초 안에 다시 제보할 수 없다.
                        Check(host.Report(room, player, "또", out string again) == null, "곧바로 다시 낸 제보를 받았습니다");
                        Check(again.Length > 0, "제보를 거절한 이유가 없습니다");
                    }
                }
                host.StepRoom(room.Match.Code, room, 1, false);
            }
            host.Detach(room, player, peer);
            // 서버를 끄면 기록이 끝 줄과 함께 닫히고 다 쓰일 때까지 기다린다.
            host.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            string liveHash;
            lock (room.Gate)
            {
                liveHash = room.Match.StateHash();
            }

            string[] files = Directory.GetFiles(folder, "*" + RecordingFormat.Extension);
            Equal(files.Length, 1);
            Check(File.Exists(files[0] + RecordingFormat.ReportedSuffix), "제보가 들어온 기록에 표시 파일이 없습니다");
            RecordingContent content = RecordingReader.Read(files[0]);
            Check(!content.Truncated, "닫힌 기록이 잘려 있습니다");
            Equal(content.Header.Room, guest.Room);
            Equal(content.Header.Seed.Length, 64);
            Equal(content.Header.Version, RecordingFormat.CodeVersion);
            string[] expected = new string[] { "join", "loadout", "await", "connect", "deploy" };
            for (int i = 0; i < expected.Length; i++)
            {
                Equal(content.Records[i].Type, expected[i]);
            }

            ReplayResult result = MatchReplay.Run(content.Header, content.Records);
            if (result.DivergedAt.HasValue)
            {
                throw new Exception($"파일에서 다시 돌리자 {result.DivergedAt}틱에서 갈라졌습니다");
            }
            Equal(result.Checkpoints, 25);
            Check(result.Ended, "기록의 끝 줄까지 가지 못했습니다");
            Equal(result.EndReason, "shutdown");
            Equal(result.Problems.Count, 0);
            Equal(result.Match.StateHash(), liveHash);
            // 제보는 지금 틱과 함께 남고, 제어 문자와 앞뒤 공백은 빠진다.
            Equal(result.Reports.Count, 1);
            Equal(result.Reports[0].Tick, reportTick);
            Equal(result.Reports[0].Player, player.Id);
            Equal(result.Reports[0].Text, "적이 벽을 지나간 것 같아요");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static void Retention()
    {
        string folder = TemporaryFolder();
        try
        {
            // 일반 기록은 최근 3개, 제보된 기록은 따로 최근 3개까지 남긴다.
            RecordingStore store = new RecordingStore(folder, 3, NullLogger<RecordingStore>.Instance);
            byte[] seed = SeededRandom.SeedFromNumber(1);
            string[] paths = new string[6];
            for (int i = 0; i < paths.Length; i++)
            {
                RecordingFile file = store.Open("ROOM" + i, seed, new Rules())!;
                paths[i] = file.Path;
                if (i == 1)
                {
                    MatchRecord report = new MatchRecord(0, "report");
                    report.Text = "keep";
                    file.Write(report);
                }
                file.Close(0, "test");
                file.Completion.GetAwaiter().GetResult();
            }
            // 일반 기록 다섯(0, 2, 3, 4, 5) 가운데 오래된 둘은 지워지고, 제보된 1번은 남는다.
            Check(!File.Exists(paths[0]) && !File.Exists(paths[2]), "오래된 일반 기록이 남아 있습니다");
            Check(File.Exists(paths[3]) && File.Exists(paths[4]) && File.Exists(paths[5]), "최근 일반 기록이 지워졌습니다");
            Check(File.Exists(paths[1]) && File.Exists(paths[1] + RecordingFormat.ReportedSuffix), "제보된 기록이 일반 기록에 밀려 지워졌습니다");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static void TruncatedRecording()
    {
        string folder = TemporaryFolder();
        try
        {
            // 기록한 세션을 파일로 다 쓴 뒤, 서버가 갑자기 꺼진 것처럼 파일 끝을 잘라 낸다.
            Session session = Recorded();
            RecordingStore store = new RecordingStore(folder, 200, NullLogger<RecordingStore>.Instance);
            RecordingFile file = store.Open(session.Header.Room, Convert.FromHexString(session.Header.Seed), session.Header.Rules)!;
            for (int i = 0; i < session.Records.Count; i++)
            {
                if (session.Records[i].Type != "end")
                {
                    file.Write(session.Records[i]);
                }
            }
            file.Close(session.Records[session.Records.Count - 1].Tick, "test");
            file.Completion.GetAwaiter().GetResult();
            using (FileStream stream = new FileStream(file.Path, FileMode.Open, FileAccess.Write))
            {
                stream.SetLength(stream.Length * 2 / 3);
            }

            RecordingContent content = RecordingReader.Read(file.Path);
            Check(content.Records.Count > 0 && content.Records.Count < session.Records.Count, $"잘린 파일에서 {content.Records.Count}줄을 읽었습니다");
            ReplayResult result = MatchReplay.Run(content.Header, content.Records);
            Check(result.DivergedAt == null, "잘린 부분까지는 기록과 같아야 합니다");
            Check(result.Checkpoints > 0, "잘린 파일에서 요약값을 하나도 확인하지 못했습니다");
            Check(!result.Ended, "잘린 기록에 끝 줄이 있습니다");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
