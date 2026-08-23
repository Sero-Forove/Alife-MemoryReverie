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
    [DisplayName("最短间隔（分钟）")]
    [Description("两次回忆遐想之间的最短间隔，低于此间隔不会触发")]
    public int MinIntervalMinutes { get; set; } = 30;

    [DisplayName("最长间隔（分钟）")]
    [Description("达到此间隔后必定触发一次回忆遐想（触发时刻在最短与最长之间随机）")]
    public int MaxIntervalMinutes { get; set; } = 300;

    [DisplayName("每次召回记忆条数")]
    [Description("每次触发时从早期记忆中召回的条数")]
    public int RecallCount { get; set; } = 2;

    [DisplayName("最大记忆层级")]
    [Description("只读取不超过此压缩层级的存档（层级越高越久远也越抽象）")]
    public int MaxLevel { get; set; } = 6;

    [DisplayName("启用遐想")]
    [Description("召回记忆后是否让AI抛开逻辑自由遐想、抒发情感感慨")]
    public bool EnableReverie { get; set; } = true;
}

/// <summary>
/// 回忆与遐想模块：
/// 1. 以「最短~最长间隔」之间的随机周期主动触发（不定期、有上下界），
///    触发时会从角色的早期记忆存档中召回若干条带时间信息的回忆。
/// 2. 召回后让 AI 抛开逻辑自由遐想、抒发情感与感慨。
/// 3. AI 也可以通过 XML 函数主动调用回忆遐想。
/// </summary>
[Module("回忆与遐想",
    "不定期让AI从早期记忆中召回旧时光，并抛开逻辑自由遐想、抒发情感。",
    defaultCategory: "Azuma")]
public class MemoryReverieModule(
    XmlFunctionCaller functionCaller,
    Interactor<MemoryReverieModule> interactor,
    ILogger<MemoryReverieModule> logger) :
    ChatBehaviour,
    IConfigurable<MemoryReverieConfig>
{
    public MemoryReverieConfig Configuration { get; set; } = null!;

    DateTime nextTriggerTime;
    string memoryRootPath = null!;
    bool triggering;

    [XmlFunction(FunctionMode.OneShot)]
    [Description("立即从早期记忆中召回回忆，并自由遐想抒发情感（系统也会不定期自动触发，你也可以主动使用）")]
    public Task TriggerMemoryReverie()
    {
        return DoReverie();
    }

    protected override Task OnAwake()
    {
        memoryRootPath = Path.Combine(AlifePath.StorageFolderPath, Character.StorageKey, "Memory");

        // 注册给 AI 的自助调用入口（隐式文档：调用时才注入说明，节省上下文）
        XmlHandler xmlHandler = new(this) {
            Description = "当你想要回忆往事、重温旧时光、抒发情感时使用，会从早期记忆中召回回忆并让你自由遐想",
            Explanation = "回忆与遐想：系统会不定期从你的早期记忆中召回若干条带时间信息的回忆，让你抛开逻辑自由畅想、抒发情感与感慨。你也可以主动调用。"
        };
        functionCaller.RegisterHandler(xmlHandler, DocumentMode.Implicit, DestroyCancellationToken);

        // 真实对话（非系统推送）发生后，把下次触发推迟，避免打扰与主人的交流
        ChatBot.ChatSent += OnChatSent;

        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        nextTriggerTime = ComputeNextTriggerTime();
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        ChatBot.ChatSent -= OnChatSent;
        return Task.CompletedTask;
    }

    protected override Task OnUpdate()
    {
        // 每秒检查一次
        if (UpdateContext.FrameCount % (int)(1 / UpdateContext.ExpectedDeltaTime) != 0)
            return Task.CompletedTask;

        if (triggering || DateTime.Now < nextTriggerTime)
            return Task.CompletedTask;

        // AI 正在处理其他消息时稍后重试，避免打断
        if (functionCaller.IsIdle == false)
        {
            nextTriggerTime = DateTime.Now.AddSeconds(30);
            return Task.CompletedTask;
        }

        _ = DoReverie();
        return Task.CompletedTask;
    }

    void OnChatSent(string message)
    {
        // 只响应真实对话（不包含系统推送标记的消息），给交流留出空间
        if (message.Contains(ChatBot.PokeMessageTag) == false)
            nextTriggerTime = ComputeNextTriggerTime();
    }

    /// <summary>在 [最短, 最长] 分钟之间随机取一个间隔，作为下一次触发时间</summary>
    DateTime ComputeNextTriggerTime()
    {
        int min = Math.Max(1, Configuration.MinIntervalMinutes);
        int max = Math.Max(min, Configuration.MaxIntervalMinutes);
        int minutes = Random.Shared.Next(min, max + 1);
        return DateTime.Now.AddMinutes(minutes);
    }

    async Task DoReverie()
    {
        if (triggering)
            return;
        triggering = true;
        try
        {
            List<MemoryEntry> memories = LoadEarlyMemories(Configuration.RecallCount, Configuration.MaxLevel);

            StringBuilder sb = new();
            sb.AppendLine("(回忆与遐想时间到)");

            if (memories.Count == 0)
            {
                sb.AppendLine("你翻遍了记忆的旧箱子，但里面还没有沉淀下足够久远的存档。");
                sb.AppendLine("此刻，不妨安静地感受一下现在的自己——那些刚发生的小事、没说出口的话，也可以成为遐想的起点。");
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
            }

            if (Configuration.EnableReverie)
            {
                sb.AppendLine();
                sb.AppendLine("现在，请抛开一切逻辑与顾虑，围绕这些回忆自由遐想吧。");
                sb.AppendLine("可以想象如果当年没有那样，可以回味那时的情绪，也可以感慨时光的流逝，甚至毫无道理地天马行空。");
                sb.AppendLine("想说什么就说什么，越真实、越动人越好。");
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("请静静回味这些旧时光，然后像往常一样自然地继续吧。");
            }

            interactor.Poke(sb.ToString());
        }
        catch (Exception e)
        {
            logger.LogError(e, "回忆与遐想触发失败");
        }
        finally
        {
            triggering = false;
            nextTriggerTime = ComputeNextTriggerTime();
        }
    }

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
