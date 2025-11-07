using System.Text;
using MelonLoader;
using Net.VO;
using Net.VO.Mai2;
using Net.Packet.Mai2;
using HarmonyLib;
using Net.Packet;

[assembly: MelonInfo(typeof(MaimaiTournamentMod.MaimaiTournamentMod), "MaimaiTournamentMod", "1.0.0", "Eternal973 & DeepSeek-V3.2-Exp")]
[assembly: MelonGame("sega-interactive", "Sinmai")]

namespace MaimaiTournamentMod
{
    public class MaimaiTournamentMod : MelonMod
    {
        private static TournamentConfig _config;

        public override void OnInitializeMelon()
        {
            _config = new TournamentConfig();
            _config.Load();
            HarmonyInstance.PatchAll(typeof(TournamentPatches));
            HarmonyInstance.PatchAll(typeof(PacketUpsertUserAllPatch));
            LoggerInstance.Msg("本Mod已开源，仅供研究学习使用，禁止任何商业行为，数据均在本地处理并存储，与任何服务器无关，如有冲突请手动禁用此Mod。");
            LoggerInstance.Msg("Tournament Mod loaded!");
        }

        [HarmonyPatch]
        public class TournamentPatches
        {
            [HarmonyPatch(typeof(PacketGetGameTournamentInfo), "Proc")]
            [HarmonyPostfix]
            public static void Proc_Postfix(PacketGetGameTournamentInfo instance, ref PacketState result)
            {
                try
                {
                    if (!_config.Enabled) return;

                    var onDoneField = AccessTools.Field(typeof(PacketGetGameTournamentInfo), "_onDone");
                    var onErrorField = AccessTools.Field(typeof(PacketGetGameTournamentInfo), "_onError");

                    var onDone = (Action<GameTournamentInfo[]>)onDoneField.GetValue(instance);
                    var onError = (Action<PacketStatus>)onErrorField.GetValue(instance);

                    if (result == PacketState.Done)
                    {
                        var customData = GetCustomTournamentData();
                        if (_config.DebugLog)
                        {
                            PrintJsonResponse(customData);
                        }
                        onDone(customData.gameTournamentInfoList ?? Array.Empty<GameTournamentInfo>());
                        MelonLogger.Msg("Custom tournament data injected!");
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Error($"Error in tournament patch: {e}");
                }
            }

            [HarmonyPatch(typeof(PacketGetUserScoreRanking), "Proc")]
            [HarmonyPrefix]
            public static bool ProcUserScoreRanking_Prefix(PacketGetUserScoreRanking instance, ref PacketState result)
            {
                try
                {
                    if (!_config.EnableScoreRanking) return true;

                    var onDoneField = AccessTools.Field(typeof(PacketGetUserScoreRanking), "_onDone");
                    var responseVOsField = AccessTools.Field(typeof(PacketGetUserScoreRanking), "_responseVOs");
                    var rankingIdsField = AccessTools.Field(typeof(PacketGetUserScoreRanking), "_rankingIds");
                    var listIndexField = AccessTools.Field(typeof(PacketGetUserScoreRanking), "_listIndex");
                    var listLengthField = AccessTools.Field(typeof(PacketGetUserScoreRanking), "_listLength");

                    var onDone = (Action<UserScoreRanking[]>)onDoneField.GetValue(instance);
                    var responseVOs = (List<UserScoreRankingResponseVO>)responseVOsField.GetValue(instance);
                    var rankingIds = (List<int>)rankingIdsField.GetValue(instance);
                    var listIndex = (int)listIndexField.GetValue(instance);
                    var listLength = (int)listLengthField.GetValue(instance);

                    if (listLength <= 0 || rankingIds == null || rankingIds.Count == 0)
                        return true;

                    var query = instance.Query as NetQuery<UserScoreRankingRequestVO, UserScoreRankingResponseVO>;
                    if (query == null) return true;

                    var request = query.Request;
                    var userId = request.userId;

                    bool alreadyProcessed = responseVOs.Any(vo => vo.userId == userId);

                    if (!alreadyProcessed)
                    {
                        var customRankings = new List<UserScoreRanking>();

                        foreach (var competitionId in rankingIds)
                        {
                            var rankingData = _config.GetUserScoreRanking(userId, competitionId);
                            if (rankingData.tournamentId != 0)
                            {
                                customRankings.Add(rankingData);
                            }
                        }

                        if (customRankings.Count > 0)
                        {
                            responseVOs.RemoveAll(vo => vo.userId == userId);

                            foreach (var ranking in customRankings)
                            {
                                responseVOs.Add(new UserScoreRankingResponseVO
                                {
                                    userId = userId,
                                    userScoreRanking = ranking
                                });
                            }

                            listIndexField.SetValue(instance, listLength);

                            var queryStateField = AccessTools.Field(query.GetType(), "_state");
                            if (queryStateField != null)
                            {
                                queryStateField.SetValue(query, 2);
                            }

                            if (_config.DebugLog)
                            {
                                PrintUserScoreRankingJsonResponse(userId, customRankings.ToArray());
                            }
                            MelonLogger.Msg($"Custom user score ranking data injected for user {userId}! Found {customRankings.Count} rankings");

                            return true;
                        }
                        else
                        {
                            MelonLogger.Msg($"No custom ranking data found for user {userId}");
                        }
                    }
                    else
                    {
                        if (_config.DebugLog)
                        {
                            MelonLogger.Msg($"User {userId} ranking data already processed, skipping injection");
                        }
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Error($"Error in user score ranking patch: {e}");
                }

                return true;
            }

            private static GameTournamentInfoResponseVO GetCustomTournamentData()
            {
                return new GameTournamentInfoResponseVO
                {
                    length = 1,
                    gameTournamentInfoList = _config.CreateTournamentInfoList()
                };
            }

            private static void PrintJsonResponse(GameTournamentInfoResponseVO response)
            {
                try
                {
                    StringBuilder jsonBuilder = new StringBuilder();
                    jsonBuilder.Append("{\"length\":");
                    jsonBuilder.Append(response.length);
                    jsonBuilder.Append(",\"gameTournamentInfoList\":");

                    if (response.gameTournamentInfoList == null || response.gameTournamentInfoList.Length == 0)
                    {
                        jsonBuilder.Append("[]");
                    }
                    else
                    {
                        jsonBuilder.Append("[");
                        for (int i = 0; i < response.gameTournamentInfoList.Length; i++)
                        {
                            if (i > 0) jsonBuilder.Append(",");
                            AppendTournamentJson(jsonBuilder, response.gameTournamentInfoList[i]);
                        }
                        jsonBuilder.Append("]");
                    }
                    jsonBuilder.Append("}");

                    MelonLogger.Msg($"GetGameTournamentInfoApi Response: {jsonBuilder}");
                }
                catch (Exception e)
                {
                    MelonLogger.Error($"Failed to print JSON response: {e}");
                }
            }

            private static void PrintUserScoreRankingJsonResponse(ulong userId, UserScoreRanking[] rankings)
            {
                try
                {
                    StringBuilder jsonBuilder = new StringBuilder();
                    jsonBuilder.Append("{\"userId\":");
                    jsonBuilder.Append(userId);
                    jsonBuilder.Append(",\"userScoreRanking\":");

                    if (rankings == null || rankings.Length == 0)
                    {
                        jsonBuilder.Append("[]");
                    }
                    else
                    {
                        jsonBuilder.Append("[");
                        for (int i = 0; i < rankings.Length; i++)
                        {
                            if (i > 0) jsonBuilder.Append(",");
                            AppendUserScoreRankingJson(jsonBuilder, rankings[i]);
                        }
                        jsonBuilder.Append("]");
                    }

                    jsonBuilder.Append("}");

                    MelonLogger.Msg($"GetUserScoreRankingApi Final Response: {jsonBuilder}");
                }
                catch (Exception e)
                {
                    MelonLogger.Error($"Failed to print user score ranking JSON response: {e}");
                }
            }

            private static void AppendUserScoreRankingJson(StringBuilder sb, UserScoreRanking ranking)
            {
                sb.Append("{");
                sb.Append($"\"tournamentId\":{ranking.tournamentId},");
                sb.Append($"\"totalScore\":{ranking.totalScore},");
                sb.Append($"\"ranking\":{ranking.ranking},");
                sb.Append($"\"rankingDate\":\"{EscapeJsonString(ranking.rankingDate)}\"");
                sb.Append("}");
            }

            private static void AppendTournamentJson(StringBuilder sb, GameTournamentInfo tournament)
            {
                sb.Append("{");
                sb.Append($"\"tournamentId\":{tournament.tournamentId},");
                sb.Append($"\"tournamentName\":\"{EscapeJsonString(tournament.tournamentName)}\",");
                sb.Append($"\"rankingKind\":{tournament.rankingKind},");
                sb.Append($"\"scoreType\":{tournament.scoreType},");
                sb.Append($"\"noticeStartDate\":\"{tournament.noticeStartDate}\",");
                sb.Append($"\"noticeEndDate\":\"{tournament.noticeEndDate}\",");
                sb.Append($"\"startDate\":\"{tournament.startDate}\",");
                sb.Append($"\"endDate\":\"{tournament.endDate}\",");
                sb.Append($"\"entryStartDate\":\"{tournament.entryStartDate}\",");
                sb.Append($"\"entryEndDate\":\"{tournament.entryEndDate}\",");
                sb.Append($"\"gameTournamentMusicList\":");

                if (tournament.gameTournamentMusicList == null || tournament.gameTournamentMusicList.Length == 0)
                {
                    sb.Append("[]");
                }
                else
                {
                    sb.Append("[");
                    for (int i = 0; i < tournament.gameTournamentMusicList.Length; i++)
                    {
                        if (i > 0) sb.Append(",");
                        AppendMusicJson(sb, tournament.gameTournamentMusicList[i]);
                    }
                    sb.Append("]");
                }
                sb.Append("}");
            }

            private static void AppendMusicJson(StringBuilder sb, GameTournamentMusic music)
            {
                sb.Append("{");
                sb.Append($"\"tournamentId\":{music.tournamentId},");
                sb.Append($"\"musicId\":{music.musicId},");
                sb.Append($"\"level\":{music.level},");
                sb.Append($"\"isFirstLock\":{(music.isFirstLock ? "true" : "false")}");
                sb.Append("}");
            }

            private static string EscapeJsonString(string str)
            {
                if (string.IsNullOrEmpty(str)) return "";

                StringBuilder sb = new StringBuilder();
                foreach (char c in str)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < ' ')
                            {
                                sb.AppendFormat("\\u{0:X4}", (int)c);
                            }
                            else
                            {
                                sb.Append(c);
                            }
                            break;
                    }
                }
                return sb.ToString();
            }
        }

        [HarmonyPatch(typeof(PacketUpsertUserAll))]
        public class PacketUpsertUserAllPatch
        {
            [HarmonyPatch("Proc")]
            [HarmonyPostfix]
            public static void Proc_Postfix(PacketUpsertUserAll instance, ref PacketState result)
            {
                try
                {
                    if (!_config.Enabled || !_config.EnableScoreRanking)
                        return;

                    if (result != PacketState.Done)
                        return;

                    var query = instance.Query as NetQuery<UserAllRequestVO, UpsertResponseVO>;
                    if (query == null) return;

                    var request = query.Request;
                    ProcessTournamentScore(request);
                }
                catch (Exception e)
                {
                    MelonLogger.Error($"Error in UpsertUserAll patch: {e}");
                }
            }

            private static void ProcessTournamentScore(UserAllRequestVO request)
            {
                if (request.upsertUserAll.userGamePlaylogList == null ||
                    request.upsertUserAll.userGamePlaylogList.Length == 0)
                    return;

                var latestPlaylog = request.upsertUserAll.userGamePlaylogList
                    .OrderByDescending(p => p.playlogId)
                    .FirstOrDefault();

                if (latestPlaylog.playTrack < 3)
                    return;

                var musicIds = ParseIntArray(_config.MusicIds);
                if (musicIds.Length < 3)
                    return;

                if (request.upsertUserAll.userMusicDetailList == null)
                    return;

                bool hasAllSongs = musicIds.All(musicId =>
                    request.upsertUserAll.userMusicDetailList.Any(detail => detail.musicId == musicId));

                if (!hasAllSongs)
                    return;

                string userName = "Unknown";
                if (request.upsertUserAll.userData != null && request.upsertUserAll.userData.Length > 0)
                {
                    var userDetail = request.upsertUserAll.userData[0];
                    userName = userDetail.userName ?? "Unknown";
                }

                var songScores = new List<SongScore>();

                for (int i = 0; i < 3; i++)
                {
                    if (i >= musicIds.Length) break;

                    var musicId = musicIds[i];
                    var bestScore = request.upsertUserAll.userMusicDetailList
                        .Where(detail => detail.musicId == musicId)
                        .OrderByDescending(detail => detail.achievement)
                        .FirstOrDefault();

                    if (bestScore.musicId != 0)
                    {
                        songScores.Add(new SongScore
                        {
                            MusicId = musicId,
                            Level = (int)bestScore.level,
                            Achievement = bestScore.achievement,
                            DeluxScore = bestScore.deluxscoreMax
                        });
                    }
                }

                if (songScores.Count != 3)
                    return;

                var record = new ScoreRankingRecord
                {
                    UserId = request.userId,
                    UserName = userName,
                    TournamentId = _config.TournamentId,
                    Level01 = songScores[0].Level,
                    Score01 = (int)songScores[0].Achievement,
                    ScoreDx01 = (int)songScores[0].DeluxScore,
                    Level02 = songScores[1].Level,
                    Score02 = (int)songScores[1].Achievement,
                    ScoreDx02 = (int)songScores[1].DeluxScore,
                    Level03 = songScores[2].Level,
                    Score03 = (int)songScores[2].Achievement,
                    ScoreDx03 = (int)songScores[2].DeluxScore,
                    PlayDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    IsBestScore = 0,
                    Ranking = 0,
                    RankingDate = ""
                };

                CalculateTotalScores(ref record);
                SaveAndUpdateRankings(record);
            }

            private static void CalculateTotalScores(ref ScoreRankingRecord record)
            {
                record.TotalDxScore = record.ScoreDx01 + record.ScoreDx02 + record.ScoreDx03;

                int baseTotalScore = record.Score01 + record.Score02 + record.Score03;

                int penalty = 0;
                int[] levels = { record.Level01, record.Level02, record.Level03 };

                foreach (int level in levels)
                {
                    switch (level)
                    {
                        case 0: penalty += 30000; break;
                        case 1: penalty += 20000; break;
                        case 2: penalty += 10000; break;
                    }
                }

                record.TotalScore = baseTotalScore - penalty;
            }

            private static void SaveAndUpdateRankings(ScoreRankingRecord newRecord)
            {
                List<ScoreRankingRecord> allRecords = new List<ScoreRankingRecord>();

                if (File.Exists(_config.ScoreRankingCsvPath))
                {
                    try
                    {
                        var lines = File.ReadAllLines(_config.ScoreRankingCsvPath);
                        for (int i = 1; i < lines.Length; i++)
                        {
                            var line = lines[i];
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            var parts = line.Split(',');
                            if (parts.Length >= 18)
                            {
                                var record = ParseRecordFromCsv(parts);
                                allRecords.Add(record);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Error($"Failed to read CSV: {e}");
                    }
                }

                allRecords.Add(newRecord);

                var uniqueRecords = allRecords
                    .GroupBy(r => new { r.UserId, r.TotalScore, r.TotalDxScore })
                    .Select(g => g.OrderBy(r => DateTime.Parse(r.PlayDate)).First())
                    .ToList();

                var userGroups = uniqueRecords
                    .GroupBy(r => r.UserId)
                    .SelectMany(g =>
                    {
                        var userRecords = g.OrderByDescending(r => r.TotalScore)
                                          .ThenByDescending(r => r.TotalDxScore)
                                          .ToList();

                        for (int i = 0; i < userRecords.Count; i++)
                        {
                            userRecords[i].IsBestScore = (i == 0) ? 1 : 0;
                        }
                        return userRecords;
                    })
                    .ToList();

                var bestRecords = userGroups.Where(r => r.IsBestScore == 1).ToList();
                var rankedBestRecords = bestRecords
                    .OrderByDescending(r => r.TotalScore)
                    .ThenByDescending(r => r.TotalDxScore)
                    .Select((r, index) =>
                    {
                        r.Ranking = index + 1;
                        r.RankingDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                        return r;
                    })
                    .ToList();

                foreach (var record in userGroups)
                {
                    if (record.IsBestScore == 1)
                    {
                        var bestRecord = rankedBestRecords.FirstOrDefault(r =>
                            r.UserId == record.UserId &&
                            r.TotalScore == record.TotalScore &&
                            r.TotalDxScore == record.TotalDxScore);
                        if (bestRecord != null)
                        {
                            record.Ranking = bestRecord.Ranking;
                            record.RankingDate = bestRecord.RankingDate;
                        }
                    }
                    else
                    {
                        record.Ranking = 0;
                        record.RankingDate = "";
                    }
                }

                SaveRecordsToCsv(userGroups);

                MelonLogger.Msg($"Updated tournament rankings. Total records: {userGroups.Count}, Participants: {rankedBestRecords.Count}");
            }

            private static ScoreRankingRecord ParseRecordFromCsv(string[] parts)
            {
                return new ScoreRankingRecord
                {
                    UserId = ulong.TryParse(parts[0], out ulong uid) ? uid : 0,
                    UserName = parts[1],
                    TournamentId = int.TryParse(parts[2], out int tid) ? tid : 0,
                    Level01 = int.TryParse(parts[3], out int l1) ? l1 : 0,
                    Score01 = int.TryParse(parts[4], out int s1) ? s1 : 0,
                    ScoreDx01 = int.TryParse(parts[5], out int dx1) ? dx1 : 0,
                    Level02 = int.TryParse(parts[6], out int l2) ? l2 : 0,
                    Score02 = int.TryParse(parts[7], out int s2) ? s2 : 0,
                    ScoreDx02 = int.TryParse(parts[8], out int dx2) ? dx2 : 0,
                    Level03 = int.TryParse(parts[9], out int l3) ? l3 : 0,
                    Score03 = int.TryParse(parts[10], out int s3) ? s3 : 0,
                    ScoreDx03 = int.TryParse(parts[11], out int dx3) ? dx3 : 0,
                    TotalScore = long.TryParse(parts[12], out long ts) ? ts : 0,
                    TotalDxScore = int.TryParse(parts[13], out int tdx) ? tdx : 0,
                    PlayDate = parts[14],
                    IsBestScore = int.TryParse(parts[15], out int best) ? best : 0,
                    Ranking = int.TryParse(parts[16], out int rank) ? rank : 0,
                    RankingDate = parts[17]
                };
            }

            private static void SaveRecordsToCsv(List<ScoreRankingRecord> records)
            {
                try
                {
                    var directory = Path.GetDirectoryName(_config.ScoreRankingCsvPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    var lines = new List<string>
                    {
                        "userId,userName,tournamentId,level01,score01,scoreDX01,level02,score02,scoreDX02,level03,score03,scoreDX03,totalScore,totalDXScore,playDate,isBestScore,ranking,rankingDate"
                    };

                    var sortedRecords = records
                        .OrderBy(r => r.UserId)
                        .ThenByDescending(r => r.TotalScore)
                        .ThenByDescending(r => r.TotalDxScore)
                        .ToList();

                    foreach (var record in sortedRecords)
                    {
                        var line = $"{record.UserId},{EscapeCsv(record.UserName)},{record.TournamentId}," +
                                   $"{record.Level01},{record.Score01},{record.ScoreDx01}," +
                                   $"{record.Level02},{record.Score02},{record.ScoreDx02}," +
                                   $"{record.Level03},{record.Score03},{record.ScoreDx03}," +
                                   $"{record.TotalScore},{record.TotalDxScore},{EscapeCsv(record.PlayDate)}," +
                                   $"{record.IsBestScore},{record.Ranking},{EscapeCsv(record.RankingDate)}";
                        lines.Add(line);
                    }

                    File.WriteAllLines(_config.ScoreRankingCsvPath, lines);
                }
                catch (Exception e)
                {
                    MelonLogger.Error($"Failed to save CSV: {e}");
                }
            }

            private static string EscapeCsv(string field)
            {
                if (string.IsNullOrEmpty(field)) return "";

                if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
                {
                    return "\"" + field.Replace("\"", "\"\"") + "\"";
                }
                return field;
            }

            private static int[] ParseIntArray(string input)
            {
                if (string.IsNullOrEmpty(input))
                    return Array.Empty<int>();

                return input.Split(',')
                    .Select(part => int.TryParse(part.Trim(), out int value) ? value : 0)
                    .Where(value => value != 0)
                    .ToArray();
            }
        }

        internal class SongScore
        {
            public int MusicId { get; set; }
            public int Level { get; set; }
            public uint Achievement { get; set; }
            public uint DeluxScore { get; set; }
        }
    }

    public class ScoreRankingRecord
    {
        public ulong UserId { get; set; }
        public string UserName { get; set; }
        public int TournamentId { get; set; }
        public int Level01 { get; set; }
        public int Score01 { get; set; }
        public int ScoreDx01 { get; set; }
        public int Level02 { get; set; }
        public int Score02 { get; set; }
        public int ScoreDx02 { get; set; }
        public int Level03 { get; set; }
        public int Score03 { get; set; }
        public int ScoreDx03 { get; set; }
        public long TotalScore { get; set; }
        public int TotalDxScore { get; set; }
        public string PlayDate { get; set; }
        public int IsBestScore { get; set; }
        public int Ranking { get; set; }
        public string RankingDate { get; set; }
    }

    public class TournamentConfig
    {
        public bool Enabled { get; set; }
        public bool DebugLog { get; set; }
        public int TournamentId { get; set; }
        public string TournamentName { get; set; }
        public int RankingKind { get; set; }
        public string NoticeStartDate { get; set; } = "1970-01-01 00:00:00";
        public string NoticeEndDate { get; set; } = "1970-01-01 00:00:00";
        public string StartDate { get; set; } = "1970-01-01 00:00:00";
        public string EndDate { get; set; } = "1970-01-01 00:00:00";
        public string EntryStartDate { get; set; } = "1970-01-01 00:00:00";
        public string EntryEndDate { get; set; } = "1970-01-01 00:00:00";
        public string MusicIds { get; set; }
        public string LockedMusicIds { get; set; }
        public bool EnableScoreRanking { get; set; }
        public string ScoreRankingCsvPath { get; set; } = "UserData/ScoreRanking.csv";

        private List<ScoreRankingRecord> _scoreRankingRecords = new List<ScoreRankingRecord>();

        public void Load()
        {
            var category = MelonPreferences.CreateCategory("TournamentMod", "Tournament Mod Settings");

            var enabled = category.CreateEntry("Enabled", Enabled, "Enable custom tournament data");
            var debugLog = category.CreateEntry("DebugLog", DebugLog, "Enable debug logging (shows API responses)");
            var tournamentId = category.CreateEntry("TournamentId", TournamentId, "Tournament ID");
            var tournamentName = category.CreateEntry("TournamentName", TournamentName, "Tournament Name");
            var rankingKind = category.CreateEntry("RankingKind", RankingKind, "Ranking Kind");
            var noticeStartDate = category.CreateEntry("NoticeStartDate", NoticeStartDate, "Notice Start Date (yyyy-MM-dd HH:mm:ss)");
            var noticeEndDate = category.CreateEntry("NoticeEndDate", NoticeEndDate, "Notice End Date (yyyy-MM-dd HH:mm:ss)");
            var startDate = category.CreateEntry("StartDate", StartDate, "Start Date (yyyy-MM-dd HH:mm:ss)");
            var endDate = category.CreateEntry("EndDate", EndDate, "End Date (yyyy-MM-dd HH:mm:ss)");
            var entryStartDate = category.CreateEntry("EntryStartDate", EntryStartDate, "Entry Start Date (yyyy-MM-dd HH:mm:ss)");
            var entryEndDate = category.CreateEntry("EntryEndDate", EntryEndDate, "Entry End Date (yyyy-MM-dd HH:mm:ss)");
            var musicIds = category.CreateEntry("MusicIds", MusicIds, "Music IDs (comma separated)");
            var lockedMusicIds = category.CreateEntry("LockedMusicIds", LockedMusicIds, "Locked Music IDs (comma separated)");
            var enableScoreRanking = category.CreateEntry("EnableScoreRanking", EnableScoreRanking, "Enable custom score ranking data");
            var scoreRankingCsvPath = category.CreateEntry("ScoreRankingCsvPath", ScoreRankingCsvPath, "Score ranking CSV file path");

            Enabled = enabled.Value;
            DebugLog = debugLog.Value;
            TournamentId = tournamentId.Value;
            TournamentName = tournamentName.Value;
            RankingKind = rankingKind.Value;
            NoticeStartDate = noticeStartDate.Value;
            NoticeEndDate = noticeEndDate.Value;
            StartDate = startDate.Value;
            EndDate = endDate.Value;
            EntryStartDate = entryStartDate.Value;
            EntryEndDate = entryEndDate.Value;
            MusicIds = musicIds.Value;
            LockedMusicIds = lockedMusicIds.Value;
            EnableScoreRanking = enableScoreRanking.Value;
            ScoreRankingCsvPath = scoreRankingCsvPath.Value;

            if (EnableScoreRanking)
            {
                LoadScoreRankingCsv();
            }

            MelonPreferences.Save();
        }

        private void LoadScoreRankingCsv()
        {
            try
            {
                if (!File.Exists(ScoreRankingCsvPath))
                {
                    MelonLogger.Msg($"Score ranking CSV file not found: {ScoreRankingCsvPath}");
                    return;
                }

                var lines = File.ReadAllLines(ScoreRankingCsvPath);
                if (lines.Length < 2)
                {
                    MelonLogger.Msg("Score ranking CSV file is empty or has no data rows");
                    return;
                }

                _scoreRankingRecords.Clear();

                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split(',');
                    if (parts.Length >= 18)
                    {
                        var record = new ScoreRankingRecord
                        {
                            UserId = ulong.TryParse(parts[0], out ulong uid) ? uid : 0,
                            UserName = parts[1],
                            TournamentId = int.TryParse(parts[2], out int tid) ? tid : 0,
                            Level01 = int.TryParse(parts[3], out int l1) ? l1 : 0,
                            Score01 = int.TryParse(parts[4], out int s1) ? s1 : 0,
                            ScoreDx01 = int.TryParse(parts[5], out int dx1) ? dx1 : 0,
                            Level02 = int.TryParse(parts[6], out int l2) ? l2 : 0,
                            Score02 = int.TryParse(parts[7], out int s2) ? s2 : 0,
                            ScoreDx02 = int.TryParse(parts[8], out int dx2) ? dx2 : 0,
                            Level03 = int.TryParse(parts[9], out int l3) ? l3 : 0,
                            Score03 = int.TryParse(parts[10], out int s3) ? s3 : 0,
                            ScoreDx03 = int.TryParse(parts[11], out int dx3) ? dx3 : 0,
                            TotalScore = long.TryParse(parts[12], out long ts) ? ts : 0,
                            TotalDxScore = int.TryParse(parts[13], out int tdx) ? tdx : 0,
                            PlayDate = parts[14],
                            IsBestScore = int.TryParse(parts[15], out int best) ? best : 0,
                            Ranking = int.TryParse(parts[16], out int rank) ? rank : 0,
                            RankingDate = parts[17]
                        };

                        _scoreRankingRecords.Add(record);
                    }
                }

                MelonLogger.Msg($"Loaded {_scoreRankingRecords.Count} score ranking records from {ScoreRankingCsvPath}");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Failed to load score ranking CSV: {e}");
            }
        }

        public UserScoreRanking GetUserScoreRanking(ulong userId, int competitionId)
        {
            if (!EnableScoreRanking || _scoreRankingRecords.Count == 0)
            {
                return new UserScoreRanking
                {
                    tournamentId = 0,
                    totalScore = 0,
                    ranking = 0,
                    rankingDate = ""
                };
            }

            var record = _scoreRankingRecords.FirstOrDefault(r =>
                r.UserId == userId &&
                r.TournamentId == competitionId &&
                r.IsBestScore == 1);

            if (record != null)
            {
                return new UserScoreRanking
                {
                    tournamentId = record.TournamentId,
                    totalScore = record.TotalScore,
                    ranking = record.Ranking,
                    rankingDate = record.RankingDate
                };
            }

            return new UserScoreRanking
            {
                tournamentId = 0,
                totalScore = 0,
                ranking = 0,
                rankingDate = ""
            };
        }

        public GameTournamentInfo[] CreateTournamentInfoList()
        {
            var musicIdArray = ParseIntArray(MusicIds);
            var lockedMusicIdArray = ParseIntArray(LockedMusicIds);

            var musicList = new GameTournamentMusic[musicIdArray.Length];
            for (int i = 0; i < musicIdArray.Length; i++)
            {
                musicList[i] = new GameTournamentMusic
                {
                    tournamentId = TournamentId,
                    musicId = musicIdArray[i],
                    level = 0,
                    isFirstLock = Array.Exists(lockedMusicIdArray, id => id == musicIdArray[i])
                };
            }

            return
            [
                new GameTournamentInfo
                {
                    tournamentId = TournamentId,
                    tournamentName = TournamentName,
                    rankingKind = RankingKind,
                    scoreType = 0,
                    noticeStartDate = NoticeStartDate,
                    noticeEndDate = NoticeEndDate,
                    startDate = StartDate,
                    endDate = EndDate,
                    entryStartDate = EntryStartDate,
                    entryEndDate = EntryEndDate,
                    gameTournamentMusicList = musicList
                }
            ];
        }

        private int[] ParseIntArray(string input)
        {
            if (string.IsNullOrEmpty(input))
                return Array.Empty<int>();

            var parts = input.Split(',');
            var result = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (int.TryParse(parts[i].Trim(), out int value))
                    result[i] = value;
            }
            return result;
        }
    }
}