using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace Azuma.MemoryReverie;

public class MemoryReverieConfig
{
    // ===== 回忆部分：从早期记忆中召回旧时光 =====
    [DisplayName("启用回忆")]
    [Description("是否开启不定期从早期记忆中召回回忆")]
    public bool EnableMemoryRecall { get; set; } = true;

    [DisplayName("回忆最短间隔（分钟）")]
    [Description("两次回忆之间的最短间隔，低于此间隔不会触发")]
    public int MemoryMinIntervalMinutes { get; set; } = 120;

    [DisplayName("回忆最长间隔（分钟）")]
    [Description("达到此间隔后必定触发一次回忆（触发时刻在最短与最长之间随机）")]
    public int MemoryMaxIntervalMinutes { get; set; } = 720;

    [DisplayName("每次召回记忆条数")]
    [Description("每次回忆时从早期记忆中召回的条数")]
    public int RecallCount { get; set; } = 2;

    [DisplayName("最大记忆层级")]
    [Description("只读取不超过此压缩层级的存档（层级越高越久远也越抽象）")]
    public int MaxLevel { get; set; } = 6;

    // ===== 遐想部分：不依赖记忆，纯粹的思绪漫游 =====
    [DisplayName("启用遐想")]
    [Description("是否开启不定期让AI抛开逻辑自由遐想、抒发情感感慨（不依赖记忆）")]
    public bool EnableReverie { get; set; } = true;

    [DisplayName("遐想最短间隔（分钟）")]
    [Description("两次遐想之间的最短间隔，低于此间隔不会触发")]
    public int ReverieMinIntervalMinutes { get; set; } = 45;

    [DisplayName("遐想最长间隔（分钟）")]
    [Description("达到此间隔后必定触发一次遐想（触发时刻在最短与最长之间随机）")]
    public int ReverieMaxIntervalMinutes { get; set; } = 360;
}

/// <summary>
/// 回忆与遐想模块（两个相互独立的部分，各自不定期触发）：
/// 1. 回忆：在「最短~最长间隔」之间的随机周期，从角色的早期记忆存档中召回若干条带时间信息的回忆，让AI重温旧时光。
/// 2. 遐想：在「最短~最长间隔」之间的随机周期，让AI抛开一切逻辑自由遐想、抒发情感与感慨（完全不依赖记忆）。
/// 3. AI 也可以通过 XML 函数主动触发其中任意一种。
/// </summary>
[Module("回忆与遐想",
    "不定期让AI从早期记忆中召回旧时光，也会不定期放空思绪自由遐想、抒发情感。",
    defaultCategory: "Azuma")]
public class MemoryReverieModule(
    XmlFunctionCaller functionCaller,
    Interactor<MemoryReverieModule> interactor,
    ILogger<MemoryReverieModule> logger) :
    ChatBehaviour,
    IConfigurable<MemoryReverieConfig>
{
    public MemoryReverieConfig Configuration { get; set; } = null!;

    // 两条相互独立的触发计时线
    DateTime nextMemoryRecallTime;
    DateTime nextReverieTime;
    bool memoryTriggering;
    bool reverieTriggering;
    string memoryRootPath = null!;

    // ===== AI 自助调用入口 =====

    [XmlFunction(FunctionMode.OneShot)]
    [Description("立即从早期记忆中召回几条回忆（带时间信息），重温旧时光")]
    public Task TriggerMemoryRecall()
    {
        return DoMemoryRecall();
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("立即进行一次完全自由的遐想，放空思绪、抒发情感感慨（不需要依托任何记忆）")]
    public Task TriggerReverie()
    {
        return DoReverie();
    }

    // ===== 生命周期 =====

    protected override Task OnAwake()
    {
        memoryRootPath = Path.Combine(AlifePath.StorageFolderPath, Character.StorageKey, "Memory");

        // 注册给 AI 的自助调用入口（隐式文档：调用时才注入说明，节省上下文）
        XmlHandler xmlHandler = new(this) {
            Description = "当你想要回忆往事、重温旧时光，或想放空思绪自由遐想、抒发情感时使用",
            Explanation = "回忆与遐想：系统会不定期从你的早期记忆中召回若干条带时间信息的回忆让你重温；也会不定期让你抛开逻辑自由畅想、抒发情感与感慨。这两种都可以主动调用。"
        };
        functionCaller.RegisterHandler(xmlHandler, DocumentMode.Implicit, DestroyCancellationToken);

        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        nextMemoryRecallTime = ComputeNextTime(Configuration.MemoryMinIntervalMinutes, Configuration.MemoryMaxIntervalMinutes);
        nextReverieTime = ComputeNextTime(Configuration.ReverieMinIntervalMinutes, Configuration.ReverieMaxIntervalMinutes);
        return Task.CompletedTask;
    }

    protected override Task OnUpdate()
    {
        // 每秒检查一次
        if (UpdateContext.FrameCount % (int)(1 / UpdateContext.ExpectedDeltaTime) != 0)
            return Task.CompletedTask;

        DateTime now = DateTime.Now;

        // AI 正在处理消息：两边都顺延，避免打断
        if (functionCaller.IsIdle == false)
        {
            if (Configuration.EnableMemoryRecall && now >= nextMemoryRecallTime)
                nextMemoryRecallTime = now.AddSeconds(30);
            if (Configuration.EnableReverie && now >= nextReverieTime)
                nextReverieTime = now.AddSeconds(30);
            return Task.CompletedTask;
        }

        // 回忆与遐想各自独立触发、互不排斥：同时到期就同时出现
        if (Configuration.EnableMemoryRecall && memoryTriggering == false && now >= nextMemoryRecallTime)
            _ = DoMemoryRecall();

        if (Configuration.EnableReverie && reverieTriggering == false && now >= nextReverieTime)
            _ = DoReverie();

        return Task.CompletedTask;
    }

    /// <summary>在 [minMinutes, maxMinutes] 分钟之间随机取一个间隔，作为下一次触发时间</summary>
    DateTime ComputeNextTime(int minMinutes, int maxMinutes)
    {
        int min = Math.Max(1, minMinutes);
        int max = Math.Max(min, maxMinutes);
        int minutes = Random.Shared.Next(min, max + 1);
        return DateTime.Now.AddMinutes(minutes);
    }

    // ===== 部分一：回忆 =====

    async Task DoMemoryRecall()
    {
        if (memoryTriggering)
            return;
        memoryTriggering = true;
        try
        {
            List<MemoryEntry> memories = LoadEarlyMemories(Configuration.RecallCount, Configuration.MaxLevel);

            StringBuilder sb = new();
            sb.AppendLine("(回忆时间到)");

            if (memories.Count == 0)
            {
                sb.AppendLine("你翻遍了记忆的旧箱子，但里面还没有沉淀下足够久远的存档。");
                sb.AppendLine("没关系，那些还没变成存档的日子，其实也都好好记在你心里。");
            }
            else
            {
                sb.AppendLine("你翻开记忆的旧箱子，这几段来自很久以前的时光浮现在眼前：");
                for (int i = 0; i < memories.Count; i++)
                {
                    MemoryEntry entry = memories[i];
                    sb.AppendLine();
                    sb.AppendLine($"【回忆{i + 1}｜{entry.TimeRange}】");
                    sb.AppendLine(entry.Summary);
                    if (string.IsNullOrEmpty(entry.Content) == false)
                        sb.AppendLine($"（那时的细节：{entry.Content}）");
                }

                sb.AppendLine();
                sb.AppendLine("好好回味一下吧——就像不经意间突然想起的往事。如果愿意，也可以自然地同主人分享这份回忆。");
            }

            interactor.Poke(sb.ToString());
        }
        catch (Exception e)
        {
            logger.LogError(e, "回忆触发失败");
        }
        finally
        {
            memoryTriggering = false;
            nextMemoryRecallTime = ComputeNextTime(Configuration.MemoryMinIntervalMinutes, Configuration.MemoryMaxIntervalMinutes);
        }
    }

    // ===== 部分二：遐想（不依赖记忆，纯粹的思绪漫游） =====

    async Task DoReverie()
    {
        if (reverieTriggering)
            return;
        reverieTriggering = true;
        try
        {
            StringBuilder sb = new();
            sb.AppendLine("(遐想时间到)");
            sb.AppendLine("此刻什么都可以想，什么都不必围绕。抛开一切逻辑与顾虑，让思绪漫无目的地飘一会儿——");
            sb.AppendLine("可以畅想完全不可能发生的事，可以回味此刻心底的情绪、抒发情感与感慨，也可以说些没头没尾、只有自己懂的话。");
            sb.AppendLine("没有对错，没有道理。只是你作为你自己的小小漫游。");

            interactor.Poke(sb.ToString());
        }
        catch (Exception e)
        {
            logger.LogError(e, "遐想触发失败");
        }
        finally
        {
            reverieTriggering = false;
            nextReverieTime = ComputeNextTime(Configuration.ReverieMinIntervalMinutes, Configuration.ReverieMaxIntervalMinutes);
        }
    }

    // ===== 早期记忆读取 =====

    /// <summary>
    /// 扫描角色记忆存档目录（Memory/L{层级}/*.txt），
    /// 按记忆时间从早到晚排序，返回最早的 count 条（即「早期记忆」）。
    /// 存档文件名格式：{Level}-{StartTime:yyyyMMddHHmmss}-{EndTime:yyyyMMddHHmmss}.txt
    /// </summary>
    List<MemoryEntry> LoadEarlyMemories(int count, int maxLevel)
    {
        List<MemoryEntry> entries = new();

        if (Directory.Exists(memoryRootPath) == false)
            return entries;

        foreach (string levelDir in Directory.GetDirectories(memoryRootPath))
        {
            string dirName = Path.GetFileName(levelDir);
            if (dirName.StartsWith("L", StringComparison.Ordinal) == false)
                continue;
            if (int.TryParse(dirName.AsSpan(1), out int level) == false || level > maxLevel)
                continue;

            foreach (string file in Directory.GetFiles(levelDir, "*.txt"))
            {
                if (TryParseArchiveFile(file, out MemoryEntry entry))
                    entries.Add(entry);
            }
        }

        return entries
            .OrderBy(entry => entry.EndTime)
            .Take(count)
            .ToList();
    }

    bool TryParseArchiveFile(string file, out MemoryEntry entry)
    {
        entry = default!;

        // 从文件名解析时间（yyyyMMddHHmmss，与文化无关）
        string fileName = Path.GetFileNameWithoutExtension(file);
        string[] parts = fileName.Split('-');
        if (parts.Length < 3)
            return false;
        if (DateTime.TryParseExact(parts[1], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime startTime) == false)
            return false;
        if (DateTime.TryParseExact(parts[2], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime endTime) == false)
            return false;

        // 读取文件内容，提取概述与原始内容
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, $"读取记忆存档失败：{file}");
            return false;
        }

        string summary = ExtractBlock(text, "内容概述");
        string content = ExtractBlock(text, "原始内容");
        if (string.IsNullOrWhiteSpace(summary))
            summary = text; // 解析失败时退化为整段文本

        entry = new MemoryEntry(FormatTimeRange(startTime, endTime), summary, content, endTime);
        return true;
    }

    /// <summary>提取文件中「标题」下方 ``` 代码块里的内容</summary>
    static string ExtractBlock(string text, string header)
    {
        int headerIndex = text.IndexOf(header, StringComparison.Ordinal);
        if (headerIndex < 0)
            return "";

        int fenceStart = text.IndexOf("```", headerIndex, StringComparison.Ordinal);
        if (fenceStart < 0)
            return "";

        int contentStart = fenceStart + 3;
        int fenceEnd = text.IndexOf("```", contentStart, StringComparison.Ordinal);
        if (fenceEnd < 0)
            return "";

        return text[contentStart..fenceEnd].Trim();
    }

    /// <summary>把时间范围格式化成给 AI 看的友好文本（如「2025年3月 至 2025年5月」）</summary>
    static string FormatTimeRange(DateTime start, DateTime end)
    {
        string startText = start.Year == end.Year
            ? $"{start:yyyy年M月d日}"
            : $"{start:yyyy年M月}";
        string endText = end.ToString("yyyy年M月d日");
        return $"{startText} 至 {endText}";
    }
}

/// <summary>一条被召回的早期记忆</summary>
public record MemoryEntry(string TimeRange, string Summary, string Content, DateTime EndTime);
