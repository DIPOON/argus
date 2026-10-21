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
        tests.Add(new TestCase("A room nobody connected to leaves no recording to push real ones out", UnconnectedRecordingDeleted));
        tests.Add(new TestCase("A room whose recording failed refuses bug reports instead of claiming to keep them", FailedRecordingRefusesReports));
        tests.Add(new TestCase("The recording check tells reproduced, unfinished, diverged, unreadable and other-version recordings apart", CheckOutcomes));
        tests.Add(new TestCase("A folder check fails on diverged or unreadable recordings, and with --strict also on unfinished or skipped ones", FolderVerdicts));
        tests.Add(new TestCase("Names and report notes keep whole characters and drop control characters", CleanTexts));
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
                    match.Deploy(player, match.Layout.Camp.X.ToDouble() + i * 30, match.Layout.Camp.Y.ToDouble());
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
                Check(room.Match.Deploy(player, room.Match.Layout.Camp.X.ToDouble(), room.Match.Layout.Camp.Y.ToDouble()));
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
                        // 공식 클라이언트처럼 버튼을 누른 순간 내용 없이 제보해 틱을 찍는다.
                        long? reported = host.Report(room, player, "", out string failure);
                        Check(reported.HasValue, failure);
                        reportTick = reported!.Value;
                        // 같은 대원은 5초 안에 다시 제보할 수 없다.
                        Check(host.Report(room, player, "또", out string again) == null, "곧바로 다시 낸 제보를 받았습니다");
                        Check(again.Length > 0, "제보를 거절한 이유가 없습니다");
                    }
                    if (tick == 450)
                    {
                        // 내용은 적는 데 걸린 시간만큼 뒤에 덧붙인다. 제보한 틱은 그대로다.
                        Check(host.ReportNote(room, player, reportTick, "  적이 벽을 지나간 것 같아요\n", out string failure), failure);
                        Check(!host.ReportNote(room, player, reportTick, "한 번 더", out string twice), "같은 제보에 내용을 두 번 덧붙였습니다");
                        Check(!host.ReportNote(room, player, reportTick + 1, "다른 틱", out string other), "없는 제보에 내용을 덧붙였습니다");
                        Check(twice.Length > 0 && other.Length > 0, "덧붙이기를 거절한 이유가 없습니다");
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
            // 제보는 버튼을 누른 틱으로 남고, 나중에 덧붙인 내용은 그 제보에 이어진다. 제어 문자와 앞뒤 공백은 빠진다.
            List<ReplayReport> reports = MatchReplay.Reports(content.Records);
            Equal(reports.Count, 1);
            Equal(reports[0].Tick, reportTick);
            Equal(reports[0].Player, player.Id);
            Equal(reports[0].Text, (string?)null);
            Equal(reports[0].Note, "적이 벽을 지나간 것 같아요");
            Equal(reports[0].NoteTick, (long?)450);
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
                // 한 번도 접속하지 않은 방의 기록은 닫을 때 지워지므로 접속 줄을 넣는다.
                file.Write(new MatchRecord(0, "connect"));
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

    private static void UnconnectedRecordingDeleted()
    {
        string folder = TemporaryFolder();
        try
        {
            RecordingStore store = new RecordingStore(folder, 200, NullLogger<RecordingStore>.Instance);
            using RoomHost host = new RoomHost(NullLogger<RoomHost>.Instance, store);
            // HTTP로 방만 만들고 접속하지 않은 방과, 접속한 방이다.
            Guest drifter = host.Create(new EntryRequest("drifter"))!;
            Guest pilot = host.Create(new EntryRequest("pilot"))!;
            Check(host.Attach(pilot.Room, pilot.Token) != null, "접속하지 못했습니다");
            host.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            // 접속이 없었던 방의 기록은 지워져 보관 자리를 차지하지 않는다.
            string[] files = Directory.GetFiles(folder, "*" + RecordingFormat.Extension);
            Equal(files.Length, 1);
            Check(files[0].Contains(pilot.Room), "접속한 방의 기록이 남지 않았습니다");
            Check(!files[0].Contains(drifter.Room), "접속하지 않은 방의 기록이 남았습니다");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static void FailedRecordingRefusesReports()
    {
        string folder = TemporaryFolder();
        try
        {
            // 없는 폴더에 쓰게 해 기록 파일을 만들지 못하게 한다. 디스크가 가득 찬 것과 같다.
            RecordingHeader header = new RecordingHeader();
            RecordingFile file = new RecordingFile(Path.Combine(folder, "missing", "FAIL01" + RecordingFormat.Extension),
                header, NullLogger.Instance, ignored => { });
            file.Completion.GetAwaiter().GetResult();
            Check(!file.Accepting, "쓰기에 실패한 기록이 아직 받고 있다고 합니다");
            Match match = new Match("FAIL01", new Rules { MaxEnemies = 0 }, 1);
            Room room = new Room(match, null);
            room.Recording = file;
            match.Recorder = file;
            Player player = match.Join("p", "pilot")!;
            match.Connect(player);
            using RoomHost host = new RoomHost(NullLogger<RoomHost>.Instance);
            // 남기지 못할 제보를 받았다고 답하면, 제보가 남았다고 믿은 사람이 그 기록을 찾지 못한다.
            Check(host.Report(room, player, "", out string failure) == null, "기록이 실패한 방이 제보를 받았습니다");
            Check(failure.Contains("기록을 남기지 못하고"), $"거절한 이유가 맞지 않습니다: {failure}");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // 검사용 기록 파일을 서버와 같은 형식(헤더 다음 줄마다 기록 하나, gzip)으로 직접 쓴다.
    private static void WriteRecording(string path, RecordingHeader header, List<MatchRecord> records)
    {
        using FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using System.IO.Compression.GZipStream zip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest);
        using StreamWriter writer = new StreamWriter(zip, new System.Text.UTF8Encoding(false));
        writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(header, RecordingFormat.Json));
        for (int i = 0; i < records.Count; i++)
        {
            writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(records[i], RecordingFormat.Json));
        }
    }

    private static RecordingHeader CopyHeader(RecordingHeader source, string version)
    {
        RecordingHeader header = new RecordingHeader();
        header.Version = version;
        header.Room = source.Room;
        header.Seed = source.Seed;
        header.Rules = source.Rules;
        return header;
    }

    private static void CheckOutcomes()
    {
        string folder = TemporaryFolder();
        try
        {
            Session session = Recorded();
            RecordingHeader current = CopyHeader(session.Header, RecordingFormat.CodeVersion);
            List<MatchRecord> unfinished = new List<MatchRecord>(session.Records);
            unfinished.RemoveAt(unfinished.Count - 1);
            Equal(session.Records[session.Records.Count - 1].Type, "end");
            List<MatchRecord> diverged = new List<MatchRecord>(session.Records);
            MatchRecord original = diverged[session.TamperIndex];
            MatchRecord changed = new MatchRecord(original.Tick, original.Type);
            changed.Player = original.Player;
            changed.Sequence = original.Sequence;
            changed.MoveX = -original.MoveX;
            changed.MoveY = -original.MoveY;
            changed.Aim = original.Aim;
            changed.Slot = original.Slot;
            diverged[session.TamperIndex] = changed;

            string complete = Path.Combine(folder, "complete" + RecordingFormat.Extension);
            string open = Path.Combine(folder, "open" + RecordingFormat.Extension);
            string bad = Path.Combine(folder, "diverged" + RecordingFormat.Extension);
            string older = Path.Combine(folder, "older" + RecordingFormat.Extension);
            string broken = Path.Combine(folder, "broken" + RecordingFormat.Extension);
            WriteRecording(complete, current, session.Records);
            WriteRecording(open, current, unfinished);
            WriteRecording(bad, current, diverged);
            WriteRecording(older, CopyHeader(session.Header, "0.0.0+other"), session.Records);
            File.WriteAllText(broken, "gzip가 아닌 파일");

            Equal(RecordingCheck.Check(complete).Outcome, CheckOutcome.Reproduced);
            // 끝 줄이 없는 기록은 요약값이 모두 맞아도 끝나지 않은 기록이다. CI(--strict)는 이것도 실패로 친다.
            CheckResult unfinishedCheck = RecordingCheck.Check(open);
            Equal(unfinishedCheck.Outcome, CheckOutcome.Incomplete);
            Check(unfinishedCheck.Reason.Contains("끝 줄 없음"), $"끝나지 않은 까닭이 맞지 않습니다: {unfinishedCheck.Reason}");
            Equal(RecordingCheck.Check(bad).Outcome, CheckOutcome.Diverged);
            Equal(RecordingCheck.Check(older).Outcome, CheckOutcome.Skipped);
            Equal(RecordingCheck.Check(broken).Outcome, CheckOutcome.Unreadable);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static void FolderVerdicts()
    {
        // 재현한 기록만 있으면 통과한다.
        CheckFolderVerdict(new CheckOutcome[] { CheckOutcome.Reproduced }, true, true);
        // 끝나지 않은 기록은 쓰는 중이거나 서버가 갑자기 꺼진 기록일 수 있어 평소에는 통과로 치지만, CI(--strict)에서는 실패다.
        CheckFolderVerdict(new CheckOutcome[] { CheckOutcome.Reproduced, CheckOutcome.Incomplete }, true, false);
        CheckFolderVerdict(new CheckOutcome[] { CheckOutcome.Incomplete }, true, false);
        // 다른 코드 버전이라 건너뛴 기록은 평소에는 넘어가지만, 같은 커밋으로 확인하는 CI에서는 실패다.
        // 대부분 건너뛰고 하나만 재현돼도 통과하면, 거의 비교하지 않았는데 결정론을 확인한 것처럼 보인다.
        CheckFolderVerdict(new CheckOutcome[] { CheckOutcome.Reproduced, CheckOutcome.Skipped, CheckOutcome.Skipped }, true, false);
        // 재현한 기록이 하나도 없으면 아무것도 확인하지 못했으므로 실패다.
        CheckFolderVerdict(new CheckOutcome[] { CheckOutcome.Skipped }, false, false);
        CheckFolderVerdict(Array.Empty<CheckOutcome>(), false, false);
        // 갈라졌거나 읽지 못한 기록은 언제나 실패다.
        CheckFolderVerdict(new CheckOutcome[] { CheckOutcome.Reproduced, CheckOutcome.Diverged }, false, false);
        CheckFolderVerdict(new CheckOutcome[] { CheckOutcome.Unreadable, CheckOutcome.Reproduced }, false, false);
    }

    // 같은 판정 목록을 평소(relaxed)와 엄격하게(strict) 볼 때의 통과 여부를 확인한다.
    private static void CheckFolderVerdict(CheckOutcome[] outcomes, bool relaxed, bool strict)
    {
        string list = string.Join(",", outcomes);
        Check(RecordingCheck.FolderPasses(outcomes, false) == relaxed, $"[{list}]를 평소대로 본 판정이 틀렸습니다");
        Check(RecordingCheck.FolderPasses(outcomes, true) == strict, $"[{list}]를 엄격하게 본 판정이 틀렸습니다");
    }

    private static void CleanTexts()
    {
        using RoomHost host = new RoomHost(NullLogger<RoomHost>.Instance);
        // 이름은 16단위까지다. 15글자 뒤의 이모지(2단위)는 반만 들어갈 수 없으므로 통째로 뺀다.
        string[] requested = new string[] { "ABCDEFGHIJKLMNO\U0001F600", "가\u0007나", "A\uD800B", "  \u0001  " };
        string[] expected = new string[] { "ABCDEFGHIJKLMNO", "가나", "AB", "대원" };
        for (int i = 0; i < requested.Length; i++)
        {
            Guest guest = host.Create(new EntryRequest(requested[i]))!;
            (Room room, Player player, Peer peer) = host.Attach(guest.Room, guest.Token)!.Value;
            Equal(player.Name, expected[i]);
        }

        // 제보 내용은 200단위까지다. 199글자 뒤의 이모지는 들어가지 않고, 198글자 뒤의 이모지는 들어간다.
        string folder = TemporaryFolder();
        try
        {
            RecordingStore store = new RecordingStore(folder, 200, NullLogger<RecordingStore>.Instance);
            using RoomHost recorded = new RoomHost(NullLogger<RoomHost>.Instance, store);
            Guest guest = recorded.Create(new EntryRequest("pilot"))!;
            (Room room, Player player, Peer peer) = recorded.Attach(guest.Room, guest.Token)!.Value;
            lock (room.Gate)
            {
                long tick = recorded.Report(room, player, "", out string failure)!.Value;
                Check(recorded.ReportNote(room, player, tick, new string('x', 199) + "\U0001F600", out failure), failure);
            }
            recorded.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            RecordingContent content = RecordingReader.Read(Directory.GetFiles(folder, "*" + RecordingFormat.Extension)[0]);
            List<ReplayReport> reports = MatchReplay.Reports(content.Records);
            Equal(reports[0].Note, new string('x', 199));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
