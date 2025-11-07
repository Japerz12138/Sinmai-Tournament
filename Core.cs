using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
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
        private static TournamentConfig config;

        public override void OnInitializeMelon()
        {
            config = new TournamentConfig();
            config.Load();
            HarmonyInstance.PatchAll(typeof(TournamentPatches));
            HarmonyInstance.PatchAll(typeof(PacketUpsertUserAllPatch));
            LoggerInstance.Msg("本Mod已开源，仅供研究学习使用，禁止任何商业行为，数据均在本地处理并存储，与任何服务器无关，如有冲突请手动禁用此Mod。");
            LoggerInstance.Msg("Tournament Mod loaded!");
            if (config.EnableApiUpload)
            {
                LoggerInstance.Msg($"API Upload enabled - URL: {config.ApiUrl}, Timeout: {config.ApiTimeout}s");
                if (!string.IsNullOrWhiteSpace(config.ApiKey))
                {
                    LoggerInstance.Msg("API Key is configured");
                }
                else
                {
                    LoggerInstance.Msg("Warning: API Key is not set");
                }
            }
            else
            {
                LoggerInstance.Msg("API Upload is disabled");
            }
        }

        [HarmonyPatch]
        public class TournamentPatches
        {
            [HarmonyPatch(typeof(PacketGetGameTournamentInfo), "Proc")]
            [HarmonyPostfix]
            public static void Proc_Postfix(PacketGetGameTournamentInfo __instance, ref PacketState __result)
            {
                try
                {
                    if (!config.Enabled) return;

                    var onDoneField = AccessTools.Field(typeof(PacketGetGameTournamentInfo), "_onDone");
                    var onErrorField = AccessTools.Field(typeof(PacketGetGameTournamentInfo), "_onError");

                    var onDone = (Action<GameTournamentInfo[]>)onDoneField.GetValue(__instance);
                    var onError = (Action<PacketStatus>)onErrorField.GetValue(__instance);

                    if (__result == PacketState.Done)
                    {
                        var customData = GetCustomTournamentData();
                        if (config.DebugLog)
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
            public static bool ProcUserScoreRanking_Prefix(PacketGetUserScoreRanking __instance, ref PacketState __result)
            {
                try
                {
                    if (!config.EnableScoreRanking) return true;

                    var onDoneField = AccessTools.Field(typeof(PacketGetUserScoreRanking), "_onDone");
                    var responseVOsField = AccessTools.Field(typeof(PacketGetUserScoreRanking), "_responseVOs");
                    var rankingIdsField = AccessTools.Field(typeof(PacketGetUserScoreRanking), "_rankingIds");
                    var listIndexField = AccessTools.Field(typeof(PacketGetUserScoreRanking), "_listIndex");
                    var listLengthField = AccessTools.Field(typeof(PacketGetUserScoreRanking), "_listLength");

                    var onDone = (Action<UserScoreRanking[]>)onDoneField.GetValue(__instance);
                    var responseVOs = (List<UserScoreRankingResponseVO>)responseVOsField.GetValue(__instance);
                    var rankingIds = (List<int>)rankingIdsField.GetValue(__instance);
                    var listIndex = (int)listIndexField.GetValue(__instance);
                    var listLength = (int)listLengthField.GetValue(__instance);

                    if (listLength <= 0 || rankingIds == null || rankingIds.Count == 0)
                        return true;

                    var query = __instance.Query as NetQuery<UserScoreRankingRequestVO, UserScoreRankingResponseVO>;
                    if (query == null) return true;

                    var request = query.Request;
                    var userId = request.userId;

                    bool alreadyProcessed = responseVOs.Any(vo => vo.userId == userId);

                    if (!alreadyProcessed)
                    {
                        var customRankings = new List<UserScoreRanking>();

                        foreach (var competitionId in rankingIds)
                        {
                            var rankingData = config.GetUserScoreRanking(userId, competitionId);
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

                            listIndexField.SetValue(__instance, listLength);

                            var queryStateField = AccessTools.Field(query.GetType(), "_state");
                            if (queryStateField != null)
                            {
                                queryStateField.SetValue(query, 2);
                            }

                            if (config.DebugLog)
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
                        if (config.DebugLog)
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
                    gameTournamentInfoList = config.CreateTournamentInfoList()
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

                    MelonLogger.Msg($"GetGameTournamentInfoApi Response: {jsonBuilder.ToString()}");
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

                    MelonLogger.Msg($"GetUserScoreRankingApi Final Response: {jsonBuilder.ToString()}");
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
            public static void Proc_Postfix(PacketUpsertUserAll __instance, ref PacketState __result)
            {
                try
                {
                    if (!config.Enabled || !config.EnableScoreRanking)
                        return;

                    if (__result != PacketState.Done)
                        return;

                    var query = __instance.Query as NetQuery<UserAllRequestVO, UpsertResponseVO>;
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

                var musicIds = ParseIntArray(config.MusicIds);
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
                    userId = request.userId,
                    userName = userName,
                    tournamentId = config.TournamentId,
                    level01 = songScores[0].Level,
                    score01 = (int)songScores[0].Achievement,
                    scoreDX01 = (int)songScores[0].DeluxScore,
                    level02 = songScores[1].Level,
                    score02 = (int)songScores[1].Achievement,
                    scoreDX02 = (int)songScores[1].DeluxScore,
                    level03 = songScores[2].Level,
                    score03 = (int)songScores[2].Achievement,
                    scoreDX03 = (int)songScores[2].DeluxScore,
                    playDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    isBestScore = 0,
                    ranking = 0,
                    rankingDate = ""
                };

                CalculateTotalScores(ref record);
                SaveAndUpdateRankings(record);
            }

            private static void CalculateTotalScores(ref ScoreRankingRecord record)
            {
                record.totalDXScore = record.scoreDX01 + record.scoreDX02 + record.scoreDX03;

                int baseTotalScore = record.score01 + record.score02 + record.score03;

                int penalty = 0;
                int[] levels = { record.level01, record.level02, record.level03 };

                foreach (int level in levels)
                {
                    switch (level)
                    {
                        case 0: penalty += 30000; break;
                        case 1: penalty += 20000; break;
                        case 2: penalty += 10000; break;
                        default: break;
                    }
                }

                record.totalScore = baseTotalScore - penalty;
            }

            private static void SaveAndUpdateRankings(ScoreRankingRecord newRecord)
            {
                List<ScoreRankingRecord> allRecords = new List<ScoreRankingRecord>();

                if (File.Exists(config.ScoreRankingCsvPath))
                {
                    try
                    {
                        var lines = File.ReadAllLines(config.ScoreRankingCsvPath);
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
                    .GroupBy(r => new { r.userId, r.totalScore, r.totalDXScore })
                    .Select(g => g.OrderBy(r => DateTime.Parse(r.playDate)).First())
                    .ToList();

                var userGroups = uniqueRecords
                    .GroupBy(r => r.userId)
                    .SelectMany(g =>
                    {
                        var userRecords = g.OrderByDescending(r => r.totalScore)
                                          .ThenByDescending(r => r.totalDXScore)
                                          .ToList();

                        for (int i = 0; i < userRecords.Count; i++)
                        {
                            userRecords[i].isBestScore = (i == 0) ? 1 : 0;
                        }
                        return userRecords;
                    })
                    .ToList();

                var bestRecords = userGroups.Where(r => r.isBestScore == 1).ToList();
                var rankedBestRecords = bestRecords
                    .OrderByDescending(r => r.totalScore)
                    .ThenByDescending(r => r.totalDXScore)
                    .Select((r, index) =>
                    {
                        r.ranking = index + 1;
                        r.rankingDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                        return r;
                    })
                    .ToList();

                foreach (var record in userGroups)
                {
                    if (record.isBestScore == 1)
                    {
                        var bestRecord = rankedBestRecords.FirstOrDefault(r =>
                            r.userId == record.userId &&
                            r.totalScore == record.totalScore &&
                            r.totalDXScore == record.totalDXScore);
                        if (bestRecord != null)
                        {
                            record.ranking = bestRecord.ranking;
                            record.rankingDate = bestRecord.rankingDate;
                        }
                    }
                    else
                    {
                        record.ranking = 0;
                        record.rankingDate = "";
                    }
                }

                SaveRecordsToCsv(userGroups);

                // Upload to API if enabled
                if (config.EnableApiUpload && !string.IsNullOrWhiteSpace(config.ApiUrl))
                {
                    MelonLogger.Msg($"API upload enabled. URL: {config.ApiUrl}, Uploading best score records...");
                    
                    // Upload only the newly added/updated best score record
                    var newBestRecord = rankedBestRecords.FirstOrDefault(r => 
                        r.userId == newRecord.userId && 
                        r.isBestScore == 1);
                    
                    if (newBestRecord != null)
                    {
                        MelonLogger.Msg($"Attempting to upload record for user {newBestRecord.userId} (Score: {newBestRecord.totalScore})");
                        Task.Run(async () =>
                        {
                            try
                            {
                                var success = await ApiUploadService.UploadScoreRecord(newBestRecord, config);
                                if (success)
                                {
                                    MelonLogger.Msg($"Successfully uploaded record for user {newBestRecord.userId}");
                                }
                            }
                            catch (Exception ex)
                            {
                                MelonLogger.Error($"Exception during API upload: {ex}");
                            }
                        });
                    }
                    else
                    {
                        MelonLogger.Msg($"No new best record found for user {newRecord.userId}, skipping upload");
                    }
                }
                else
                {
                    if (config.DebugLog)
                    {
                        MelonLogger.Msg($"API upload disabled or URL not set. EnableApiUpload: {config.EnableApiUpload}, ApiUrl: {config.ApiUrl}");
                    }
                }

                MelonLogger.Msg($"Updated tournament rankings. Total records: {userGroups.Count}, Participants: {rankedBestRecords.Count}");
            }

            private static ScoreRankingRecord ParseRecordFromCsv(string[] parts)
            {
                return new ScoreRankingRecord
                {
                    userId = ulong.TryParse(parts[0], out ulong uid) ? uid : 0,
                    userName = parts[1],
                    tournamentId = int.TryParse(parts[2], out int tid) ? tid : 0,
                    level01 = int.TryParse(parts[3], out int l1) ? l1 : 0,
                    score01 = int.TryParse(parts[4], out int s1) ? s1 : 0,
                    scoreDX01 = int.TryParse(parts[5], out int dx1) ? dx1 : 0,
                    level02 = int.TryParse(parts[6], out int l2) ? l2 : 0,
                    score02 = int.TryParse(parts[7], out int s2) ? s2 : 0,
                    scoreDX02 = int.TryParse(parts[8], out int dx2) ? dx2 : 0,
                    level03 = int.TryParse(parts[9], out int l3) ? l3 : 0,
                    score03 = int.TryParse(parts[10], out int s3) ? s3 : 0,
                    scoreDX03 = int.TryParse(parts[11], out int dx3) ? dx3 : 0,
                    totalScore = long.TryParse(parts[12], out long ts) ? ts : 0,
                    totalDXScore = int.TryParse(parts[13], out int tdx) ? tdx : 0,
                    playDate = parts[14],
                    isBestScore = int.TryParse(parts[15], out int best) ? best : 0,
                    ranking = int.TryParse(parts[16], out int rank) ? rank : 0,
                    rankingDate = parts[17]
                };
            }

            private static void SaveRecordsToCsv(List<ScoreRankingRecord> records)
            {
                try
                {
                    var directory = Path.GetDirectoryName(config.ScoreRankingCsvPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    var lines = new List<string>
                    {
                        "userId,userName,tournamentId,level01,score01,scoreDX01,level02,score02,scoreDX02,level03,score03,scoreDX03,totalScore,totalDXScore,playDate,isBestScore,ranking,rankingDate"
                    };

                    var sortedRecords = records
                        .OrderBy(r => r.userId)
                        .ThenByDescending(r => r.totalScore)
                        .ThenByDescending(r => r.totalDXScore)
                        .ToList();

                    foreach (var record in sortedRecords)
                    {
                        var line = $"{record.userId},{EscapeCsv(record.userName)},{record.tournamentId}," +
                                   $"{record.level01},{record.score01},{record.scoreDX01}," +
                                   $"{record.level02},{record.score02},{record.scoreDX02}," +
                                   $"{record.level03},{record.score03},{record.scoreDX03}," +
                                   $"{record.totalScore},{record.totalDXScore},{EscapeCsv(record.playDate)}," +
                                   $"{record.isBestScore},{record.ranking},{EscapeCsv(record.rankingDate)}";
                        lines.Add(line);
                    }

                    File.WriteAllLines(config.ScoreRankingCsvPath, lines);
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
        public ulong userId { get; set; }
        public string userName { get; set; }
        public int tournamentId { get; set; }
        public int level01 { get; set; }
        public int score01 { get; set; }
        public int scoreDX01 { get; set; }
        public int level02 { get; set; }
        public int score02 { get; set; }
        public int scoreDX02 { get; set; }
        public int level03 { get; set; }
        public int score03 { get; set; }
        public int scoreDX03 { get; set; }
        public long totalScore { get; set; }
        public int totalDXScore { get; set; }
        public string playDate { get; set; }
        public int isBestScore { get; set; }
        public int ranking { get; set; }
        public string rankingDate { get; set; }
    }

    public class ApiUploadService
    {
        public static async Task<bool> UploadScoreRecord(ScoreRankingRecord record, TournamentConfig config)
        {
            if (!config.EnableApiUpload || string.IsNullOrWhiteSpace(config.ApiUrl))
            {
                MelonLogger.Warning($"API upload skipped: EnableApiUpload={config.EnableApiUpload}, ApiUrl={config.ApiUrl}");
                return false;
            }

            MelonLogger.Msg($"Starting API upload for user {record.userId} to {config.ApiUrl}");

            return await Task.Run(() =>
            {
                try
                {
                    var json = SerializeToJson(record);
                    if (config.DebugLog)
                    {
                        MelonLogger.Msg($"Request JSON: {json}");
                    }

                    var request = (HttpWebRequest)WebRequest.Create(config.ApiUrl);
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Timeout = config.ApiTimeout * 1000; // Convert to milliseconds

                    if (!string.IsNullOrWhiteSpace(config.ApiKey))
                    {
                        request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
                        if (config.DebugLog)
                        {
                            MelonLogger.Msg("API Key provided, adding Authorization header");
                        }
                    }
                    else
                    {
                        if (config.DebugLog)
                        {
                            MelonLogger.Msg("No API Key provided, sending request without authentication");
                        }
                    }

                    var jsonBytes = Encoding.UTF8.GetBytes(json);
                    request.ContentLength = jsonBytes.Length;

                    MelonLogger.Msg($"Sending POST request to {config.ApiUrl}...");

                    using (var requestStream = request.GetRequestStream())
                    {
                        requestStream.Write(jsonBytes, 0, jsonBytes.Length);
                    }

                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        var statusCode = (int)response.StatusCode;
                        string responseContent = "";
                        
                        // Read response content first
                        try
                        {
                            using (var responseStream = response.GetResponseStream())
                            {
                                if (responseStream != null)
                                {
                                    using (var reader = new StreamReader(responseStream, Encoding.UTF8))
                                    {
                                        responseContent = reader.ReadToEnd();
                                    }
                                }
                            }
                        }
                        catch (Exception readEx)
                        {
                            MelonLogger.Warning($"Failed to read response content: {readEx.Message}");
                            responseContent = "[Unable to read response]";
                        }
                        
                        if (statusCode >= 200 && statusCode < 300)
                        {
                            MelonLogger.Msg($"Successfully uploaded score record for user {record.userId} to API (Status: {statusCode})");
                            
                            if (config.DebugLog && !string.IsNullOrEmpty(responseContent))
                            {
                                try
                                {
                                    MelonLogger.Msg($"API Response: {responseContent}");
                                }
                                catch (Exception logEx)
                                {
                                    MelonLogger.Warning($"Failed to log API response: {logEx.Message}");
                                }
                            }
                            return true;
                        }
                        else
                        {
                            MelonLogger.Error($"API upload failed with status {statusCode}: {responseContent}");
                            return false;
                        }
                    }
                }
                catch (WebException ex)
                {
                    if (ex.Response is HttpWebResponse httpResponse)
                    {
                        var statusCode = (int)httpResponse.StatusCode;
                        string errorContent = "";
                        try
                        {
                            using (var responseStream = httpResponse.GetResponseStream())
                            {
                                if (responseStream != null)
                                {
                                    using (var reader = new StreamReader(responseStream, Encoding.UTF8))
                                    {
                                        errorContent = reader.ReadToEnd();
                                    }
                                }
                            }
                        }
                        catch (Exception readEx)
                        {
                            MelonLogger.Warning($"Failed to read error response: {readEx.Message}");
                            errorContent = "[Unable to read error response]";
                        }
                        
                        try
                        {
                            MelonLogger.Error($"API upload failed with status {statusCode}: {errorContent}");
                        }
                        catch (Exception logEx)
                        {
                            MelonLogger.Error($"API upload failed with status {statusCode} (unable to log response content)");
                        }
                    }
                    else
                    {
                        MelonLogger.Error($"Web exception during API upload: {ex.Message}");
                        if (ex.InnerException != null)
                        {
                            MelonLogger.Error($"Inner exception: {ex.InnerException.Message}");
                        }
                    }
                    return false;
                }
                catch (Exception e)
                {
                    MelonLogger.Error($"Failed to upload score record to API: {e.GetType().Name} - {e.Message}");
                    MelonLogger.Error($"Stack trace: {e.StackTrace}");
                    return false;
                }
            });
        }

        private static string SerializeToJson(ScoreRankingRecord record)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"userId\":{record.userId},");
            sb.Append($"\"userName\":\"{EscapeJsonString(record.userName)}\",");
            sb.Append($"\"tournamentId\":{record.tournamentId},");
            sb.Append($"\"level01\":{record.level01},");
            sb.Append($"\"score01\":{record.score01},");
            sb.Append($"\"scoreDX01\":{record.scoreDX01},");
            sb.Append($"\"level02\":{record.level02},");
            sb.Append($"\"score02\":{record.score02},");
            sb.Append($"\"scoreDX02\":{record.scoreDX02},");
            sb.Append($"\"level03\":{record.level03},");
            sb.Append($"\"score03\":{record.score03},");
            sb.Append($"\"scoreDX03\":{record.scoreDX03},");
            sb.Append($"\"totalScore\":{record.totalScore},");
            sb.Append($"\"totalDXScore\":{record.totalDXScore},");
            sb.Append($"\"playDate\":\"{EscapeJsonString(record.playDate)}\",");
            sb.Append($"\"isBestScore\":{record.isBestScore},");
            sb.Append($"\"ranking\":{record.ranking},");
            sb.Append($"\"rankingDate\":\"{EscapeJsonString(record.rankingDate)}\"");
            sb.Append("}");
            return sb.ToString();
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

    public class TournamentConfig
    {
        public bool Enabled { get; set; } = false;
        public bool DebugLog { get; set; } = false;
        public int TournamentId { get; set; } = 0;
        public string TournamentName { get; set; } = "";
        public int RankingKind { get; set; } = 0;
        public string NoticeStartDate { get; set; } = "1970-01-01 00:00:00";
        public string NoticeEndDate { get; set; } = "1970-01-01 00:00:00";
        public string StartDate { get; set; } = "1970-01-01 00:00:00";
        public string EndDate { get; set; } = "1970-01-01 00:00:00";
        public string EntryStartDate { get; set; } = "1970-01-01 00:00:00";
        public string EntryEndDate { get; set; } = "1970-01-01 00:00:00";
        public string MusicIds { get; set; } = "";
        public string LockedMusicIds { get; set; } = "";
        public bool EnableScoreRanking { get; set; } = false;
        public string ScoreRankingCsvPath { get; set; } = "UserData/ScoreRanking.csv";
        public bool EnableApiUpload { get; set; } = false;
        public string ApiUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public int ApiTimeout { get; set; } = 30;

        private List<ScoreRankingRecord> scoreRankingRecords = new List<ScoreRankingRecord>();

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
            var enableApiUpload = category.CreateEntry("EnableApiUpload", EnableApiUpload, "Enable API upload for score ranking data");
            var apiUrl = category.CreateEntry("ApiUrl", ApiUrl, "API endpoint URL for uploading score data");
            var apiKey = category.CreateEntry("ApiKey", ApiKey, "API key for authentication (optional)");
            var apiTimeout = category.CreateEntry("ApiTimeout", ApiTimeout, "API request timeout in seconds");

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
            EnableApiUpload = enableApiUpload.Value;
            ApiUrl = apiUrl.Value;
            ApiKey = apiKey.Value;
            ApiTimeout = apiTimeout.Value;

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

                scoreRankingRecords.Clear();

                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split(',');
                    if (parts.Length >= 18)
                    {
                        var record = new ScoreRankingRecord
                        {
                            userId = ulong.TryParse(parts[0], out ulong uid) ? uid : 0,
                            userName = parts[1],
                            tournamentId = int.TryParse(parts[2], out int tid) ? tid : 0,
                            level01 = int.TryParse(parts[3], out int l1) ? l1 : 0,
                            score01 = int.TryParse(parts[4], out int s1) ? s1 : 0,
                            scoreDX01 = int.TryParse(parts[5], out int dx1) ? dx1 : 0,
                            level02 = int.TryParse(parts[6], out int l2) ? l2 : 0,
                            score02 = int.TryParse(parts[7], out int s2) ? s2 : 0,
                            scoreDX02 = int.TryParse(parts[8], out int dx2) ? dx2 : 0,
                            level03 = int.TryParse(parts[9], out int l3) ? l3 : 0,
                            score03 = int.TryParse(parts[10], out int s3) ? s3 : 0,
                            scoreDX03 = int.TryParse(parts[11], out int dx3) ? dx3 : 0,
                            totalScore = long.TryParse(parts[12], out long ts) ? ts : 0,
                            totalDXScore = int.TryParse(parts[13], out int tdx) ? tdx : 0,
                            playDate = parts[14],
                            isBestScore = int.TryParse(parts[15], out int best) ? best : 0,
                            ranking = int.TryParse(parts[16], out int rank) ? rank : 0,
                            rankingDate = parts[17]
                        };

                        scoreRankingRecords.Add(record);
                    }
                }

                MelonLogger.Msg($"Loaded {scoreRankingRecords.Count} score ranking records from {ScoreRankingCsvPath}");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Failed to load score ranking CSV: {e}");
            }
        }

        public UserScoreRanking GetUserScoreRanking(ulong userId, int competitionId)
        {
            if (!EnableScoreRanking || scoreRankingRecords.Count == 0)
            {
                return new UserScoreRanking
                {
                    tournamentId = 0,
                    totalScore = 0,
                    ranking = 0,
                    rankingDate = ""
                };
            }

            var record = scoreRankingRecords.FirstOrDefault(r =>
                r.userId == userId &&
                r.tournamentId == competitionId &&
                r.isBestScore == 1);

            if (record != null)
            {
                return new UserScoreRanking
                {
                    tournamentId = record.tournamentId,
                    totalScore = record.totalScore,
                    ranking = record.ranking,
                    rankingDate = record.rankingDate
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

            return new GameTournamentInfo[]
            {
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
            };
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